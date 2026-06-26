namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Text;
    using GameHelper;
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
        private const int ScrollOffsetFieldOffset = 0x120;
        // The resolved viewport's scroll offset, re-read once per frame in DrawOverlay and added to the
        // content rows' positions (see TryGetUnscaledPosition).
        private Vector2 viewportScrollOffset;

        private const int NameWStringOffset = 0x390;
        private const int UiElementChildrenOffset = 0x10;
        private const int UiElementFlagsOffset = 0x180;
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

        private IntPtr processHandle = IntPtr.Zero;
        private int handlePid;

        private readonly List<Recipe> recipes = new();
        private readonly PriceCache priceCache = new();
        private DateTime nextAutoRefreshCheckUtc = DateTime.MinValue;

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

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");
        private string PriceCachePathname => Path.Join(this.DllDirectory, "config", "prices.json");

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

            var fresh = this.priceCache.TryLoadFromDisk(this.PriceCachePathname, this.Settings.CacheTtlMinutes);
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
            ImGui.TextWrapped("RunecraftHelper: while the in-game Runeshape Combinations panel is open, the " +
                              "poe.ninja Exalted price is drawn on the right edge of each visible reward row. " +
                              "The reward name shown is the game's own (any client language).");

            ImGui.Spacing();
            ImGui.Separator();

            ImGui.InputText("League", ref this.Settings.League, 64);
            ImGui.SliderInt("Refresh interval (min)", ref this.Settings.CacheTtlMinutes, 5, 60);

            int colorMode = (int)this.Settings.ColorMode;
            if (ImGui.Combo("Price color", ref colorMode,
                    "Off\0Relative (vs. median on screen)\0Absolute (Exalted thresholds)\0"))
                this.Settings.ColorMode = (RewardColorMode)colorMode;

            ImGui.SliderFloat("Price X offset", ref this.Settings.OverlayXOffset, -400f, 400f, "%.0f px");

            ImGui.Checkbox("Highlight locked recipe (sealed monolith)", ref this.Settings.HighlightLockedRecipeInPanel);
            if (this.Settings.HighlightLockedRecipeInPanel)
                ImGui.TextDisabled("Gold border on the panel row of a sealed monolith's locked-in recipe.");

            ImGui.Spacing();
            ImGui.SeparatorText("Monolith rewards");
            ImGui.Checkbox("Show monolith reward window", ref this.Settings.ShowMonolithRewards);
            if (this.Settings.ShowMonolithRewards)
            {
                ImGui.SliderFloat("Hide rewards under (ex)", ref this.Settings.MonolithRewardsMinExalted, 0f, 50f, "%.0f ex");

                ImGui.InputFloat("Highlight threshold (ex)", ref this.Settings.MonolithHighlightThreshold, 1f, 10f, "%.0f");
                if (this.Settings.MonolithHighlightThreshold < 0f) this.Settings.MonolithHighlightThreshold = 0f;
                ImGui.TextDisabled("Tints a monolith's header by its best reward value: green at/above the\n" +
                    "threshold, yellow from 0.6× up to it, none below. 0 = off (use Price color).");

                ImGui.Checkbox("Draw value on map overlay", ref this.Settings.DrawMonolithValueOnMap);
                if (this.Settings.DrawMonolithValueOnMap)
                {
                    ImGui.TextDisabled("Paints each monolith's best value (ex) on the large-map overlay, like\n" +
                        "Radar's socket count (tinted by the threshold above). If it doesn't line\n" +
                        "up with the monolith, match these to your Radar large-map settings:");
                    ImGui.Checkbox("Hide map values while Combinations panel open", ref this.Settings.HideMapValueWhenPanelOpen);
                    ImGui.SliderFloat("Map value scale", ref this.Settings.MapValueScaleMultiplier, 0.1f, 3f, "%.2f");
                    ImGui.SliderFloat("Map value X offset", ref this.Settings.MapValueXOffset, -200f, 200f, "%.0f");
                    ImGui.SliderFloat("Map value Y offset", ref this.Settings.MapValueYOffset, -200f, 200f, "%.0f");
                }

                ImGui.TextDisabled("For each nearby monolith: its anchor rune + hole, and the candidate\n" +
                    "recipes (from Expedition2Recipes.dat, filtered by the anchor) with\n" +
                    "poe.ninja Exalted prices. Reads the anchor off the persistent device,\n" +
                    "so it works even out of the network bubble.");
            }

            ImGui.Checkbox("Show monolith debug window", ref this.Settings.ShowWindow);
            if (this.Settings.ShowWindow)
                ImGui.TextDisabled("Pick a monolith and dump everything the offer rule uses (anchor/p/N,\n" +
                    "sockets-vs-station N, area level, addresses, and every offered recipe with\n" +
                    "row/size/gate/category/reward/levels/rune pattern). 'Copy report' → clipboard\n" +
                    "for reporting a game-vs-plugin mismatch.");

            ImGui.Spacing();

            var status = this.priceCache.Status;
            var lastSync = this.priceCache.LastSyncUtc;
            string statusText = status switch
            {
                PriceSyncStatus.Syncing => "syncing…",
                PriceSyncStatus.Ready => lastSync == DateTime.MinValue
                    ? "ready (no data yet)"
                    : $"updated {FormatRelative(lastSync)} ago",
                PriceSyncStatus.Error => $"error: {this.priceCache.LastError}",
                _ => "idle",
            };

            ImGui.Text($"Status: {statusText}");
            ImGui.Text($"Items cached: {this.priceCache.PriceCount}");
            if (this.priceCache.DivineToExaltedRate > 0)
                ImGui.Text($"1 Divine = {this.priceCache.DivineToExaltedRate:F2} Exalted");

            ImGui.BeginDisabled(status == PriceSyncStatus.Syncing);
            if (ImGui.Button("Refresh now"))
                this.priceCache.StartRefresh(this.Settings.League, this.PriceCachePathname);
            ImGui.EndDisabled();
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
                this.Settings.DrawMonolithValueOnMap || wantLockHighlight)
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

                ParseNameAndCount(raw, out var count, out var name);
                this.nameToArtId.TryGetValue(name.Trim(), out var keys);
                // RowAddress is the visible row UiElement — re-resolved every frame here, so the
                // overlay always draws against fresh (post-scroll) screen coordinates. Name is kept
                // only as a localized-name price fallback for English clients; it is never displayed.
                this.recipes.Add(new Recipe(count, row, keys.MetaId ?? string.Empty, keys.DdsArt ?? string.Empty, name));
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

            if (this.priceCache.Status == PriceSyncStatus.Syncing) return;
            var ttl = TimeSpan.FromMinutes(Math.Max(1, this.Settings.CacheTtlMinutes));
            if (this.priceCache.LastSyncUtc != DateTime.MinValue && now - this.priceCache.LastSyncUtc < ttl) return;

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
        private readonly List<(Vector2 Pos, Vector2 Size, double Total, bool Locked)> overlayRows = new();

        // Priced rows for the current frame (RowAddress + total), built BEFORE geometry is resolved so
        // the Relative-mode median is computed over the full priced set, independent of whether any
        // individual row's screen geometry read succeeds this frame. Locked: see overlayRows.
        private readonly List<(IntPtr Addr, double Total, bool Locked)> pricedScratch = new();

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
                    this.pricedScratch.Add((r.RowAddress, unit * Math.Max(1, r.Count), locked));
                }
            if (this.pricedScratch.Count == 0) return;

            double median = 0;
            if (this.Settings.ColorMode == RewardColorMode.Relative)
                median = MedianOf(this.pricedScratch);

            // 2) Resolve each row's screen geometry, falling back to its last-good (pos, size) for a few
            //    frames on a read miss so the price doesn't blink out or teleport on a single bad read.
            foreach (var (addr, total, locked) in this.pricedScratch)
            {
                if (!this.TryResolveRowGeometry(addr, out var pos, out var size)) continue;
                this.overlayRows.Add((pos, size, total, locked));
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
                if (row.Locked)
                {
                    // Sealed monolith: ring the locked-in recipe's price box in gold so it's obvious
                    // which of the listed combinations the monolith will actually produce.
                    drawList.AddRect(at - bgPad, at + ts + bgPad, ColorGold, 3f * k, ImDrawFlags.None, 2f * k);
                }

                drawList.AddText(font, fontPx, at + new Vector2(1f, 1f), ColorShadow, text);
                drawList.AddText(font, fontPx, at, color, text);
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

        private static double MedianOf(List<(IntPtr Addr, double Total, bool Locked)> rows)
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
            // trailing digits with no "Level" marker. Match ONLY on dds-art + level (e.g.
            // "SkillGemUncut19" + art "UncutSkillGem" → "UncutSkillGem19"). Never fall through: the
            // bare dds-art key holds an arbitrary level's price, and base/quest variants (no digit)
            // aren't tradable at all.
            if (IsUncutGem(r.MetaId))
            {
                int gemLevel = UncutGemLevel(r.MetaId);
                if (gemLevel >= 0 && !string.IsNullOrEmpty(r.DdsArt) &&
                    this.priceCache.TryGetPriceByArtId(r.DdsArt + gemLevel.ToString(), out unit) && unit > 0)
                    return true;
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
            if (!string.IsNullOrEmpty(r.MetaId))
            {
                this.BuildMetaIdToEnglishIfNeeded();
                if (this.metaIdToEnglish.TryGetValue(r.MetaId, out var eng) &&
                    this.priceCache.TryGetExaltedPrice(eng, out unit) && unit > 0)
                    return true;
            }

            if (this.priceCache.TryGetExaltedPrice(r.Name, out unit) && unit > 0)
                return true;
            unit = 0;
            return false;
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
        private readonly record struct Recipe(int Count, IntPtr RowAddress, string MetaId, string DdsArt, string Name);
    }
}
