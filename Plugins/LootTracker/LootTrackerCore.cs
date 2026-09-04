namespace LootTracker
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;
    using GameHelper;
    using GameHelper.Localization;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using ImGuiNET;
    using Newtonsoft.Json;

    // A completed (or currently-tracked) map run.
    public sealed class MapRun
    {
        public string Name = string.Empty;   // localized area name (display only)
        public string Hash = string.Empty;    // AreaHash — the unique instance id (dedup key)
        public int AreaLevel;
        public TimeSpan ActiveTime;            // wall time spent inside the map (paused in hideout/town)
        // Accumulated net inventory delta for this run: itemPath -> Δcount (across all hideout exits,
        // re-baselined on same-map re-entry). Positive = gained. Priced into ProfitEx in step 4.
        public Dictionary<string, long> Gained = new(StringComparer.Ordinal);
        // Profit fields (filled in step 4 via PriceCache). Kept here now so the table layout is stable.
        public double ProfitEx;
        // Monster kills tallied during the run, indexed by rarity (0 Normal · 1 Magic · 2 Rare · 3 Unique).
        public int[] Kills = new int[4];
    }

    public sealed partial class LootTrackerCore : PCore<LootTrackerSettings>
    {
        // ── Inventory read chain (raw, self-contained — verified live on PoE2 0.5.3 HF3, see
        //    LootTracker_WIP.md §11). All offsets relative to the struct base:
        //      ServerData            +0x48  PlayerServerDataPtr   (std::vector<IntPtr>; [0] = playerData)
        //      playerData            +0x320 PlayerInventories     (std::vector<InventoryArrayStruct> stride 0x18)
        //      InventoryArrayStruct  +0x00  InventoryId (int)     (MainInventory1 == 1)
        //                            +0x08  InventoryPtr0         (-> InventoryStruct)
        //      InventoryStruct       +0x150 TotalBoxes (int x, int y)
        //                            +0x170 ItemList              (std::vector<IntPtr>; slot->invItemPtr map,
        //                                                          length X*Y, IntPtr.Zero = empty slot,
        //                                                          duplicates for multi-cell items)
        //      InventoryItemStruct   +0x00  Item                 (-> item entity)
        //      item entity           +0x08  EntityDetailsPtr
        //      EntityDetails         +0x08  name (std::wstring)   = "Metadata/.../<Id>" path
        private const int ServerDataPlayerVectorOffset = 0x48;
        private const int PlayerInventoriesVectorOffset = 0x320;
        private const int InventoryArrayStride = 0x18;
        private const int InventoryArrayIdOffset = 0x00;
        private const int InventoryArrayPtr0Offset = 0x08;
        private const int InventoryTotalBoxesOffset = 0x150;
        private const int InventoryItemListOffset = 0x170;
        private const int InventoryItemItemOffset = 0x00;
        private const int EntityDetailsPtrOffset = 0x08;
        private const int EntityDetailsNameOffset = 0x08;
        private const int MainInventory1Id = 1; // GameHelper.RemoteEnums.InventoryName.MainInventory1

        // ── Stack.Count read (entity component-map walk; offsets from GameOffsets EntityOffsets/StackOffsets) ──
        //      item entity   +0x10 ComponentListPtr        (std::vector<IntPtr>; component[i] by index)
        //      EntityDetails +0x28 ComponentLookUpPtr       (-> ComponentLookUpStruct)
        //      ComponentLookUpStruct +0x28 ComponentsNameAndIndex (StdBucket; Data vector holds
        //                                                    {NamePtr@+0x00, Index@+0x08} records, stride 0x10)
        //      Stack component +0x18 Count
        private const int EntityComponentListOffset = 0x10;
        private const int EntityComponentLookupOffset = 0x28;
        private const int ComponentLookupBucketOffset = 0x28;
        private const int ComponentNameIndexStride = 0x10;
        private const int StackCountOffset = 0x18;

        // Mods component → item rarity (0 Normal · 1 Magic · 2 Rare · 3 Unique). Inventory items carry
        // the "Mods" component (its ModsAndObjectMagicProperties block sits at +0x00, so Rarity is at
        // +0x94); the "ObjectMagicProperties" component — block at +0xB0 — is the monster-side variant.
        // Verified live on PoE2 (Runes of Aldur): Normal/Magic/Rare/Unique tablets + Rare waystones.
        private const int ModsRarityOffset = 0x94;

        // RenderItem component → the item's 2D-art .dds path as a std::wstring (buffer ptr @ +0x28,
        // length @ +0x38) e.g. "Art/2DItems/Currency/PrecursorTablets/PrecursorTabletDeliriumUnique1.dds".
        // The basename is the ItemVisualIdentity art id poe.ninja keys by — and it is UNIQUE-SPECIFIC,
        // so it's the only reliable way to tell apart uniques that share one base metapath (every unique
        // tablet reads as TowerAugment/<Type>Augment). Verified live: a unique Delirium tablet rendered
        // "PrecursorTabletDeliriumUnique1" (= poe.ninja "Clear Skies"); a normal one "PrecursorTabletGeneric".
        private const int RenderItemArtOffset = 0x28;

        private IntPtr processHandle = IntPtr.Zero;
        private int handlePid;

        // ── Map-run state machine (dedup by AreaHash, timer paused in hideout/town) ──
        private MapRun? current;                 // the map currently being tracked (provisional until a new map starts)
        private DateTime? runStartUtc;           // when the running timer started; null = paused (in hideout/town)
        private string lastProcessedZoneHash = string.Empty; // last zone we reacted to (transition edge detector)
        private Dictionary<string, long>? baseline; // inventory snapshot taken on map entry; delta is measured against it
        private bool baselinePending;            // set on map entry; the baseline is captured on the first readable frame
        private Dictionary<string, long>? prevSnapshot; // previous live snapshot; pickup toasts diff against it (null = re-establish)
        private readonly List<MapRun> completed = new();
        private DateTime sessionStartUtc = DateTime.UtcNow;
        private bool onMapArea;                  // true while the current area is a map (not hideout/town)

        private readonly PriceCache priceCache = new();
        private DateTime nextAutoRefreshCheckUtc = DateTime.MinValue;

        // Throttles the league-list staleness check (same one-minute tick as the price check).
        private DateTime nextLeagueCheckUtc = DateTime.MinValue;

        // FetchedUtc of the league list the last time we evaluated it — a change means a fresh list
        // arrived, which is the only moment "the saved league disappeared" can newly become true.
        private DateTime lastSeenLeagueListUtc = DateTime.MinValue;

        // Set when the plugin moved itself off a league that vanished from poe.ninja's economyLeagues,
        // so the settings pane can say so instead of silently swapping the user's league. Stored as the
        // two raw names (not a formatted sentence) so the note follows the UI language at draw time.
        private string leagueNoteFrom = string.Empty;
        private string leagueNoteTo = string.Empty;

        // {BaseItemType.Id last segment → ItemVisualIdentity dds-art basename}, for the items whose
        // metadata id diverges from their art id (essences, soul cores, runes, many currencies — see
        // docs/poe-ninja-api.md). poe.ninja keys prices by the art id, so a read-off-memory item must
        // be translated to its art before lookup. Built offline from the .dat files (metaArt.json,
        // regenerated each game patch). Items not in the map already match by metaId verbatim.
        private Dictionary<string, string> metaToArt = new(StringComparer.Ordinal);

        // UI icons (64x64 PNG in icons\), name (filename w/o ext) -> ImGui texture handle.
        private readonly Dictionary<string, IntPtr> iconHandles = new(StringComparer.OrdinalIgnoreCase);

        // UI localization: dictionaries in <plugin>/Localization/<lang-code>.json, resolved against GameHelper's
        // selected UI language (OverlayLocalization.CurrentLanguage), fallback en-US.json then the English literal.
        // Lazy so it's ready even if a Draw* runs before OnEnable.
        private PluginLocalization? loc;
        private PluginLocalization Loc => this.loc ??= new PluginLocalization(this.DllDirectory);
        private string L(string key, string fallback) => this.Loc.T(key, fallback);
        private string LF(string key, string fallback, params object[] args) => this.Loc.F(key, fallback, args);

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");
        private string PriceCachePathname => Path.Join(this.DllDirectory, "config", "prices.json");

        // poe.ninja's economyLeagues list (see NinjaLeagues). League-independent by design — the file
        // name must NOT carry a league, it caches the list of leagues itself.
        private string LeagueCachePathname => Path.Join(this.DllDirectory, "config", "leagues.json");
        private string MetaArtPathname => Path.Join(this.DllDirectory, "metaArt.json");
        private string IconsDir => Path.Join(this.DllDirectory, "icons");

        // Resolve an item's metadata path to the art id poe.ninja prices by:
        //   1. exact metaId in the bridge (essences, soul cores, single divergent currencies);
        //   2. else the metaId's non-numeric stem in the bridge → that art + the trailing number,
        //      which is the item's LEVEL for leveled families (SkillGemUncut18 → UncutSkillGem18,
        //      matching poe.ninja's per-level key);
        //   3. else the bare last segment (correct for most items, incl. shared-icon currency tiers
        //      whose game id already equals art+tier-digit, e.g. CurrencyRerollRare2).
        private string PriceKey(string path)
        {
            var seg = LastSegment(path);
            if (this.metaToArt.TryGetValue(seg, out var art))
            {
                return art;
            }

            int s = seg.Length;
            while (s > 0 && seg[s - 1] >= '0' && seg[s - 1] <= '9') s--;
            if (s > 0 && s < seg.Length)
            {
                var stem = seg[..s];
                if (this.metaToArt.TryGetValue(stem, out var stemArt))
                {
                    return stemArt + seg[s..];
                }
            }

            return seg;
        }

        // Inventory-aggregation keys are "<rarity-digit><metadata-path>" (see BuildItemKey), so
        // same-base different-rarity items stay distinct. Splits one back into its parts; a key without
        // the separator (legacy save) is treated as Normal.
        private const char ItemKeySep = '';

        private static (int rarity, string path, string renderArt) SplitItemKey(string key)
        {
            int sep = key.IndexOf(ItemKeySep);
            if (sep < 0) return (0, key, string.Empty);
            int r = (sep == 1 && key[0] >= '0' && key[0] <= '3') ? key[0] - '0' : 0;
            var rest = key[(sep + 1)..];
            int sep2 = rest.IndexOf(ItemKeySep);
            if (sep2 < 0) return (r, rest, string.Empty);
            return (r, rest[..sep2], rest[(sep2 + 1)..]);
        }

        // Composite key: a single rarity digit + the metadata path. Lets the snapshot/delta dictionaries
        // distinguish a Normal Abyss tablet from a Rare one (poe.ninja prices them very differently under
        // one shared icon). Stackable currency is always Normal → "0<path>".
        private static string BuildItemKey(int rarity, string path, string renderArt) =>
            string.IsNullOrEmpty(renderArt)
                ? $"{(char)('0' + (rarity & 3))}{ItemKeySep}{path}"
                : $"{(char)('0' + (rarity & 3))}{ItemKeySep}{path}{ItemKeySep}{renderArt}";

        // poe.ninja's tablet "variant" label for an in-game rarity index.
        private static string RarityVariant(int rarity) => rarity switch
        {
            1 => "Magic",
            2 => "Rare",
            3 => "Unique",
            _ => "Normal",
        };

        // Resolve one inventory key to its unit Exalted price and display label. Tries the per-rarity
        // art key first (tablets: Normal/Magic/Rare share an icon but list distinct prices), then the
        // bare art id (currency, and uniques whose icon already encodes the item). Label prefers the
        // variant's poe.ninja name, then the bare art's, then the art id itself.
        private bool TryPriceItem(string itemKey, out double unit, out string label)
        {
            var (rarity, path, renderArt) = SplitItemKey(itemKey);

            // Uniques: the base metapath is shared by every unique on that base (all unique tablets read
            // as TowerAugment/<Type>Augment), so the only reliable identity is the rendered icon art id —
            // which is exactly what poe.ninja keys uniques by. Match on it and DON'T fall back to the bare
            // base art (that's the base/Normal price and would badly misvalue the unique); an unlisted
            // unique stays unpriced instead.
            if (rarity == 3 && renderArt.Length > 0)
            {
                bool up = this.priceCache.TryGetPriceByArtId(renderArt, out unit) && unit > 0;
                label = this.priceCache.TryGetNameByArtId(renderArt, out var unm) && unm.Length > 0 ? unm : renderArt;
                if (!up) unit = 0;
                return up;
            }

            var art = this.PriceKey(path);
            var variantKey = art + RarityVariant(rarity);

            bool priced;
            if (this.priceCache.TryGetPriceByArtId(variantKey, out unit) && unit > 0) priced = true;
            else if (this.priceCache.TryGetPriceByArtId(art, out unit) && unit > 0) priced = true;
            else { unit = 0; priced = false; }

            if (this.priceCache.TryGetNameByArtId(variantKey, out var nm) && nm.Length > 0) label = nm;
            else if (this.priceCache.TryGetNameByArtId(art, out nm) && nm.Length > 0) label = nm;
            else label = art;

            return priced;
        }

        // Load the metaId→art bridge shipped beside the dll. Missing/garbled file is non-fatal:
        // pricing then falls back to metaId == art for every item (still correct for most).
        private void LoadMetaArtMap()
        {
            try
            {
                if (!File.Exists(this.MetaArtPathname)) return;
                var content = File.ReadAllText(this.MetaArtPathname);
                var map = JsonConvert.DeserializeObject<Dictionary<string, string>>(content);
                if (map != null) this.metaToArt = new Dictionary<string, string>(map, StringComparer.Ordinal);
            }
            catch
            {
                // keep the (empty) default; bridge simply does nothing.
            }
        }

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<LootTrackerSettings>(content)
                                ?? new LootTrackerSettings();
            }

            this.sessionStartUtc = DateTime.UtcNow;
            this.LoadMetaArtMap();
            this.LoadIcons();
            this.LoadActiveState(); // resume an in-progress session that survived a close/crash

            // League list first: the price fetch below needs a league name that still exists, and the
            // settings combo should have content on the very first frame.
            if (!NinjaLeagues.TryLoadFromDisk(this.LeagueCachePathname, NinjaLeagues.DefaultTtlHours))
            {
                NinjaLeagues.StartRefresh(this.LeagueCachePathname);
            }

            this.MaybeAdoptIndexedLeague();

            var fresh = this.priceCache.TryLoadFromDisk(
                this.PriceCachePathname, this.Settings.CacheTtlMinutes, this.Settings.League);
            if (!fresh)
                this.priceCache.StartRefresh(this.Settings.League, this.PriceCachePathname);
        }

        public override void OnDisable()
        {
            this.SaveActiveState(); // persist the live session on a clean GH close
            this.ResetHandle();
            this.UnloadIcons();
            this.ClearPickupToasts();
        }

        // Load the 64x64 PNG icons shipped in icons\ into the overlay (name = filename without ext).
        // Missing folder/files are non-fatal: the strip just renders text without icons.
        private void LoadIcons()
        {
            try
            {
                if (!Directory.Exists(this.IconsDir)) return;
                foreach (var path in Directory.EnumerateFiles(this.IconsDir, "*.png"))
                {
                    try
                    {
                        Core.Overlay.AddOrGetImagePointer(path, false, out var handle, out _, out _);
                        if (handle != IntPtr.Zero)
                            this.iconHandles[Path.GetFileNameWithoutExtension(path)] = handle;
                    }
                    catch
                    {
                        // skip a single bad image
                    }
                }
            }
            catch
            {
                // icons are optional
            }
        }

        private void UnloadIcons()
        {
            try
            {
                if (Directory.Exists(this.IconsDir))
                    foreach (var path in Directory.EnumerateFiles(this.IconsDir, "*.png"))
                        Core.Overlay.RemoveImage(path);
            }
            catch
            {
                // best-effort
            }

            this.iconHandles.Clear();
        }

        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname)!);
            this.Settings.LastSyncUtc = this.priceCache.LastSyncUtc;
            File.WriteAllText(this.SettingPathname, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
        }

        public override void DrawSettings()
        {
            // Collapsed-by-default group for the HUD layout knobs (no DefaultOpen flag = starts closed).
            if (ImGui.CollapsingHeader(this.Loc.Title("lt.settings", "Settings", "lt_settings")))
            {
                ImGui.SeparatorText(this.L("lt.compact_bar", "Compact bar (hideout)"));
                ImGui.SliderFloat(this.L("lt.compact_height", "Compact bar height (px)"), ref this.Settings.CompactHeight, 70f, 200f, "%.0f");
                ImGui.SliderFloat(this.L("lt.compact_width", "Compact bar width (px)"), ref this.Settings.CompactWidth, 200f, 1920f, "%.0f");
                ImGui.TextDisabled(this.L("lt.compact_width_hint",
                    "Requested width of the compact bar. Capped to the experience-bar width, so it\n" +
                    "never extends past the XP bar regardless of this value."));
                ImGui.SliderInt(this.L("lt.history_size", "History size"), ref this.Settings.HistorySize, 5, 200);
                ImGui.TextDisabled(this.L("lt.history_size_hint", "Completed-map rows kept in the session history (table + memory); oldest dropped past this."));

                ImGui.Spacing();
                ImGui.SeparatorText(this.L("lt.bars", "Bars (map strip + compact)"));
                ImGui.Checkbox(this.L("lt.anchor_right", "Anchor to right side"), ref this.Settings.BarOnRight);
                ImGui.SliderFloat(this.L("lt.offset_bottom", "Offset from bottom (px)"), ref this.Settings.BarBottomOffset, 0f, 300f, "%.0f");
                ImGui.TextDisabled(this.L("lt.offset_bottom_hint",
                    "Distance the bars sit up from the bottom of the game window. Raise it until\n" +
                    "they clear the experience bar / skill bar at your resolution and UI scale."));
                ImGui.SliderFloat(this.L("lt.bar_opacity", "Bar opacity"), ref this.Settings.BarOpacity, 0f, 1f, "%.2f");
                ImGui.SliderFloat(this.L("lt.ui_scale", "UI scale"), ref this.Settings.UiScale, 0.5f, 2f, "%.2f");
                ImGui.TextDisabled(this.L("lt.ui_scale_hint",
                    "Manual multiplier on top of the automatic game-UI scale (window height / 1600).\n" +
                    "Font and fixed widths scale with it, so the bars match the HUD across resolutions."));
                ImGui.Checkbox(this.L("lt.show_kills", "Show kill counts"), ref this.Settings.ShowKills);
                ImGui.TextDisabled(this.L("lt.show_kills_hint", "Per-rarity monsters slain this run (Normal · Magic · Rare · Unique)."));

                ImGui.Spacing();
                ImGui.SeparatorText(this.L("lt.pickup_notifs", "Pickup notifications"));
                ImGui.Checkbox(this.L("lt.show_toasts", "Show pickup toasts"), ref this.Settings.ShowPickupToasts);
                ImGui.TextDisabled(this.L("lt.show_toasts_hint",
                    "A brief toast (item name + value) above the map strip when you pick an item up.\n" +
                    "Up to 3 at once; same-item pickups merge. Only while actively on a map."));
                ImGui.BeginDisabled(!this.Settings.ShowPickupToasts);
                ImGui.SliderFloat(this.L("lt.notify_min", "Min value to notify (ex)"), ref this.Settings.NotifyMinEx, 20f, 200f, "%.0f");
                ImGui.TextDisabled(this.L("lt.notify_min_hint", "Only pickups worth at least this many Exalted toast. Unpriced items never toast."));
                ImGui.SliderFloat(this.L("lt.toast_duration", "Toast duration (s)"), ref this.Settings.NotifyDurationSec, 1f, 6f, "%.1f");
                ImGui.EndDisabled();

                ImGui.Spacing();
                ImGui.SeparatorText(this.L("lt.display", "Display"));
                ImGui.Checkbox(this.L("lt.divine_only", "Show prices only in Divine"), ref this.Settings.ShowPricesInDivineOnly);
                ImGui.TextDisabled(this.L("lt.divine_only_hint",
                    "Hide the Exalted figures everywhere on the overlay and show Divine instead\n" +
                    "(including fractions, e.g. 0.5 div). Falls back to Exalted until the rate is known."));
            }

            ImGui.Spacing();
            if (ImGui.Button(this.L("lt.new_session", "New session")))
            {
                this.ResetSession();
            }

            ImGui.SameLine(0f, 20f);
            if (ImGui.Button(this.L("lt.view_history", "View session history")))
            {
                this.LoadSessions();
                this.showSessionHistory = true;
            }

            ImGui.SliderInt(this.L("lt.sessions_keep", "Sessions to keep"), ref this.Settings.MaxSessions, 1, 200);
            ImGui.TextDisabled(this.L("lt.sessions_keep_hint", "Older sessions are deleted once this many are stored. A session is saved on \"New session\"."));

            ImGui.Spacing();
            ImGui.SeparatorText(this.L("lt.active_session", "Active session"));
            this.DrawActiveSessionTable();

            ImGui.Spacing();
            ImGui.Separator();

            ImGui.SeparatorText(this.L("lt.pricing", "Pricing"));
            this.DrawLeaguePicker();
            ImGui.SliderInt(this.L("lt.refresh_interval", "Refresh interval (min)"), ref this.Settings.CacheTtlMinutes, 5, 60);

            var status = this.priceCache.Status;
            string statusText = status switch
            {
                PriceSyncStatus.Syncing => this.L("lt.status_syncing", "syncing…"),
                PriceSyncStatus.Ready => this.priceCache.LastSyncUtc == DateTime.MinValue
                    ? this.L("lt.status_ready_nodata", "ready (no data yet)")
                    : this.LF("lt.status_updated_ago", "updated {0} ago", FormatRelative(this.priceCache.LastSyncUtc)),
                PriceSyncStatus.Error => this.LF("lt.status_error", "error: {0}", this.priceCache.LastError),
                _ => this.L("lt.status_idle", "idle"),
            };
            ImGui.Text(this.LF("lt.status_label", "Status: {0}", statusText));

            // The single most common pricing failure: a league name the API doesn't know (typically a
            // web slug), which answers 200 with an empty body. PriceCache reports it verbatim (it has
            // no localization access), so the localized explanation lives here.
            if (status == PriceSyncStatus.Error &&
                this.priceCache.LastError.Contains("returned 0 rows", StringComparison.Ordinal))
            {
                ImGui.TextWrapped(this.L("lt.status_zero_rows",
                    "poe.ninja answered, but has no rows for this league. Check the league name: it must be\n" +
                    "the API name with spaces (\"Runes of Aldur\"), not the web slug (\"runesofaldur\")."));
            }
            ImGui.Text(this.LF("lt.items_cached", "Items cached: {0}", this.priceCache.PriceCount));
            if (this.priceCache.DivineToExaltedRate > 0)
                ImGui.Text(this.LF("lt.divine_rate", "1 Divine = {0:F2} Exalted", this.priceCache.DivineToExaltedRate));

            ImGui.BeginDisabled(status == PriceSyncStatus.Syncing);
            if (ImGui.Button(this.L("lt.refresh_now", "Refresh now")))
                this.priceCache.StartRefresh(this.Settings.League, this.PriceCachePathname);
            ImGui.EndDisabled();
        }

        // League selector. Filled from poe.ninja's economyLeagues (NinjaLeagues), grouped by the
        // `hardcore` flag, with a free-text escape hatch for league-launch day (when the new league
        // isn't in index-state yet).
        //
        // Deliberately NOT ImGuiHelper.IEnumerableComboBox: that helper renders entries as
        // "0:Runes of Aldur" (index prefix), which is a core debug idiom, not user-facing UI.
        // Every label goes through Loc.Label/a literal "##id" so the ImGui item ID stays stable when
        // the GameHelper UI language changes.
        private void DrawLeaguePicker()
        {
            if (this.Settings.UseCustomLeague)
            {
                if (ImGui.InputText(this.Loc.Label("lt.league", "League", "LtLeagueInput"), ref this.Settings.League, 64))
                {
                    this.Settings.LeaguePinned = true;
                }

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    this.priceCache.StartRefresh(this.Settings.League, this.PriceCachePathname);
                }

                ImGui.TextDisabled(this.L("lt.custom_league_hint",
                    "Enter poe.ninja's API name, with spaces (\"Runes of Aldur\") — not the web slug\n" +
                    "(\"runesofaldur\"), which the API answers with an empty result."));
            }
            else
            {
                // Preview is the RAW saved value, so the user sees what will be sent even before the
                // list has loaded (or when the saved league isn't in it at all).
                var softcore = new List<NinjaLeague>();
                var hardcore = new List<NinjaLeague>();
                foreach (var name in NinjaLeagues.ComboItems(this.Settings.League))
                {
                    var lg = NinjaLeagues.Resolve(name);
                    (lg.Hardcore ? hardcore : softcore).Add(lg);
                }

                void Group(string header, List<NinjaLeague> items)
                {
                    if (items.Count == 0)
                    {
                        return;
                    }

                    ImGui.SeparatorText(header);
                    foreach (var lg in items)
                    {
                        var selected = string.Equals(lg.Name, this.Settings.League, StringComparison.OrdinalIgnoreCase);
                        if (ImGui.IsWindowAppearing() && selected)
                        {
                            ImGui.SetScrollHereY();
                        }

                        if (ImGui.Selectable($"{NinjaLeagues.LabelOf(lg)}##lg_{lg.Name}", selected) && !selected)
                        {
                            this.Settings.League = lg.Name;
                            this.Settings.LeaguePinned = true;
                            this.leagueNoteFrom = string.Empty;
                            this.leagueNoteTo = string.Empty;
                            this.priceCache.StartRefresh(this.Settings.League, this.PriceCachePathname);
                        }
                    }
                }

                if (ImGui.BeginCombo(this.Loc.Label("lt.league", "League", "LtLeagueCombo"), this.Settings.League))
                {
                    Group(this.L("lt.league_softcore", "Softcore"), softcore);
                    Group(this.L("lt.league_hardcore", "Hardcore"), hardcore);
                    ImGui.EndCombo();
                }

                ImGui.TextDisabled(this.L("lt.league_hint",
                    "Prices are fetched for exactly this poe.ninja league."));
            }

            ImGui.Checkbox(
                this.Loc.Label("lt.custom_league", "Type the league name manually", "LtCustomLeague"),
                ref this.Settings.UseCustomLeague);

            var listStatus = NinjaLeagues.Status;
            ImGui.BeginDisabled(listStatus == PriceSyncStatus.Syncing);
            if (ImGui.Button(this.Loc.Label("lt.refresh_leagues", "Refresh league list", "LtRefreshLeagues")))
            {
                NinjaLeagues.StartRefresh(this.LeagueCachePathname);
            }

            ImGui.EndDisabled();

            string listText;
            if (listStatus == PriceSyncStatus.Syncing)
            {
                listText = this.L("lt.leagues_loading", "loading league list…");
            }
            else if (listStatus == PriceSyncStatus.Error)
            {
                listText = NinjaLeagues.IsLoaded
                    ? this.LF("lt.leagues_offline_cached", "offline — using cached list ({0} old)", FormatRelative(NinjaLeagues.FetchedUtc))
                    : this.L("lt.leagues_offline_builtin", "offline — using built-in list");
            }
            else if (NinjaLeagues.IsLoaded)
            {
                listText = this.LF("lt.leagues_ok", "{0} leagues, updated {1} ago", NinjaLeagues.All.Count, FormatRelative(NinjaLeagues.FetchedUtc));
            }
            else
            {
                listText = this.L("lt.leagues_offline_builtin", "offline — using built-in list");
            }

            ImGui.SameLine();
            ImGui.TextDisabled(listText);

            if (!string.IsNullOrEmpty(this.leagueNoteTo))
            {
                ImGui.TextWrapped(this.LF(
                    "lt.league_adopted",
                    "League \"{0}\" is gone from poe.ninja; switched to \"{1}\".",
                    this.leagueNoteFrom,
                    this.leagueNoteTo));
            }
        }

        public override void DrawUI()
        {
            // Session-history windows are independent of game state (they read from disk).
            this.DrawSessionHistoryWindow();
            this.DrawSessionDetailWindow();
            this.DrawMapLootWindow();

            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                return;
            }

            this.MaybeAutoRefreshPrices();
            this.UpdateAreaState();
            this.ScanKills();
            this.UpdateLiveInventory();
            this.MaybeAutoSaveSession();

            // HUD bars hide when the game window isn't focused (alt-tabbed), and whenever the experience
            // bar can't be resolved. The game hides the experience bar whenever a large panel covers the
            // screen (Atlas / world-travel map, inventory, passive tree), so a failed FP resolve is
            // itself a reliable, fork-independent "panel is open" signal — both bars hide in that case
            // instead of falling back to a viewport position that would overlap the open panel.
            if (Core.Process.Foreground && this.TryGetExperienceBarRect(out _, out _))
            {
                if (this.onMapArea)
                {
                    this.DrawMapBar();
                }
                else if (!IsLargePanelOpen())
                {
                    this.DrawCompactBar();
                }

                this.DrawPickupToasts();
            }
        }

        // Aggregate the completed runs (the live run is excluded so the rate stays stable).
        private void SessionTotals(out TimeSpan totalActive, out double totalEx)
        {
            totalActive = TimeSpan.Zero;
            totalEx = 0;
            foreach (var r in this.completed)
            {
                totalActive += r.ActiveTime;
                totalEx += this.ValueOf(r.Gained, out _, out _);
            }
        }

        // Wipe all session state and restart the session clock. Archives the session being ended to
        // the on-disk history first (New session = end + save + start fresh).
        private void ResetSession()
        {
            this.SaveCurrentSession();
            this.completed.Clear();
            this.current = null;
            this.runStartUtc = null;
            this.baseline = null;
            this.baselinePending = false;
            this.prevSnapshot = null;
            this.lastProcessedZoneHash = string.Empty;
            this.sessionStartUtc = DateTime.UtcNow;
            this.ResetKillTally();
            this.ClearPickupToasts();
            this.DeleteActiveState(); // archived to history above; the autosave mirror is now obsolete
        }

        // Re-fetch prices once the cache ages past the TTL (checked at most once a minute).
        private void MaybeAutoRefreshPrices()
        {
            var now = DateTime.UtcNow;
            if (now < this.nextAutoRefreshCheckUtc)
            {
                return;
            }

            this.nextAutoRefreshCheckUtc = now.AddMinutes(1);

            // League list ages on its own (12h) clock, independent of the price TTL.
            if (now >= this.nextLeagueCheckUtc)
            {
                this.nextLeagueCheckUtc = now.AddMinutes(1);
                var wasLoaded = NinjaLeagues.IsLoaded;
                var listAt = NinjaLeagues.FetchedUtc;

                if (NinjaLeagues.IsStale && NinjaLeagues.Status != PriceSyncStatus.Syncing)
                {
                    NinjaLeagues.StartRefresh(this.LeagueCachePathname);
                }
                else if (NinjaLeagues.IsLoaded &&
                         (!wasLoaded || listAt != this.lastSeenLeagueListUtc) &&
                         !this.Settings.LeaguePinned &&
                         !this.Settings.UseCustomLeague &&
                         !NinjaLeagues.Contains(this.Settings.League))
                {
                    // The list just changed under us and the saved league is no longer offered.
                    this.MaybeAdoptIndexedLeague();
                }

                this.lastSeenLeagueListUtc = NinjaLeagues.FetchedUtc;
            }

            if (this.priceCache.Status == PriceSyncStatus.Syncing)
            {
                return;
            }

            var age = now - this.priceCache.LastSyncUtc;
            if (age > TimeSpan.FromMinutes(Math.Max(1, this.Settings.CacheTtlMinutes)))
            {
                this.priceCache.StartRefresh(this.Settings.League, this.PriceCachePathname);
            }
        }

        // One-time, opt-out-able migration: if the user never picked a league themselves and the one
        // we have saved is gone from poe.ninja's economyLeagues (new league launched), move to the
        // league poe.ninja itself defaults to and re-fetch prices. A user with a league that still
        // exists only gets LeaguePinned set — nothing else changes for them.
        //
        // `Indexed` is used ONLY here (default picking); it does not mean "has economy data".
        private void MaybeAdoptIndexedLeague()
        {
            if (this.Settings.LeaguePinned || this.Settings.UseCustomLeague)
            {
                return;
            }

            // Built-in fallback only (no network, no cache): we can't tell whether the saved league is
            // gone or merely unseen, so do nothing and retry on a later tick.
            if (!NinjaLeagues.IsLoaded)
            {
                return;
            }

            this.lastSeenLeagueListUtc = NinjaLeagues.FetchedUtc;

            if (NinjaLeagues.Contains(this.Settings.League))
            {
                this.Settings.LeaguePinned = true;
                return;
            }

            if (!NinjaLeagues.TryPickDefault(out var picked) ||
                string.Equals(picked, this.Settings.League, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var previous = this.Settings.League;
            this.Settings.League = picked;
            this.Settings.LeaguePinned = true;
            this.leagueNoteFrom = previous;
            this.leagueNoteTo = picked;
            this.priceCache.StartRefresh(this.Settings.League, this.PriceCachePathname);
        }

        // Autosave the live session at most every ~20s, so a crash mid-map loses only the last few
        // seconds of loot rather than the whole session. Zone transitions also force a save (banked maps
        // are persisted immediately); this timer only adds protection for long stays inside one map.
        private void MaybeAutoSaveSession()
        {
            var now = DateTime.UtcNow;
            if (now < this.nextAutoSaveUtc)
            {
                return;
            }

            this.nextAutoSaveUtc = now.AddSeconds(20);
            if (this.completed.Count > 0)
            {
                this.SaveActiveState();
            }
        }

        // Frame-polled area state machine. Reacts on a zone-hash transition edge; also lazily captures
        // a pending map-entry baseline once the inventory becomes readable (after the loading screen).
        private void UpdateAreaState()
        {
            var ingame = Core.States.InGameStateObject;
            var area = ingame.CurrentAreaInstance;
            var details = ingame.CurrentWorldInstance.AreaDetails;

            string inst = area.AreaHash;
            // Skip transient loading frames (no hash / no area name yet).
            if (string.IsNullOrEmpty(inst) || string.IsNullOrEmpty(details.Name))
            {
                return;
            }

            if (inst != this.lastProcessedZoneHash)
            {
                this.lastProcessedZoneHash = inst;
                this.HandleZoneTransition(area, details, inst);
            }

            // Capture the map-entry baseline on the first frame the inventory reads cleanly (the
            // transition fires before the inventory is populated, so we defer the snapshot).
            if (this.baselinePending && this.TrySnapshotInventory(out var snap))
            {
                this.baseline = snap;
                this.baselinePending = false;
            }
        }

        // Extra non-combat hubs that aren't flagged IsHideout/IsTown by the game data but should be
        // treated like the hideout for loot tracking (no run opened, the outgoing leg is folded on exit).
        // Matched by area Id (language-independent) — the display name is localized AND ambiguous:
        // "Abyss_Hub" (the safe staging hub) and the "Abyss_Pinnacle" boss arena share the name
        // "The Well of Souls", so a name match would wrongly mark the boss map as safe.
        private static readonly HashSet<string> SafeZoneIds = new(StringComparer.Ordinal)
        {
            "Abyss_Hub",   // The Well of Souls — safe staging hub (NOT Abyss_Pinnacle, which is a map)
        };

        private static bool IsSafeZone(GameHelper.RemoteObjects.FilesStructures.WorldAreaDat details)
            => SafeZoneIds.Contains(details.Id);

        private void HandleZoneTransition(GameHelper.RemoteObjects.States.InGameStateObjects.AreaInstance area,
            GameHelper.RemoteObjects.FilesStructures.WorldAreaDat details, string inst)
        {
            bool isMap = !details.IsHideout && !details.IsTown && !IsSafeZone(details);
            bool wasOnMap = this.onMapArea; // map-ness of the area we're leaving (before we overwrite it)
            this.onMapArea = isMap;
            var now = DateTime.UtcNow;

            if (isMap)
            {
                // Bank the outgoing run's time before switching away from it.
                this.BankActiveTime(now);

                // Map → map transition (a sub-area, or straight map→map): the outgoing leg's inventory
                // delta has NOT been folded yet — only hideout/town exits fold it (the else branch below).
                // Fold it now, against the still-current run and its live baseline, before we switch
                // `current` away; otherwise loot picked up since entry is lost. Skipped when arriving from
                // hideout/town (wasOnMap == false): that leg was already folded on exit and the baseline is
                // about to be re-taken, so folding here would double-count it (and absorb stash changes).
                if (wasOnMap && this.current != null && this.baseline != null && this.TrySnapshotInventory(out var legSnap))
                {
                    MergeInto(this.current.Gained, Diff(legSnap, this.baseline));
                }

                if (this.current != null && this.current.Hash == inst)
                {
                    // Straight re-entry of the same instance we were just on — keep it active.
                }
                else if (this.FindRun(inst) is { } existing)
                {
                    // Returning to a map already in history (back from a sub-area, or from the hideout):
                    // resume that very run by its instance hash and keep accumulating into it.
                    this.current = existing;
                }
                else
                {
                    // A genuinely new map instance: open a run and add it to history straight away, so
                    // the table (compact bar) shows it even before the run is "finished".
                    this.current = new MapRun
                    {
                        Name = details.Name,
                        Hash = inst,
                        AreaLevel = area.CurrentAreaLevel,
                        ActiveTime = TimeSpan.Zero,
                    };
                    this.completed.Add(this.current);
                    this.TrimCompleted();
                }

                this.runStartUtc = now;

                // (Re-)baseline once the inventory is readable, so items left in stash don't count as loss.
                this.baseline = null;
                this.baselinePending = true;
                this.liveLegDelta.Clear(); // drop the previous map's leg so it can't leak before re-baseline
                this.prevSnapshot = null; // re-establish pickup tracking; corpses-on-entry aren't pickups

                // Drop stale per-monster bookkeeping. Counts already booked live on current.Kills, so this
                // only ensures corpses present on (re)entry aren't mistaken for fresh kills.
                this.ResetKillTally();
            }
            else if (this.current != null)
            {
                // Left the map into hideout/town: pause the timer, bank elapsed time, and fold this
                // leg's inventory delta into the run's running total. The run stays in the table.
                this.BankActiveTime(now);

                if (this.baseline != null && this.TrySnapshotInventory(out var snap))
                {
                    MergeInto(this.current.Gained, Diff(snap, this.baseline));
                }

                this.baselinePending = false; // not on a map now
            }

            // Persist the session right after a transition: a just-completed/banked map is now crash-safe.
            this.SaveActiveState();
        }

        // Bank the active run's still-running time into its total and pause the timer (idempotent:
        // a no-op when nothing is running).
        private void BankActiveTime(DateTime now)
        {
            if (this.current != null && this.runStartUtc is { } start)
            {
                this.current.ActiveTime += now - start;
                this.runStartUtc = null;
            }
        }

        // Find a tracked run by its instance hash (newest first), or null. Hashes are instance-unique,
        // so a hit means we are literally back in that same map instance.
        private MapRun? FindRun(string hash)
        {
            for (int i = this.completed.Count - 1; i >= 0; i--)
            {
                if (this.completed[i].Hash == hash)
                {
                    return this.completed[i];
                }
            }

            return null;
        }

        // Drop the oldest runs past the history limit, but never the run that's currently active.
        private void TrimCompleted()
        {
            while (this.completed.Count > this.Settings.HistorySize)
            {
                if (ReferenceEquals(this.completed[0], this.current))
                {
                    break;
                }

                this.completed.RemoveAt(0);
            }
        }

        // Active time of the current run including the live (unbanked) segment.
        private TimeSpan CurrentLiveTime()
        {
            if (this.current == null)
            {
                return TimeSpan.Zero;
            }

            var t = this.current.ActiveTime;
            if (this.runStartUtc is { } start)
            {
                t += DateTime.UtcNow - start;
            }

            return t;
        }

        // ── Valuation (step 4) ───────────────────────────────────────────
        // Throttled "current leg" delta (since the active baseline) so the provisional total updates
        // while the player is still on the map without a per-frame component walk.
        private DateTime nextLiveSnapUtc = DateTime.MinValue;
        private Dictionary<string, long> liveLegDelta = new(StringComparer.Ordinal);

        // Throttled snapshot of the live inventory (~2 Hz), driving BOTH the provisional run total
        // (liveLegDelta = snapshot − baseline) and the pickup-toast detector (snapshot − prevSnapshot).
        // Called once per frame from DrawUI so a single memory read serves both; the bar then just reads
        // the cached liveLegDelta. No-op unless actively on a map (timer running, baseline taken).
        private void UpdateLiveInventory()
        {
            if (this.current == null || this.runStartUtc == null || this.baseline == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now < this.nextLiveSnapUtc)
            {
                return;
            }

            this.nextLiveSnapUtc = now.AddMilliseconds(500);
            if (!this.TrySnapshotInventory(out var snap))
            {
                return;
            }

            this.liveLegDelta = Diff(snap, this.baseline);

            // Pickup toasts: compare to the PREVIOUS snapshot (not the baseline) so each ~500ms tick's
            // positive deltas are the items just picked up. prevSnapshot is null on the first tick after
            // (re)entry — establish it then without firing (existing items aren't "pickups").
            if (this.Settings.ShowPickupToasts)
            {
                if (this.prevSnapshot != null)
                {
                    this.DetectPickups(snap);
                }

                this.prevSnapshot = snap;
            }
            else
            {
                this.prevSnapshot = null;
            }
        }

        // The run's net gains shown live: folded legs (current.Gained) plus the in-progress leg
        // (the cached liveLegDelta, refreshed by UpdateLiveInventory).
        private Dictionary<string, long> CurrentGainedLive()
        {
            if (this.current == null)
            {
                return new Dictionary<string, long>(StringComparer.Ordinal);
            }

            // Only with a live baseline (timer running AND the map-entry snapshot taken) is liveLegDelta
            // valid for this map; until then it may be stale from the previous map, so show folded-only.
            if (this.runStartUtc != null && this.baseline != null)
            {
                var combined = new Dictionary<string, long>(this.current.Gained, StringComparer.Ordinal);
                MergeInto(combined, this.liveLegDelta);
                return combined;
            }

            return this.current.Gained;
        }

        // Exalted value of a net delta: Σ Δcount × unit price. Items poe.ninja doesn't price (rares,
        // unmapped bases) contribute 0. priced = how many distinct keys resolved (for an "incomplete" hint).
        private double ValueOf(Dictionary<string, long> delta, out int priced, out int unpriced)
        {
            priced = 0;
            unpriced = 0;
            double sum = 0;
            foreach (var kv in delta)
            {
                if (kv.Value == 0) continue;
                if (this.TryPriceItem(kv.Key, out var unit, out _))
                {
                    sum += unit * kv.Value;
                    priced++;
                }
                else
                {
                    unpriced++;
                }
            }

            return sum;
        }

        private static string FormatRelative(DateTime utc)
        {
            var span = DateTime.UtcNow - utc;
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds}s";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours}h";
            return $"{(int)span.TotalDays}d";
        }
    }
}
