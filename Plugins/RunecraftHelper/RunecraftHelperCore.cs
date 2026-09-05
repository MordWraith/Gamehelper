namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Text;
    using GameHelper;
    using GameHelper.Localization;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameOffsets.Natives;
    using GameOffsets.Objects.UiElement;
    using ImGuiNET;
    using Newtonsoft.Json;

    public sealed partial class RunecraftHelperCore : PCore<RunecraftHelperSettings>
    {
        // Fixed UI path through PoE2 0.5.x's Runeshape Combinations panel:
        //   GameUi → window-container → ? → ? → ? → recipes-container
        // Child indices wiggle across game restarts, but each UiElement's Flags field encodes
        // its "role" (panel/list/row/etc.) and those bits stay stable — so we match by Flags
        // fingerprint instead of by index. The IsVisible bit (bit 0x0B / mask 0x800) is masked
        // out before comparison because it toggles when the player opens/closes the panel.
        //
        // PoE2's UI tree has many sibling UiElements sharing the same fp at each level, so a
        // greedy "pick the first/visible match" walk can step into the wrong subtree and
        // silently dead-end. WalkFp instead BACKTRACKS: at each step it tries every matching
        // sibling (visible candidates first), recurses, and keeps whichever branch reaches a
        // valid recipes-container at the bottom (see IsRecipesContainer). Mirrors the Atlas
        // plugin's resolver.
        //
        // GateStep (the window-container) is the panel-open gate: its IsVisible bit flips with
        // the panel, so that hop only accepts a visible match — when the panel is closed the
        // whole walk fails and we draw nothing.
        //
        // The recipes-container has ~320 child rows; only a handful are visible at a time (rest
        // are scrolled off / templated). Each visible row's kid[0] holds an inline std::wstring
        // "<count>x <name>" at +0x390.
        private static readonly uint[] PanelFlagFingerprints =
        {
            0x00462EF1, // window-container (its IsVisible bit toggles with the panel)
            0x00502EF3,
            0x00502EF7,
            0x00542EF1,
            0x00502EF1, // recipes-container
        };
        /// <summary>
        ///     GameHelper's own "a large game panel is covering the screen" check: the side panels
        ///     (inventory / character), the passive tree, the atlas skill tree, the currency exchange,
        ///     the temple console and the Sekhema trial map. Upstream asks plugins to route their
        ///     hide-the-overlay logic through this rather than probing panels themselves -- a plugin's
        ///     own probe is usually a fixed child index, which is exactly what silently moves on a patch.
        ///
        ///     Applied to the WORLD and large-map overlays only. The Runeshape Combinations overlay is
        ///     deliberately NOT gated on it: that overlay is positioned on the game's own panel and only
        ///     drawn once the panel resolves, and if that panel ever counts as one of the panels above,
        ///     gating it here would hide the plugin's main feature exactly when it is needed.
        /// </summary>
        private static bool IsAnyLargePanelOpen =>
            Core.States.InGameStateObject.GameUi.IsAnyLargePanelOpen;

        private const int GateStep = 0;

        // The scroll viewport (the fixed-size clip window) is the element matched at this fp step —
        // PanelFlagFingerprints[2] = 0x00502EF7, the recipes-container's grandparent. Live reads
        // (docs/re-findings.md §3) show it has a FIXED UnscaledSize (~770×800) while the container
        // below it is the full ~7990px-tall content that slides under it. Rows scrolled out of this
        // window keep their IsVisible bit set (the game clips them with a scissor rect, NOT the flag),
        // so the overlay must clip prices to this viewport's screen rect instead of trusting IsVisible.
        private const int ViewportStep = 2;
        private IntPtr resolvedViewport;

        // Scroll content offset of a UiElement, at +0x120 (StdTuple2D<float>, just past RelativePosition
        // @ +0x118). On a scroll-viewport (mask) element this is the translation applied to its content
        // child as the list scrolls (Y goes negative scrolling down); it is NOT reflected in the content
        // child's RelativePosition/PositionModifier. Read directly here (not via GameOffsets) so the
        // plugin stays self-contained across GH versions. Verified live on PoE2 0.5.x (docs/re-findings.md §3).
        private const int ScrollOffsetFieldOffset = 0x108;   // 0.5.5: -0x18 (was 0x120)
        // Note (0.5.5 RE): this field IS UiElementBase.PositionModifier -- same offset in both builds
        // (0.5.4 0x120, 0.5.5 0x108). That explains the mechanism: the viewport translates its content by
        // the modifier, and the game adds a parent's modifier to a child whose flag bit 0x400 is set.
        // The resolved viewport's scroll offset, re-read once per frame in DrawOverlay and added to the
        // content rows' positions (see TryGetUnscaledPosition).
        private Vector2 viewportScrollOffset;

        // 0.5.5: -0x30 (was 0x390), NOT the -0x18 that UiElementBase moved by. These wstrings live on the
        // derived TEXT element, which lost another 0x18 of its own, so the base's delta alone lands short.
        // Measured, not shifted: the wstring header at kid[0]+0x360 reads "1x Aldur's Legacy" live, and the
        // MSVC layout confirms it (buffer/ptr at +0x00, size at +0x10 = 17, capacity at +0x18 = 23).
        private const int NameWStringOffset = 0x360;
        private const int UiElementChildrenOffset = 0x10;
        private const int UiElementFlagsOffset = 0x168;      // 0.5.5: -0x18 (was 0x180), measured
        private const int IsVisibleBit = 0x0B;
        private const uint IsVisibleMask = 1u << IsVisibleBit; // = 0x800

        // ── Language-independent reward matching (BaseItemTypes layout re-verified live; see memory
        //    project-runecraft-overlay-ru-empty-dict) ──
        // The visible reward is shown only as LOCALIZED text, so matching that text to poe.ninja
        // (English) fails on non-English clients. We translate the localized name → the item's
        // language-independent BaseItemType.Id via the game's own MAIN BaseItemTypes table, whose Name
        // column is merged to the active-language strings at runtime:
        //   BaseItemType row (stride 0x168):
        //     +0x00 → meta-path "Metadata/Items/.../<Id>" (Id last segment is the canonical key
        //             — its trailing digit, when present, encodes the currency tier:
        //             Regal=CurrencyUpgradeMagicToRare, Greater=…2, Perfect=…3).
        //     +0x20 → localized display-name buffer.
        //     +0x78 → +0x08 → art ".dds" path (one icon can cover 3 currency tiers, so we prefer
        //             +0x00's tiered Id and use the dds only as a fallback key).
        // The table is located DIRECTLY by walking the panel's (and the recipe handle's) pointer graph
        // to the dat-file handle whose path ends with "Balance/BaseItemTypes.dat" — NOT a per-language
        // overlay ("…/Russian/…"), whose rows lack the schema. Previously we hopped via the recipe
        // table's reward-FK at recipe+0x34, but that row layout drifts between patches (it broke the
        // whole dict, invisibly on EN where the English-name price fallback masked it). The dat handle:
        // in-module vtable at +0x00, path string at +0x08, {begin,end} rows-vector pointer at +0x28.
        private const int TableRowsVectorOffset = 0x28;    // table object → ptr to {begin,end} rows vector
        private const int DatPathOffset = 0x08;            // dat-file handle/table object → path string ptr
        private const int BaseItemTypeStride = 0x168;
        private const int BaseItemTypeIdOffset = 0x00;     // → meta-path "Metadata/Items/.../<Id>"
        private const int BaseItemTypeNameOffset = 0x20;   // → localized display-name buffer
        private const int BaseItemTypeArtOffset = 0x78;    // → sub-object; +0x08 → ".dds" art path
        private const int ArtSubPathOffset = 0x08;         //   art path = poe.ninja image-id

        // Combinations-panel row UiElement → its Expedition2Recipes_Row (live-verified back-ptr).
        private const int RecipeRowBackPtrOffset = 0x540;
        // Expedition2Recipes_Row → reward BaseItemType* (null = "random currency" / no fixed item).
        private const int RecipeRewardItemOffset = 0x2c;
        // Expedition2Recipes_Row → level band (i32 MinLevelReq / i32 MaxLevelReq).
        private const int RecipeMinLevelOffset = 0x24;
        private const int RecipeMaxLevelOffset = 0x28;

        private IntPtr processHandle = IntPtr.Zero;
        private int handlePid;

        private readonly List<Recipe> recipes = new();
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

        // {localizedName → (metaId, ddsArt)}, built once per game session from BaseItemTypes.
        // metaId  = BaseItemType.Id last segment  — matches poe.ninja's tiered key for shared-icon
        //           families (Regal: …/…2/…3).
        // ddsArt  = .dds art filename             — matches poe.ninja's image-id for distinct-icon
        //           families (Jeweller's: …01/02/03) where the game's BaseItemType.Id diverges.
        // The price lookup tries metaId first, then ddsArt (see TryGetRecipePrice).
        private Dictionary<string, (string MetaId, string DdsArt)> nameToArtId = new(StringComparer.Ordinal);
        // While the dict is empty, throttle the (BFS-heavy) build attempts so a table that can't be
        // located doesn't re-run the pointer walk every frame. Reset on process change.
        private DateTime nameToArtNextTryUtc = DateTime.MinValue;

        // {BaseItemType.Id (full meta path) → LOCALIZED display name}, built once per session from the
        // in-memory BaseItemTypes table (row Id @+0x00, localized Name @+0x20). Used by the Monolith
        // rewards window to show reward names in the client's language instead of the English catalog
        // json. Keyed by the FULL meta path because the catalog's reward.id is the full path
        // ("Metadata/Items/Currency/…"), which is unique (no last-segment collisions). Reset on process
        // change (language-specific). See obsidian poe2/Loaders.md (FileRoot route).
        private Dictionary<string, string> metaToLocalName = new(StringComparer.Ordinal);
        private DateTime metaToLocalNextTryUtc = DateTime.MinValue;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");
        private string PriceCachePathname => Path.Join(this.DllDirectory, "config", "prices.json");

        // poe.ninja's economyLeagues list (see NinjaLeagues). League-independent by design — the file
        // name must NOT carry a league, it caches the list of leagues itself.
        private string LeagueCachePathname => Path.Join(this.DllDirectory, "config", "leagues.json");

        // Localization: JSON dictionaries in <plugin>/Localization/<lang-code>.json, keyed by the stable keys
        // used at the call sites. Resolves against GameHelper's selected UI language (OverlayLocalization.
        // CurrentLanguage), falling back to en-US.json and then the English literal passed as `fallback`.
        // Lazy so it's ready even if a Draw* runs before OnEnable. See obsidian fork-mods/runecraft-localization.
        private PluginLocalization? loc;
        private PluginLocalization Loc => this.loc ??= new PluginLocalization(this.DllDirectory);

        // Short helpers: L = plain string, LF = formatted (args). Fallback is the canonical English text, so the
        // plugin still reads correctly with NO json files present (English users need no Localization dir at all).
        private string L(string key, string fallback) => this.Loc.T(key, fallback);
        private string LF(string key, string fallback, params object[] args) => this.Loc.F(key, fallback, args);

        // Metadata substring identifying the persistent monolith device entity (used by the
        // Monolith reward window in RunecraftHelperCore.MonolithRewards.cs).
        private const string MonolithDevicePath = "Expedition2Encounter";

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<RunecraftHelperSettings>(content)
                                ?? new RunecraftHelperSettings();
            }

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

        public override void OnDisable() => this.ResetHandle();

        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname)!);
            this.Settings.LastSyncUtc = this.priceCache.LastSyncUtc;
            File.WriteAllText(this.SettingPathname, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
        }

        public override void DrawSettings()
        {
            ImGui.TextWrapped(this.L("settings.intro",
                            "RunecraftHelper: while the in-game Runeshape Combinations panel is open, the " +
                            "poe.ninja Exalted price is drawn on the right edge of each visible reward row. " +
                            "The reward name shown is the game's own (any client language)."));

            ImGui.Spacing();
            ImGui.Separator();

            if(ImGui.CollapsingHeader(this.Loc.Title("settings.poeninja", "poe.ninja settings", "rh_poeninja"))) {
                this.DrawLeaguePicker();
                ImGui.SliderInt(this.L("settings.refresh_interval", "Refresh interval (min)"), ref this.Settings.CacheTtlMinutes, 5, 60);

                // poe.ninja price sync status + manual refresh — common (the price overlay is shared by all features).
                ImGui.Spacing();
                var status = this.priceCache.Status;
                var lastSync = this.priceCache.LastSyncUtc;
                string statusText = status switch
                {
                    PriceSyncStatus.Syncing => this.L("status.syncing", "syncing…"),
                    PriceSyncStatus.Ready => lastSync == DateTime.MinValue
                        ? this.L("status.ready_nodata", "ready (no data yet)")
                        : this.LF("status.updated_ago", "updated {0} ago", FormatRelative(lastSync)),
                    PriceSyncStatus.Error => this.LF("status.error", "error: {0}", this.priceCache.LastError),
                    _ => this.L("status.idle", "idle"),
                };

                ImGui.Text(this.LF("status.label", "Status: {0}", statusText));

                // The single most common pricing failure: a league name the API doesn't know (typically a
                // web slug), which answers 200 with an empty body. PriceCache reports it verbatim (it has
                // no localization access), so the localized explanation lives here.
                if (status == PriceSyncStatus.Error &&
                    this.priceCache.LastError.Contains("returned 0 rows", StringComparison.Ordinal))
                {
                    ImGui.TextWrapped(this.L("status.zero_rows",
                        "poe.ninja answered, but has no rows for this league. Check the league name: it must be\n" +
                        "the API name with spaces (\"Runes of Aldur\"), not the web slug (\"runesofaldur\")."));
                }

                ImGui.Text(this.LF("status.items_cached", "Items cached: {0}", this.priceCache.PriceCount));
                if (this.priceCache.DivineToExaltedRate > 0)
                    ImGui.Text(this.LF("status.divine_rate", "1 Divine = {0:F2} Exalted", this.priceCache.DivineToExaltedRate));

                ImGui.BeginDisabled(status == PriceSyncStatus.Syncing);
                if (ImGui.Button(this.L("settings.refresh_now", "Refresh now")))
                    this.priceCache.StartRefresh(this.Settings.League, this.PriceCachePathname);
                ImGui.EndDisabled();
            }

            ImGui.Spacing();
            if (!ImGui.BeginTabBar("rh_settings_tabs"))
                return;

            if (ImGui.BeginTabItem(this.Loc.Title("tab.monoliths", "Runestone monoliths", "rh_tab_monoliths")))
            {
                ImGui.Spacing();

                ImGui.TextDisabled(this.L("mono.map_value_hint", "Paints each monolith's best value (ex) on the large-map overlay"));
                ImGui.Checkbox(this.L("mono.draw_on_map", "Draw value on map overlay"), ref this.Settings.DrawMonolithValueOnMap);

                ImGui.Spacing();

                // Just the toggle — which runes are worth showing comes from the rune-chain weight table
                // (Expedition tab), so there is no second rune list to keep in sync with it.
                ImGui.Checkbox(this.L("mono.show_glow_runes", "Show glow runes"), ref this.Settings.ShowGlowRunes);
                if (this.Settings.ShowGlowRunes)
                    ImGui.TextDisabled(this.L("mono.glow_hint",
                        "Labels a monolith on the large map with the rune(s) it would propagate from its gold\n" +
                        "socket, best first, joined by \" | \". Only runes the rune-chain table values above\n" +
                        "1.00 are shown — a monolith with nothing worth propagating stays unlabelled."));

                ImGui.Separator();
                ImGui.Spacing();

                ImGui.Checkbox(this.L("mono.show_rewards", "Show monolith reward window"), ref this.Settings.ShowMonolithRewards);
                if (this.Settings.ShowMonolithRewards)
                {
                    // Price overlay controls live here — they tint / position the per-recipe price text drawn on the
                    // in-game Runeshape Combinations panel (the monolith reward overlay).
                    int colorMode = (int)this.Settings.ColorMode;
                    // Combo items are null-separated for ImGui; keep the \0 joins in C# and localize each item on
                    // its own key (avoids fragile \0 escapes inside the JSON dictionaries).
                    string priceItems = this.L("mono.price_off", "Off") + "\0" +
                                        this.L("mono.price_relative", "Relative (vs. median on screen)") + "\0" +
                                        this.L("mono.price_absolute", "Absolute (Exalted thresholds)") + "\0";
                    if (ImGui.Combo(this.L("mono.price_color", "Price color"), ref colorMode, priceItems))
                        this.Settings.ColorMode = (RewardColorMode)colorMode;

                    ImGui.SliderFloat(this.L("mono.price_x", "Price X offset"), ref this.Settings.OverlayXOffset, -400f, 400f, "%.0f px");

                    ImGui.SliderFloat(this.L("mono.hide_under", "Hide rewards under (ex)"), ref this.Settings.MonolithRewardsMinExalted, 0f, 50f, "%.0f ex");

                    ImGui.InputFloat(this.L("mono.highlight_threshold", "Highlight threshold (ex)"), ref this.Settings.MonolithHighlightThreshold, 1f, 10f, "%.0f");
                    if (this.Settings.MonolithHighlightThreshold < 0f) this.Settings.MonolithHighlightThreshold = 0f;
                    ImGui.TextDisabled(this.L("mono.highlight_threshold_hint",
                        "Tints a monolith's header by its best reward value: green at/above the\n" +
                        "threshold, yellow from 0.6× up to it, none below. 0 = off (use Price color)."));
                }

                //ImGui.Checkbox("Show monolith debug window", ref this.Settings.ShowWindow);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(this.Loc.Title("tab.expedition", "Expedition", "rh_tab_expedition")))
            {
                ImGui.Spacing();

                ImGui.TextDisabled(this.L("exp.planner_caption", "Explosive-chain route planner"));
                ImGui.Checkbox(this.L("exp.show_planner", "Show route planner"), ref this.Settings.ShowExpeditionPlanner);
                if (this.Settings.ShowExpeditionPlanner)
                {
                    ImGui.TextDisabled(this.L("exp.planner_hint", "A planner window appears while the in-game explosive HUD is visible"));
                    if(ImGui.CollapsingHeader(this.Loc.Title("exp.reward_profile", "Reward / target profile", "rh_exp_reward"))) {
                        this.DrawExpeditionTargetProfileSettings();
                    }

                    if(ImGui.CollapsingHeader(this.Loc.Title("exp.buff_profile", "Relic buff profile", "rh_exp_buff"))) {
                        this.DrawExpeditionBuffProfileSettings();
                    }

                    // Rune-chain valuation is a route-planning input (it values the chain of monsters the
                    // explosives unearth), so it lives with the planner and is gated on it.
                    this.DrawRuneChainSection();
                }

                // Separator sits outside the planner gate so Debug is always set off from the section above
                // it, planner on or off.
                ImGui.Separator();
                ImGui.Spacing();

                if (ImGui.CollapsingHeader(this.Loc.Title("common.debug", "Debug", "rh_exp_debug")))
                {
                    //ImGui.Checkbox("Show Expedition debug window", ref this.Settings.ShowExpeditionDebug);
                    ImGui.Checkbox(this.L("exp.show_grid_value", "Show grid value"), ref this.Settings.ShowExpeditionGridValue);
                    //ImGui.Checkbox("Show path blockers (gates)", ref this.Settings.ShowExpeditionGates);
                    //if (this.Settings.ShowExpeditionGates)
                    //{
                    //    ImGui.TextDisabled("Paints, on the large map (Tab), the footprint of each TriggerableBlockage\n" +
                    //        "terrain object — the hole it punches in the walkable grid. Red = still blocking,\n" +
                    //        "green dot = open. Visualization + route now uses blast-opened paths (WIP).");
                    //    ImGui.Indent();
                    //    ImGui.SetNextItemWidth(160);
                    //    ImGui.SliderInt("flood max radius", ref this.Settings.ExpGateFloodMaxRadius, 5, 120);
                    //    ImGui.SetNextItemWidth(160);
                    //    ImGui.SliderInt("flood max cells", ref this.Settings.ExpGateFloodMaxCells, 100, 8000);
                    //    ImGui.SetNextItemWidth(160);
                    //    ImGui.SliderInt("disk fallback radius", ref this.Settings.ExpGateDiskRadius, 2, 40);
                    //    ImGui.TextDisabled("Tune until the red footprint covers the blocker (re-floods live).");
                    //    ImGui.Unindent();
                    //}

                    //ImGui.Checkbox("Show weight heatmap (all)", ref this.Settings.ShowExpeditionHeatmap);
                    //ImGui.Checkbox("Show marker heatmap (non-monolith)", ref this.Settings.ShowExpeditionHeatmapMarkers);
                    //if (this.Settings.ShowExpeditionHeatmap || this.Settings.ShowExpeditionHeatmapMarkers)
                    //{
                    //    ImGui.Indent();
                    //    ImGui.SetNextItemWidth(160);
                    //    ImGui.SliderInt("heatmap radius", ref this.Settings.ExpHeatmapRadius, 20, 200);
                    //    ImGui.TextDisabled("Dumps expedition_inventory.txt.");
                    //    ImGui.Unindent();
                    //}

                    ImGui.Checkbox(this.L("exp.show_spine", "Show route spine (Router)"), ref this.Settings.ShowExpeditionSpine);
                    if (this.Settings.ShowExpeditionSpine)
                        ImGui.TextDisabled(this.L("exp.spine_hint",
                            "Large map (Tab): draws the Router's strict-spine polyline (cyan) — the path\n" +
                            "walked (detonator → anchors), shown separately from the charge placements."));

                    ImGui.Checkbox(this.L("exp.log_planner", "Log planner decisions"), ref this.Settings.ExpLogPlanner);
                    if (this.Settings.ExpLogPlanner)
                    {
                        ImGui.TextDisabled(this.L("exp.log_hint",
                            "Writes a full decision trace on each Run (every candidate, its score,\n" +
                            "rejections, gate openings, final pick) to expedition_planner_log.txt in the\n" +
                            "plugin folder. For debugging why the route chose a path."));
                        if (this.expLogLines > 0)
                        {
                            ImGui.TextDisabled(this.LF("exp.log_lines", "planner log: {0} lines", this.expLogLines));
                            ImGui.SameLine();
                            if (ImGui.SmallButton(this.L("exp.open_log", "open log")))
                            {
                                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(this.expLogPath) { UseShellExecute = true }); }
                                catch { /* ignore */ }
                            }

                            ImGui.SameLine();
                            if (ImGui.SmallButton(this.L("exp.copy_path", "copy path"))) ImGui.SetClipboardText(this.expLogPath);
                        }
                    }
                }

                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
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
                if (ImGui.InputText(this.Loc.Label("settings.league", "League", "RhLeagueInput"), ref this.Settings.League, 64))
                {
                    this.Settings.LeaguePinned = true;
                }

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    this.priceCache.StartRefresh(this.Settings.League, this.PriceCachePathname);
                }

                ImGui.TextDisabled(this.L("settings.custom_league_hint",
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

                if (ImGui.BeginCombo(this.Loc.Label("settings.league", "League", "RhLeagueCombo"), this.Settings.League))
                {
                    Group(this.L("settings.league_softcore", "Softcore"), softcore);
                    Group(this.L("settings.league_hardcore", "Hardcore"), hardcore);
                    ImGui.EndCombo();
                }

                ImGui.TextDisabled(this.L("settings.league_hint",
                    "Prices are fetched for exactly this poe.ninja league."));
            }

            ImGui.Checkbox(
                this.Loc.Label("settings.custom_league", "Type the league name manually", "RhCustomLeague"),
                ref this.Settings.UseCustomLeague);

            var listStatus = NinjaLeagues.Status;
            ImGui.BeginDisabled(listStatus == PriceSyncStatus.Syncing);
            if (ImGui.Button(this.Loc.Label("settings.refresh_leagues", "Refresh league list", "RhRefreshLeagues")))
            {
                NinjaLeagues.StartRefresh(this.LeagueCachePathname);
            }

            ImGui.EndDisabled();

            string listText;
            if (listStatus == PriceSyncStatus.Syncing)
            {
                listText = this.L("settings.leagues_loading", "loading league list…");
            }
            else if (listStatus == PriceSyncStatus.Error)
            {
                listText = NinjaLeagues.IsLoaded
                    ? this.LF("settings.leagues_offline_cached", "offline — using cached list ({0} old)", FormatRelative(NinjaLeagues.FetchedUtc))
                    : this.L("settings.leagues_offline_builtin", "offline — using built-in list");
            }
            else if (NinjaLeagues.IsLoaded)
            {
                listText = this.LF("settings.leagues_ok", "{0} leagues, updated {1} ago", NinjaLeagues.All.Count, FormatRelative(NinjaLeagues.FetchedUtc));
            }
            else
            {
                listText = this.L("settings.leagues_offline_builtin", "offline — using built-in list");
            }

            ImGui.SameLine();
            ImGui.TextDisabled(listText);

            if (!string.IsNullOrEmpty(this.leagueNoteTo))
            {
                ImGui.TextWrapped(this.LF(
                    "settings.league_adopted",
                    "League \"{0}\" is gone from poe.ninja; switched to \"{1}\".",
                    this.leagueNoteFrom,
                    this.leagueNoteTo));
            }
        }

        public override void DrawUI()
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                this.recipes.Clear();
                return;
            }

            this.MaybeAutoRefreshPrices();

            // When neither the game nor GameHelper is the foreground window the game hides its
            // panels; our overlay must follow suit, otherwise the price text floats over the
            // desktop / other apps (the game stays InGameState while alt-tabbed out).
            if (!Core.Process.Foreground &&
                System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle != GetForegroundWindow())
            {
                this.recipes.Clear();
                return;
            }

            if (!this.EnsureProcess()) return;

            // Resolve the Runeshape Combinations panel first: a non-zero result means it's open (the
            // fp-walk's gate requires a visible window-container). The monolith map labels use this to
            // hide themselves while the panel is up (HideMapValueWhenPanelOpen).
            var panel = this.ResolvePanel();
            bool panelOpen = panel != IntPtr.Zero;

            // Monolith windows (rewards list + per-monolith debug dump). Both are driven by the same
            // scan inside DrawMonolithRewards; ShowWindow now opens the monolith debug window. The
            // locked-recipe highlight also needs the scan (to know each nearby station's locked recipe).
            bool wantLockHighlight = panelOpen && this.Settings.HighlightLockedRecipeInPanel;
            if (this.Settings.ShowMonolithRewards || this.Settings.ShowWindow ||
                this.Settings.DrawMonolithValueOnMap || this.Settings.ShowGlowRunes || wantLockHighlight)
                this.DrawMonolithRewards(panelOpen);

            // Which monolith's panel is open? Distance is NOT a discriminator — several monoliths can sit
            // together and any be opened from one spot. The open monolith's station carries a panel-open
            // listener (read in the scan); if that monolith is also sealed, take its locked recipe metaId.
            if (wantLockHighlight)
            {
                this.ResolveLockedPanelReward();
            }
            else
            {
                this.lockedPanelMetaId = string.Empty;
                this.lockedPanelName = string.Empty;
            }

            // Expedition planner (WIP) — runs independently of the Runeshape Combinations panel, so do it
            // before the panel-open early-return below.
            if (this.Settings.ShowExpeditionDebug || this.Settings.ShowExpeditionGridValue ||
                this.Settings.ShowExpeditionPlanner || this.Settings.ShowExpeditionGates ||
                this.Settings.ShowExpeditionHeatmap || this.Settings.ShowExpeditionHeatmapMarkers)
                this.ExpeditionTick();

            if (!panelOpen)
            {
                this.recipes.Clear();
                return;
            }

            this.BuildNameToArtIfNeeded(panel);
            this.ReadVisibleRecipes(panel);
            if (this.recipes.Count == 0) return;

            this.DrawOverlay();
        }

        // ── Panel resolution ──────────────────────────────────────────────

        // Walk from GameUi.Address down to the recipes container by matching each step's Flags
        // fingerprint (IsVisible bit masked), backtracking across sibling matches.
        private IntPtr ResolvePanel()
        {
            var gameUi = Core.States.InGameStateObject.GameUi.Address;
            this.resolvedViewport = IntPtr.Zero;
            if (gameUi == IntPtr.Zero) return IntPtr.Zero;
            return this.WalkFp(gameUi, PanelFlagFingerprints, GateStep, 0);
        }

        // Recursive backtracking fp-walk. At `step`, scan `parent`'s children for ones whose
        // Flags (IsVisible bit masked) match fps[step], trying visible candidates before
        // invisible ones, and recurse into each until a branch reaches a valid recipes container
        // at the bottom. `gateStep` only accepts a visible match (the panel-open gate).
        private IntPtr WalkFp(IntPtr parentAddr, uint[] fps, int gateStep, int step)
        {
            if (step == fps.Length)
                return this.IsRecipesContainer(parentAddr) ? parentAddr : IntPtr.Zero;

            if (!this.TryReadStdVector(parentAddr + UiElementChildrenOffset, out var first, out var last))
                return IntPtr.Zero;
            long n = ((long)last - (long)first) / 8;
            if (n <= 0 || n > 4000) return IntPtr.Zero;

            uint target = fps[step] & ~IsVisibleMask;

            // Pass 0 = visible candidates, pass 1 = invisible — so the gate naturally prefers
            // the open instance, and other steps still fall back to invisible siblings.
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantVisible = pass == 0;
                for (int i = 0; i < n; i++)
                {
                    var childAddr = this.ReadPtr(first + (nint)(i * 8));
                    if (childAddr == IntPtr.Zero) continue;
                    if (!this.TryReadFlags(childAddr, out var flags)) continue;
                    if ((flags & ~IsVisibleMask) != target) continue;

                    bool visible = (flags & IsVisibleMask) != 0;
                    if (visible != wantVisible) continue;
                    if (step == gateStep && !visible) continue;

                    var deeper = this.WalkFp(childAddr, fps, gateStep, step + 1);
                    if (deeper != IntPtr.Zero)
                    {
                        // On the successful branch, the child matched at ViewportStep IS the scroll
                        // viewport (the fixed clip window) — remember it for DrawOverlay's clipping.
                        if (step == ViewportStep)
                            this.resolvedViewport = childAddr;
                        return deeper;
                    }
                }
            }
            return IntPtr.Zero;
        }

        // Terminal validation for the fp-walk: the real recipes container holds row elements
        // whose kid[0] carries the "<count>x <name>" label as an inline std::wstring at +0x390.
        // Requiring at least one child to yield a non-empty label distinguishes it from
        // unrelated siblings that share the same 0x00502EF1 fingerprint but contain no rows.
        private bool IsRecipesContainer(IntPtr addr)
        {
            if (!this.TryReadStdVector(addr + UiElementChildrenOffset, out var first, out var last)) return false;
            long n = ((long)last - (long)first) / 8;
            if (n <= 0 || n > 4000) return false;

            for (int i = 0; i < n; i++)
            {
                var row = this.ReadPtr(first + (nint)(i * 8));
                if (row == IntPtr.Zero) continue;
                var label = this.GetChild(row, 0);
                if (label == IntPtr.Zero) continue;
                if (!string.IsNullOrEmpty(this.ReadStdWString(label + NameWStringOffset)))
                    return true;
            }
            return false;
        }

        // The recipes container itself uses index 0 for the row's label — that index is stable
        // because each row has a fixed layout (label first, then rune icons).
        private IntPtr GetChild(IntPtr addr, int index)
        {
            if (addr == IntPtr.Zero) return IntPtr.Zero;
            if (!this.TryReadStdVector(addr + UiElementChildrenOffset, out var first, out var last)) return IntPtr.Zero;
            long n = ((long)last - (long)first) / 8;
            if (index < 0 || index >= n) return IntPtr.Zero;
            return this.ReadPtr(first + (nint)(index * 8));
        }

        // ── Reading rows ──────────────────────────────────────────────────

        private void ReadVisibleRecipes(IntPtr panel)
        {
            this.recipes.Clear();
            if (!this.TryReadStdVector(panel + UiElementChildrenOffset, out var first, out var last)) return;
            long n = ((long)last - (long)first) / 8;
            if (n <= 0 || n > 4000) return;

            for (int i = 0; i < n; i++)
            {
                var row = this.ReadPtr(first + (nint)(i * 8));
                if (row == IntPtr.Zero) continue;
                if (!this.IsUiElementVisible(row)) continue;

                var label = this.GetChild(row, 0);
                if (label == IntPtr.Zero) continue;

                var raw = this.ReadStdWString(label + NameWStringOffset);
                if (string.IsNullOrEmpty(raw)) continue;

                // Count ("Nx ") is digits — locale-independent — so keep parsing it from the label.
                ParseNameAndCount(raw, out var count, out var name);

                // Reward identity from the recipe row, not the localized text. row UiElement +0x540 →
                // Expedition2Recipes_Row; +0x2c → reward BaseItemType (Id/meta +0x00, Name +0x20, art +0x78→+0x08).
                string metaId = string.Empty, ddsArt = string.Empty, recipeId = string.Empty;
                var recipe = this.ReadPtr(row + RecipeRowBackPtrOffset);
                if (recipe != IntPtr.Zero)
                {
                    // Recipe row +0x00 → language-independent Id ("4Slot…"); used to look up the offline
                    // catalog for the glow-rune panel label (see GlowRuneLabelForRecipe).
                    recipeId = this.ReadUtf16Z(this.ReadPtr(recipe), 64);
                    var bit = this.ReadPtr(recipe + RecipeRewardItemOffset);
                    if (bit != IntPtr.Zero)
                    {
                        metaId = LastMetaSegment(this.ReadUtf16Z(this.ReadPtr(bit + BaseItemTypeIdOffset), 128));
                        var artSub = this.ReadPtr(bit + BaseItemTypeArtOffset);
                        if (artSub != IntPtr.Zero)
                            ddsArt = ArtIdFromDdsPath(this.ReadUtf16Z(this.ReadPtr(artSub + ArtSubPathOffset), 128));
                        var bitName = this.ReadUtf16Z(this.ReadPtr(bit + BaseItemTypeNameOffset), 64);
                        if (!string.IsNullOrEmpty(bitName)) name = bitName; // localized reward name (locked-match fallback)
                    }
                    // bit == 0 → "random currency": no fixed BaseItemType. Keep the label name; it has no price.
                }
                // RowAddress is the visible row UiElement — re-resolved every frame here, so the
                // overlay always draws against fresh (post-scroll) screen coordinates. Name is kept
                // only as a localized-name price fallback; it is never displayed.
                this.recipes.Add(new Recipe(count, row, metaId, ddsArt, name, recipeId));
            }
        }

        // ── Reward art-id dictionary (localized name → language-independent art-id) ──────────

        // Build {localizedName → (metaId, ddsArt)} from the game's MAIN BaseItemTypes table, once per
        // session (loaded globally, stable until the game restarts). The table is located DIRECTLY (no
        // longer via the recipe table's reward FK, whose row layout drifts between patches and silently
        // emptied this dict). Throttled while empty so the BFS doesn't run every frame.
        private void BuildNameToArtIfNeeded(IntPtr panel)
        {
            if (this.nameToArtId.Count > 0) return;
            var now = DateTime.UtcNow;
            if (now < this.nameToArtNextTryUtc) return;
            this.nameToArtNextTryUtc = now.AddSeconds(2);

            // The recipe handle is reliably reachable from the panel and sits in the same dat-table
            // registry as BaseItemTypes, so it serves as a second BFS root to reach the latter.
            var recipeHandle = this.FindRecipeTableHandle(panel);
            var bitTable = this.FindBaseItemTypesHandle(panel, recipeHandle);
            if (bitTable == IntPtr.Zero) return;

            var bitVec = this.ReadPtr(bitTable + TableRowsVectorOffset);
            var bitBegin = this.ReadPtr(bitVec);
            var bitEnd = this.ReadPtr(bitVec + 8);
            if (bitBegin == IntPtr.Zero || (long)bitEnd <= (long)bitBegin) return;
            long bitCount = ((long)bitEnd - (long)bitBegin) / BaseItemTypeStride;
            if (bitCount <= 0 || bitCount > 200000) return;

            var dict = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            for (long j = 0; j < bitCount; j++)
            {
                var row = bitBegin + (nint)(j * BaseItemTypeStride);
                var name = this.ReadUtf16Z(this.ReadPtr(row + BaseItemTypeNameOffset), 64);
                if (name.Length < 2) continue;
                // metaId: BaseItemType.Id's last meta-path segment (e.g. "CurrencyUpgradeMagicToRare2").
                // Its trailing digit encodes the currency tier for shared-icon families (Regal …/…2/…3).
                var metaId = LastMetaSegment(this.ReadUtf16Z(this.ReadPtr(row + BaseItemTypeIdOffset), 128));
                // ddsArt: the .dds art filename (= poe.ninja's image-id). Distinct per tier for families
                // whose BaseItemType.Id diverges from the art name (Jeweller's "…01/02/03"). row+0x78 →
                // sub-object, +0x08 → "Art/2DItems/.../<ArtId>.dds".
                var artSub = this.ReadPtr(row + BaseItemTypeArtOffset);
                var ddsArt = artSub == IntPtr.Zero
                    ? string.Empty
                    : ArtIdFromDdsPath(this.ReadUtf16Z(this.ReadPtr(artSub + ArtSubPathOffset), 128));
                if (metaId.Length == 0 && ddsArt.Length == 0) continue;
                // Key by the RAW localized name (trimmed). NOT PriceCache.Normalize — that keeps only
                // a-z0-9 and would collapse every Cyrillic/CJK name to the empty string.
                dict[name.Trim()] = (metaId, ddsArt);
            }

            if (dict.Count > 0) this.nameToArtId = dict;
        }

        // Walk a pointer graph (BFS) to a loaded dat-file handle whose path satisfies `pathMatch`: a heap
        // object whose +0x00 is an in-module vtable and whose +0x08 points to its path string. The vtable
        // gate keeps the (remote) string read off the vast majority of nodes. TWO roots are accepted so a
        // table not reachable from the panel can still be found via another handle in the same dat
        // registry. Bounded by visited count + depth so it can't run away.
        private IntPtr FindDatHandle(IntPtr root1, IntPtr root2, Func<string, bool> pathMatch)
        {
            var seen = new HashSet<long>();
            var queue = new Queue<(IntPtr addr, int depth)>();
            if (root1 != IntPtr.Zero && seen.Add((long)root1)) queue.Enqueue((root1, 0));
            if (root2 != IntPtr.Zero && seen.Add((long)root2)) queue.Enqueue((root2, 0));
            int visited = 0;
            while (queue.Count > 0 && visited < 80000)
            {
                var (addr, depth) = queue.Dequeue();
                visited++;

                if (IsExeAddr(this.ReadPtr(addr)))
                {
                    var pathPtr = this.ReadPtr(addr + DatPathOffset);
                    if (pathPtr != IntPtr.Zero)
                    {
                        var s = this.ReadUtf16Z(pathPtr, 96);
                        if (pathMatch(s)) return addr;
                    }
                }

                if (depth >= 8) continue;
                var buf = new byte[0x180];
                if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out var got)) continue;
                for (int o = 0; o + 8 <= got; o += 8)
                {
                    long v = BitConverter.ToInt64(buf, o);
                    if ((ulong)v < 0x10000 || (ulong)v > 0x7FFFFFFFFFFF) continue;
                    if (seen.Add(v)) queue.Enqueue(((IntPtr)v, depth + 1));
                }
            }
            return IntPtr.Zero;
        }

        // Main Expedition2Recipes dat handle (path ends ".../Balance/Expedition2Recipes.dat") — excludes
        // the per-language overlay (".../Russian/...") and ".datc64" caches, whose rows lack the schema.
        private IntPtr FindRecipeTableHandle(IntPtr panel) =>
            this.FindDatHandle(panel, IntPtr.Zero,
                s => s.EndsWith("Balance/Expedition2Recipes.dat", StringComparison.Ordinal));

        // Main BaseItemTypes dat handle (path ends ".../Balance/BaseItemTypes.dat"). `recipeHandle` is a
        // second BFS root (same dat registry) for when the table isn't reachable from the panel alone.
        private IntPtr FindBaseItemTypesHandle(IntPtr panel, IntPtr recipeHandle) =>
            this.FindDatHandle(panel, recipeHandle,
                s => s.EndsWith("Balance/BaseItemTypes.dat", StringComparison.Ordinal));

        // True for addresses inside a loaded module (exe/dll) — user-mode module region is ≥ ~0x7FF0…,
        // far above heap allocations (~0x000002…). Cheap gate for "looks like a vtable".
        private static bool IsExeAddr(IntPtr p) => (ulong)p >= 0x7FF000000000ul && (ulong)p < 0x800000000000ul;

        // Read a NUL-terminated UTF-16 string from a raw buffer pointer (the .dat string-column layout
        // — a direct char* into the file's string heap, not an MSVC std::wstring).
        private string ReadUtf16Z(IntPtr ptr, int maxChars)
        {
            if (ptr == IntPtr.Zero) return string.Empty;
            ulong u = (ulong)ptr;
            if (u < 0x10000 || u > 0x7FFFFFFFFFFF) return string.Empty;
            var buf = new byte[maxChars * 2];
            if (!ReadProcessMemory(this.processHandle, ptr, buf, (uint)buf.Length, out var read)) return string.Empty;
            int n = read / 2;
            var sb = new StringBuilder(n);
            for (int i = 0; i < n; i++)
            {
                char c = (char)BitConverter.ToUInt16(buf, i * 2);
                if (c == '\0') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ── Price refresh polling ─────────────────────────────────────────

        // Cheap once-a-minute poll: if the cache is older than the configured TTL and no sync is
        // already in flight, kick one off. The first refresh after OnEnable is initiated there;
        // this only handles long-lived sessions where the TTL eventually expires.
        private void MaybeAutoRefreshPrices()
        {
            var now = DateTime.UtcNow;
            if (now < this.nextAutoRefreshCheckUtc) return;
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

            if (this.priceCache.Status == PriceSyncStatus.Syncing) return;
            var ttl = TimeSpan.FromMinutes(Math.Max(1, this.Settings.CacheTtlMinutes));
            if (this.priceCache.LastSyncUtc != DateTime.MinValue && now - this.priceCache.LastSyncUtc < ttl) return;

            this.priceCache.StartRefresh(this.Settings.League, this.PriceCachePathname);
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

        // ── Drawing (overlay) ─────────────────────────────────────────────
        //
        // Instead of a separate ImGui window, the reward NAME the player already reads off the
        // game's own panel (in their client language) is left untouched, and we paint just the
        // PRICE onto the right edge of each visible row via the foreground draw list. Row screen
        // rects are computed from each row UiElement's RelativePosition / scale chain — the exact
        // arithmetic GameHelper's UiElementBase.Position uses (those APIs are internal to the GH
        // assembly, so the math is mirrored here over the public UiElementBaseOffset struct).
        // The horizontal letterbox cull offset (Core.GameCull) is also GH-internal and omitted;
        // it is 0 on non-letterboxed displays (the common case).

        // Per-frame cache of ancestor UiElementBaseOffsets. All visible rows share the same parent
        // chain up to GameUi, so without this each row would re-read the whole chain. Cleared at the
        // top of every DrawOverlay.
        private readonly Dictionary<long, UiElementBaseOffset> frameBaseCache = new();

        // Scratch list of resolved rows, rebuilt each frame (kept as a field to avoid per-frame allocs).
        // Locked = this row is the sealed monolith's locked-in recipe (gold-bordered).
        // Two INDEPENDENT signals per row, deliberately not merged into one number:
        //   BestPrice — highest reward price (green frame, unchanged behaviour);
        //   BestRune  — this row's recipe would drop the best-valued rune on the monolith's gold
        //               (propagating) socket → amber frame around the rune name, no figures.
        // A row can carry either, both, or neither; the player weighs price against chain themselves.
        private readonly List<(Vector2 Pos, Vector2 Size, double Total, bool Locked, string Rune, bool BestPrice, bool BestRune, uint RuneColor)> overlayRows = new();

        // Priced rows for the current frame (RowAddress + total), built BEFORE geometry is resolved so
        // the Relative-mode median is computed over the full priced set, independent of whether any
        // individual row's screen geometry read succeeds this frame. Locked: see overlayRows.
        // RuneMult = the propagated rune's effective loot multiplier (1.0 = none / no rune), the ranking
        // key for the BestRune frame. ChainRune distinguishes a chain-resolved propagating rune (tinted by
        // its class) from the older glow-rune SCOUTING label, which keeps its plain amber.
        private readonly List<(IntPtr Addr, double Total, bool Locked, string Rune, double RuneMult, bool ChainRune)> pricedScratch = new();

        // Last-good screen geometry per row UiElement. A single ReadProcessMemory miss on a live client
        // would otherwise blank or teleport that row's price for a frame; instead we reuse the previous
        // good (pos, size) for up to MaxStaleGeomFrames frames. Reused ONLY on a read failure — a row
        // the game reports hidden is dropped at once, so a scrolled-off row never ghosts.
        private readonly Dictionary<long, (Vector2 Pos, Vector2 Size, int StaleFrames)> lastGoodGeom = new();
        private const int MaxStaleGeomFrames = 6;

        private const uint ColorWhite = 0xFFFFFFFFu;
        private const uint ColorGreen = 0xFF55FF55u;
        private const uint ColorYellow = 0xFF55FFFFu;
        private const uint ColorRed = 0xFF4040FFu;
        private const uint ColorShadow = 0xCC000000u;
        private const uint ColorPriceBg = 0xE6000000u; // 90%-opaque black plate behind the price text
        private const uint ColorGold = 0xFF00D7FFu;     // gold border on the sealed monolith's locked row
        private const uint ColorGlowRune = 0xFF4DCCFFu;  // amber — watched-rune name after the price (matches the map label)
        // Rune-chain tinting of that name, so "no good rune here" is distinguishable from "not working"
        // without printing a single figure. Amber (above) = gains loot down the chain; grey = pure danger,
        // no loot effect (most runes, and any rune absent from the weight table); red = a net cost to
        // propagate (Oath's immortal loot-less waves, Wisdom's experience-only).
        private const uint ColorRuneNeutral = 0xFF9A9A9Au;

        // Reward metaId of the locked recipe for the sealed monolith the open panel belongs to (the
        // closest monolith). Set each frame in DrawUI; a visible panel row whose MetaId matches gets a
        // gold border. Empty = not a sealed monolith / highlight disabled → no border.
        private string lockedPanelMetaId = string.Empty;
        // Reward NAME of that locked recipe — the fallback match key. Rune/SoulCore rewards read an
        // empty BaseItemType.Id (so MetaId is blank for the whole rune panel); the localized name is the
        // only identity shared between the offline recipe catalog and the live panel rows for them.
        private string lockedPanelName = string.Empty;
        // Alpha for the plate behind the monolith price on the large-map overlay. Matches the LootTracker
        // map/hideout bars (their BarOpacity default); the plate uses the theme's WindowBg colour at this
        // alpha, computed live in DrawMonolithMapLabels.
        private const float MonolithMapBgAlpha = 0.55f;

        private void DrawOverlay()
        {
            this.frameBaseCache.Clear();
            this.overlayRows.Clear();

            // Re-read the viewport's scroll offset once per frame; it's added to each content row's
            // position in TryGetUnscaledPosition so the rows (and their prices) track the scroll.
            this.viewportScrollOffset = this.ReadScrollOffset(this.resolvedViewport);

            // 1) Resolve prices first (lock-guarded, stable). The Relative-mode median is computed over
            //    this full priced set — NOT over the rows whose geometry happens to resolve this frame —
            //    so a transient geometry read miss can't shift the colour thresholds and flip every
            //    row green/yellow/red.
            this.pricedScratch.Clear();
            foreach (var r in this.recipes)
                if (this.TryGetRecipePrice(in r, out var unit))
                {
                    // Match the locked row on metaId when it's available, else fall back to the reward
                    // name (rune/SoulCore rewards have a blank live MetaId — see lockedPanelName).
                    bool locked =
                        (this.lockedPanelMetaId.Length > 0 &&
                         string.Equals(r.MetaId, this.lockedPanelMetaId, StringComparison.Ordinal)) ||
                        (this.lockedPanelName.Length > 0 &&
                         string.Equals(r.Name, this.lockedPanelName, StringComparison.Ordinal));
                    // Watched rune this recipe would place on the open monolith's glowing socket (empty if none).
                    string rune = this.GlowRuneLabelForRecipe(r.Id);
                    // Rune-chain: the gold socket is a POSITION, so we know which rune this recipe would
                    // propagate even before the player picks anything. Its label wins over the scouting one
                    // (it names the rune that actually propagates, not merely a watched one).
                    double runeMult = 1.0;
                    bool chainRune = this.TryGetPropagatedRuneForRecipeId(
                        r.Id, out var pRune, out var pMult, out var pTaken);
                    if (chainRune)
                    {
                        // Spell out WHY a strong-looking rune is drawn plain: it is already propagating in
                        // this chain (locked in on another monolith, or on one detonated earlier), and the
                        // same runeshape modifier does not stack with itself.
                        rune = pTaken
                            ? pRune + " " + this.L("panel.rune_taken", "(taken)")
                            : pRune;
                        runeMult = pMult;
                    }

                    this.pricedScratch.Add((r.RowAddress, unit * Math.Max(1, r.Count), locked, rune, runeMult, chainRune));
                }
            if (this.pricedScratch.Count == 0) return;

            // Best PRICE (green frame below) — the highest-priced offered row, computed over the full priced
            // set so it is independent of which rows' geometry resolves this frame.
            double bestTotal = double.NegativeInfinity;
            foreach (var p in this.pricedScratch) if (p.Total > bestTotal) bestTotal = p.Total;

            // Best RUNE (amber frame around the rune name) — the strongest rune any offered row can drop on
            // the gold socket. Only a rune that actually gains loot qualifies (> 1.0), so a panel where the
            // only propagatable runes are Oath/Wisdom (multiplier below 1) frames nothing. Ties frame all.
            double bestRuneMult = 1.0;
            foreach (var p in this.pricedScratch) if (p.RuneMult > bestRuneMult) bestRuneMult = p.RuneMult;

            double median = 0;
            if (this.Settings.ColorMode == RewardColorMode.Relative)
                median = MedianOf(this.pricedScratch);

            // 2) Resolve each row's screen geometry, falling back to its last-good (pos, size) for a few
            //    frames on a read miss so the price doesn't blink out or teleport on a single bad read.
            foreach (var (addr, total, locked, rune, runeMult, chainRune) in this.pricedScratch)
            {
                if (!this.TryResolveRowGeometry(addr, out var pos, out var size)) continue;
                bool bestPrice = total >= bestTotal && bestTotal > double.NegativeInfinity;
                bool bestRune = bestRuneMult > 1.0 && runeMult >= bestRuneMult;
                // A scouting-only label keeps its plain amber; a chain-resolved rune is tinted by class.
                uint runeColor = !chainRune ? ColorGlowRune
                    : runeMult > 1.0 ? ColorGlowRune
                    : runeMult < 1.0 ? ColorRed
                    : ColorRuneNeutral;
                this.overlayRows.Add((pos, size, total, locked, rune, bestPrice, bestRune, runeColor));
            }
            if (this.overlayRows.Count == 0) return;

            // Draw at an explicit per-row pixel size via the font-size AddText overload, rather than
            // mutating the shared font's global Scale per iteration (that leaks ImGui font state
            // between rows and makes the size flip-flop). The ambient font size is read once and used
            // only to scale the measured text width.
            var drawList = ImGui.GetForegroundDrawList();
            var font = ImGui.GetFont();
            float ambient = ImGui.GetFontSize();

            // Resolve the scroll viewport's screen rect — the fixed clip window (fp 0x00502EF7, the
            // recipes-container's grandparent; see docs/re-findings.md §3). Rows scrolled out of this
            // window still report IsVisible=true (the game clips them with a scissor rect, not the
            // flag), so we drop any row whose vertical centre falls outside it. We clip only vertically:
            // the X position is the user's to set via Price X offset, so it may intentionally sit
            // outside the frame. If the viewport can't be resolved this frame, fall back to no clip.
            Vector2 vpPos = default, vpSize = default;
            bool haveClip = this.resolvedViewport != IntPtr.Zero &&
                            this.TryResolveRowGeometry(this.resolvedViewport, out vpPos, out vpSize);
            float clipTop = haveClip ? vpPos.Y : 0f;
            float clipBottom = haveClip ? vpPos.Y + vpSize.Y : 0f;

            foreach (var row in this.overlayRows)
            {
                // Vertical clip: drop rows whose centre is outside the viewport (scrolled off-list).
                if (haveClip)
                {
                    float centreY = row.Pos.Y + row.Size.Y * 0.5f;
                    if (centreY < clipTop || centreY > clipBottom) continue;
                }

                var text = FormatExalted(row.Total);
                uint color = this.PickColor(row.Total, median);

                // Scale the price text to the row height so it reads at any UI scale.
                float fontPx = Math.Clamp(row.Size.Y * 0.5f, 12f, 40f);
                float k = fontPx / ambient;
                var ts = ImGui.CalcTextSize(text) * k;
                float padding = 6f * k;
                float x = row.Pos.X + row.Size.X - ts.X - padding + this.Settings.OverlayXOffset;
                float y = row.Pos.Y + (row.Size.Y - ts.Y) * 0.5f;
                var at = new Vector2(x, y);
                var bgPad = new Vector2(4f * k, 2f * k);
                drawList.AddRectFilled(at - bgPad, at + ts + bgPad, ColorPriceBg, 3f * k);

                // Best PRICE: green frame. Drawn as an OUTER ring (offset beyond the gold box) so it never
                // overlaps the locked-recipe gold frame — when the best row IS the locked row you see green
                // outside + gold inside; otherwise each ring sits on its own row.
                if (row.BestPrice)
                {
                    var gp = bgPad + new Vector2(2f * k, 2f * k);
                    drawList.AddRect(at - gp, at + ts + gp, ColorGreen, 4f * k, ImDrawFlags.None, 2f * k);
                }

                if (row.Locked)
                {
                    // Sealed monolith: ring the locked-in recipe's price box in gold so it's obvious
                    // which of the listed combinations the monolith will actually produce.
                    drawList.AddRect(at - bgPad, at + ts + bgPad, ColorGold, 3f * k, ImDrawFlags.None, 2f * k);
                }

                drawList.AddText(font, fontPx, at + new Vector2(1f, 1f), ColorShadow, text);
                drawList.AddText(font, fontPx, at, color, text);

                // Glow-rune label: this recipe places a watched / propagating rune on the monolith's gold
                // socket — write its NAME AFTER the price, on its own transparent plate (same style as the
                // price box). No figures here on purpose: price and chain are two separate decisions, and
                // mixing them into one number hid which was which.
                if (!string.IsNullOrEmpty(row.Rune))
                {
                    var rts = ImGui.CalcTextSize(row.Rune) * k;
                    var rat = new Vector2(at.X + ts.X + bgPad.X + (8f * k), y);
                    drawList.AddRectFilled(rat - bgPad, rat + rts + bgPad, ColorPriceBg, 3f * k);

                    // Best rune: ring the NAME plate in the rune text's own colour, mirroring the green
                    // price ring. Green says "most valuable reward", amber says "strongest chain rune" —
                    // the two can land on different rows, which is exactly the trade-off to see.
                    if (row.BestRune)
                        drawList.AddRect(rat - bgPad, rat + rts + bgPad, ColorGlowRune, 3f * k, ImDrawFlags.None, 2f * k);

                    drawList.AddText(font, fontPx, rat + new Vector2(1f, 1f), ColorShadow, row.Rune);
                    drawList.AddText(font, fontPx, rat, row.RuneColor, row.Rune);
                }
            }
        }

        // Resolve a row's screen geometry with a short-lived last-good fallback. Returns false (row not
        // drawn) only when the read fails AND there is no fresh last-good to reuse, or when the game
        // reports the row hidden (read succeeded) — the latter is dropped at once so a scrolled-off row
        // never ghosts.
        private bool TryResolveRowGeometry(IntPtr addr, out Vector2 pos, out Vector2 size)
        {
            pos = default;
            size = default;
            if (addr == IntPtr.Zero) return false;
            long key = (long)addr;

            if (this.TryReadUiBase(addr, out var el))
            {
                if ((el.Flags & IsVisibleMask) == 0) { this.lastGoodGeom.Remove(key); return false; }

                var s = this.ScaledSize(in el);
                if (s.X > 1f && s.Y > 1f &&
                    this.TryScreenPosition(in el, out var p) && !float.IsNaN(p.X) && !float.IsNaN(p.Y))
                {
                    pos = p;
                    size = s;
                    this.lastGoodGeom[key] = (p, s, 0);
                    return true;
                }
                // read OK but geometry invalid (e.g. an ancestor read failed mid-chain) → reuse last-good
            }

            if (this.lastGoodGeom.TryGetValue(key, out var lg) && lg.StaleFrames < MaxStaleGeomFrames)
            {
                pos = lg.Pos;
                size = lg.Size;
                this.lastGoodGeom[key] = (lg.Pos, lg.Size, lg.StaleFrames + 1);
                return true;
            }

            this.lastGoodGeom.Remove(key);
            return false;
        }

        private uint PickColor(double total, double median)
        {
            switch (this.Settings.ColorMode)
            {
                case RewardColorMode.Absolute:
                    if (total >= 5.0) return ColorGreen;
                    if (total < 0.5) return ColorRed;
                    return ColorYellow;
                case RewardColorMode.Relative:
                    if (median <= 0) return ColorWhite;
                    double ratio = total / median;
                    if (ratio >= 1.3) return ColorGreen;
                    if (ratio <= 0.7) return ColorRed;
                    return ColorYellow;
                default:
                    return ColorWhite;
            }
        }

        private static double MedianOf(List<(IntPtr Addr, double Total, bool Locked, string Rune, double RuneMult, bool ChainRune)> rows)
        {
            var arr = new double[rows.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = rows[i].Total;
            Array.Sort(arr);
            int n = arr.Length;
            return n % 2 == 1 ? arr[n / 2] : (arr[n / 2 - 1] + arr[n / 2]) * 0.5;
        }

        // ── UiElement screen geometry (mirrors GameHelper.UiElementBase.Position / Size) ──────

        // The game's per-axis window scale, replicated from GameHelper.GameWindowScale.GetScaleValue
        // (which is internal). v1 is the width ratio, v2 the height ratio vs. the 2560×1600 base UI
        // resolution; ScaleIndex selects which pair applies. The letterbox cull term is omitted (0
        // on non-letterboxed displays).
        private static (float W, float H) ScaleValue(byte index, float multiplier)
        {
            var io = ImGui.GetIO();
            float v1 = io.DisplaySize.X / (float)UiElementBaseFuncs.BaseResolution.X;
            float v2 = io.DisplaySize.Y / (float)UiElementBaseFuncs.BaseResolution.Y;
            float w = multiplier, h = multiplier;
            switch (index)
            {
                case 1: w *= v1; h *= v1; break;
                case 2: w *= v2; h *= v2; break;
                case 3: w *= v1; h *= v2; break;
            }
            return (w, h);
        }

        private Vector2 ScaledSize(in UiElementBaseOffset el)
        {
            var (w, h) = ScaleValue(el.ScaleIndex, el.LocalScaleMultiplier);
            return new Vector2(el.UnscaledSize.X * w, el.UnscaledSize.Y * h);
        }

        private bool TryScreenPosition(in UiElementBaseOffset el, out Vector2 screen)
        {
            if (!this.TryGetUnscaledPosition(in el, 0, out var p))
            {
                screen = default;
                return false;
            }

            var (w, h) = ScaleValue(el.ScaleIndex, el.LocalScaleMultiplier);
            screen = new Vector2(p.X * w, p.Y * h);
            return true;
        }

        // Recursive parent-chain walk — the exact arithmetic of UiElementBase.GetUnScaledPosition.
        // Returns false when an ancestor read FAILS, so the caller keeps the last-good position instead
        // of drawing the half-resolved local coordinate (which would teleport the price to the wrong
        // spot for a frame). Reaching the root (ParentPtr == 0) is success, not failure.
        private bool TryGetUnscaledPosition(in UiElementBaseOffset el, int depth, out Vector2 pos)
        {
            var local = new Vector2(el.RelativePosition.X, el.RelativePosition.Y);
            if (el.ParentPtr == IntPtr.Zero || depth >= 64)
            {
                pos = local;
                return true;
            }

            if (!this.TryReadBaseCached(el.ParentPtr, out var parent))
            {
                pos = local;
                return false;
            }

            if (!this.TryGetUnscaledPosition(in parent, depth + 1, out var parentPos))
            {
                pos = local;
                return false;
            }

            if (UiElementBaseFuncs.ShouldModifyPos(el.Flags))
                parentPos += new Vector2(parent.PositionModifier.X, parent.PositionModifier.Y);

            // Scroll: the recipes list is a fixed-size mask (the resolved viewport) whose content child
            // is translated by a scroll offset at +0x120 — NOT by RelativePosition/PositionModifier
            // (verified live, docs/re-findings.md §3). Add it ONLY for the viewport's direct content
            // child; without it every row sits at its unscrolled position and prices freeze on scroll.
            if (el.ParentPtr == this.resolvedViewport)
                parentPos += this.viewportScrollOffset;

            if (parent.ScaleIndex == el.ScaleIndex &&
                parent.LocalScaleMultiplier == el.LocalScaleMultiplier)
            {
                pos = parentPos + local;
                return true;
            }

            var (psw, psh) = ScaleValue(parent.ScaleIndex, parent.LocalScaleMultiplier);
            var (msw, msh) = ScaleValue(el.ScaleIndex, el.LocalScaleMultiplier);
            pos = new Vector2(
                parentPos.X * psw / msw + local.X,
                parentPos.Y * psh / msh + local.Y);
            return true;
        }

        private bool TryReadBaseCached(IntPtr addr, out UiElementBaseOffset ui)
        {
            if (this.frameBaseCache.TryGetValue((long)addr, out ui)) return true;
            if (!this.TryReadUiBase(addr, out ui)) return false;
            this.frameBaseCache[(long)addr] = ui;
            return true;
        }

        private static readonly int UiBaseSize =
            System.Runtime.CompilerServices.Unsafe.SizeOf<UiElementBaseOffset>();
        private readonly byte[] uiBaseBuf = new byte[UiBaseSize];

        private bool TryReadUiBase(IntPtr addr, out UiElementBaseOffset ui)
        {
            ui = default;
            ulong u = (ulong)addr;
            if (u < 0x10000 || u > 0x7FFFFFFFFFFF) return false;
            if (!ReadProcessMemory(this.processHandle, addr, this.uiBaseBuf, (uint)UiBaseSize, out var got)
                || got < UiBaseSize)
                return false;
            ui = System.Runtime.InteropServices.MemoryMarshal.Read<UiElementBaseOffset>(this.uiBaseBuf);
            return true;
        }

        // Read a UiElement's scroll content offset (+0x120, two floats). Read directly off the element
        // rather than through the marshalled UiElementBaseOffset so the plugin doesn't depend on that
        // GameHelper struct carrying the field — keeps it working across GH versions.
        private Vector2 ReadScrollOffset(IntPtr addr)
        {
            if (addr == IntPtr.Zero) return Vector2.Zero;
            var buf = new byte[8];
            if (!ReadProcessMemory(this.processHandle, addr + ScrollOffsetFieldOffset, buf, (uint)buf.Length, out _))
                return Vector2.Zero;
            return new Vector2(BitConverter.ToSingle(buf, 0), BitConverter.ToSingle(buf, 4));
        }

        // ── Parsing / formatting ─────────────────────────────────────────

        // The reward label embeds the quantity in a locale-dependent way:
        //   "<name> (<count>)"  — e.g. ru "Деталь доспеха (6)"
        //   "<count>x <name>"   — e.g. ko/en "6x 방어구 장인의 고철" / "6x Armourer's Scrap"
        // We strip whichever form is present so `name` is just the localized reward item name.
        private static void ParseNameAndCount(string raw, out int count, out string name)
        {
            count = 1;
            name = raw?.Trim() ?? string.Empty;
            if (name.Length == 0) return;

            // leading "<N>x " (count first)
            int i = 0;
            while (i < name.Length && char.IsDigit(name[i])) i++;
            if (i > 0 && i < name.Length && (name[i] == 'x' || name[i] == 'X'))
            {
                if (int.TryParse(name.AsSpan(0, i), out var c) && c > 0)
                {
                    count = c;
                    name = name[(i + 1)..].TrimStart();
                    return;
                }
            }

            // trailing "(<N>)" (count last)
            if (name[^1] == ')')
            {
                int open = name.LastIndexOf('(');
                if (open > 0)
                {
                    var inner = name.Substring(open + 1, name.Length - open - 2).Trim();
                    if (int.TryParse(inner, out var c) && c > 0)
                    {
                        count = c;
                        name = name[..open].TrimEnd();
                    }
                }
            }
        }

        // "Metadata/Items/Currency/CurrencyUpgradeMagicToRare2" → "CurrencyUpgradeMagicToRare2"
        // — keeps any trailing digit that encodes the currency tier (Greater / Perfect).
        private static string LastMetaSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path[(slash + 1)..] : path;
        }

        // "Art/2DItems/Currency/CurrencyRerollSocketNumbers02.dds" → "CurrencyRerollSocketNumbers02".
        private static string ArtIdFromDdsPath(string path)
        {
            var seg = LastMetaSegment(path);
            int dot = seg.LastIndexOf('.');
            return dot > 0 ? seg[..dot] : seg;
        }

        // Price for a reward, by priority:
        //   0) uncut gems        — handled separately (see below): strictly dds-art + level, where
        //        the level is the metaId's trailing digits ("SkillGemUncut19" → 19). No fall-through.
        //   1) metaId            — exact BaseItemType.Id (Regal tier families: …/…2/…3).
        //   2a) dds-art + level  — for leveled shared-icon currency (Thaumaturgic Flux): the icon is
        //        shared across levels, so we must pin the level (parsed from "…Level<n>"); we do NOT
        //        fall through to the bare dds-art here — it would return some arbitrary level's price.
        //   2b) dds-art          — for non-leveled distinct-icon families (Jeweller's …01/02/03).
        //   3) English name      — from the offline catalog (poe.ninja keys some items, e.g. logbooks,
        //        by NAME, and their metaId/dds miss); language-independent.
        //   4) localized name    — English clients / unmapped.
        private bool TryGetRecipePrice(in Recipe r, out double unit)
        {
            // Uncut gems (Skill/Support/Spirit) reuse ONE icon per family; the level is the metaId's
            // trailing digits with no "Level" marker. Try dds-art + level first (e.g. "UncutSkillGemBuff"
            // + 19), then fall back to the metaId→English-name path, which is level-SPECIFIC (the catalog
            // name carries "(Level 19)") and language-independent — this rescues the gem when the live
            // dds-art read comes back empty (offset drift) or its art id differs from poe.ninja's. We must
            // NOT fall through to the BARE dds-art key, which holds an arbitrary level's price.
            if (IsUncutGem(r.MetaId))
            {
                int gemLevel = UncutGemLevel(r.MetaId);
                if (gemLevel < 0) { unit = 0; return false; }   // base/quest variant — not tradable
                if (!string.IsNullOrEmpty(r.DdsArt) &&
                    this.priceCache.TryGetPriceByArtId(r.DdsArt + gemLevel.ToString(), out unit) && unit > 0)
                    return true;
                if (this.TryPriceByMetaEnglish(r.MetaId, out unit)) return true;
                unit = 0;
                return false;
            }

            if (!string.IsNullOrEmpty(r.MetaId) && this.priceCache.TryGetPriceByArtId(r.MetaId, out unit) && unit > 0)
                return true;

            int level = LevelFromMetaId(r.MetaId);
            if (level >= 0)
            {
                if (!string.IsNullOrEmpty(r.DdsArt) &&
                    this.priceCache.TryGetPriceByArtId(r.DdsArt + level.ToString(), out unit) && unit > 0)
                    return true;
            }
            else if (!string.IsNullOrEmpty(r.DdsArt) && this.priceCache.TryGetPriceByArtId(r.DdsArt, out unit) && unit > 0)
            {
                return true;
            }

            // English-name fallback: poe.ninja keys some items (notably Expedition logbooks) by display
            // NAME, not metaId/dds. The live panel only gives the LOCALIZED name, so resolve the reward's
            // ENGLISH name from the offline catalog (by metaId) and price by that — language-independent.
            if (this.TryPriceByMetaEnglish(r.MetaId, out unit)) return true;

            if (this.priceCache.TryGetExaltedPrice(r.Name, out unit) && unit > 0)
                return true;
            unit = 0;
            return false;
        }

        // Price by the reward's ENGLISH catalog name resolved from its metaId (BaseItemType.Id last
        // segment). Language-independent and level-specific (the catalog name carries "(Level N)"), so it
        // works on any client. metaIdToEnglish is built from the offline recipe catalog.
        private bool TryPriceByMetaEnglish(string metaId, out double unit)
        {
            unit = 0;
            if (string.IsNullOrEmpty(metaId)) return false;
            this.BuildMetaIdToEnglishIfNeeded();
            return this.metaIdToEnglish.TryGetValue(metaId, out var eng) &&
                   this.priceCache.TryGetExaltedPrice(eng, out unit) && unit > 0;
        }

        // Debug: mirror TryGetRecipePrice's branch order and report which path priced the row (and to what),
        // so a mis-resolution (e.g. an uncut gem priced as a different item) is visible in the debug window.
        private (double price, string branch) TraceRecipePrice(in Recipe r)
        {
            if (IsUncutGem(r.MetaId))
            {
                int lvl = UncutGemLevel(r.MetaId);
                if (lvl < 0) return (0, "uncut base/quest (no level)");
                if (!string.IsNullOrEmpty(r.DdsArt) &&
                    this.priceCache.TryGetPriceByArtId(r.DdsArt + lvl, out var u) && u > 0)
                    return (u, $"uncut dds+lvl [{r.DdsArt}{lvl}]");
                if (this.TryPriceByMetaEnglish(r.MetaId, out var ue2)) return (ue2, $"uncut meta→eng");
                return (0, $"uncut MISS lvl={lvl} dds={r.DdsArt}");
            }

            if (!string.IsNullOrEmpty(r.MetaId) && this.priceCache.TryGetPriceByArtId(r.MetaId, out var um) && um > 0)
                return (um, $"metaId [{r.MetaId}]");

            int level = LevelFromMetaId(r.MetaId);
            if (level >= 0)
            {
                if (!string.IsNullOrEmpty(r.DdsArt) &&
                    this.priceCache.TryGetPriceByArtId(r.DdsArt + level, out var ul) && ul > 0)
                    return (ul, $"dds+lvl [{r.DdsArt}{level}]");
            }
            else if (!string.IsNullOrEmpty(r.DdsArt) && this.priceCache.TryGetPriceByArtId(r.DdsArt, out var ud) && ud > 0)
            {
                return (ud, $"dds bare [{r.DdsArt}]");
            }

            if (!string.IsNullOrEmpty(r.MetaId))
            {
                this.BuildMetaIdToEnglishIfNeeded();
                if (this.metaIdToEnglish.TryGetValue(r.MetaId, out var eng) &&
                    this.priceCache.TryGetExaltedPrice(eng, out var ue) && ue > 0)
                    return (ue, $"meta→eng [{eng}]");
            }

            if (this.priceCache.TryGetExaltedPrice(r.Name, out var un) && un > 0)
                return (un, $"name [{r.Name}]");
            return (0, "none");
        }

        // {metaId (BaseItemType.Id last segment) → English reward name}, from the offline recipe catalog
        // (built once; English names are language-independent). Used as a price fallback for items
        // poe.ninja keys by name. monolithRecipes / LoadMonolithData live in the MonolithRewards partial.
        private Dictionary<string, string> metaIdToEnglish = new(StringComparer.Ordinal);

        private void BuildMetaIdToEnglishIfNeeded()
        {
            if (this.metaIdToEnglish.Count > 0) return;
            if (!this.LoadMonolithData()) return;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rec in this.monolithRecipes)
            {
                var id = rec?.reward?.id;
                var nm = rec?.reward?.name;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(nm)) continue;
                var key = LastMetaSegment(id);
                if (key.Length > 0) map[key] = nm;
            }

            if (map.Count > 0) this.metaIdToEnglish = map;
        }

        // BaseItemType.Id ending in "Level<n>" → n (leveled gem currency, e.g. Thaumaturgic Flux's
        // "CurrencySetKalguuranSkillGemLevel9" → 9), else -1. The literal "Level" guard keeps
        // tier-suffixed ids like "…Socket4" / "…ToRare2" out — those use metaId / dds-art directly.
        private static int LevelFromMetaId(string metaId)
        {
            if (string.IsNullOrEmpty(metaId)) return -1;
            int i = metaId.Length;
            while (i > 0 && char.IsDigit(metaId[i - 1])) i--;
            if (i == metaId.Length) return -1;
            const string marker = "Level";
            if (i < marker.Length || !metaId.AsSpan(i - marker.Length, marker.Length).SequenceEqual(marker))
                return -1;
            return int.TryParse(metaId.AsSpan(i), out var n) ? n : -1;
        }

        // True for the uncut-gem families (Uncut Skill / Support / Spirit gems). Each family shares
        // one .dds icon across all levels; the level is the metaId's trailing digits (NO "Level"
        // marker, so LevelFromMetaId misses them on purpose). Priced strictly as dds-art + level.
        private static bool IsUncutGem(string metaId) =>
            !string.IsNullOrEmpty(metaId) &&
            (metaId.StartsWith("SkillGemUncut", StringComparison.Ordinal)
             || metaId.StartsWith("SupportGemUncut", StringComparison.Ordinal)
             || metaId.StartsWith("ReservationGemUncut", StringComparison.Ordinal));

        // Uncut-gem level = trailing digits of the metaId ("SkillGemUncut19" → 19,
        // "ReservationGemUncut8" → 8). Base/quest variants carry no digit
        // ("SkillGemUncutQuest", "ReservationGemUncut", "SupportGemUncut") → -1 → not tradable.
        private static int UncutGemLevel(string metaId)
        {
            if (string.IsNullOrEmpty(metaId)) return -1;
            int i = metaId.Length;
            while (i > 0 && char.IsDigit(metaId[i - 1])) i--;
            if (i == metaId.Length) return -1;
            return int.TryParse(metaId.AsSpan(i), out var n) ? n : -1;
        }

        private static string FormatExalted(double value)
        {
            // Round by magnitude, then strip trailing zeros but keep at least one decimal for
            // sub-100 values — so a ~1ex reward reads "1,0 ex", not "1,000 ex".
            if (value >= 100) return $"{value:F0} ex";
            int decimals = value >= 1 ? 1 : value >= 0.1 ? 2 : 3;
            double rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            string num = rounded.ToString("0.###");
            var sep = System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            if (!num.Contains(sep)) num += sep + "0";
            return $"{num} ex";
        }

        private static string FormatRelative(DateTime utc)
        {
            var span = DateTime.UtcNow - utc;
            if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
            if (span.TotalHours < 24)   return $"{(int)span.TotalHours}h";
            return $"{(int)span.TotalDays}d";
        }

        // ── Memory primitives ────────────────────────────────────────────

        private bool EnsureProcess()
        {
            int pid = (int)Core.Process.Pid;
            if (pid == 0)
            {
                if (this.handlePid != 0) this.ResetHandle();
                return false;
            }

            if (pid == this.handlePid && this.processHandle != IntPtr.Zero) return true;

            this.ResetHandle();
            this.processHandle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
            if (this.processHandle == IntPtr.Zero) return false;
            this.handlePid = pid;
            return true;
        }

        private void ResetHandle()
        {
            if (this.processHandle != IntPtr.Zero)
            {
                CloseHandle(this.processHandle);
                this.processHandle = IntPtr.Zero;
            }

            this.handlePid = 0;
            this.lastGoodGeom.Clear();
            // The name→keys dict is built from the client's localized BaseItemTypes names, so it's
            // language-specific. Drop it on process change so it rebuilds (e.g. after a language switch).
            this.nameToArtId = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            this.nameToArtNextTryUtc = DateTime.MinValue;
            this.metaToLocalName = new Dictionary<string, string>(StringComparer.Ordinal);
            this.metaToLocalNextTryUtc = DateTime.MinValue;
        }

        private bool IsUiElementVisible(IntPtr addr)
        {
            return this.TryReadFlags(addr, out var flags) && (flags & IsVisibleMask) != 0;
        }

        private bool TryReadFlags(IntPtr addr, out uint flags)
        {
            flags = 0;
            if (addr == IntPtr.Zero) return false;
            var buf = new byte[4];
            if (!ReadProcessMemory(this.processHandle, addr + UiElementFlagsOffset, buf, (uint)buf.Length, out _)) return false;
            flags = BitConverter.ToUInt32(buf, 0);
            return true;
        }

        private IntPtr ReadPtr(IntPtr addr)
        {
            if (addr == IntPtr.Zero) return IntPtr.Zero;
            var buf = new byte[8];
            if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out _)) return IntPtr.Zero;
            return (IntPtr)BitConverter.ToInt64(buf, 0);
        }

        private bool TryReadStdVector(IntPtr addr, out IntPtr first, out IntPtr last)
        {
            first = IntPtr.Zero;
            last = IntPtr.Zero;
            var buf = new byte[16];
            if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out _)) return false;
            first = (IntPtr)BitConverter.ToInt64(buf, 0);
            last = (IntPtr)BitConverter.ToInt64(buf, 8);
            if (first == IntPtr.Zero) return false;
            ulong f = (ulong)(long)first;
            if (f < 0x10000 || f > 0x7FFFFFFFFFFFul) return false;
            if ((long)last < (long)first) return false;
            return true;
        }

        // Read the value of a NAMED state from a StateMachine component, RAW (not via GameHelper's StateMachine
        // component, which is refreshed on a slow tier for static "awake" terrain objects like the detonator and
        // so kept a STALE cached value — the bug that broke detonator-pressed detection). Layout: state VALUES are
        // std::vector<long> @ comp+0x160; state NAMES are MSVC std::strings at *(comp+0x158 → +0x10) with stride
        // 0xC0, parallel by index. Returns the matched state's value, or 0 if not found / unreadable.
        //
        // Match by NAME (not index): the detonator's "activated" is the dig-started flag (0 before the press, 1
        // after), while "light_colour" flips 0→1 merely on PLACING a charge — matching "any non-zero state" or a
        // fixed index would false-trigger on placement. Live-verified 0.5.4HF3: [activated,light_colour,3rd] =
        // [0,0,0] idle, [0,1,0] with a charge placed (not detonated), [1,2,1] after detonation.
        private long StateMachineNamedStateValue(IntPtr smComponentAddr, string stateName)
        {
            if (smComponentAddr == IntPtr.Zero) return 0;
            if (!this.TryReadStdVector(smComponentAddr + 0x160, out var first, out var last)) return 0;
            long count = ((long)last - (long)first) / 8;
            if (count <= 0 || count > 256) return 0;

            var valbuf = new byte[count * 8];
            if (!ReadProcessMemory(this.processHandle, first, valbuf, (uint)valbuf.Length, out _)) return 0;

            var p = new byte[8];
            if (!ReadProcessMemory(this.processHandle, smComponentAddr + 0x158, p, 8, out _)) return 0;
            long statesPtr = BitConverter.ToInt64(p, 0);
            if (statesPtr < 0x10000) return 0;
            if (!ReadProcessMemory(this.processHandle, (IntPtr)(statesPtr + 0x10), p, 8, out _)) return 0;
            long namesArr = BitConverter.ToInt64(p, 0);
            if (namesArr < 0x10000) return 0;

            for (int i = 0; i < count; i++)
            {
                if (this.ReadStdStringNarrow((IntPtr)(namesArr + (i * 0xC0))) == stateName)
                    return BitConverter.ToInt64(valbuf, i * 8);
            }

            return 0;
        }

        // MSVC std::string (narrow): chars inline at +0x00 when capacity < 16 (SSO), else buffer ptr @ +0x00;
        // length @ +0x10, capacity @ +0x18.
        private string ReadStdStringNarrow(IntPtr addr)
        {
            var buf = new byte[0x20];
            if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out _)) return string.Empty;
            int len = BitConverter.ToInt32(buf, 0x10);
            if (len <= 0 || len > 256) return string.Empty;
            int cap = BitConverter.ToInt32(buf, 0x18);
            if (cap < len) return string.Empty;

            if (cap < 16)
            {
                return Encoding.ASCII.GetString(buf, 0, Math.Min(len, 16));
            }

            long ptr = BitConverter.ToInt64(buf, 0);
            if (ptr < 0x10000 || ptr > 0x7FFFFFFFFFFF) return string.Empty;
            var outBuf = new byte[len];
            if (!ReadProcessMemory(this.processHandle, (IntPtr)ptr, outBuf, (uint)outBuf.Length, out _)) return string.Empty;
            return Encoding.ASCII.GetString(outBuf);
        }

        // MSVC std::wstring: buffer ptr at +0x00 (or 8 chars inline if cap < 8), length at +0x10, capacity at +0x18.
        private string ReadStdWString(IntPtr addr)
        {
            var buf = new byte[0x20];
            if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out _)) return string.Empty;

            int len = BitConverter.ToInt32(buf, 0x10);
            if (len <= 0 || len > 256) return string.Empty;
            int cap = BitConverter.ToInt32(buf, 0x18);
            if (cap < len) return string.Empty;

            if (cap < 8)
            {
                int byteLen = Math.Min(len * 2, 16);
                return Encoding.Unicode.GetString(buf, 0, byteLen);
            }

            long ptr = BitConverter.ToInt64(buf, 0);
            if (ptr < 0x10000 || ptr > 0x7FFFFFFFFFFF) return string.Empty;
            var outBuf = new byte[len * 2];
            if (!ReadProcessMemory(this.processHandle, (IntPtr)ptr, outBuf, (uint)outBuf.Length, out _)) return string.Empty;
            return Encoding.Unicode.GetString(outBuf);
        }

        // ── P/Invoke ─────────────────────────────────────────────────────

        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint dwSize, out int lpNumberOfBytesRead);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // RowAddress: the visible row UiElement (for overlay placement).
        // MetaId: BaseItemType.Id last segment (primary price key).
        // DdsArt: .dds art filename = poe.ninja image-id (fallback price key).
        // Name: localized reward name — kept only as an English-client price fallback, never shown.
        private readonly record struct Recipe(int Count, IntPtr RowAddress, string MetaId, string DdsArt, string Name, string Id);
    }
}
