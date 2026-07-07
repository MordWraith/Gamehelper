namespace Atlas
{
    using GameHelper;
    using GameHelper.Localization;
    using GameHelper.Plugin;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using GameOffsets.Objects.UiElement;
    using ImGuiNET;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Text;

    public sealed class Atlas : PCore<AtlasSettings>
    {
        private const uint SearchLineColor = 0xFFFFFFFF;   // white  — routes to search hits
        private const uint UniqueLineColor = 0xFFFF00FF;   // magenta — routes to unique maps
        private const uint LineageLineColor = 0xFF00E000;  // green  — routes to 'lineage' maps
        private const uint ArbiterLineColor = 0xFF0000FF;  // red    — routes to 'arbiter' maps
        private const uint CompletedNodeDotColor = 0xFF00FF00;
        private const uint DotOutlineColor = 0xFF000000;

        private const int ChannelGrid = 0;
        private const int ChannelLines = 1;
        private const int ChannelDots = 2;
        private const int ChannelLabels = 3;

        // Atlas connection (edge) list — a flat StdVector on the atlas-panel UiElement. Each entry
        // is {int unknown; grid Source; grid Target}; Source/Target are grid coords matched against
        // each node's grid (node+0x320). Verified live in GameHelper2-main for PoE2 0.5.x.
        private const int AtlasConnectionsVectorOffset = 0x5A8;

        // fp of the "you are here" marker child (shares the node-list container fp, not the
        // map-node fp 0x542EF3). Used to locate the player's current atlas node by screen position.
        private const uint AtlasCurrentNodeFp = 0x502EF3;

        // fp of a MIST-shrouded map node (King in the Mists): the map-node fp 0x542EF3 with
        // bit 20 cleared. Data-block layout is identical to a regular node. Upstream's core atlas
        // reader keeps only 0x542EF3, silently dropping these from GameUi.AtlasMaps — so when the
        // cache is fed from the core list, AppendMistNodesMissedByCore() sweeps them back in.
        // (Kept plugin-side on purpose: no core edit to re-apply after an upstream sync.)
        private const uint AtlasMistNodeFp = 0x442EF3;

        // ── Uncharted Waters ships (sea chunk reveal buttons) ──────────────────────────────
        // A sea "ship" is NOT an atlas node: it's an EndgameRegionActionButton widget living in
        // the same children list as the map nodes (row 0=Breach, 1=Forest, 2=Ocean/ship,
        // 3=Tower). Using a logbook on a ship reveals the ship's whole 16x16 atlas chunk, and
        // the hidden nodes of that chunk are already materialized client-side with their map
        // assigned — so the reveal set is known in advance. Verified live 0.5.4BHF3 (2026-07);
        // see obsidian poe2/Atlas.md §"Sea / ships".
        private const int RegionButtonRowPtrOffset = 0x320;   // ptr → EndgameRegionActionButtons row
        private const int RegionButtonGridOffset = 0x330;     // int32 x, int32 y (button grid coords)
        private const int RegionButtonRowIndexOffset = 0x338; // int32 row index; 2 = Ocean/ship
        private const int RegionButtonOceanRow = 2;
        // icons\UnchartedShip.png — the ship graphic drawn on fog ships. Game asset:
        // Art/2DArt/UIImages/InGame/MapQuickUseButton/QuickUseItemIconLogbook (the dat row's
        // QuickUseIcon — despite the name, the art is the framed ship).
        private const string FogShipIconName = "UnchartedShip";

        // ── Ritual atlas line (the line drawn to the Crux of Nothingness) ───────────────────
        // When a node is picked onto the line, the game sets flag bit 20 on the node widget and
        // attaches a text child at +0x3B8 whose label already holds the LOCALIZED Rite-mod lines
        // (rolled client-side, translated via stat_descriptions.csd). We just read that text.
        // See obsidian poe2/Ritual.md; Ghidra AtlasPanel_ritualLineToggleNode / FUN_140b18010.
        private const uint RitualLineFlagMask = 0x100000u;  // node widget +0x180 bit 20 = "on the ritual line"
        private const int RitualModsChildOffset = 0x3B8;    // ptr → text child carrying the Rite-mod lines
        private const int TextElementTextOffset = 0x4C0;    // std::wstring on a game text element (uitree guide)
        // Ritual-line state on the atlas panel (== the node-list container, verified live 0.5.4):
        private const int PanelLineModeOffset = 0x637;      // u8 bool: ritual line mode (page mode 6) active
        private const int PanelLineIdOffset = 0x63C;        // u32 line id/seed word (TinyMT input word 0)
        private const int PanelPendingVecOffset = 0x648;    // std::vector<(i32,i32)> candidate grids
        private const int PanelCommittedVecOffset = 0x660;  // std::vector<(i32,i32)> committed line grids
        // Precomputed next-candidate table (AtlasPanel_ritualLineNextCandidates does a binary search
        // here): std::vector begin@+0x590 / end@+0x598, entry stride 0x44 = 17 int32:
        //   [0]=nodeX [1]=nodeY, then 5 candidates × (x,y,extra) = ints [2..16]. Sorted by x<<16|y.
        // The roll's candIdx = the clicked node's rank among these 5 (minus (0,0) / already-committed)
        // sorted lexicographically by (x,y). See obsidian poe2/Ritual.md.
        private const int PanelCandTableBeginOffset = 0x590;
        private const int PanelCandTableEndOffset = 0x598;
        private const int CandTableEntryStride = 0x44;   // bytes; 17 int32
        private const int CandTableMaxCandidates = 5;
        // Node widget +0x300 → per-map dat-row ptr; row +0x7C = special-map category id
        // (0 normal, 1 unique, 3 hideout, 5/7/8/13 citadels & league bosses, 6 tower — audited
        // live over the whole atlas, re-tools/ritual/reachcheck_audit.py). The game's reach
        // check (ritualLineReachCheck 140b775f0) refuses ANY nonzero category, so this — not a
        // maps.json type/tag guess — is the authoritative "line can't pass here" discriminator.
        private const int NodeDatRowPtrOffset = 0x300;
        private const int DatRowSpecialCategoryOffset = 0x7C;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct AtlasConnectionEdge
        {
            public int Unknown;
            public StdTuple2D<int> Source;
            public StdTuple2D<int> Target;
        }

        private string SettingPathname => Path.Join(DllDirectory, "config", "settings.txt");
        private string MapGroupsPathname => Path.Join(DllDirectory, "config", "mapgroups.json");
        private string MapRatingsPathname => Path.Join(DllDirectory, "config", "mapratings.json");
        private string RitualRollLogPathname => Path.Join(DllDirectory, "config", "ritual_roll_log.jsonl");
        private string NewGroupName = string.Empty;

        // ── UI-chrome localization (GameHelper PluginLocalization; Localization\<lang>.json next to the
        // dll). Lazy so it's ready even if a Draw* runs before OnEnable. English literal fallback at each
        // call site keeps the plugin working with no dictionaries present. This is the plugin's own UI
        // text; it is SEPARATE from Settings.Language, which selects the game map-name data language. ──
        private PluginLocalization loc;
        private PluginLocalization Loc => this.loc ??= new PluginLocalization(this.DllDirectory);
        private string L(string key, string fallback) => this.Loc.T(key, fallback);

        // ── Map-name (data) language. "auto" (default) tracks GameHelper's UI language; any other value
        // is an explicit maps.json "translates" token override. See the Settings language combo. ──
        private const string AutoLang = "auto";
        private static bool IsAutoLang(string s) =>
            string.IsNullOrWhiteSpace(s) || s.Equals(AutoLang, StringComparison.OrdinalIgnoreCase);

        // maps.json "translates" token for GameHelper's current UI language (used when Language == auto).
        // ChineseSimplified has no map translations → falls back to traditional chinese (then English via
        // ApplyContentLanguage). Thai map values are often English in the data; that's the data's choice.
        private static string UiLanguageMapToken() => OverlayLocalization.CurrentLanguage switch
        {
            OverlayLanguage.Russian => "russian",
            OverlayLanguage.French => "french",
            OverlayLanguage.German => "german",
            OverlayLanguage.SpanishSpain => "spanish",
            OverlayLanguage.Japanese => "japanese",
            OverlayLanguage.Korean => "korean",
            OverlayLanguage.PortugueseBrazil => "portuguese",
            OverlayLanguage.Thai => "thai",
            OverlayLanguage.ChineseTraditional => "traditional chinese",
            OverlayLanguage.ChineseSimplified => "traditional chinese",
            _ => "english",
        };

        // Effective map-name token: resolves "auto" to the UI-language token; else the explicit override.
        private string EffectiveLanguage => IsAutoLang(Settings?.Language) ? UiLanguageMapToken() : Settings.Language;

        // Last map-name token actually applied to the content overlays; lets DrawUI re-slice live when the
        // GH UI language changes while Language == auto. Set by ApplyContentLanguage.
        private static string appliedContentLang = null;
        // Free-text filters for the "Add content…" / "Add map…" (content-route) / map-group pickers
        // (one combo open at a time).
        private string ContentAddFilter = string.Empty;
        private string MapAddFilter = string.Empty;
        private string RatingFilter = string.Empty;
        private string MapGroupAddFilter = string.Empty;
        // Distinct map display names for the picker, as (canonical English name, localized name), sorted
        // by the localized name. Rebuilt when the UI language changes (MapPickCacheLang tracks it).
        private static readonly List<(string English, string Localized)> MapPickCache = new();
        private static string MapPickCacheLang = null;

        private static readonly Dictionary<string, ContentInfo> MapTags = [];
        private static readonly Dictionary<string, ContentInfo> MapPlain = [];
        private static readonly Dictionary<byte, BiomeInfo> Biomes = [];
        // Internal WorldArea MapId (e.g. "MapUniqueMerchant03_Beach") → map info (display name +
        // type/group/tags), loaded from json/maps.json (generated from WorldAreaNames.tsv).
        // Multiple internal ids can map to the same display name, so searching/grouping by the
        // display name highlights every variant at once; group/tags drive category highlights.
        private static readonly Dictionary<string, MapInfo> MapInfos = new(StringComparer.OrdinalIgnoreCase);
        // Languages available in maps.json "translates" (union across entries), for the settings dropdown.
        private static readonly List<string> AvailableLanguages = new();
        // Class-2 (badge) content id → display name, loaded from json/mapcontent.json (generated from
        // EndgameMapContent.tsv: id = row+100, plus special 1000=Corruption). Keyed by the low 16 bits
        // of badge+0x188. See docs/re-findings.md §2.10.3.
        private static readonly Dictionary<uint, string> BadgeContentNames = new();
        // Content display-name → icon basename (the AtlasIcon/PassiveArt asset, sans extension), from
        // mapcontent.json. Drives optional in-game-style icons; works for both badge- and token-named
        // content since both resolve to the same EndgameMapContent names.
        private static readonly Dictionary<string, string> NameToIcon = new(StringComparer.OrdinalIgnoreCase);
        // Content display-name → effect description (EndgameMapContent.Description, markup-stripped),
        // for the on-hover tooltip.
        private static readonly Dictionary<string, string> NameToDesc = new(StringComparer.OrdinalIgnoreCase);
        // Localized overlays for the plugin's selected language, keyed by the canonical ENGLISH name
        // (which stays the lookup key for icons/hit-tests). Rebuilt by ApplyContentLanguage() whenever
        // Settings.Language changes. Empty for English (falls through to the canonical name/desc).
        private static readonly Dictionary<string, string> NameToLocalizedName = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> NameToLocalizedDesc = new(StringComparer.OrdinalIgnoreCase);
        // English name → its raw localization table ({lang-token → {name,desc}}), parsed from
        // mapcontent.json once at load; ApplyContentLanguage() slices it for the active language.
        private static readonly Dictionary<string, Dictionary<string, LocalizedText>> ContentTranslations =
            new(StringComparer.OrdinalIgnoreCase);
        // Player-selectable content names for the Map Content route-group editor (real content only:
        // the DNT/hidden "[...]" placeholders are filtered out). Sorted; built in LoadMapContent.
        private static readonly List<string> ContentChoices = new();
        // Loaded icon textures, keyed by basename: (ImGui texture ptr, width, height). Zero ptr = the
        // icons\<basename>.png file is absent (negative-cached so we don't stat it every frame).
        private static readonly Dictionary<string, (IntPtr Ptr, int W, int H)> IconCache = new();

        private sealed class MapContentEntry
        {
            public string Name { get; set; }
            public string Icon { get; set; }
            public string Desc { get; set; }
            // lang-token (lowercase, e.g. "russian") → localized name/desc. Optional.
            public Dictionary<string, LocalizedText> Translates { get; set; }
        }

        private sealed class LocalizedText
        {
            public string Name { get; set; }
            public string Desc { get; set; }
        }

        public static IntPtr Handle { get; set; }
        private static int _handlePid;

        // ── Per-node static-data cache ──────────────────────────────────────
        // Reading + chasing pointers for all ~1700 atlas nodes every frame was the FPS killer
        // (tens of thousands of cross-process reads per frame). The slow-changing per-node data
        // (map id, biome, completed/accessible state, content badges) is cached and refreshed on
        // an interval instead; each frame we only read the node's UiElementBase for a live screen
        // position (so panning/zoom stay exact) and draw the nodes that are actually on-screen.
        private struct NodeData
        {
            public IntPtr Address;
            public int ChildIndex;          // index in the atlas-panel child list (the node number used for RE/debug)
            public string InternalId;       // internal WorldArea MapId, e.g. "MapUniqueMerchant03_Beach"
            public string MapName;          // display name for the selected language (falls back to English name / id)
            public bool Drawable;           // precomputed: MapName is non-empty and printable (avoids per-frame rune scan)
            public MapInfo MapInfo;         // maps.json classification (type/group/tags); null when unmapped
            public byte BiomeId;
            public AtlasNodeState State;
            public List<string> RawContents;
            public int ContentCount;        // number of content markers (node[0][0] children); reliable for all nodes
            public uint[] ContentTokens;    // raw per-node content tokens (StdVector<u32> @ element+0x350); see re-findings §2.10
            public uint[] BadgeContentIds;  // class-2 badge content ids (badge+0x188); see re-findings §2.10.3
            public string[] ContentNames;   // resolved + filtered + de-duped display names (precomputed in cache, not per-frame)
            public StdTuple2D<int> GridPosition;
            public int Rating;              // 0..10 from config/mapratings.json, keyed by MapInfo.Name (-1 = unrated)
            public bool RitualSpecial;      // game's ritual reach-check refuses this node (dat-row category != 0)
        }
        private readonly List<NodeData> nodeCache = new();
        // Uncharted Waters ships found in the atlas children list, with the button's grid coords
        // (== the grid of the chunk node the button snapped to) and the 16x16 chunk they chart
        // (grid >> 4). Refreshed with nodeCache; empty while both ship toggles are off.
        private readonly List<(IntPtr Address, int GridX, int GridY, int ChunkX, int ChunkY)> shipCache = new();
        // Fog-ship icons drawn THIS frame (chunk + screen rect), so the leyline hover can
        // hit-test them. Rebuilt every frame by DrawFogShips; cleared when the pass is off.
        private readonly List<((int X, int Y) Chunk, Vector2 Center, float Half)> fogShipIcons = new();
        // Addresses of "you are here" marker candidates (fp 0x502EF3), refreshed with nodeCache.
        private readonly List<IntPtr> markerCandidates = new();
        private int cacheFrameCounter = int.MaxValue;   // force refresh on first frame
        private int cachedAtlasCount = -1;
        private const int CacheRefreshFrames = 20;       // rebuild static data ~3×/sec at 60fps

        // Per-frame memo for GetFinalTopLeft's parent-chain reads: every atlas node shares the
        // same ancestors, so without this each node re-reads the whole chain. Cleared each frame.
        private static readonly Dictionary<IntPtr, UiElementBaseOffset> frameBaseCache = new();
        // Per-frame memo of each parent container's accumulated top-left. Atlas nodes share one parent
        // chain, so this is computed once per frame and every node's position becomes O(1) math off it
        // (instead of walking the whole ancestor chain per node). Cleared each frame.
        private static readonly Dictionary<IntPtr, Vector2> parentOffsetCache = new();


        public override void OnDisable()
        {
            UniversalFont.Restore();
            CloseAndResetHandle();
        }

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(SettingPathname))
            {
                var content = File.ReadAllText(SettingPathname);
                var serializerSettings = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
                Settings = JsonConvert.DeserializeObject<AtlasSettings>(content, serializerSettings);
            }

            // Migrate out-of-range ship icon sizes (old default was 28, slider is now 32..96).
            if (Settings.ShipIconSize is < 32f or > 96f)
                Settings.ShipIconSize = 64f;

            LoadMapGroups();
            LoadBiomeMap();
            LoadContentMap();
            LoadMapContent();
            LoadMaps();
            LoadMapRatings();
            EnsureBuiltInContentGroup();

            if (Settings.UniversalFont)
                UniversalFont.Apply(DllDirectory);
        }

        public override void SaveSettings()
        {
            var dir = Path.GetDirectoryName(SettingPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var settingsData = JsonConvert.SerializeObject(Settings, Formatting.Indented);
            File.WriteAllText(SettingPathname, settingsData);

            SaveMapGroups();
            SaveMapRatings();
        }

        // MapGroups live in their own config/mapgroups.json (kept out of settings.txt via [JsonIgnore]).
        // Loads that file when present; otherwise migrates the MapGroups array from an older settings.txt
        // so customizations survive the split, falling back to the constructor defaults.
        private void LoadMapGroups()
        {
            if (Settings == null)
                return;

            if (File.Exists(MapGroupsPathname))
            {
                var groups = JsonConvert.DeserializeObject<List<MapGroupSettings>>(File.ReadAllText(MapGroupsPathname));
                if (groups != null)
                    Settings.MapGroups = groups;
                return;
            }

            if (File.Exists(SettingPathname))
            {
                try
                {
                    var root = JObject.Parse(File.ReadAllText(SettingPathname));
                    if (root.TryGetValue(nameof(AtlasSettings.MapGroups), out var token) && token.Type == JTokenType.Array)
                    {
                        var groups = token.ToObject<List<MapGroupSettings>>();
                        if (groups != null && groups.Count > 0)
                            Settings.MapGroups = groups;
                    }
                }
                catch (JsonException) { /* malformed legacy settings — keep constructor defaults */ }
            }

            SaveMapGroups();   // materialize the new file so subsequent loads use it directly
        }

        private void SaveMapGroups()
        {
            if (Settings?.MapGroups == null)
                return;

            var dir = Path.GetDirectoryName(MapGroupsPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(MapGroupsPathname, JsonConvert.SerializeObject(Settings.MapGroups, Formatting.Indented));
        }

        // Map ratings live in config/mapratings.json (dict: canonical English map name → 0..10).
        // First run (no config file) seeds from the bundled json\mapratings.json defaults.
        private void LoadMapRatings()
        {
            if (Settings == null)
                return;

            Settings.MapRatings.Clear();
            try
            {
                var path = File.Exists(MapRatingsPathname)
                    ? MapRatingsPathname
                    : Path.Join(DllDirectory, "json", "mapratings.json");
                if (!File.Exists(path))
                    return;

                var ratings = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(path));
                if (ratings == null)
                    return;

                foreach (var kv in ratings)
                    if (!string.IsNullOrWhiteSpace(kv.Key))
                        Settings.MapRatings[kv.Key] = Math.Clamp(kv.Value, 0, 10);
            }
            catch (JsonException) { /* malformed file — start with no ratings */ }
        }

        private void SaveMapRatings()
        {
            if (Settings?.MapRatings == null)
                return;

            var dir = Path.GetDirectoryName(MapRatingsPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var ordered = new SortedDictionary<string, int>(Settings.MapRatings, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(MapRatingsPathname, JsonConvert.SerializeObject(ordered, Formatting.Indented));
        }

        public override void DrawSettings()
        {
            #region SettingsUI
            // Collapsed-by-default top section grouping the rarely-touched setup toggles
            // (input layout, font, map-name language). CollapsingHeader matches GameHelper's
            // General-tab section style (full-width bar).
            if (ImGui.CollapsingHeader(this.Loc.Title("atlas.settings", "Settings", "atlas_settings")))
            {
                ImGui.SeparatorText(this.L("atlas.input", "Input"));
                if (ImGui.Checkbox(this.L("atlas.controller_mode", "Controller Mode"), ref Settings.ControllerMode))
                    nodeCache.Clear(); // re-resolve the panel on the other layout next frame
                ImGuiHelper.ToolTip(this.L("atlas.controller_mode_hint", "GameHelper auto-detects controller mode, so you normally don't need this. " +
                    "Tick it only to FORCE the controller Atlas layout if auto-detect ever fails. Either way the " +
                    "plugin falls back to the other layout when the selected one isn't found. In controller mode " +
                    "the overlay also stays visible while the inventory is open."));

                ImGui.SeparatorText(this.L("atlas.font", "Font"));
                if (ImGui.Checkbox(this.L("atlas.universal_font", "Universal font (render map names in any language)"), ref Settings.UniversalFont))
                {
                    if (Settings.UniversalFont)
                        UniversalFont.Apply(DllDirectory);
                    else
                        UniversalFont.Restore();
                }
                ImGuiHelper.ToolTip(this.L("atlas.universal_font_hint", "Loads the plugin's bundled DejaVuSans + GNU Unifont into the overlay so " +
                    "any-language map names render without configuring a font in GameHelper. Affects the whole overlay; " +
                    "turning it off restores GameHelper's configured font."));

                ImGui.SeparatorText(this.L("atlas.map_name_language", "Map name language"));
                bool isAuto = IsAutoLang(Settings.Language);
                string autoLabel = this.L("atlas.lang_auto", "Auto (follow UI language)");
                // Preview shows "Auto" (with the resolved token) or the explicit override token.
                string preview = isAuto ? $"{autoLabel} — {UiLanguageMapToken()}" : Settings.Language;
                if (ImGui.BeginCombo(this.L("atlas.language", "Language"), preview))
                {
                    // Auto = track GameHelper's UI language; re-resolves live when the UI language changes.
                    if (ImGui.Selectable(autoLabel, isAuto) && !isAuto)
                    {
                        Settings.Language = AutoLang;
                        ApplyContentLanguage(EffectiveLanguage);
                        nodeCache.Clear();
                    }
                    if (isAuto)
                        ImGui.SetItemDefaultFocus();
                    foreach (var lang in AvailableLanguages)
                    {
                        bool selected = !isAuto && string.Equals(lang, Settings.Language, StringComparison.OrdinalIgnoreCase);
                        if (ImGui.Selectable(lang, selected) && !selected)
                        {
                            Settings.Language = lang;
                            ApplyContentLanguage(lang); // re-slice content name/desc overlays for the new language
                            nodeCache.Clear(); // force a node-cache rebuild next frame so labels re-localize live
                        }
                        if (selected)
                            ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
                ImGuiHelper.ToolTip(this.L("atlas.language_hint", "Display language for map-node names (from maps.json 'translates'). " +
                    "Auto follows GameHelper's UI language; pick a specific language to match your game client instead. " +
                    "Changing it re-labels nodes immediately. Map Group names are matched in the selected language."));

                ImGui.SeparatorText(this.L("atlas.draw_lines", "Draw Lines"));
                if (ImGui.TreeNode(this.Loc.Title("atlas.draw_lines_settings", "Draw Lines Settings", "atlas_draw_lines")))
                {
                    ImGui.Checkbox(this.L("atlas.shortest_path", "Shortest Path"), ref Settings.RouteLinesThroughNodes);
                    ImGuiHelper.ToolTip(this.L("atlas.shortest_path_hint", "Route lines follow the shortest hop-path through the revealed atlas edges " +
                        "(from the nearest accessible node). When off, a straight line is drawn instead."));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150);
                    ImGui.SliderFloat(this.L("atlas.path_thickness", "Path Thickness"), ref Settings.PathLineThickness, 1.0f, 8.0f);
                    ImGui.SetNextItemWidth(150);
                    ImGui.SliderFloat(this.L("atlas.arrow_spacing", "Arrow spacing"), ref Settings.RouteArrowSpacing, 6.0f, 18.0f);
                    ImGuiHelper.ToolTip(this.L("atlas.arrow_spacing_hint", "Gap between the direction arrows drawn along a route (higher = more spread out)."));
                    ImGui.SetNextItemWidth(150);
                    ImGui.SliderFloat(this.L("atlas.search_route_range", "Search route range"), ref Settings.DrawSearchInRange, 1.0f, 10.0f);
                    ImGui.TreePop();
                }

                ImGui.SeparatorText(this.L("atlas.debug", "Debug"));
                ImGui.Checkbox(this.L("atlas.show_node_index", "Show Node Index (debug/RE)"), ref Settings.ShowNodeIndex);
                ImGuiHelper.ToolTip(this.L("atlas.show_node_index_hint", "DEBUG: draws each node's child-index (its number in the atlas-panel child list) as a badge " +
                    "to the left of the map name, so a node referenced by number is easy to locate on-screen."));
            }

            // Collapsed-by-default Display section: node-visibility filters, biome border, label
            // layout, and the content-icon overlay.
            if (ImGui.CollapsingHeader(this.Loc.Title("atlas.display", "Display", "atlas_display")))
            {
                ImGui.SeparatorText(this.L("atlas.atlas_settings", "Atlas Settings"));
                ImGui.Checkbox(this.L("atlas.hide_completed", "Hide Completed Maps"), ref Settings.HideCompletedMaps);
                ImGui.Checkbox(this.L("atlas.hide_not_accessible", "Hide Not Accessible Maps"), ref Settings.HideNotAccessibleMaps);
                ImGui.Checkbox(this.L("atlas.hide_available", "Hide Available Maps"), ref Settings.HideAvailableMaps);
                ImGuiHelper.ToolTip(this.L("atlas.hide_available_hint", "Hide maps that are accessible/runnable right now. Route/search targets stay visible."));
                ImGui.Checkbox(this.L("atlas.show_connections", "Show node connections"), ref Settings.ShowAtlasGraph);
                ImGuiHelper.ToolTip(this.L("atlas.show_connections_hint", "Draw the atlas connection graph — a faint line along every edge between adjacent map nodes, beneath the labels and routes."));
                if (Settings.ShowAtlasGraph)
                {
                    ColorSwatch("##AtlasGraphColor", ref Settings.AtlasGraphLineColor);
                    ImGui.SameLine();
                    ImGui.Text(this.L("atlas.connection_color", "Connection Color"));
                    ImGui.SetNextItemWidth(150);
                    ImGui.SliderFloat(this.L("atlas.connection_thickness", "Connection Thickness"), ref Settings.AtlasGraphThickness, 0.5f, 5.0f);
                }

                ImGui.Checkbox(this.L("atlas.show_biome_border", "Show Biome Border"), ref Settings.ShowBiomeBorder);
                if (Settings.ShowBiomeBorder)
                    if (ImGui.TreeNode(this.Loc.Title("atlas.biome_settings", "Biome Settings", "atlas_biome")))
                    {
                        ImGui.SetNextItemWidth(180);
                        ImGui.SliderFloat(this.L("atlas.biome_border_thickness", "Biome Border Thickness"), ref Settings.BiomeBorderThickness, 1.0f, 6.0f);

                        if (ImGui.BeginTable("split", 3))
                        {
                            foreach (var biome in Biomes)
                            {
                                ImGui.TableNextColumn();
                                var id = biome.Key;
                                var info = biome.Value;

                                if (!Settings.BiomeOverrides.TryGetValue(id, out var ov))
                                {
                                    ov = new ContentOverride();
                                    Settings.BiomeOverrides[id] = ov;
                                }

                                bool show = ov.Show ?? info.Show;
                                if (ImGui.Checkbox($"##Show##{id}", ref show))
                                {
                                    ov.Show = show;
                                    ApplyBiomeOverrides();
                                }

                                var border = ov.BorderColor ?? info.BdColor;
                                ImGui.SameLine();
                                ColorSwatch($"{this.L("atlas.border_color", "Border Color")}##Biome{id}", ref border);
                                if (!ColorsEqual(border, ov.BorderColor ?? info.BdColor))
                                {
                                    ov.BorderColor = border;
                                    ApplyBiomeOverrides();
                                }

                                var label = string.IsNullOrWhiteSpace(info.Label) ? $"Biome {id}" : info.Label;
                                ImGui.SameLine();
                                ImGui.Text(label);
                            }
                            ImGui.EndTable();
                        }

                        ImGui.TreePop();
                    }

                if (ImGui.TreeNode(this.Loc.Title("atlas.layout_settings", "Layout Settings", "atlas_layout")))
                {
                    var nudge = Settings.AnchorNudge;
                    if (ImGui.SliderFloat2(this.L("atlas.layout_nudge", "Layout Nudge (px)"), ref nudge, -60f, 60f))
                        Settings.AnchorNudge = nudge;
                    ImGui.SliderFloat(this.L("atlas.scale_multiplier", "Scale Multiplier"), ref Settings.ScaleMultiplier, 0.5f, 3.0f);
                    ImGui.TreePop();
                }

                // Per-map rating (0 = normal … 10 = terrible): colored number pill right of the map
                // name. Ratings are keyed by canonical English name (config/mapratings.json) while
                // the table lists maps in the UI language, so they work on any client language.
                if (ImGui.TreeNode(this.Loc.Title("atlas.maps_rating", "Maps rating", "atlas_maps_rating")))
                {
                    ImGui.Checkbox(this.L("atlas.map_rating_show", "Show maps rating"), ref Settings.ShowMapRating);
                    ImGuiHelper.ToolTip(this.L("atlas.map_rating_show_hint",
                        "Draws each rated map's rating (0 = normal … 10 = terrible) as a colored pill " +
                        "to the right of the map name: green → yellow → red."));

                    ImGui.SetNextItemWidth(220);
                    ImGui.InputTextWithHint("##RatingFilter", this.L("atlas.hint_filter", "filter…"), ref RatingFilter, 64);

                    EnsureMapPickCache();
                    if (ImGui.BeginTable("##MapRatings", 3,
                        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.ScrollY,
                        new Vector2(0, 320)))
                    {
                        ImGui.TableSetupScrollFreeze(0, 1);
                        ImGui.TableSetupColumn(this.L("atlas.rating_map_col", "Map"), ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn(this.L("atlas.rating_col", "Rating"), ImGuiTableColumnFlags.WidthFixed, 170f);
                        ImGui.TableSetupColumn("##clear", ImGuiTableColumnFlags.WidthFixed, 26f);
                        ImGui.TableHeadersRow();

                        var rfilter = RatingFilter;
                        foreach (var (english, localized) in MapPickCache)
                        {
                            if (!string.IsNullOrEmpty(rfilter)
                                && localized.IndexOf(rfilter, StringComparison.OrdinalIgnoreCase) < 0
                                && english.IndexOf(rfilter, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;

                            ImGui.PushID(english);
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted(localized);

                            ImGui.TableNextColumn();
                            bool rated = Settings.MapRatings.TryGetValue(english, out int rating);
                            if (rated)
                            {
                                ImGui.ColorButton("##sw", RatingColor(rating),
                                    ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker | ImGuiColorEditFlags.NoDragDrop,
                                    new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight()));
                                ImGui.SameLine();
                            }
                            int v = rated ? rating : 0;
                            ImGui.SetNextItemWidth(-1);
                            // Unrated rows show "—" until first touched; any edit stores a rating.
                            if (ImGui.SliderInt("##rating", ref v, 0, 10, rated ? "%d" : "—"))
                                Settings.MapRatings[english] = v;
                            if (ImGui.IsItemDeactivatedAfterEdit())
                                SaveMapRatings();

                            ImGui.TableNextColumn();
                            if (rated && ImGui.SmallButton("X"))
                            {
                                Settings.MapRatings.Remove(english);
                                SaveMapRatings();
                            }
                            ImGui.PopID();
                        }

                        ImGui.EndTable();
                    }

                    ImGui.TreePop();
                }

                // Expedition (sea / Uncharted Waters) overlays: leyline highlight + fog ships.
                if (ImGui.TreeNode(this.Loc.Title("atlas.expedition_settings", "Expedition Settings", "atlas_expedition")))
                {
                    if (ImGui.Checkbox(this.L("atlas.uncharted_leylines", "Uncharted waters leyline"), ref Settings.ShowUnchartedLeylines))
                        nodeCache.Clear(); // force a cache rebuild next frame — the ship scan is gated on this toggle
                    ImGuiHelper.ToolTip(this.L("atlas.uncharted_leylines_hint",
                        "Hover a sea ship (Uncharted Waters) to highlight its reveal area: the connection graph " +
                        "between the map nodes a logbook used there will uncover, thicker than the normal node " +
                        "connections. The hidden maps are already assigned client-side, so the highlighted " +
                        "cluster is exactly what that ship yields."));
                    if (Settings.ShowUnchartedLeylines)
                    {
                        ColorSwatch("##UnchartedLeylineColor", ref Settings.UnchartedLeylineColor);
                        ImGui.SameLine();
                        ImGui.Text(this.L("atlas.leyline_color", "Leyline Color"));
                        ImGui.SetNextItemWidth(150);
                        ImGui.SliderFloat(this.L("atlas.leyline_thickness", "Leyline Thickness"), ref Settings.UnchartedLeylineThickness, 1.0f, 8.0f);
                    }

                    if (ImGui.Checkbox(this.L("atlas.ships_in_fog", "Show ships in fog"), ref Settings.ShowShipsInFog))
                        nodeCache.Clear(); // force a cache rebuild next frame — the ship scan is gated on the toggles
                    ImGuiHelper.ToolTip(this.L("atlas.ships_in_fog_hint",
                        "Marks the Uncharted Waters ships the game isn't rendering yet (deep fog) with an icon, " +
                        "one per uncharted sea chunk — so upcoming logbook spots are visible before you sail " +
                        "close. Uses icons\\UnchartedShip.png when present, else a ring marker. Hovering the " +
                        "icon also triggers the leyline highlight."));
                    if (Settings.ShowShipsInFog)
                    {
                        ImGui.SetNextItemWidth(150);
                        ImGui.SliderFloat(this.L("atlas.ship_icon_size", "Ship icon size"), ref Settings.ShipIconSize, 32f, 96f);
                    }

                    ImGui.TreePop();
                }

                // Ritual (atlas line to the Crux of Nothingness) overlays.
                if (ImGui.TreeNode(this.Loc.Title("atlas.ritual_settings", "Ritual Settings", "atlas_ritual")))
                {
                    if (ImGui.Checkbox(this.L("atlas.ritual_predict", "Show Ritual mods (on hover)"),
                        ref Settings.ShowRitualPrediction))
                        nodeCache.Clear(); // force a cache rebuild next frame — the mod-text read is gated on this toggle
                    ImGuiHelper.ToolTip(this.L("atlas.ritual_predict_hint",
                        "Shows the Rite mods of the Ritual atlas line (the line drawn to the Crux of " +
                        "Nothingness): the game's own mods on committed line nodes (blue), plus the exact " +
                        "predicted mods (green) every still-reachable node WILL roll — the whole look-ahead " +
                        "chain into the fog, before you click, including which nodes get a second mod. Before " +
                        "the first node is placed, hover any accessible map in line mode to preview the whole " +
                        "chain that start would give. Predicted labels are in English."));

                    ImGui.Checkbox(this.L("atlas.ritual_planner", "Head of the king planner window"),
                        ref Settings.ShowRitualPlanner);
                    ImGuiHelper.ToolTip(this.L("atlas.ritual_planner_hint",
                        "Opens a window while the atlas is in line-drawing mode listing every path the line " +
                        "could take from every possible start map, with the predicted reward chain for each. " +
                        "Pick desired rewards in the filter dropdown (a path shows if ANY of them is in its " +
                        "chain); tick a path to highlight its route on the atlas and draw a ray to its first " +
                        "map. Closing the window with X also clears this checkbox."));
                    if (Settings.ShowRitualPlanner)
                        this.DrawRewardWeightsTable();

                    // The "Log Ritual rolls (RE)" debug toggle is HIDDEN from the UI for release;
                    // LogRitualRolls still works when set by hand in config/settings.txt.
                    ImGui.TreePop();
                }

                ImGui.SeparatorText(this.L("atlas.content_icons", "Content Icons"));
                ImGui.Checkbox(this.L("atlas.show_content_icons", "Show Content Icons"), ref Settings.ShowContentIcons);
                ImGuiHelper.ToolTip(this.L("atlas.show_content_icons_hint", "Draws each content as its in-game icon (from Plugins\\Atlas\\icons\\<name>.png) above the map " +
                    "name. Content without an icon file falls back to its text name. Icons are suppressed on visible nodes " +
                    "(the game already draws them there) and shown only on hidden ones."));
                if (Settings.ShowContentIcons)
                {
                    ImGui.SetNextItemWidth(180);
                    ImGui.SliderFloat(this.L("atlas.content_icon_size", "Content Icon Size"), ref Settings.ContentIconSize, 16f, 64f);
                    var iconOffset = Settings.ContentIconOffset;
                    ImGui.SetNextItemWidth(180);
                    if (ImGui.SliderFloat2(this.L("atlas.content_icon_offset", "Content Icon Offset (X,Y)"), ref iconOffset, -64f, 64f))
                        Settings.ContentIconOffset = iconOffset;
                }

                if (ImGui.TreeNode(this.Loc.Title("atlas.map_styles", "Map Styles", "MapStyles")))
                {
                    ImGui.InputTextWithHint("##MapGroupName", this.L("atlas.hint_group_name", "group name"), ref Settings.GroupNameInput, 256);
                    ImGui.SameLine();
                    if (ImGui.Button(this.L("atlas.add_map_group", "Add new map group")))
                    {
                        Settings.MapGroups.Add(new MapGroupSettings(Settings.GroupNameInput, Settings.DefaultBackgroundColor, Settings.DefaultFontColor));
                        Settings.GroupNameInput = string.Empty;
                    }

                    for (int i = 0; i < Settings.MapGroups.Count; i++)
                    {
                        var mapGroup = Settings.MapGroups[i];
                        if (ImGui.TreeNode($"{mapGroup.Name}##MapGroup{i}"))
                        {
                            float buttonSize = ImGui.GetFrameHeight();
                            if (TriangleButton($"##Up{i}", buttonSize, new Vector4(1, 1, 1, 1), true))
                            {
                                MoveMapGroup(i, -1);
                            }
                            ImGui.SameLine();
                            if (TriangleButton($"##Down{i}", buttonSize, new Vector4(1, 1, 1, 1), false))
                            {
                                MoveMapGroup(i, 1);
                            }
                            ImGui.SameLine();
                            if (ImGui.Button($"{this.L("atlas.rename_group", "Rename Group")}##{i}"))
                            {
                                NewGroupName = mapGroup.Name;
                                ImGui.OpenPopup($"RenamePopup##{i}");
                            }
                            ImGui.SameLine();
                            if (ImGui.Button($"{this.L("atlas.delete_group", "Delete Group")}##{i}"))
                            {
                                DeleteMapGroup(i);
                            }
                            ImGui.SameLine();
                            ColorSwatch($"##MapGroupBackgroundColor{i}", ref mapGroup.BackgroundColor);
                            ImGui.SameLine();
                            ImGui.Text(this.L("atlas.background_color", "Background Color"));
                            ImGui.SameLine();
                            ColorSwatch($"##MapGroupFontColor{i}", ref mapGroup.FontColor);
                            ImGui.SameLine(); ImGui.Text(this.L("atlas.font_color", "Font Color"));

                            for (int j = 0; j < mapGroup.Maps.Count; j++)
                            {
                                var mapName = mapGroup.Maps[j];
                                if (ImGui.InputTextWithHint($"##MapName{i}-{j}", this.L("atlas.hint_map_name", "map name"), ref mapName, 256))
                                    mapGroup.Maps[j] = mapName;

                                ImGui.SameLine();
                                if (ImGui.Button($"{this.L("atlas.delete", "Delete")}##MapNameDelete{i}-{j}"))
                                {
                                    mapGroup.Maps.RemoveAt(j);
                                    break;
                                }
                            }

                            if (ImGui.Button($"{this.L("atlas.add_new_map", "Add new map")}##AddNewMap{i}"))
                                mapGroup.Maps.Add(string.Empty);

                            // Pick a map from a filtered list instead of typing it. Stores the localized
                            // name (map styles match by the displayed name in the selected language); the
                            // filter narrows by localized or English name. Skips maps already in the group.
                            ImGui.SameLine();
                            ImGui.SetNextItemWidth(220);
                            if (ImGui.BeginCombo($"##MapGroupAdd{i}", this.L("atlas.add_from_list", "Add from list…")))
                            {
                                EnsureMapPickCache();
                                ImGui.SetNextItemWidth(-1);
                                ImGui.InputTextWithHint($"##MapGroupFilter{i}", this.L("atlas.hint_filter", "filter…"), ref MapGroupAddFilter, 64);
                                var gfilter = MapGroupAddFilter;
                                foreach (var (english, localized) in MapPickCache)
                                {
                                    if (!string.IsNullOrEmpty(gfilter)
                                        && localized.IndexOf(gfilter, StringComparison.OrdinalIgnoreCase) < 0
                                        && english.IndexOf(gfilter, StringComparison.OrdinalIgnoreCase) < 0)
                                        continue;
                                    if (mapGroup.Maps.Exists(m => NormalizeName(m).Equals(localized, StringComparison.OrdinalIgnoreCase)))
                                        continue;
                                    if (ImGui.Selectable($"{localized}##mg{english}"))
                                    {
                                        mapGroup.Maps.Add(localized);
                                        MapGroupAddFilter = string.Empty;
                                    }
                                }
                                ImGui.EndCombo();
                            }

                            if (ImGui.BeginPopupModal($"RenamePopup##{i}", ImGuiWindowFlags.AlwaysAutoResize))
                            {
                                ImGui.InputText(this.L("atlas.new_name", "New Name"), ref NewGroupName, 256);
                                if (ImGui.Button(this.L("atlas.ok", "OK")))
                                {
                                    mapGroup.Name = NewGroupName;
                                    ImGui.CloseCurrentPopup();
                                }
                                ImGui.SameLine();
                                if (ImGui.Button(this.L("atlas.cancel", "Cancel")))
                                {
                                    ImGui.CloseCurrentPopup();
                                }
                                ImGui.EndPopup();
                            }
                            ImGui.TreePop();
                        }
                    }
                    ImGui.TreePop();
                }
            }

            ImGui.SeparatorText(this.L("atlas.search_maps", "Search Maps"));
            ImGui.InputTextWithHint(this.L("atlas.search_map", "Search Map"), this.L("atlas.search_map_hint", "You can search multiple maps at once using a comma separator ','"), ref Settings.SearchQuery, 256);
            ImGui.SameLine();
            if (ImGui.SmallButton(this.L("atlas.clear", "Clear")))
                Settings.SearchQuery = string.Empty;
            // Search routing is always on now (the old "Draw Lines to Search in range" toggle is hidden);
            // a non-empty Search query draws routes to the matching maps within range.
            Settings.DrawLinesSearchQuery = true;

            ImGui.SeparatorText(this.L("atlas.target_farming", "Target farming"));
            DrawMapContentSettings();
            #endregion
        }

        // "Target farming" settings: user-defined content route groups, always shown (no outer
        // collapsible). Each group holds content entries; each entry routes to the nearest node
        // carrying that content with its own colour / thickness / hop-limit / draw toggle, and the
        // group's master toggle gates the whole set without clearing the per-entry flags. (The
        // content-icon overlay toggle lives in Display.)
        private void DrawMapContentSettings()
        {
            {
                ImGui.InputTextWithHint("##ContentGroupName", this.L("atlas.hint_group_name", "group name"), ref Settings.ContentGroupNameInput, 256);
                ImGui.SameLine();
                if (ImGui.Button(this.L("atlas.add_content_group", "Add content group")))
                {
                    Settings.ContentGroups.Add(new ContentGroupSettings
                    {
                        Name = string.IsNullOrWhiteSpace(Settings.ContentGroupNameInput) ? "Content Group" : Settings.ContentGroupNameInput,
                    });
                    Settings.ContentGroupNameInput = string.Empty;
                }

                for (int gi = 0; gi < Settings.ContentGroups.Count; gi++)
                {
                    var grp = Settings.ContentGroups[gi];
                    string title = grp.Locked ? $"{grp.Name} {this.L("atlas.builtin_suffix", "(built-in)")}##ContentGroup{gi}" : $"{grp.Name}##ContentGroup{gi}";
                    // The built-in group stays expanded while it's the only group; once other groups
                    // exist it collapses by default but the user can still toggle it freely.
                    if (grp.Locked && Settings.ContentGroups.Count == 1)
                        ImGui.SetNextItemOpen(true, ImGuiCond.Always);
                    if (!ImGui.TreeNode(title))
                        continue;

                    bool drawPaths = grp.DrawPaths;
                    if (ImGui.Checkbox($"{this.L("atlas.draw_paths", "Draw paths")}##CG{gi}", ref drawPaths))
                        grp.DrawPaths = drawPaths;
                    ImGuiHelper.ToolTip(this.L("atlas.draw_paths_hint", "Master switch for this group: when off, no route is drawn for any of its content, " +
                        "but each entry keeps its own 'route' checkbox unchanged."));

                    // One line thickness for all entries in the group, shown right under "Draw paths".
                    ImGui.SetNextItemWidth(180);
                    float gth = grp.LineThickness;
                    if (ImGui.SliderFloat($"{this.L("atlas.line_thickness", "Line thickness")}##CGth{gi}", ref gth, 1f, 8f))
                        grp.LineThickness = gth;

                    // The built-in group can't be deleted and its content list is fixed.
                    if (!grp.Locked)
                    {
                        ImGui.SameLine();
                        if (ImGui.Button($"{this.L("atlas.delete_group_lc", "Delete group")}##CG{gi}"))
                        {
                            Settings.ContentGroups.RemoveAt(gi);
                            ImGui.TreePop();
                            break;
                        }

                        // Add-content combo (only content types not already in this group). Filter box
                        // narrows by content name OR description (in the selected UI language).
                        ImGui.SetNextItemWidth(220);
                        if (ImGui.BeginCombo($"##AddContent{gi}", this.L("atlas.add_content", "Add content…")))
                        {
                            ImGui.SetNextItemWidth(-1);
                            ImGui.InputTextWithHint($"##ContentFilter{gi}", this.L("atlas.hint_filter", "filter…"), ref ContentAddFilter, 64);
                            var cfilter = ContentAddFilter;
                            foreach (var choice in ContentChoices)
                            {
                                if (grp.Contents.Exists(c => string.Equals(c.ContentName, choice, StringComparison.OrdinalIgnoreCase)))
                                    continue;
                                // Show "Name — description" (description truncated when long); the
                                // stable id (##choice) keeps selection independent of the shown text.
                                var name = LocalizedName(choice);
                                var desc = LocalizedDesc(choice);
                                if (!string.IsNullOrEmpty(cfilter)
                                    && name.IndexOf(cfilter, StringComparison.OrdinalIgnoreCase) < 0
                                    && (desc is null || desc.IndexOf(cfilter, StringComparison.OrdinalIgnoreCase) < 0)
                                    && choice.IndexOf(cfilter, StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;
                                var label = name;
                                if (desc is { Length: > 0 } cd)
                                    label += " — " + Truncate(cd, 60);
                                if (ImGui.Selectable($"{label}##{choice}"))
                                {
                                    grp.Contents.Add(new ContentRouteEntry { ContentName = choice });
                                    ContentAddFilter = string.Empty;
                                }
                            }
                            ImGui.EndCombo();
                        }

                        // Add-map combo: route by map name (matches every internal id-variant of that
                        // name). Names are shown/sorted in the selected UI language; filter box narrows.
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(220);
                        if (ImGui.BeginCombo($"##AddMap{gi}", this.L("atlas.add_map", "Add map…")))
                        {
                            EnsureMapPickCache();
                            ImGui.SetNextItemWidth(-1);
                            ImGui.InputTextWithHint($"##MapFilter{gi}", this.L("atlas.hint_filter", "filter…"), ref MapAddFilter, 64);
                            var filter = MapAddFilter;
                            foreach (var (english, localized) in MapPickCache)
                            {
                                if (!string.IsNullOrEmpty(filter)
                                    && localized.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                                    && english.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;
                                var match = "name:" + english;
                                if (grp.Contents.Exists(c => string.Equals(c.Match, match, StringComparison.OrdinalIgnoreCase)))
                                    continue;
                                if (ImGui.Selectable($"{localized}##map{english}"))
                                {
                                    grp.Contents.Add(new ContentRouteEntry { Match = match });
                                    MapAddFilter = string.Empty;
                                }
                            }
                            ImGui.EndCombo();
                        }
                    }

                    for (int ci = 0; ci < grp.Contents.Count; ci++)
                    {
                        var entry = grp.Contents[ci];
                        ImGui.PushID($"CG{gi}_C{ci}");

                        // One aligned row per entry: [route on/off] [route colour] [max hops] [icon] name [X].
                        // Each leading widget is fixed-width, so the name column lines up across all rows.
                        bool draw = entry.DrawPath;
                        if (ImGui.Checkbox("##route", ref draw))
                            entry.DrawPath = draw;
                        ImGuiHelper.ToolTip(this.L("atlas.route_entry_hint", "Draw a route to the nearest node carrying this content."));

                        ImGui.SameLine();
                        var col = entry.LineColor;
                        ColorSwatch("##color", ref col);
                        entry.LineColor = col;

                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(60);
                        int hops = entry.MaxHops;
                        if (ImGui.DragInt("##hops", ref hops, 0.1f, 0, 1000))
                            entry.MaxHops = Math.Max(0, hops);
                        ImGuiHelper.ToolTip(this.L("atlas.max_hops_hint", "Max hops to route through (0 = unlimited). A longer route is suppressed."));

                        // Icon (content entries only) + localized name (map name for built-in entries).
                        ImGui.SameLine();
                        if (NameToIcon.TryGetValue(entry.ContentName, out var basename)
                            && TryGetIcon(DllDirectory, basename, out var iptr, out var iw, out var ih) && iptr != IntPtr.Zero)
                        {
                            float h = ImGui.GetFontSize();
                            ImGui.Image(iptr, new Vector2(h * iw / Math.Max(1, ih), h));
                            ImGui.SameLine();
                        }
                        ImGui.TextUnformatted(ContentEntryDisplayName(entry));
                        if (LocalizedDesc(entry.ContentName) is { Length: > 0 } d)
                            ImGuiHelper.ToolTip(d);

                        // Built-in entries can't be removed (fixed content list).
                        if (!grp.Locked)
                        {
                            ImGui.SameLine();
                            if (ImGui.SmallButton("X"))
                            {
                                grp.Contents.RemoveAt(ci);
                                ImGui.PopID();
                                break;
                            }
                        }

                        ImGui.PopID();
                    }

                    ImGui.TreePop();
                }
            }
        }

        public override void DrawUI()
        {
            var inventoryPanel = InventoryPanel();

            var isGameHelperForeground = Process.GetCurrentProcess().MainWindowHandle == GetForegroundWindow();
            if (!Core.Process.Foreground && !isGameHelperForeground)
                return;

            EnsureProcessHandle();

            // Auto map-name language: re-slice the content/name overlays live if the GH UI language
            // changed since the last apply (no-op in explicit-override mode once tokens match).
            if (!string.Equals(appliedContentLang, EffectiveLanguage, StringComparison.OrdinalIgnoreCase))
            {
                ApplyContentLanguage(EffectiveLanguage);
                nodeCache.Clear();
            }

            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            if (!player.TryGetComponent<Render>(out var playerRender))
                return;

            var drawList = ImGui.GetBackgroundDrawList();

            drawList.ChannelsSplit(4);

            var atlasPanelAddr = GetAtlasPanelAddress();
            var atlasUi = atlasPanelAddr == IntPtr.Zero ? default : Read<UiElement>(atlasPanelAddr);
            if (!atlasUi.IsVisible)
                return;

            // Node positions/connections come from the live UI tree + the panel's edge list
            // (panel+0x5A8); the 0.4.x inline vectors at +0x510/+0x528 no longer apply.
            var atlasCount = atlasUi.Length;

            if (atlasCount <= 0 || atlasCount > 10000)
                return;

            // Reset the per-frame parent-read memo.
            frameBaseCache.Clear();
            parentOffsetCache.Clear();

            // Search terms + whether anything routes — computed up-front so we can skip the
            // (expensive) node-cache refresh AND the whole draw pass when nothing is shown.
            var searchQuery = NormalizeName(Settings.SearchQuery);
            bool doSearch = !string.IsNullOrWhiteSpace(searchQuery);
            List<string> searchList = [];
            if (doSearch)
            {
                searchList = searchQuery
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }
            bool wantContentRoute = Settings.ContentGroups is { Count: > 0 }
                && Settings.ContentGroups.Any(g => g.DrawPaths && g.Contents.Any(c => c.DrawPath));

            // When every node state is hidden and nothing searches/routes to a node, no node is ever
            // drawn — so reading per-node data this frame would be wasted work. Skip the read + draw.
            bool allStatesHidden = Settings.HideCompletedMaps && Settings.HideNotAccessibleMaps && Settings.HideAvailableMaps;
            // Ritual line mode (page mode 6): gates the "Head of the King Rewards" planner window/overlay.
            bool ritualLineMode = Read<byte>(IntPtr.Add(atlasPanelAddr, PanelLineModeOffset)) != 0;
            bool wantPlanner = Settings.ShowRitualPlanner && ritualLineMode;
            // While drawing the ritual line the Hide toggles are ignored: they cull nodes BEFORE
            // the hover hit-test, so with all three on the pre-click hover start could never
            // register (its prediction-exemption only kicks in once a node is already predicted).
            bool ritualShowAll = ritualLineMode
                && (Settings.ShowRitualPrediction || wantPlanner);
            bool needNodeData = !allStatesHidden || doSearch || wantContentRoute
                || Settings.DrawLinesToUniqueMaps || Settings.PathToLineageMaps || Settings.PathToArbiterMaps
                || Settings.ShowAtlasGraph || Settings.ShowUnchartedLeylines || Settings.ShowShipsInFog
                || Settings.ShowRitualPrediction || Settings.LogRitualRolls
                || wantPlanner;
            if (!needNodeData)
            {
                // cacheFrameCounter is left past the threshold (not incremented) so a re-enable
                // triggers a fresh read on the very next frame instead of waiting an interval.
                drawList.ChannelsMerge();
                return;
            }

            // Rebuild the slow-changing per-node data only on an interval (or when the node count
            // changes / cache is empty).
            if (++cacheFrameCounter >= CacheRefreshFrames || cachedAtlasCount != atlasCount || nodeCache.Count == 0)
            {
                this.RefreshNodeCache(atlasUi, atlasCount);
                cacheFrameCounter = 0;
            }

            // RE ground-truth collector for the deterministic Rite-mod roll (see poe2/Ritual.md).
            // Snapshots the ritual line (lineId + committed/pending grids + each node's rolled mod
            // text) to config/ritual_roll_log.jsonl, deduped, so walking many maps builds a dataset.
            if (Settings.LogRitualRolls)
                this.LogRitualSnapshot(atlasPanelAddr);

            // Predict the next candidates' Rite mods (shown before you click them). Rebuilt each frame
            // it's on — cheap (candidate table is cached; the roll is a few dozen TinyMT draws).
            this.ritualPredictions = Settings.ShowRitualPrediction
                ? this.BuildRitualPredictions(atlasPanelAddr)
                : EmptyRitualPredictions;

            // "Head of the King Rewards" planner: enumerate the chains from the current start/frontier
            // (cached by line state, so idle frames cost only the state reads).
            if (wantPlanner)
                this.BuildPlannerChains(atlasPanelAddr);

            var panelTopLeft = GetFinalTopLeft(in atlasUi.UiElementBase);
            var panelScale = ComputeScalePair(in atlasUi.UiElementBase);
            var panelSize = new Vector2(
                atlasUi.UiElementBase.UnscaledSize.X * panelScale.X,
                atlasUi.UiElementBase.UnscaledSize.Y * panelScale.Y);
            var panelRect = new RectangleF(panelTopLeft.X, panelTopLeft.Y, panelSize.X, panelSize.Y);

            var boundsSearch = CalculateBounds(Settings.DrawSearchInRange);

            var playerLocation = Core.States.InGameStateObject.CurrentWorldInstance.WorldToScreen(playerRender.WorldPosition);

            float resScale = ComputeRelativeUiScale(in atlasUi.UiElementBase, Settings.BaseWidth, Settings.BaseHeight);
            float uiScale = Math.Clamp(Settings.ScaleMultiplier * resScale, 0.5f, 4.0f);

            // Cursor pos + which content marker it's over this frame (filled in the node pass below);
            // the tooltip is drawn after the FontScaleScope so its text stays at normal size.
            var mousePos = ImGui.GetMousePos();
            string hoverContentName = null;

            using (new FontScaleScope(uiScale))
            {
                if (!(Core.GHSettings.EnableControllerMode || Settings.ControllerMode))
                    if (inventoryPanel)
                        return;

                // ── Route planning (shortest hops over the revealed atlas edges) ──────────
                // Built once per frame when a routed target is wanted: the edge graph from
                // panel+0x5A8, screen centers for on-screen nodes, the impassable set (failed
                // maps), the accessible frontier, and one multi-source BFS from it. Each target's
                // route is reconstructed from that BFS tree (shortest hops from nearest entry).
                Dictionary<StdTuple2D<int>, Vector2> routeCenters = null;
                Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>> routeGraph = null;
                HashSet<StdTuple2D<int>> routeBlocked = null;
                HashSet<StdTuple2D<int>> accessibleSet = null;        // nodes you can run from now
                Dictionary<StdTuple2D<int>, StdTuple2D<int>> accessibleCameFrom = null; // multi-source BFS tree
                bool routeReady = false;
                Vector2 routeAnchor = playerLocation;   // "you are here" marker (context dot only, not the route start)
                bool markerFound = false;

                bool wantRoute = Settings.RouteLinesThroughNodes &&
                    (doSearch || wantContentRoute || Settings.DrawLinesToUniqueMaps
                     || Settings.PathToLineageMaps || Settings.PathToArbiterMaps);
                if (wantRoute)
                {
                    routeCenters = new Dictionary<StdTuple2D<int>, Vector2>(nodeCache.Count);
                    routeBlocked = new HashSet<StdTuple2D<int>>();
                    accessibleSet = new HashSet<StdTuple2D<int>>();
                    foreach (var nd in nodeCache)
                    {
                        var ub = Read<UiElementBaseOffset>(nd.Address);
                        var sc = ComputeScalePair(in ub);
                        var tl = GetLeafTopLeft(in ub);
                        var sz = new Vector2(ub.UnscaledSize.X * sc.X, ub.UnscaledSize.Y * sc.Y);
                        var center = tl + sz * 0.5f;
                        if (panelRect.Contains(center.X, center.Y))
                            routeCenters[nd.GridPosition] = center;
                        if (nd.State == AtlasNodeState.Failed)
                            routeBlocked.Add(nd.GridPosition);
                        if (nd.State == AtlasNodeState.AccessibleNow)
                            accessibleSet.Add(nd.GridPosition);
                    }
                    routeGraph = BuildConnectionGraph(atlasPanelAddr);

                    // Routes start from the accessible frontier (nodes you can run now), NOT from the
                    // player: one multi-source BFS from all accessible nodes gives, for every target,
                    // the shortest hop path back to its nearest accessible entry.
                    accessibleCameFrom = MultiSourceBfs(routeGraph, accessibleSet, routeBlocked);
                    routeReady = accessibleSet.Count > 0;

                    // Anchor at the "you are here" marker (it renders on the current map node):
                    // among visible candidates inside the panel, pick the one sitting closest to a
                    // map node. Fall back to the player's world projection if none is found.
                    float bestMarkerD = float.MaxValue;
                    foreach (var mAddr in markerCandidates)
                    {
                        var mb = Read<UiElementBaseOffset>(mAddr);
                        var msc = ComputeScalePair(in mb);
                        var mtl = GetFinalTopLeft(in mb);
                        var msz = new Vector2(mb.UnscaledSize.X * msc.X, mb.UnscaledSize.Y * msc.Y);
                        var mc = mtl + msz * 0.5f;
                        if (!panelRect.Contains(mc.X, mc.Y))
                            continue;

                        float dNode = float.MaxValue;
                        foreach (var c in routeCenters.Values)
                        {
                            float d = Vector2.DistanceSquared(mc, c);
                            if (d < dNode) dNode = d;
                        }
                        if (dNode < bestMarkerD)
                        {
                            bestMarkerD = dNode;
                            routeAnchor = mc;
                            markerFound = true;
                        }
                    }
                }

                // Off-screen labels/badges are culled (nothing to draw); a margin keeps
                // partially-visible labels alive. Lines below are drawn before this cull so
                // off-screen citadel/tower/search targets still get their line.
                var screenBounds = new RectangleF(0, 0, ImGui.GetIO().DisplaySize.X, ImGui.GetIO().DisplaySize.Y);
                screenBounds.Inflate(64f, 64f);
                // Coarse bound (generous margin) for an early skip on the node CENTER before the costly
                // CalcTextSize/label work; the precise screenBounds cull below still trims with the label rect.
                var coarseBounds = new RectangleF(0, 0, ImGui.GetIO().DisplaySize.X, ImGui.GetIO().DisplaySize.Y);
                coarseBounds.Inflate(256f, 256f);

                // Routes are COLLECTED here and drawn after the node pass (below), so each one can be
                // assigned a distinct chevron phase on every edge it shares with another route. Drawing
                // inline with a global phase let two routes whose phase slots collided stamp opaque
                // triangles on the same spots — the later colour (usually a white/cream content route)
                // then fully hid the earlier one (e.g. a red arbiter route) on their common segment.
                // ── Atlas connection graph: faint line along every edge between adjacent nodes ──
                // Drawn on ChannelGrid (the bottom layer) so labels/routes sit on top. Reuses the
                // routing edge-graph + centers when a route is also being computed this frame.
                if (Settings.ShowAtlasGraph && !ritualShowAll)
                {
                    var gGraph = routeGraph ?? BuildConnectionGraph(atlasPanelAddr);
                    var gCenters = routeCenters;
                    if (gCenters == null)
                    {
                        gCenters = new Dictionary<StdTuple2D<int>, Vector2>(nodeCache.Count);
                        foreach (var nd in nodeCache)
                        {
                            var ub = Read<UiElementBaseOffset>(nd.Address);
                            var sc = ComputeScalePair(in ub);
                            var tl = GetLeafTopLeft(in ub);
                            var sz = new Vector2(ub.UnscaledSize.X * sc.X, ub.UnscaledSize.Y * sc.Y);
                            var center = tl + sz * 0.5f;
                            if (panelRect.Contains(center.X, center.Y))
                                gCenters[nd.GridPosition] = center;
                        }
                    }

                    drawList.ChannelsSetCurrent(ChannelGrid);
                    uint gcol = ImGuiHelper.Color(Settings.AtlasGraphLineColor);
                    float gth = MathF.Max(0.5f, uiScale * Settings.AtlasGraphThickness);
                    foreach (var kv in gGraph)
                    {
                        if (!gCenters.TryGetValue(kv.Key, out var ca))
                            continue;
                        foreach (var b in kv.Value)
                        {
                            // AddEdge stores both directions; draw each undirected edge once.
                            if (!IsCanonicalEdge(kv.Key, b))
                                continue;
                            if (gCenters.TryGetValue(b, out var cb))
                                drawList.AddLine(ca, cb, gcol, gth);
                        }
                    }
                }

                // Uncharted Waters leylines (connection graph of the hovered ship's reveal set),
                // under labels/routes on the same bottom layer as the connection graph. Reuses
                // the routing edge-graph when one was built this frame.
                // Fog ships first: they record this frame's icon rects, which the leyline
                // hover below also hit-tests.
                if (Settings.ShowShipsInFog && shipCache.Count > 0 && !ritualShowAll)
                    DrawFogShips(drawList, in panelRect, uiScale);
                else
                    fogShipIcons.Clear();

                if (Settings.ShowUnchartedLeylines && shipCache.Count > 0 && !ritualShowAll)
                    DrawUnchartedLeylines(drawList, in panelRect, uiScale, mousePos,
                        routeGraph ?? BuildConnectionGraph(atlasPanelAddr));

                var pendingRoutes = new List<(List<StdTuple2D<int>> path, uint color, float thickness)>();

                this.ritualHoverGrid = null;
                foreach (var nd in nodeCache)
                {
                    if (!nd.Drawable)
                        continue;
                    var mapName = nd.MapName;

                    if (doSearch && !searchList.Any(searchTerm => mapName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    bool completed = nd.State == AtlasNodeState.CompletedBase;
                    bool available = nd.State == AtlasNodeState.AccessibleNow;
                    bool notAccessible = nd.State != AtlasNodeState.AccessibleNow && nd.State != AtlasNodeState.CompletedBase;

                    // Route targets (position-independent): a reachable, not-yet-completed map that a
                    // "Draw Lines" checkbox points at. These override "Hide Not Accessible Maps" so the
                    // map you're routing to stays visible even when other inaccessible maps are hidden.
                    bool targetUnique = Settings.DrawLinesToUniqueMaps && !completed
                        && string.Equals(nd.MapInfo?.Type, "unique", StringComparison.OrdinalIgnoreCase);
                    bool targetLineage = Settings.PathToLineageMaps && !completed && (nd.MapInfo?.HasTag("lineage") ?? false);
                    bool targetArbiter = Settings.PathToArbiterMaps && !completed && (nd.MapInfo?.HasTag("arbiter") ?? false);
                    ContentRouteEntry contentEntry = null;
                    ContentGroupSettings contentGroup = null;
                    bool targetContent = !completed && MatchContentRoute(in nd, out contentEntry, out contentGroup);
                    bool routeTarget = targetUnique || targetLineage || targetArbiter || targetContent || doSearch;

                    // A predicted candidate keeps its label (candidates are usually fogged/not
                    // accessible, which the hide toggles would cull).
                    string ritualPredText = null;
                    bool ritualCand = Settings.ShowRitualPrediction
                        && this.ritualPredictions.TryGetValue(nd.GridPosition, out ritualPredText);

                    if (!ritualShowAll)
                    {
                        if (Settings.HideCompletedMaps && completed && !ritualCand)
                            continue;
                        if (Settings.HideNotAccessibleMaps && notAccessible && !routeTarget && !ritualCand)
                            continue;
                        if (Settings.HideAvailableMaps && available && !routeTarget && !ritualCand)
                            continue;
                    }

                    // Screen position read LIVE per frame (this atlas scrolls by moving the nodes' own
                    // RelativePosition, so a cached leaf would make labels step/jump every cache cycle).
                    // Read happens AFTER the cheap culls above (hidden/completed nodes never get here),
                    // and the ancestor walk in GetLeafTopLeft is O(1) via the per-frame parentOffsetCache.
                    var uiBase = Read<UiElementBaseOffset>(nd.Address);
                    var nodeScale = ComputeScalePair(in uiBase);
                    var nodeTopLeft = GetLeafTopLeft(in uiBase);
                    var nodeSize = new Vector2(uiBase.UnscaledSize.X * nodeScale.X,
                                               uiBase.UnscaledSize.Y * nodeScale.Y);
                    var nodeCenter = nodeTopLeft + nodeSize * 0.5f;

                    // Hypothetical ritual-line start = the node under the cursor. Only nodes the
                    // line could actually start from: accessible (per RE: start needs the
                    // accessible state bits) and not a node the line can never include
                    // (unique / tower / hideout — completed is excluded by `available` already).
                    // Used next frame by BuildRitualPredictions while no node is committed yet.
                    if (Settings.ShowRitualPrediction && available
                        && !string.Equals(nd.MapInfo?.Type, "unique", StringComparison.OrdinalIgnoreCase)
                        && !(nd.MapInfo?.HasTag("tower") ?? false)
                        && !(nd.MapInfo?.HasTag("hideout") ?? false)
                        && mousePos.X >= nodeTopLeft.X && mousePos.X < nodeTopLeft.X + nodeSize.X
                        && mousePos.Y >= nodeTopLeft.Y && mousePos.Y < nodeTopLeft.Y + nodeSize.Y)
                        this.ritualHoverGrid = nd.GridPosition;

                    // Ritual focus: while the line is being drawn only rite-relevant labels draw —
                    // predicted candidates and the hovered start; every other node label/icon is
                    // noise here. The hover hit-test above already ran, so any accessible node
                    // still registers as the pre-click start while undrawn.
                    if (ritualShowAll && !ritualCand
                        && !(this.ritualHoverGrid is { } rhg && rhg.Equals(nd.GridPosition)))
                        continue;

                    // Coarse off-screen skip BEFORE CalcTextSize (the per-node hot cost). Route/search
                    // targets draw a line even when off-screen, so they're exempt and handled below.
                    if (!routeTarget && !coarseBounds.Contains(nodeCenter.X, nodeCenter.Y))
                        continue;

                    var textSize = ImGui.CalcTextSize(mapName);
                    Vector2 drawPosition = nodeCenter - textSize * 0.5f + Settings.AnchorNudge;

                    var padding = new Vector2(5, 2) * uiScale;
                    var bgPos = drawPosition - padding;
                    var bgSize = textSize + padding * 2;
                    var rectCenter = (bgPos + (bgPos + bgSize)) * 0.5f;

                    // Routes to search hits / unique / lineage / arbiter maps — drawn even when the
                    // target is off-screen, so this happens before the visibility cull.
                    bool shouldDrawSearch = Settings.DrawLinesSearchQuery && doSearch
                        && searchList.Any(searchTerm => mapName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                        && boundsSearch.Contains(new PointF(drawPosition.X, drawPosition.Y));
                    if (shouldDrawSearch || targetContent || targetUnique || targetLineage || targetArbiter)
                    {
                        // Content routes carry their own colour/thickness/hop-limit; search takes
                        // precedence over a content match on the same node.
                        bool contentRoute = targetContent && !shouldDrawSearch;
                        uint lineColor = shouldDrawSearch ? SearchLineColor
                            : contentRoute ? ImGuiHelper.Color(contentEntry.LineColor)
                            : targetUnique ? UniqueLineColor
                            : targetLineage ? LineageLineColor
                            : ArbiterLineColor;
                        // Every group uses one group-level line thickness for all its entries.
                        float contentThickness = contentRoute
                            ? (contentGroup?.LineThickness ?? Settings.PathLineThickness)
                            : Settings.PathLineThickness;
                        float thickness = MathF.Max(1f, uiScale * (contentRoute ? contentThickness : Settings.PathLineThickness));
                        int maxHops = contentRoute ? contentEntry.MaxHops : 0;
                        bool drewRoute = false;

                        // Shortest hop path from the nearest accessible node to this target
                        // (skipping failed maps). path[0] = the accessible entry you'd run first.
                        if (routeReady && accessibleCameFrom != null)
                        {
                            var path = PathFromAccessible(nd.GridPosition, accessibleCameFrom, accessibleSet);
                            // Hop-limit: suppress (but still mark handled, so no straight-line fallback)
                            // a content route longer than the entry's MaxHops (0 = unlimited).
                            if (path != null && path.Count > 0 && maxHops > 0 && path.Count - 1 > maxHops)
                            {
                                drewRoute = true;
                            }
                            else if (path != null && path.Count > 0)
                            {
                                pendingRoutes.Add((path, lineColor, thickness));
                                int hops = path.Count - 1;

                                // Green dot on the accessible entry node (where you start running).
                                if (routeCenters.TryGetValue(path[0], out var startC))
                                {
                                    drawList.ChannelsSetCurrent(ChannelDots);
                                    float sr = MathF.Max(3f, thickness * 1.3f);
                                    drawList.AddCircleFilled(startC, sr, ImGuiHelper.Color(new Vector4(0.2f, 1f, 0.2f, 1f)));
                                    drawList.AddCircle(startC, sr, DotOutlineColor, 0, MathF.Max(1f, sr * 0.35f));
                                }

                                // Hop count to the LEFT of the map-name box, vertically centered on it,
                                // drawn as the route pill "N→" so the arrow points at the map ("N hops
                                // to get here").
                                drawList.ChannelsSetCurrent(ChannelLabels);
                                string ht = hops.ToString(CultureInfo.InvariantCulture) + "→";
                                float pillH = 18f * uiScale;
                                var htSize = ImGui.CalcTextSize(ht);
                                float pillW = MathF.Max(pillH, htSize.X + 8f * uiScale);
                                float pillCenterX = bgPos.X - (4f * uiScale) - pillW * 0.5f;
                                float pillTopY = rectCenter.Y - pillH * 0.5f;
                                var hopBg = new Vector4(0.05f, 0.05f, 0.05f, 0.85f);
                                var hopFg = new Vector4(1f, 0.9f, 0.2f, 1f); // bright yellow (route line itself carries the color)
                                DrawPill(drawList, ht, pillCenterX, pillTopY, hopBg, hopFg, uiScale);

                                drewRoute = true;
                            }
                        }

                        // Straight-line fallback only when node-routing isn't active (toggle off /
                        // no accessible nodes) — never the player-anchored fan when routing is on.
                        if (!drewRoute && !routeReady)
                        {
                            var intersectionPoint = GetLineRectangleIntersection(playerLocation, rectCenter, bgPos, bgPos + bgSize);

                            drawList.ChannelsSetCurrent(ChannelLines);
                            drawList.AddLine(playerLocation, intersectionPoint, lineColor, thickness);
                            var endDot = OffsetPointOutsideRect(intersectionPoint, rectCenter, thickness * 0.6f);
                            drawList.ChannelsSetCurrent(ChannelDots);
                            drawList.AddCircleFilled(endDot, thickness, lineColor);
                            drawList.AddCircle(endDot, thickness, DotOutlineColor, 0, MathF.Max(1f, thickness * 0.35f));
                        }
                    }

                    if (!screenBounds.IntersectsWith(new RectangleF(bgPos.X, bgPos.Y, bgSize.X, bgSize.Y)))
                        continue;

                    // Match group entries against the displayed name in the selected language: type the
                    // name in the language you've selected and it highlights.
                    var group = Settings.MapGroups.Find(g => g.Maps.Exists(
                        m => NormalizeName(m).Equals(mapName, StringComparison.OrdinalIgnoreCase)));

                    var backgroundColor = group?.BackgroundColor ?? Settings.DefaultBackgroundColor;
                    var fontColor = group?.FontColor ?? Settings.DefaultFontColor;
                    if (completed)
                        backgroundColor.W *= 0.4f;

                    drawList.ChannelsSetCurrent(ChannelLabels);
                    float rounding = 3f * uiScale;

                    if (Settings.ShowBiomeBorder && !ritualShowAll
                        && Biomes.TryGetValue(nd.BiomeId, out var biome) && biome.Show)
                    {
                        var biomeColor = biome.BdColor;
                        if (completed)
                            biomeColor.W *= 0.4f;

                        float bBorderTh = MathF.Max(1f, uiScale * Settings.BiomeBorderThickness);
                        var half = bBorderTh * 0.5f;
                        var outMin = bgPos - new Vector2(half, half);
                        var outMax = (bgPos + bgSize) + new Vector2(half, half);
                        var outRounding = MathF.Max(0f, rounding + half);

                        drawList.AddRect(outMin, outMax, ImGuiHelper.Color(biomeColor),
                            outRounding, ImDrawFlags.RoundCornersAll, bBorderTh);
                    }

                    drawList.AddRectFilled(bgPos, bgPos + bgSize, ImGuiHelper.Color(backgroundColor), rounding);
                    drawList.AddText(drawPosition, ImGuiHelper.Color(fontColor), mapName);

                    // DEBUG/RE: node child-index badge, sitting to the LEFT of the name and vertically
                    // centered against it, so a node called out by number is easy to find on-screen.
                    if (Settings.ShowNodeIndex && !ritualShowAll)
                    {
                        string idxLabel = nd.ChildIndex.ToString(CultureInfo.InvariantCulture);
                        var idxSize = ImGui.CalcTextSize(idxLabel);
                        var ipad = new Vector2(4, 2) * uiScale;
                        var idxBoxSize = idxSize + ipad * 2;
                        var idxMin = new Vector2(bgPos.X - (3f * uiScale) - idxBoxSize.X,
                            rectCenter.Y - idxBoxSize.Y * 0.5f);
                        drawList.AddRectFilled(idxMin, idxMin + idxBoxSize,
                            ImGuiHelper.Color(new Vector4(0.12f, 0.12f, 0.18f, 0.9f)), rounding);
                        drawList.AddText(idxMin + ipad, ImGuiHelper.Color(new Vector4(0.55f, 0.85f, 1f, 1f)), idxLabel);
                    }

                    // Map rating (0 normal … 10 terrible) as a colored number pill to the RIGHT of
                    // the map name, vertically centered on the label box. Green→yellow→red gradient.
                    if (Settings.ShowMapRating && nd.Rating >= 0 && !ritualShowAll)
                    {
                        string rl = nd.Rating.ToString(CultureInfo.InvariantCulture);
                        float pillH = 18f * uiScale;
                        var rlSize = ImGui.CalcTextSize(rl);
                        float pillW = MathF.Max(pillH, rlSize.X + 8f * uiScale);
                        var rBg = RatingColor(nd.Rating);
                        if (completed)
                            rBg.W *= 0.4f;
                        DrawPill(drawList, rl,
                            bgPos.X + bgSize.X + (4f * uiScale) + pillW * 0.5f,
                            rectCenter.Y - pillH * 0.5f,
                            rBg, RatingTextColor(rBg), uiScale);
                    }

                    // Predicted Rite mod of a next-candidate node, BELOW the map name — shown BEFORE
                    // it is committed (the game only reveals it on click). Green to set it apart from
                    // the game's own (blue) committed-node mods above.
                    if (ritualCand && !string.IsNullOrEmpty(ritualPredText))
                    {
                        var pmSize = ImGui.CalcTextSize(ritualPredText);
                        var pmPad = new Vector2(4, 2) * uiScale;
                        var pmPos = new Vector2(rectCenter.X - pmSize.X * 0.5f,
                            bgPos.Y + bgSize.Y + 3f * uiScale + pmPad.Y);
                        drawList.AddRectFilled(pmPos - pmPad, pmPos + pmSize + pmPad,
                            ImGuiHelper.Color(new Vector4(0.02f, 0.10f, 0.03f, 0.88f)), rounding);
                        drawList.AddText(pmPos, ImGuiHelper.Color(new Vector4(0.45f, 1f, 0.55f, 1f)), ritualPredText);
                    }

                    // Per-node content shown ABOVE the map name. Two disjoint sources merge into one
                    // name list: the token vector (element+0x350, class-1: atlas/tower content) and the
                    // badge ids (badge+0x188, class-2: boss/corruption/unique). Each name draws as its
                    // in-game icon when available, else as a text chip. See re-findings §2.10.3.
                    if ((Settings.ShowContentTokens || Settings.ShowContentIcons)
                        && !ritualShowAll && nd.ContentNames is { Length: > 0 })
                    {
                        // Suppress our (duplicate) icon where the game already renders the node's native
                        // icon (IsVisible bit 0x800 set), show it only where the game isn't (fog/off-screen).
                        // uiBase is read live this frame, so the bit is current — no pan lag.
                        bool nodeVisible = (uiBase.Flags & IsVisibleMask) != 0;

                        var hov = DrawContentRow(drawList, nd.ContentNames, DllDirectory, drawPosition, textSize, uiScale,
                            Settings.ShowContentIcons && !nodeVisible, Settings.ShowContentTokens,
                            Settings.ContentIconSize * uiScale, mousePos, Settings.ContentIconOffset * uiScale);
                        if (hov != null)
                            hoverContentName = hov;
                    }

                    float labelCenterX = drawPosition.X + textSize.X * 0.5f;
                    float nextRowTopY = drawPosition.Y + textSize.Y + (4f * uiScale);
                    float rowGap = 4f * uiScale;

                    CategorizeContents(nd.RawContents, MapTags, MapPlain, out var flags, out var contents);

                    if (Settings.ShowMapBadges)
                        DrawSquares(drawList, flags, labelCenterX, ref nextRowTopY, rowGap, uiScale);

                    DrawSquares(drawList, contents, labelCenterX, ref nextRowTopY, rowGap, uiScale);

                    if (Settings.ShowContentCount && nd.ContentCount > 0)
                        DrawContentDots(drawList, nd.ContentCount, labelCenterX, ref nextRowTopY, rowGap, uiScale);
                }

                // ── Deferred route drawing — interleave chevrons per shared edge ──────────
                // Build, per atlas edge, the ordered list of routes that traverse it; each route
                // then draws its chevrons at a distinct phase slot (local index / count) on that
                // edge, so every colour on a shared segment stays visible instead of being
                // overprinted by whichever route happened to draw last.
                if (pendingRoutes.Count > 0)
                {
                    var edgeRoutes = new Dictionary<(StdTuple2D<int>, StdTuple2D<int>), List<int>>();
                    for (int ri = 0; ri < pendingRoutes.Count; ri++)
                    {
                        var p = pendingRoutes[ri].path;
                        for (int i = 1; i < p.Count; i++)
                        {
                            if (!routeCenters.ContainsKey(p[i - 1]) || !routeCenters.ContainsKey(p[i]))
                                continue;
                            var key = EdgeKey(p[i - 1], p[i]);
                            if (!edgeRoutes.TryGetValue(key, out var list))
                                edgeRoutes[key] = list = new List<int>();
                            list.Add(ri);
                        }
                    }
                    for (int ri = 0; ri < pendingRoutes.Count; ri++)
                    {
                        var (p, col, th) = pendingRoutes[ri];
                        DrawNodePath(drawList, p, routeCenters, col, th, uiScale, Settings.RouteArrowSpacing, ri, edgeRoutes);
                    }
                }

                // Selected "Head of the King Rewards" chains: ray to each chain's start + highlighted
                // route with per-node reward labels.
                if (wantPlanner)
                    this.DrawPlannerOverlay(drawList, playerLocation, uiScale);

                // "You are here" marker dot (context only — not the route start).
                if (wantRoute && markerFound)
                {
                    drawList.ChannelsSetCurrent(ChannelDots);
                    float r = MathF.Max(3f, uiScale * 4f);
                    drawList.AddCircleFilled(routeAnchor, r, ImGuiHelper.Color(new Vector4(1f, 0.3f, 0.3f, 1f)));
                    drawList.AddCircle(routeAnchor, r, DotOutlineColor, 0, MathF.Max(1f, r * 0.35f));
                }

                drawList.ChannelsMerge();
            }

            // Tooltip for the content marker under the cursor — drawn after the FontScaleScope so the
            // text is normal-sized. ImGui tooltip windows render above the background draw list.
            if (hoverContentName != null)
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(LocalizedName(hoverContentName));
                if (LocalizedDesc(hoverContentName) is { Length: > 0 } desc)
                {
                    ImGui.Separator();
                    ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
                    ImGui.TextUnformatted(desc);
                    ImGui.PopTextWrapPos();
                }
                ImGui.EndTooltip();
            }

            // "Head of the King Rewards" planner window — after the FontScaleScope so its text stays
            // normal-sized regardless of the overlay label scale.
            if (wantPlanner)
                this.DrawPlannerWindow();
        }

        // Rebuild the per-node static-data cache (map id / biome / state / content names). This is
        // the expensive pass (pointer chains + wide-string reads per node), so it runs only on an
        // interval — not every frame. Positions are NOT cached here; they're read live each frame.
        //
        // Adaptive source (avoids the "double read" on upstream GameHelper): upstream's core already
        // reads the whole atlas every refresh into GameUi.AtlasMaps (its own Atlas plugin consumes
        // that). When that list is populated we reuse it — zero pointer-chasing here — so an upstream
        // user running this plugin pays the core read once instead of twice. On our fork the core path
        // is stripped (AtlasMaps absent/empty) and we read the nodes ourselves. Access is via
        // reflection so a single plugin binary loads on both builds (the AtlasMapNode type does not
        // exist on the fork, so a compile-time reference would break loading there).
        // The exact node-eligibility test the game's ritual reach check applies (see the
        // NodeDatRowPtrOffset comment): special-category maps (uniques, towers, hideouts,
        // citadels, league bosses, quest nodes…) can never carry the line. Reading it here
        // (2 reads/node, cache-refresh cadence, ritual toggles only) replaced the maps.json
        // tag heuristic, which missed citadels/bosses and let the planner draw routes whose
        // completion ran through them — routes the game then refused.
        private static bool IsRitualSpecialNode(IntPtr addr)
        {
            if (addr == IntPtr.Zero)
                return true;
            var row = Read<IntPtr>(IntPtr.Add(addr, NodeDatRowPtrOffset));
            return row == IntPtr.Zero
                || Read<int>(IntPtr.Add(row, DatRowSpecialCategoryOffset)) != 0;
        }

        // "Show Ritual mods (on hover)" (ShowRitualPrediction) also owns the committed-node blue
        // mod display (the old standalone Show-Ritual-mods toggle was folded into it); the planner
        // needs the same node data.
        private bool RitualFeaturesOn => Settings.ShowRitualPrediction || Settings.ShowRitualPlanner;

        private void RefreshNodeCache(UiElement atlasUi, int atlasCount)
        {
            if (this.TryRefreshNodeCacheFromCore(atlasCount))
                this.AppendMistNodesMissedByCore(atlasUi, atlasCount);
            else
                this.RefreshNodeCacheSelf(atlasUi, atlasCount);
            this.RefreshShipCache(atlasUi, atlasCount);
        }

        // Merge in the mist-shrouded map nodes (fp 0x442EF3) that upstream core's fp filter drops
        // from AtlasMaps (see AtlasMistNodeFp). Self-read (RefreshNodeCacheSelf) loads every child
        // and doesn't need this. Costs one u32 read per child per cache refresh. De-duped by grid
        // so nothing breaks if a future upstream starts including them itself.
        private void AppendMistNodesMissedByCore(UiElement atlasUi, int atlasCount)
        {
            var seen = new HashSet<StdTuple2D<int>>(nodeCache.Count);
            foreach (var nd in nodeCache)
                seen.Add(nd.GridPosition);

            for (int i = 0; i < atlasCount; i++)
            {
                var addr = atlasUi.GetChildAddress(i);
                if (addr == IntPtr.Zero)
                    continue;

                uint f = Read<uint>(IntPtr.Add(addr, 0x180));
                if ((f & ~IsVisibleMask) != (AtlasMistNodeFp & ~IsVisibleMask))
                    continue;

                var node = AtlasNode.Load(addr);
                if (node.GridPosition.X is < -0x8000 or > 0x8000
                    || node.GridPosition.Y is < -0x8000 or > 0x8000
                    || !seen.Add(node.GridPosition))
                    continue;

                var nodeUi = Read<UiElement>(addr);
                var internalId = NormalizeName(node.MapName);
                var mapInfo = GetMapInfo(internalId);
                var contentTokens = GetContentTokens(addr);
                var badgeIds = GetBadgeContentIds(nodeUi);
                var mapName = ResolveLocalizedName(internalId, mapInfo, EffectiveLanguage);
                nodeCache.Add(new NodeData
                {
                    Address = addr,
                    ChildIndex = i,
                    InternalId = internalId,
                    MapName = mapName,
                    Drawable = !string.IsNullOrWhiteSpace(mapName) && IsPrintableUnicode(mapName),
                    MapInfo = mapInfo,
                    BiomeId = node.BiomeId,
                    State = node.State,
                    RawContents = GetContentName(nodeUi),
                    ContentCount = GetContentCount(nodeUi),
                    ContentTokens = contentTokens,
                    BadgeContentIds = badgeIds,
                    ContentNames = BuildContentNames(contentTokens, badgeIds, internalId),
                    GridPosition = node.GridPosition,
                    Rating = GetMapRating(mapInfo),
                    RitualSpecial = this.RitualFeaturesOn && IsRitualSpecialNode(addr),
                });
            }
        }

        // Scan the atlas children for Uncharted Waters ship buttons. Runs on the node-cache
        // interval and only while the leyline overlay is on (one extra int read per child).
        // A chunk spawns up to 4 edge buttons; all chart the same chunk, so duplicates are
        // fine here — the draw pass keeps one per chunk.
        private void RefreshShipCache(UiElement atlasUi, int atlasCount)
        {
            shipCache.Clear();
            if (!Settings.ShowUnchartedLeylines && !Settings.ShowShipsInFog)
                return;

            for (int i = 0; i < atlasCount; i++)
            {
                var addr = atlasUi.GetChildAddress(i);
                if (addr == IntPtr.Zero)
                    continue;

                // Cheap discriminator first: map nodes keep zeros at +0x338, the other button
                // kinds (Breach 0 / Forest 1 / Tower 3) fail the exact Ocean row-index check.
                if (Read<int>(IntPtr.Add(addr, RegionButtonRowIndexOffset)) != RegionButtonOceanRow)
                    continue;
                if (Read<IntPtr>(IntPtr.Add(addr, RegionButtonRowPtrOffset)) == IntPtr.Zero)
                    continue;

                int bx = Read<int>(IntPtr.Add(addr, RegionButtonGridOffset));
                int by = Read<int>(IntPtr.Add(addr, RegionButtonGridOffset + 4));
                if (bx is < -0x80000 or > 0x80000 || by is < -0x80000 or > 0x80000)
                    continue;

                shipCache.Add((addr, bx, by, bx >> 4, by >> 4)); // arithmetic >> floors negatives too
            }
        }

        private void RefreshNodeCacheSelf(UiElement atlasUi, int atlasCount)
        {
            nodeCache.Clear();
            markerCandidates.Clear();
            for (int i = 0; i < atlasCount; i++)
            {
                var addr = atlasUi.GetChildAddress(i);
                if (addr == IntPtr.Zero)
                    continue;

                var nodeUi = Read<UiElement>(addr);

                // The "you are here" marker is a child sharing the node-list container's fp
                // (0x502EF3) rather than the map-node fp (0x542EF3); it has no grid/MapId, so we
                // can't key it by grid — we locate it by screen position (it renders on the
                // player's current map node). Collect visible candidates; the live frame picks one.
                uint f = nodeUi.Flags;
                if ((f & ~IsVisibleMask) == (AtlasCurrentNodeFp & ~IsVisibleMask) && (f & IsVisibleMask) != 0)
                {
                    markerCandidates.Add(addr);
                    // The marker is not a map node (garbage grid / no MapId). Keep it out of
                    // nodeCache so it never pollutes routeCenters and gets picked as the start.
                    continue;
                }

                var node = AtlasNode.Load(addr);
                var internalId = NormalizeName(node.MapName);
                var mapInfo = GetMapInfo(internalId);
                var contentTokens = GetContentTokens(addr);
                var badgeIds = GetBadgeContentIds(nodeUi);
                var mapName = ResolveLocalizedName(internalId, mapInfo, EffectiveLanguage);
                nodeCache.Add(new NodeData
                {
                    Address = addr,
                    ChildIndex = i,
                    InternalId = internalId,
                    MapName = mapName,
                    Drawable = !string.IsNullOrWhiteSpace(mapName) && IsPrintableUnicode(mapName),
                    MapInfo = mapInfo,
                    BiomeId = node.BiomeId,
                    State = node.State,
                    RawContents = GetContentName(nodeUi),
                    ContentCount = GetContentCount(nodeUi),
                    ContentTokens = contentTokens,
                    BadgeContentIds = badgeIds,
                    ContentNames = BuildContentNames(contentTokens, badgeIds, internalId),
                    GridPosition = node.GridPosition,
                    Rating = GetMapRating(mapInfo),
                    RitualSpecial = this.RitualFeaturesOn && IsRitualSpecialNode(addr),
                });
            }
            cachedAtlasCount = atlasCount;
        }

        // ── Adaptive consumer of upstream core's GameUi.AtlasMaps (see RefreshNodeCache) ─────────
        private static bool coreAtlasProbed;
        private static bool coreNodePropsResolved;
        private static PropertyInfo coreAtlasMapsProp;     // GameUi.AtlasMaps
        private static PropertyInfo coreAtlasMarkersProp;  // GameUi.AtlasMarkers
        private static PropertyInfo nIndex, nAddress, nMapId, nGrid, nBiome, nState, nTokens, nBadgeIds, nBadgeCount;
        private static PropertyInfo markerAddress;

        // True only when upstream's core actually supplied nodes (then we skip our own read).
        // Property absent (our fork) or momentarily empty (atlas-open transition) → false → self-read.
        private bool TryRefreshNodeCacheFromCore(int atlasCount)
        {
            try
            {
                var gameUi = Core.States.InGameStateObject.GameUi;
                if (gameUi == null)
                    return false;

                if (!coreAtlasProbed)
                {
                    var t = gameUi.GetType();
                    coreAtlasMapsProp = t.GetProperty("AtlasMaps");
                    coreAtlasMarkersProp = t.GetProperty("AtlasMarkers");
                    coreAtlasProbed = true;
                }

                if (coreAtlasMapsProp == null)
                    return false; // fork build: no core atlas source → read the nodes ourselves

                if (coreAtlasMapsProp.GetValue(gameUi) is not IEnumerable maps)
                    return false;

                var newCache = new List<NodeData>(atlasCount);
                foreach (var map in maps)
                {
                    if (map == null)
                        continue;
                    if (!coreNodePropsResolved)
                        ResolveCoreNodeProps(map.GetType());

                    var internalId = NormalizeName((string)(nMapId.GetValue(map) ?? string.Empty));
                    var mapInfo = GetMapInfo(internalId);
                    var tokens = ToUintArray(nTokens?.GetValue(map));
                    var badgeIds = ToUintArray(nBadgeIds?.GetValue(map));
                    var mapName = ResolveLocalizedName(internalId, mapInfo, EffectiveLanguage);
                    var nodeAddr = (IntPtr)(nAddress.GetValue(map) ?? IntPtr.Zero);
                    newCache.Add(new NodeData
                    {
                        Address = nodeAddr,
                        ChildIndex = (int)(nIndex.GetValue(map) ?? 0),
                        InternalId = internalId,
                        MapName = mapName,
                        Drawable = !string.IsNullOrWhiteSpace(mapName) && IsPrintableUnicode(mapName),
                        MapInfo = mapInfo,
                        BiomeId = (byte)(nBiome.GetValue(map) ?? (byte)0),
                        State = ConvertCoreState(nState?.GetValue(map)),
                        RawContents = new List<string>(),
                        ContentCount = (int)(nBadgeCount?.GetValue(map) ?? 0),
                        ContentTokens = tokens,
                        BadgeContentIds = badgeIds,
                        ContentNames = BuildContentNames(tokens, badgeIds, internalId),
                        GridPosition = (StdTuple2D<int>)(nGrid.GetValue(map) ?? default(StdTuple2D<int>)),
                        Rating = GetMapRating(mapInfo),
                        RitualSpecial = this.RitualFeaturesOn && IsRitualSpecialNode(nodeAddr),
                    });
                }

                if (newCache.Count == 0)
                    return false; // present but empty (transition) → self-read this cycle

                nodeCache.Clear();
                nodeCache.AddRange(newCache);
                this.CollectCoreMarkers(gameUi);
                cachedAtlasCount = atlasCount;
                return true;
            }
            catch
            {
                // Any reflection / shape mismatch → safe fallback to the self-read path.
                return false;
            }
        }

        private static void ResolveCoreNodeProps(Type t)
        {
            nIndex = t.GetProperty("Index");
            nAddress = t.GetProperty("Address");
            nMapId = t.GetProperty("MapId");
            nGrid = t.GetProperty("GridPosition");
            nBiome = t.GetProperty("BiomeId");
            nState = t.GetProperty("State");
            nTokens = t.GetProperty("ContentTokens");
            nBadgeIds = t.GetProperty("BadgeContentIds");
            nBadgeCount = t.GetProperty("BadgeCount");
            coreNodePropsResolved = nIndex != null && nAddress != null && nMapId != null
                && nGrid != null && nBiome != null;
        }

        private void CollectCoreMarkers(object gameUi)
        {
            markerCandidates.Clear();
            if (coreAtlasMarkersProp?.GetValue(gameUi) is not IEnumerable markers)
                return;
            foreach (var m in markers)
            {
                if (m == null)
                    continue;
                markerAddress ??= m.GetType().GetProperty("Address");
                if (markerAddress?.GetValue(m) is IntPtr addr && addr != IntPtr.Zero)
                    markerCandidates.Add(addr);
            }
        }

        private static uint[] ToUintArray(object listObj)
        {
            if (listObj is not IEnumerable e)
                return Array.Empty<uint>();
            var list = new List<uint>();
            foreach (var v in e)
                list.Add(Convert.ToUInt32(v));
            return list.ToArray();
        }

        private static AtlasNodeState ConvertCoreState(object stateObj) => stateObj?.ToString() switch
        {
            "CompletedBase" => AtlasNodeState.CompletedBase,
            "AccessibleNow" => AtlasNodeState.AccessibleNow,
            "Failed" => AtlasNodeState.Failed,
            _ => AtlasNodeState.None,
        };

        #region Routing helpers

        // Adjacency graph of the revealed atlas, built from the panel's connection (edge) list at
        // panel+0x5A8. Keyed by grid position; undirected. Source/Target are grid coords that match
        // each node's grid (node+0x320).
        private static Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>> BuildConnectionGraph(IntPtr atlasPanelAddr)
        {
            var graph = new Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>>();
            if (atlasPanelAddr == IntPtr.Zero)
                return graph;

            var vec = Read<StdVector>(atlasPanelAddr + AtlasConnectionsVectorOffset);
            if (!TryVectorCount<AtlasConnectionEdge>(vec, out int count))
                return graph;

            for (int i = 0; i < count; i++)
            {
                var e = ReadVectorAt<AtlasConnectionEdge>(vec, i);
                if (e.Source.Equals(e.Target))
                    continue;
                AddEdge(graph, e.Source, e.Target);
                AddEdge(graph, e.Target, e.Source);
            }

            return graph;
        }

        private static void AddEdge(
            Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>> graph,
            StdTuple2D<int> a,
            StdTuple2D<int> b)
        {
            if (!graph.TryGetValue(a, out var list))
            {
                list = new List<StdTuple2D<int>>(4);
                graph[a] = list;
            }

            if (!list.Contains(b))
                list.Add(b);
        }

        // Keep only one direction of an undirected edge (a precedes b), so the connection graph
        // draws each line once instead of twice.
        private static bool IsCanonicalEdge(StdTuple2D<int> a, StdTuple2D<int> b)
            => a.X < b.X || (a.X == b.X && a.Y <= b.Y);

        // Multi-source BFS over the undirected graph seeded from every accessible (frontier) node,
        // skipping blocked (failed) nodes. Returns a cameFrom tree pointing back toward the nearest
        // source — reconstruct any target's path with PathFromAccessible. Sources have no cameFrom.
        private static Dictionary<StdTuple2D<int>, StdTuple2D<int>> MultiSourceBfs(
            Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>> graph,
            HashSet<StdTuple2D<int>> sources,
            HashSet<StdTuple2D<int>> blocked)
        {
            var cameFrom = new Dictionary<StdTuple2D<int>, StdTuple2D<int>>();
            var visited = new HashSet<StdTuple2D<int>>();
            var queue = new Queue<StdTuple2D<int>>();

            foreach (var s in sources)
                if (graph.ContainsKey(s) && !blocked.Contains(s) && visited.Add(s))
                    queue.Enqueue(s);

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var nb in graph[cur])
                {
                    if (blocked.Contains(nb) || !visited.Add(nb))
                        continue;
                    cameFrom[nb] = cur;
                    queue.Enqueue(nb);
                }
            }

            return cameFrom;
        }

        // Reconstruct the shortest path accessible-source → target from a MultiSourceBfs tree.
        // Returns source..target inclusive (target alone if it's already accessible), or null if
        // the target wasn't reached.
        private static List<StdTuple2D<int>> PathFromAccessible(
            StdTuple2D<int> target,
            Dictionary<StdTuple2D<int>, StdTuple2D<int>> cameFrom,
            HashSet<StdTuple2D<int>> sources)
        {
            if (sources.Contains(target))
                return new List<StdTuple2D<int>> { target };
            if (!cameFrom.ContainsKey(target))
                return null;

            var path = new List<StdTuple2D<int>> { target };
            var cur = target;
            while (cameFrom.TryGetValue(cur, out var prev))
            {
                cur = prev;
                path.Add(cur);
            }
            path.Reverse();
            return path;
        }

        // Draw a node path (accessible source → target) as a thin guide line plus evenly-spaced
        // directional chevrons (filled triangles) pointing toward the target. The chevrons make the
        // route direction obvious and — because routes sharing an edge interleave their chevrons by
        // distinct phase slots — keep ALL of them visible where they overlap (a solid line would just
        // blend, and equal-phase opaque triangles would overprint). Off-screen path nodes break the
        // path into visible segments. `edgeRoutes` maps each shared edge to the ordered list of route
        // indices traversing it; this route's slot on an edge picks its chevron phase there.
        private static void DrawNodePath(
            ImDrawListPtr drawList,
            List<StdTuple2D<int>> path,
            Dictionary<StdTuple2D<int>, Vector2> centers,
            uint color,
            float thickness,
            float uiScale,
            float spacingMul,
            int routeIndex,
            Dictionary<(StdTuple2D<int>, StdTuple2D<int>), List<int>> edgeRoutes)
        {
            float chevron = MathF.Max(7f * uiScale, thickness * 2.2f); // triangle length along the path
            float spacing = chevron * MathF.Max(1.5f, spacingMul);     // distance between chevrons
            float guide = MathF.Max(1f, thickness * 0.5f);             // faint connecting line under them

            drawList.ChannelsSetCurrent(ChannelLines);
            Vector2? prev = null;
            StdTuple2D<int> prevG = default;
            foreach (var g in path)
            {
                if (!centers.TryGetValue(g, out var c))
                {
                    prev = null;
                    continue;
                }

                if (prev.HasValue)
                {
                    drawList.AddLine(prev.Value, c, color, guide);

                    // This route's phase among all routes sharing this edge: chevrons sit at
                    // (local + 0.5)/count of the spacing, so colours interleave evenly and never
                    // land on identical spots. Carry is reset per segment (kept phase-correct).
                    float phaseFrac = 0.5f;
                    if (edgeRoutes != null && edgeRoutes.TryGetValue(EdgeKey(prevG, g), out var sharers) && sharers.Count > 0)
                    {
                        int local = sharers.IndexOf(routeIndex);
                        if (local < 0)
                            local = 0;
                        phaseFrac = (local + 0.5f) / sharers.Count;
                    }
                    float carry = spacing * phaseFrac;
                    DrawChevrons(drawList, prev.Value, c, color, chevron, spacing, ref carry);
                }
                prev = c;
                prevG = g;
            }

            drawList.ChannelsSetCurrent(ChannelDots);
            foreach (var g in path)
                if (centers.TryGetValue(g, out var c))
                    drawList.AddCircleFilled(c, MathF.Max(2f, thickness * 0.9f), color);
        }

        // Direction-independent key for an atlas edge, so a segment shared by two routes hashes to the
        // same bucket regardless of which way each route walks it.
        private static (StdTuple2D<int>, StdTuple2D<int>) EdgeKey(StdTuple2D<int> a, StdTuple2D<int> b)
        {
            bool aFirst = a.X < b.X || (a.X == b.X && a.Y <= b.Y);
            return aFirst ? (a, b) : (b, a);
        }

        // Lay filled arrowhead triangles along a→b at `spacing` intervals, each `size` long, pointing
        // toward b. `carry` holds the leftover distance into the next segment so chevron spacing stays
        // even across an entire multi-segment path.
        private static void DrawChevrons(ImDrawListPtr drawList, Vector2 a, Vector2 b, uint color,
            float size, float spacing, ref float carry)
        {
            var d = b - a;
            float len = d.Length();
            if (len < 1e-3f)
                return;

            var dir = d / len;
            var perp = new Vector2(-dir.Y, dir.X);
            float half = size * 0.5f;

            float t = carry;
            while (t < len)
            {
                var p = a + dir * t;
                var tip = p + dir * half;
                var baseMid = p - dir * half;
                drawList.AddTriangleFilled(tip, baseMid + perp * half, baseMid - perp * half, color);
                t += spacing;
            }
            carry = t - len;
        }

#endregion

        // ── Uncharted Waters leylines: the connection graph of the HOVERED ship's reveal ────
        // Every uncharted sea chunk carries ship buttons, so lighting all of them at once melts
        // into one giant mesh over the whole fog (adjacent uncharted chunks touch). Instead the
        // overlay follows the game's own UX: hover a ship (its tooltip opens) → highlight the
        // nodes of THAT ship's 16x16 chunk — exactly what a logbook used there reveals (their
        // map identities are already assigned client-side). Drawn as the atlas connection edges
        // between those nodes — same edges as "Show node connections" (panel+0x5A8 graph), just
        // thicker — plus a dot per node so chunk nodes without an in-chunk edge still show up.
        // Positions are read live (the atlas scrolls by moving the widgets).
        private void DrawUnchartedLeylines(ImDrawListPtr drawList, in RectangleF panelRect, float uiScale,
            Vector2 mousePos, Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>> graph)
        {
            // The ship under the cursor picks the highlighted chunk (4 edge buttons share one
            // chunk, so whichever is hovered yields the same reveal set).
            (int X, int Y) chunk = default;
            bool hovered = false;
            foreach (var (addr, _, _, cx, cy) in shipCache)
            {
                var ub = ReadBaseCached(addr);
                // A culled button's own position is stale — only game-rendered ships are
                // hit-testable here. Fog ships are covered by the fogShipIcons pass below.
                if ((ub.Flags & IsVisibleMask) == 0)
                    continue;
                var sc = ComputeScalePair(in ub);
                var tl = GetLeafTopLeft(in ub);
                var sz = new Vector2(ub.UnscaledSize.X * sc.X, ub.UnscaledSize.Y * sc.Y);
                if (mousePos.X >= tl.X && mousePos.X <= tl.X + sz.X
                    && mousePos.Y >= tl.Y && mousePos.Y <= tl.Y + sz.Y)
                {
                    chunk = (cx, cy);
                    hovered = true;
                    break;
                }
            }

            // Our fog-ship icons (drawn this frame by DrawFogShips) hit-test by their rect.
            if (!hovered)
            {
                foreach (var (iconChunk, center, half) in fogShipIcons)
                {
                    if (mousePos.X >= center.X - half && mousePos.X <= center.X + half
                        && mousePos.Y >= center.Y - half && mousePos.Y <= center.Y + half)
                    {
                        chunk = iconChunk;
                        hovered = true;
                        break;
                    }
                }
            }
            if (!hovered)
                return;

            // Live screen centers of the hovered chunk's nodes.
            var centers = new Dictionary<StdTuple2D<int>, Vector2>();
            foreach (var nd in nodeCache)
            {
                if ((nd.GridPosition.X >> 4, nd.GridPosition.Y >> 4) != chunk)
                    continue;
                var ub = Read<UiElementBaseOffset>(nd.Address);
                var sc = ComputeScalePair(in ub);
                var tl = GetLeafTopLeft(in ub);
                var center = tl + new Vector2(ub.UnscaledSize.X * sc.X, ub.UnscaledSize.Y * sc.Y) * 0.5f;
                if (panelRect.Contains(center.X, center.Y))
                    centers[nd.GridPosition] = center;
            }
            if (centers.Count == 0)
                return;

            drawList.ChannelsSetCurrent(ChannelGrid);
            uint col = ImGuiHelper.Color(Settings.UnchartedLeylineColor);
            float th = MathF.Max(1f, uiScale * Settings.UnchartedLeylineThickness);
            foreach (var kv in centers)
            {
                drawList.AddCircleFilled(kv.Value, MathF.Max(2f, th * 0.9f), col);
                if (!graph.TryGetValue(kv.Key, out var neighbors))
                    continue;
                foreach (var nb in neighbors)
                {
                    // AddEdge stores both directions; draw each undirected edge once. Both ends
                    // are in `centers` = the hovered chunk only — no bleed into neighbours.
                    if (!IsCanonicalEdge(kv.Key, nb))
                        continue;
                    if (centers.TryGetValue(nb, out var cb))
                        drawList.AddLine(kv.Value, cb, col, th);
                }
            }
        }

        // ── Fog ships: mark uncharted-water spots the game isn't rendering yet ──────────────
        // Ship buttons exist for every streamed uncharted chunk, but the game only renders the
        // ones near explored water; the rest sit with IsVisible clear AND a stale position (a
        // culled button's own coordinates can't be trusted — that's why the icon is anchored
        // differently). Anchor: a button's grid coords equal the grid of the chunk node it
        // snapped to (createRegionActionButton min-dist snap), and fog NODES do keep live
        // positions — so the icon is drawn at that node's center. One icon per chunk; chunks
        // that already show a game-rendered ship are skipped. Icon = icons\UnchartedShip.png
        // (the game's QuickUseItemIconLogbook asset); fallback ring marker when absent.
        // Also records this frame's icon rects (fogShipIcons) for the leyline hover.
        private void DrawFogShips(ImDrawListPtr drawList, in RectangleF panelRect, float uiScale)
        {
            fogShipIcons.Clear();

            // Split chunks into "game already shows a ship" vs "wanted": for the latter keep
            // one button grid per chunk — the icon anchor.
            var chunkVisible = new HashSet<(int X, int Y)>();
            var wanted = new Dictionary<(int X, int Y), StdTuple2D<int>>();
            foreach (var (addr, gx, gy, cx, cy) in shipCache)
            {
                if ((Read<uint>(IntPtr.Add(addr, 0x180)) & IsVisibleMask) != 0)
                    chunkVisible.Add((cx, cy));
                else if (!wanted.ContainsKey((cx, cy)))
                    wanted[(cx, cy)] = new StdTuple2D<int> { X = gx, Y = gy };
            }
            foreach (var key in chunkVisible)
                wanted.Remove(key);
            if (wanted.Count == 0)
                return;

            // Resolve each anchor grid to its node and read the node's LIVE screen center.
            var iconPos = new Dictionary<(int X, int Y), Vector2>();
            foreach (var nd in nodeCache)
            {
                var chunk = (nd.GridPosition.X >> 4, nd.GridPosition.Y >> 4);
                if (!wanted.TryGetValue(chunk, out var anchor) || iconPos.ContainsKey(chunk))
                    continue;
                if (nd.GridPosition.X != anchor.X || nd.GridPosition.Y != anchor.Y)
                    continue;
                var ub = Read<UiElementBaseOffset>(nd.Address);
                var sc = ComputeScalePair(in ub);
                var tl = GetLeafTopLeft(in ub);
                var center = tl + new Vector2(ub.UnscaledSize.X * sc.X, ub.UnscaledSize.Y * sc.Y) * 0.5f;
                if (panelRect.Contains(center.X, center.Y))
                    iconPos[chunk] = center;
            }
            if (iconPos.Count == 0)
                return;

            drawList.ChannelsSetCurrent(ChannelLabels);
            float h = MathF.Max(8f, Settings.ShipIconSize * uiScale);
            bool haveIcon = TryGetIcon(DllDirectory, FogShipIconName, out var ptr, out var iw, out var ih)
                && ptr != IntPtr.Zero && iw > 0 && ih > 0;
            foreach (var kv in iconPos)
            {
                var c = kv.Value;
                if (haveIcon)
                {
                    float w = h * iw / ih;
                    drawList.AddImage(ptr, c - new Vector2(w, h) * 0.5f, c + new Vector2(w, h) * 0.5f);
                }
                else
                {
                    // fallback marker until icons\UnchartedShip.png is provided
                    float r = h * 0.35f;
                    drawList.AddCircleFilled(c, r, ImGuiHelper.Color(new Vector4(0.04f, 0.08f, 0.12f, 0.9f)));
                    drawList.AddCircle(c, r, ImGuiHelper.Color(Settings.UnchartedLeylineColor), 0, MathF.Max(1.5f, r * 0.25f));
                }
                fogShipIcons.Add((kv.Key, c, h * 0.5f));
            }
        }

        private void LoadBiomeMap()
        {
            var path = Path.Join(DllDirectory, "json", "biome.json");
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var contents = JsonConvert.DeserializeObject<Dictionary<string, BiomeInfo>>(json);

            Biomes.Clear();

            if (contents is null)
                return;

            foreach (var content in contents)
            {
                if (byte.TryParse(content.Key, out var id))
                    Biomes[id] = content.Value;
            }

            ApplyBiomeOverrides();
        }

        private void LoadContentMap()
        {
            var path = Path.Join(DllDirectory, "json", "content.json");
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var contents = JsonConvert.DeserializeObject<Dictionary<string, ContentInfo>>(json);

            MapTags.Clear();
            MapPlain.Clear();

            if (contents is null)
                return;

            foreach (var content in contents)
            {
                if (content.Key.All(char.IsLetter))
                    MapTags[content.Key] = content.Value;
                else
                    MapPlain[content.Key] = content.Value;
            }

            ApplyContentOverrides();
        }

        // Load the class-2 badge content id → name table (json/mapcontent.json). Keys are the badge
        // content id (low 16 bits of badge+0x188); generated from EndgameMapContent.tsv. See §2.10.3.
        private void LoadMapContent()
        {
            BadgeContentNames.Clear();
            NameToIcon.Clear();
            NameToDesc.Clear();
            ContentTranslations.Clear();
            IconCache.Clear();
            var path = Path.Join(DllDirectory, "json", "mapcontent.json");
            if (!File.Exists(path))
                return;

            var contents = JsonConvert.DeserializeObject<Dictionary<string, MapContentEntry>>(File.ReadAllText(path));
            if (contents is null)
                return;

            foreach (var kv in contents)
            {
                var name = kv.Value?.Name;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (uint.TryParse(kv.Key, out var id))
                    BadgeContentNames[id] = name;
                if (!string.IsNullOrWhiteSpace(kv.Value.Icon))
                    NameToIcon[name] = kv.Value.Icon;
                if (!string.IsNullOrWhiteSpace(kv.Value.Desc))
                    NameToDesc[name] = kv.Value.Desc;
                if (kv.Value.Translates is { Count: > 0 })
                    ContentTranslations[name] = kv.Value.Translates;
            }

            SeedSpecialBadges();

            // Selectable content list for the route-group editor: real content names only (skip the
            // "[DNT] ..." placeholders and any "(...)"-wrapped non-content markers), de-duped + sorted.
            ContentChoices.Clear();
            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in BadgeContentNames.Values)
            {
                if (string.IsNullOrWhiteSpace(n) || n[0] == '[' || n[0] == '(' || n[0] == '#')
                    continue;
                if (distinct.Add(n))
                    ContentChoices.Add(n);
            }
            ContentChoices.Sort(StringComparer.OrdinalIgnoreCase);

            ApplyContentLanguage(EffectiveLanguage);
        }

        // Special map-state contents that have a VisualIdentity icon but NO EndgameMapContent row, so
        // they are absent from mapcontent.json (which is generated row+100). Their badge+0x188 low16 is
        // a Stats.dat row id (NOT row+100) and DRIFTS by ±1 across game patches — so every observed id
        // is mapped. See docs/re-findings-atlas.md §2.10.6.
        //   Grand Mirror = DeliriumGigaMirror, badge id = stat `map_delirium_has_giga_mirror`
        //   (24918 in the 2026-06-10 dump, 24919 live). The durable fix is to resolve that stat name to
        //   its current row id at dump time and emit it into mapcontent.json; this runtime seed makes it
        //   resolve on the current client without a json rebuild. Icon = icons/AtlasIconContentGigaMirror.png
        //   (deployed by the csproj icon-copy step); falls back to a text chip if the PNG is missing.
        private static void SeedSpecialBadges()
        {
            const string grandMirror = "Grand Mirror";
            foreach (var id in new uint[] { 24918u, 24919u })
                BadgeContentNames[id] = grandMirror;
            NameToIcon[grandMirror] = "AtlasIconContentGigaMirror";
            NameToDesc[grandMirror] = "Contains a reflection of the Map Boss. When the bosses are " +
                "defeated Delirium fog spreads to nearby Maps.";
        }

        // The built-in (locked) content group. Its entries route by MAP classification (maps.json
        // tag/type) rather than by node content — in-game these feel like content (Arbiter bosses,
        // Citadels, Lineage maps). The list is fixed; only per-entry colour/thickness/hops/draw and
        // the group master toggle are user-editable. Citadels carry the 'arbiter' tag in the data.
        private const string BuiltInGroupName = "Map Targets";
        // Built-in targets, each matched by a full matcher key: "id:<MapId>" (exact internal id) or
        // "name:<DisplayName>" (every id-variant sharing that display name). Display names are resolved
        // live to the selected UI language (ContentEntryDisplayName). `On`/`Hops` are the default
        // per-entry DrawPath / MaxHops when the group is first created (no config yet).
        private static readonly (string Match, Vector4 Color, bool On, int Hops)[] BuiltInTargets =
        {
            ("id:MapUberBoss_StoneCitadel",     new Vector4(1.00f, 0.94509804f, 0.39215687f, 1f), true,  25),  // gold
            ("id:MapUberBoss_IronCitadel",      new Vector4(1.00f, 0.94509804f, 0.39215687f, 1f), true,  25),  // gold
            ("id:MapUberBoss_CopperCitadel",    new Vector4(1.00f, 0.94509804f, 0.39215687f, 1f), true,  25),  // gold
            ("id:MapMothersoul_Male",           new Vector4(1.00f, 0.94430125f, 0.39215684f, 1f), true,  25),  // gold
            ("id:MapMothersoul_Female",         new Vector4(1.00f, 0.94509804f, 0.39215687f, 1f), true,  25),  // gold
            ("id:MapDerelictMansion",           new Vector4(0.02f, 0.5568628f, 0.23137255f, 1f), true,  25),   // green
            ("id:MapCavernCity",                new Vector4(0.019607844f, 0.5568628f, 0.23137255f, 1f), true,  25),  // green
            ("id:MapVaalVault",                 new Vector4(0.019607844f, 0.5568628f, 0.23137255f, 1f), true,  25),  // green
            ("id:MapUberBoss_JadeCitadel",      new Vector4(0.019607844f, 0.5568628f, 0.23137255f, 1f), true,  25),  // green
            ("id:MapUniqueUntaintedParadise",   new Vector4(1.00f, 0.60f, 0.20f, 1f), false, 25),  // orange
            ("id:MapUniqueCastaway",            new Vector4(1.00f, 0.60f, 0.20f, 1f), false, 25),  // orange
            // Expedition maps (migrated from the user's "expedition" group), matched by exact id.
            ("id:ExpeditionSubArea_MedvedBoss", new Vector4(1.00f, 0.97236055f, 0.8525896f, 1f), false, 25),  // Sprawling Jungle
            ("id:ExpeditionSubArea_VoranaBoss", new Vector4(1.00f, 0.9738546f, 0.8605578f, 1f), false, 25),   // Mournful Cliffside
            ("id:ExpeditionSubArea_OlrothBoss", new Vector4(0.98804784f, 0.9651673f, 0.86601806f, 1f), false, 25),  // Obscure Island
            ("id:ExpeditionSubArea_UhtredBoss", new Vector4(0.9760956f, 0.94182533f, 0.7933208f, 1f), false, 25),   // Secluded Temple
            ("id:ExpeditionLogBook_Heath",      new Vector4(1.00f, 0.0f, 0.0f, 1f), false, 25),                // Moor of Fallen Skies
        };

        // Make sure the locked built-in group exists and its content list matches the fixed preset,
        // preserving any per-entry style the user has customised (matched by the Match key).
        private void EnsureBuiltInContentGroup()
        {
            if (Settings?.ContentGroups == null)
                return;

            var grp = Settings.ContentGroups.Find(g => g.Locked);
            bool freshGroup = grp == null;
            if (freshGroup)
            {
                grp = new ContentGroupSettings { Name = BuiltInGroupName, Locked = true, LineThickness = 1.5f };
                Settings.ContentGroups.Insert(0, grp);
            }

            var reconciled = new List<ContentRouteEntry>(BuiltInTargets.Length);
            bool anyMatched = false;
            foreach (var (match, color, on, hops) in BuiltInTargets)
            {
                // Fallback label = the matcher value (after "id:" / "name:"); the UI resolves the
                // localized display name from it via ContentEntryDisplayName.
                int sep = match.IndexOf(':');
                var label = sep >= 0 ? match[(sep + 1)..] : match;
                var existing = grp.Contents?.Find(c => string.Equals(c.Match, match, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.ContentName = label;  // fallback label; UI shows the localized map name
                    reconciled.Add(existing);
                    anyMatched = true;
                }
                else
                {
                    reconciled.Add(new ContentRouteEntry { ContentName = label, Match = match, LineColor = color, DrawPath = on, MaxHops = hops });
                }
            }
            grp.Contents = reconciled;

            // Fresh group, or migration from an older preset (no entry matched the new id: keys): apply
            // the seed default for the group master (on). An existing id-format group keeps the user's choice.
            if (freshGroup || !anyMatched)
                grp.DrawPaths = true;
        }

        // Evaluate a built-in map matcher against a node: "id:<MapId>" (exact internal id),
        // "tag:<tag>" or "type:<type>" (maps.json classification).
        private static bool MatchMapTarget(string match, string internalId, MapInfo info)
        {
            if (string.IsNullOrEmpty(match))
                return false;
            int c = match.IndexOf(':');
            if (c < 0)
                return false;
            var kind = match[..c];
            var val = match[(c + 1)..];
            return kind switch
            {
                "id" => string.Equals(internalId, val, StringComparison.OrdinalIgnoreCase),
                // Match by canonical English display name → catches every internal id-variant sharing it.
                "name" => info != null && string.Equals(info.Name, val, StringComparison.OrdinalIgnoreCase),
                "tag" => info != null && info.HasTag(val),
                "type" => info != null && string.Equals(info.Type, val, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        // Trim a string to maxLen characters, appending an ellipsis when it was longer.
        private static string Truncate(string s, int maxLen)
            => string.IsNullOrEmpty(s) || s.Length <= maxLen ? s : s[..maxLen].TrimEnd() + "…";

        // Display label for a route entry in the active UI language: built-in (map) entries resolve the
        // localized map name from maps.json; content entries use the localized content name.
        private string ContentEntryDisplayName(ContentRouteEntry e)
        {
            if (!string.IsNullOrEmpty(e.Match) && e.Match.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            {
                var id = e.Match[3..];
                return ResolveLocalizedName(id, GetMapInfo(id), EffectiveLanguage);
            }
            if (!string.IsNullOrEmpty(e.Match) && e.Match.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                return ResolveLocalizedMapName(e.Match[5..], EffectiveLanguage);
            return LocalizedName(e.ContentName);
        }

        // Localized display for a canonical English map name (resolved via any id-variant carrying it).
        private static string ResolveLocalizedMapName(string englishName, string lang)
        {
            if (string.IsNullOrWhiteSpace(englishName))
                return englishName;
            foreach (var info in MapInfos.Values)
                if (string.Equals(info.Name, englishName, StringComparison.OrdinalIgnoreCase))
                    return ResolveLocalizedName(null, info, lang);
            return englishName;
        }

        // Build/refresh the deduped, language-sorted map list backing the "Add map…" picker.
        private void EnsureMapPickCache()
        {
            var lang = EffectiveLanguage ?? string.Empty;
            if (MapPickCacheLang == lang && MapPickCache.Count > 0)
                return;

            MapPickCache.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in MapInfos.Values)
            {
                if (info == null || string.IsNullOrWhiteSpace(info.Name) || !seen.Add(info.Name))
                    continue;
                MapPickCache.Add((info.Name, ResolveLocalizedName(null, info, lang)));
            }
            MapPickCache.Sort((a, b) => string.Compare(a.Localized, b.Localized, StringComparison.InvariantCultureIgnoreCase));
            MapPickCacheLang = lang;
        }

        // Rebuild the active-language overlays (NameToLocalizedName/Desc) from ContentTranslations for
        // the language token in Settings.Language. English (or any missing token) leaves the maps empty,
        // so display falls back to the canonical English name/desc. Called on load and on language change.
        private static void ApplyContentLanguage(string lang)
        {
            appliedContentLang = lang;
            NameToLocalizedName.Clear();
            NameToLocalizedDesc.Clear();
            if (string.IsNullOrWhiteSpace(lang) || lang.Equals("english", StringComparison.OrdinalIgnoreCase))
                return;

            foreach (var kv in ContentTranslations)
            {
                if (!kv.Value.TryGetValue(lang, out var loc) || loc is null)
                    continue;
                if (!string.IsNullOrWhiteSpace(loc.Name))
                    NameToLocalizedName[kv.Key] = loc.Name;
                if (!string.IsNullOrWhiteSpace(loc.Desc))
                    NameToLocalizedDesc[kv.Key] = loc.Desc;
            }
        }

        // Display name/desc for a canonical English content name in the active language (English fallback).
        private static string LocalizedName(string englishName)
            => NameToLocalizedName.TryGetValue(englishName, out var n) ? n : englishName;

        private static string LocalizedDesc(string englishName)
            => NameToLocalizedDesc.TryGetValue(englishName, out var d) ? d
               : (NameToDesc.TryGetValue(englishName, out var en) ? en : null);

        // Lazily load (and cache) the icon texture for a content basename from icons\<basename>.png.
        // Returns false when the file is absent (negative-cached) so the caller falls back to text.
        private static bool TryGetIcon(string dllDir, string basename, out IntPtr ptr, out int w, out int h)
        {
            ptr = IntPtr.Zero; w = 0; h = 0;
            if (string.IsNullOrEmpty(basename))
                return false;

            if (IconCache.TryGetValue(basename, out var cached))
            {
                ptr = cached.Ptr; w = cached.W; h = cached.H;
                return ptr != IntPtr.Zero;
            }

            var file = Path.Join(dllDir, "icons", basename + ".png");
            if (!File.Exists(file))
            {
                IconCache[basename] = (IntPtr.Zero, 0, 0);
                return false;
            }

            try
            {
                Core.Overlay.AddOrGetImagePointer(file, false, out var p, out var iw, out var ih);
                IconCache[basename] = (p, (int)iw, (int)ih);
                ptr = p; w = (int)iw; h = (int)ih;
                return p != IntPtr.Zero;
            }
            catch
            {
                IconCache[basename] = (IntPtr.Zero, 0, 0);
                return false;
            }
        }

        private void LoadMaps()
        {
            var path = Path.Join(DllDirectory, "json", "maps.json");
            MapInfos.Clear();
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var contents = JsonConvert.DeserializeObject<Dictionary<string, MapInfo>>(json);
            if (contents is null)
                return;

            foreach (var kv in contents)
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
                    MapInfos[kv.Key] = kv.Value;

            // Collect the language set for the dropdown (union of every entry's "translates" keys).
            var langs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in MapInfos.Values)
                if (info.Translates != null)
                    foreach (var lang in info.Translates.Keys)
                        langs.Add(lang);
            langs.Add("english"); // always selectable even if maps.json lacks translations
            AvailableLanguages.Clear();
            AvailableLanguages.AddRange(langs);
        }

        // Look up the maps.json entry for an internal MapId (null when unmapped).
        private static MapInfo GetMapInfo(string internalId) =>
            !string.IsNullOrWhiteSpace(internalId) && MapInfos.TryGetValue(internalId, out var info) ? info : null;

        // Localized display name for the selected language: translates[lang] → English name → internal id.
        private static string ResolveLocalizedName(string internalId, MapInfo info, string lang)
        {
            if (info != null)
            {
                if (info.Translates != null && !string.IsNullOrWhiteSpace(lang)
                    && info.Translates.TryGetValue(lang, out var t) && !string.IsNullOrWhiteSpace(t))
                    return NormalizeName(t);
                if (!string.IsNullOrWhiteSpace(info.Name))
                    return NormalizeName(info.Name);
            }
            return internalId;
        }

        private static Vector2 ComputeScalePair(in UiElementBaseOffset uiBase)
        {
            var io = ImGui.GetIO();
            float baseW = (float)UiElementBaseFuncs.BaseResolution.X;
            float baseH = (float)UiElementBaseFuncs.BaseResolution.Y;
            float sx = io.DisplaySize.X / MathF.Max(1f, baseW);
            float sy = io.DisplaySize.Y / MathF.Max(1f, baseH);

            Vector2 pair;
            switch (uiBase.ScaleIndex)
            {
                case 0:
                    pair = new Vector2(sx, sx);
                    break;
                case 1:
                    pair = new Vector2(sy, sy);
                    break;
                case 2:
                    float s = MathF.Min(sx, sy);
                    pair = new Vector2(s, s);
                    break;
                default:
                    pair = new Vector2(sx, sy);
                    break;
            }

            return pair * MathF.Max(0.0001f, uiBase.LocalScaleMultiplier);
        }

        private static float ComputeUniformScale(in UiElementBaseOffset uiBase, float dispW, float dispH)
        {
            float baseW = (float)UiElementBaseFuncs.BaseResolution.X;
            float baseH = (float)UiElementBaseFuncs.BaseResolution.Y;
            float sx = dispW / MathF.Max(1f, baseW);
            float sy = dispH / MathF.Max(1f, baseH);

            float s = uiBase.ScaleIndex switch
            {
                0 => sx,
                1 => sy,
                2 => MathF.Min(sx, sy),
                _ => MathF.Min(sx, sy),
            };

            return s * MathF.Max(0.0001f, uiBase.LocalScaleMultiplier);
        }

        private static float ComputeRelativeUiScale(in UiElementBaseOffset uiBase, float refW, float refH)
        {
            var io = ImGui.GetIO();
            float cur = ComputeUniformScale(in uiBase, io.DisplaySize.X, io.DisplaySize.Y);
            float pref = ComputeUniformScale(in uiBase, refW, refH);

            return pref > 0 ? cur / pref : 1f;
        }

        private static Vector2 GetFinalTopLeft(in UiElementBaseOffset leaf)
        {
            Vector2 pos = Vector2.Zero;
            UiElementBaseOffset cur = leaf;
            int guard = 0;
            IntPtr last = IntPtr.Zero;
            while (true)
            {
                var scale = ComputeScalePair(in cur);
                pos += new Vector2(cur.RelativePosition.X * scale.X,
                    cur.RelativePosition.Y * scale.Y);

                if (UiElementBaseFuncs.ShouldModifyPos(cur.Flags))
                {
                    pos += new Vector2(cur.PositionModifier.X * scale.X,
                        cur.PositionModifier.Y * scale.Y);
                }

                if (cur.ParentPtr == IntPtr.Zero || cur.ParentPtr == last || ++guard > 64)
                    break;

                last = cur.Self;
                cur = ReadBaseCached(cur.ParentPtr);
            }

            return pos;
        }

        // O(1) screen top-left for a leaf whose ancestor chain is shared with other leaves: the parent
        // container's accumulated offset is computed once per frame (parentOffsetCache) and reused, so
        // we don't walk the whole chain for every node. Equivalent to GetFinalTopLeft(in leaf).
        private static Vector2 GetLeafTopLeft(in UiElementBaseOffset leaf)
        {
            Vector2 parentOffset;
            if (leaf.ParentPtr == IntPtr.Zero)
            {
                parentOffset = Vector2.Zero;
            }
            else if (!parentOffsetCache.TryGetValue(leaf.ParentPtr, out parentOffset))
            {
                var parent = ReadBaseCached(leaf.ParentPtr);
                parentOffset = GetFinalTopLeft(in parent);
                parentOffsetCache[leaf.ParentPtr] = parentOffset;
            }

            var scale = ComputeScalePair(in leaf);
            var pos = parentOffset + new Vector2(leaf.RelativePosition.X * scale.X, leaf.RelativePosition.Y * scale.Y);
            if (UiElementBaseFuncs.ShouldModifyPos(leaf.Flags))
                pos += new Vector2(leaf.PositionModifier.X * scale.X, leaf.PositionModifier.Y * scale.Y);
            return pos;
        }

        // Per-frame-cached UiElementBase read — atlas nodes share their ancestor chain, so the
        // parent walk in GetFinalTopLeft reads each ancestor at most once per frame.
        private static UiElementBaseOffset ReadBaseCached(IntPtr addr)
        {
            if (frameBaseCache.TryGetValue(addr, out var cached))
                return cached;
            var v = Read<UiElementBaseOffset>(addr);
            frameBaseCache[addr] = v;
            return v;
        }

        private static void DrawSquares(ImDrawListPtr drawList, List<ContentInfo> infos, float centerX,
            ref float nextRowTopY, float rowGap, float uiScale)
        {
            if (infos.Count == 0)
                return;

            const float fixedHeightBase = 18f;
            const float paddingBase = 6f;
            float fixedHeight = fixedHeightBase * uiScale;
            float padding = paddingBase * uiScale;

            var widths = new List<float>(infos.Count);
            float totalW = 0f;

            foreach (var info in infos)
            {
                var abbrev = string.IsNullOrWhiteSpace(info.Abbrev) ? info.Label[..1] : info.Abbrev;
                var textSize = ImGui.CalcTextSize(abbrev);
                float w = MathF.Max(fixedHeight, textSize.X + padding);
                widths.Add(w);
                totalW += w;
            }

            var basePos = new Vector2(centerX - totalW * 0.5f, nextRowTopY);

            for (int i = 0; i < infos.Count; i++)
            {
                var info = infos[i];
                string abbrev;
                if (string.IsNullOrWhiteSpace(info.Abbrev))
                    abbrev = !string.IsNullOrEmpty(info.Label) ? info.Label.Substring(0, 1) : "?";
                else
                    abbrev = info.Abbrev;
                var boxSize = new Vector2(widths[i], fixedHeight);
                var squareMin = basePos;
                var squareMax = squareMin + boxSize;

                drawList.AddRectFilled(squareMin, squareMax, ImGuiHelper.Color(info.BgColor));

                var textSize = ImGui.CalcTextSize(abbrev);
                var textPos = squareMin + (boxSize - textSize) * 0.5f;
                drawList.AddText(textPos, ImGuiHelper.Color(info.FtColor), abbrev);

                basePos.X += boxSize.X;
            }

            nextRowTopY += fixedHeight + rowGap;
        }

        // Draw a centered rounded pill with a label. centerX/topY = top-center anchor.
        // Returns the pill height (so callers can advance their layout cursor).
        // Rating for a map by its canonical English display name; -1 when unrated.
        private int GetMapRating(MapInfo info) =>
            info?.Name != null && Settings.MapRatings.TryGetValue(info.Name, out var r) ? r : -1;

        // Rating pill background: green (0 = normal) → yellow (5) → red (10 = terrible).
        private static Vector4 RatingColor(int rating)
        {
            float t = Math.Clamp(rating / 10f, 0f, 1f);
            var green = new Vector4(0.10f, 0.72f, 0.15f, 0.92f);
            var yellow = new Vector4(0.95f, 0.80f, 0.05f, 0.92f);
            var red = new Vector4(0.85f, 0.08f, 0.08f, 0.92f);
            return t < 0.5f ? Vector4.Lerp(green, yellow, t * 2f) : Vector4.Lerp(yellow, red, (t - 0.5f) * 2f);
        }

        // Black text on light (yellow-ish) pills, white on dark green/red ones.
        private static Vector4 RatingTextColor(Vector4 bg) =>
            0.299f * bg.X + 0.587f * bg.Y + 0.114f * bg.Z > 0.55f
                ? new Vector4(0f, 0f, 0f, 1f)
                : new Vector4(1f, 1f, 1f, 1f);

        private static float DrawPill(ImDrawListPtr drawList, string label, float centerX, float topY,
            Vector4 bg, Vector4 fg, float uiScale)
        {
            const float fixedHeightBase = 18f;
            const float paddingBase = 8f;
            float fixedHeight = fixedHeightBase * uiScale;
            float padding = paddingBase * uiScale;

            var textSize = ImGui.CalcTextSize(label);
            float w = MathF.Max(fixedHeight, textSize.X + padding);
            var boxSize = new Vector2(w, fixedHeight);

            var min = new Vector2(centerX - w * 0.5f, topY);
            drawList.AddRectFilled(min, min + boxSize, ImGuiHelper.Color(bg), 3f * uiScale);
            var textPos = min + (boxSize - textSize) * 0.5f;
            drawList.AddText(textPos, ImGuiHelper.Color(fg), label);

            return fixedHeight;
        }

        // Draw N pips (small filled dots) = number of content markers on the node, one per content
        // item. Reliable for every node incl. off-screen; the exact content TYPE isn't persisted by
        // the client (rolled from a per-node seed) so only the count is shown. See re-findings §2.7.
        private static void DrawContentDots(ImDrawListPtr drawList, int count, float centerX,
            ref float nextRowTopY, float rowGap, float uiScale)
        {
            if (count <= 0)
                return;

            float radius = 3.5f * uiScale;
            float gap = 4f * uiScale;
            float step = radius * 2f + gap;
            float totalW = count * (radius * 2f) + MathF.Max(0, count - 1) * gap;

            float cy = nextRowTopY + radius;
            float startX = centerX - totalW * 0.5f + radius;

            var fill = ImGuiHelper.Color(new Vector4(1f, 0.78f, 0.27f, 1f));   // amber
            var outline = ImGuiHelper.Color(new Vector4(0f, 0f, 0f, 0.85f));

            for (int i = 0; i < count; i++)
            {
                var c = new Vector2(startX + i * step, cy);
                drawList.AddCircleFilled(c, radius, fill);
                drawList.AddCircle(c, radius, outline, 0, MathF.Max(1f, radius * 0.4f));
            }

            nextRowTopY += radius * 2f + rowGap;
        }

        // Draw a centered row of content markers ABOVE the map name. Each name renders as its in-game
        // icon (icons\<basename>.png, drawn at iconH px) when showIcons is on and the texture exists;
        // otherwise as a text chip when showNames is on (so content without an icon still appears).
        // Mixed rows are fine; the row height is the tallest item and shorter items are centered.
        // Reused across calls (single-threaded render) so the per-node draw path allocates nothing.
        // display = text actually drawn (localized for chips); key = canonical English name for the
        // icon lookup / hover-tooltip key (kept English so both stay language-independent).
        private static readonly List<(bool isIcon, IntPtr ptr, float w, float h, string display, string key)> RowScratch = new();
        private static string DrawContentRow(ImDrawListPtr drawList, IReadOnlyList<string> names, string dllDir,
            Vector2 drawPosition, Vector2 textSize, float uiScale, bool showIcons, bool showNames, float iconH,
            Vector2 mousePos, Vector2 iconOffset)
        {
            var items = RowScratch;
            items.Clear();

            float sumW = 0f, maxH = 0f;
            foreach (var n in names)
            {
                if (showIcons && NameToIcon.TryGetValue(n, out var basename)
                    && TryGetIcon(dllDir, basename, out var p, out var iw, out var ih) && iw > 0 && ih > 0)
                {
                    float w = iconH * iw / ih;
                    items.Add((true, p, w, iconH, null, n));
                    sumW += w; if (iconH > maxH) maxH = iconH;
                }
                else if (showNames)
                {
                    var display = LocalizedName(n);
                    var ts = ImGui.CalcTextSize(display);
                    items.Add((false, IntPtr.Zero, ts.X, ts.Y, display, n));
                    sumW += ts.X; if (ts.Y > maxH) maxH = ts.Y;
                }
            }

            if (items.Count == 0)
                return null;

            float gap = 4f * uiScale;
            float totalW = sumW + gap * (items.Count - 1);
            float rowH = maxH;
            float startX = drawPosition.X + textSize.X * 0.5f - totalW * 0.5f;
            float topY = drawPosition.Y - rowH - 2f * uiScale;

            var pad = new Vector2(3, 1) * uiScale;
            drawList.AddRectFilled(new Vector2(startX, topY) - pad, new Vector2(startX + totalW, topY + rowH) + pad,
                ImGuiHelper.Color(new Vector4(0f, 0f, 0f, 0.8f)), 3f * uiScale);

            float x = startX;
            string hovered = null;
            var textColor = ImGuiHelper.Color(new Vector4(0.3f, 0.95f, 1f, 1f));
            foreach (var it in items)
            {
                float y = topY + (rowH - it.h) * 0.5f;
                // Icons can be nudged by the user (ContentIconOffset); text chips stay put.
                float ix = it.isIcon ? x + iconOffset.X : x;
                float iy = it.isIcon ? y + iconOffset.Y : y;
                if (it.isIcon)
                    drawList.AddImage(it.ptr, new Vector2(ix, iy), new Vector2(ix + it.w, iy + it.h));
                else
                    drawList.AddText(new Vector2(ix, iy), textColor, it.display);

                // Hit-test the cursor against this marker's rect (the overlay tracks the atlas-screen
                // cursor); the hovered (English) key drives the tooltip drawn after the node pass.
                if (mousePos.X >= ix && mousePos.X <= ix + it.w && mousePos.Y >= iy && mousePos.Y <= iy + it.h)
                    hovered = it.key;

                x += it.w + gap;
            }

            return hovered;
        }

        private readonly struct FontScaleScope : IDisposable
        {
            private readonly ImFontPtr _font;
            private readonly float _prevScale;
            public FontScaleScope(float scale)
            {
                _font = ImGui.GetFont();
                _prevScale = _font.Scale;
                _font.Scale = _prevScale * scale;
                ImGui.PushFont(_font);
            }
            public void Dispose()
            {
                ImGui.PopFont();
                _font.Scale = _prevScale;
            }
        }

        private static Vector2 GetLineRectangleIntersection(Vector2 lineStart, Vector2 rectCenter, Vector2 rectMin, Vector2 rectMax)
        {
            if (lineStart.X >= rectMin.X && lineStart.X <= rectMax.X &&
                lineStart.Y >= rectMin.Y && lineStart.Y <= rectMax.Y)
                return lineStart;

            Vector2 direction = rectCenter - lineStart;

            float dirX = direction.X == 0 ? 1e-6f : direction.X;
            float dirY = direction.Y == 0 ? 1e-6f : direction.Y;

            float tMinX = (rectMin.X - lineStart.X) / dirX;
            float tMaxX = (rectMax.X - lineStart.X) / dirX;
            float tMinY = (rectMin.Y - lineStart.Y) / dirY;
            float tMaxY = (rectMax.Y - lineStart.Y) / dirY;

            if (tMinX > tMaxX)
                (tMaxX, tMinX) = (tMinX, tMaxX);

            if (tMinY > tMaxY)
                (tMaxY, tMinY) = (tMinY, tMaxY);

            float tEnter = Math.Max(tMinX, tMinY);
            float tExit = Math.Min(tMaxX, tMaxY);

            if (tEnter > tExit || tEnter < 0)
                return rectCenter;

            float t = Math.Min(tEnter, 1.0f);

            return lineStart + direction * t;
        }

        private static Vector2 OffsetPointOutsideRect(Vector2 borderPoint, Vector2 rectCenter, float distance)
        {
            var dir = borderPoint - rectCenter;
            float lenSq = dir.X * dir.X + dir.Y * dir.Y;
            if (lenSq< 1e-6f)
                return borderPoint;
            dir /= MathF.Sqrt(lenSq);

            return borderPoint + dir* distance;
        }

        private void MoveMapGroup(int index, int direction)
        {
            if (index < 0 || index >= Settings.MapGroups.Count)
                return;

            int to = index + direction;
            if (to < 0 || to >= Settings.MapGroups.Count)
                return;

            var item = Settings.MapGroups[index];
            Settings.MapGroups.RemoveAt(index);
            Settings.MapGroups.Insert(to, item);
        }

        private void DeleteMapGroup(int index)
        {
            if (index < 0 || index >= Settings.MapGroups.Count)
                return;

            Settings.MapGroups.RemoveAt(index);
        }

        private static void ColorSwatch(string label, ref Vector4 color)
        {
            if (ImGui.ColorButton(label, color))
                ImGui.OpenPopup(label);

            if (ImGui.BeginPopup(label))
            {
                ImGui.ColorPicker4(label, ref color);
                ImGui.EndPopup();
            }
        }

        private static bool TriangleButton(string id, float buttonSize, Vector4 color, bool isUp)
        {
            var pressed = ImGui.Button(id, new Vector2(buttonSize, buttonSize));
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetItemRectMin();
            var triSize = buttonSize * 0.5f;
            var center = new Vector2(pos.X + buttonSize * 0.5f, pos.Y + buttonSize * 0.5f);

            Vector2 p1, p2, p3;
            if (isUp)
            {
                p1 = new Vector2(center.X, center.Y - triSize * 0.5f);
                p2 = new Vector2(center.X - triSize * 0.5f, center.Y + triSize * 0.5f);
                p3 = new Vector2(center.X + triSize * 0.5f, center.Y + triSize * 0.5f);
            }
            else
            {
                p1 = new Vector2(center.X - triSize * 0.5f, center.Y - triSize * 0.5f);
                p2 = new Vector2(center.X + triSize * 0.5f, center.Y - triSize * 0.5f);
                p3 = new Vector2(center.X, center.Y + triSize * 0.5f);
            }

            drawList.AddTriangleFilled(p1, p2, p3, ImGuiHelper.Color(color));

            return pressed;
        }

        private static void EnsureProcessHandle()
        {
            int pid = (int)Core.Process.Pid;
            if (Handle == IntPtr.Zero)
            {
                Handle = ProcessMemoryUtilities.Managed.NativeWrapper.OpenProcess(
                               ProcessMemoryUtilities.Native.ProcessAccessFlags.Read, pid);
                _handlePid = pid;

                return;
            }

            if (_handlePid != pid)
            {
                CloseAndResetHandle();
                Handle = ProcessMemoryUtilities.Managed.NativeWrapper.OpenProcess(
                               ProcessMemoryUtilities.Native.ProcessAccessFlags.Read, pid);
                _handlePid = pid;
            }
        }

        private static void CloseAndResetHandle()
        {
            if (Handle != IntPtr.Zero)
            {
                CloseHandle(Handle);
                Handle = IntPtr.Zero;
            }
            _handlePid = 0;
        }

        public static T Read<T>(IntPtr address) where T : unmanaged
        {
            if (address == IntPtr.Zero)
                return default;

            EnsureProcessHandle();
            T result = default;
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemory(Handle, address, ref result);

            return result;
        }

        private static bool TryVectorCount<T>(in StdVector vector, out int count)
            where T : unmanaged
        {
            count = 0;
            if (vector.First == IntPtr.Zero || vector.Last == IntPtr.Zero)
                return false;

            long bytes = vector.Last.ToInt64() - vector.First.ToInt64();
            if (bytes <= 0)
                return false;

            int stride = Marshal.SizeOf<T>();
            if (stride <= 0 || (bytes % stride) != 0)
                return false;

            long c = bytes / stride;
            if (c <= 0 || c > 10000)
                return false;

            count = (int)c;

            return true;
        }

        private static T ReadVectorAt<T>(in StdVector vector, int index)
            where T : unmanaged
        {
            int stride = Marshal.SizeOf<T>();
            var addr = IntPtr.Add(vector.First, index * stride);

            return Read<T>(addr);
        }

        // MSVC std::wstring (SSO): length @ +0x10, capacity @ +0x18; chars inline @ +0x00 while
        // capacity < 8, otherwise +0x00 is the heap buffer pointer. Same layout the game uses for
        // UI label text (see docs/uitree-guide.md).
        private static string ReadGameWString(IntPtr address)
        {
            if (address == IntPtr.Zero)
                return string.Empty;

            long len = Read<long>(IntPtr.Add(address, 0x10));
            long cap = Read<long>(IntPtr.Add(address, 0x18));
            if (len <= 0 || len > 2048 || cap < len)
                return string.Empty;

            var src = cap >= 8 ? Read<IntPtr>(address) : address;
            return src == IntPtr.Zero ? string.Empty : ReadWideString(src, (int)len);
        }

        // Session-dedup of ritual snapshots already written (signature → skip re-append).
        private readonly HashSet<string> ritualLogSeen = new();
        private bool ritualLogHeaderDone;

        // grid -> predicted first Rite mod for the current line's next candidates (see BuildRitualPredictions).
        private static readonly Dictionary<StdTuple2D<int>, string> EmptyRitualPredictions = new();
        private Dictionary<StdTuple2D<int>, string> ritualPredictions = EmptyRitualPredictions;
        // Node under the cursor (accessible/completed only) — the hypothetical START for the
        // pre-click ritual chain while no node is committed yet. One-frame lag by design:
        // predictions build before the node pass hit-tests the cursor.
        private StdTuple2D<int>? ritualHoverGrid;

        // Reads the committed line grids (panel+0x660) as (x,y) int pairs.
        private static List<StdTuple2D<int>> ReadGridVector(IntPtr vecAddr)
        {
            var result = new List<StdTuple2D<int>>();
            var vec = Read<StdVector>(vecAddr);
            if (vec.First == IntPtr.Zero || vec.Last == IntPtr.Zero)
                return result;
            long bytes = vec.Last.ToInt64() - vec.First.ToInt64();
            if (bytes <= 0 || bytes % 8 != 0 || bytes > 8 * 64)
                return result;
            int n = (int)(bytes / 8);
            for (int i = 0; i < n; i++)
                result.Add(Read<StdTuple2D<int>>(IntPtr.Add(vec.First, i * 8)));
            return result;
        }

        // Read the panel's precomputed next-candidate table (panel+0x590) into a map
        // node(x,y) -> its raw candidate list (up to 5, (0,0) sentinels dropped). The table is what
        // AtlasPanel_ritualLineNextCandidates looks up; the roll's candIdx is a node's rank among the
        // frontier's candidates. We read the whole span in one cross-process read and parse locally.
        // Cache: the neighbour table is static per atlas instance, so re-read only when its backing
        // vector changes (atlas reload). Keyed by the (begin, end) pointer pair — begin alone can
        // collide when the allocator reuses the base address for a different atlas's table.
        private static IntPtr candTableCacheBegin = IntPtr.Zero;
        private static IntPtr candTableCacheEnd = IntPtr.Zero;
        // Span already re-read once because a frontier lookup missed (see below) — a legit
        // dead-end frontier must not force a full re-read every frame.
        private static IntPtr candTableHealedBegin = IntPtr.Zero;
        private static IntPtr candTableHealedEnd = IntPtr.Zero;
        private static Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>> candTableCache;

        private static Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>> ReadCandidateTable(
            IntPtr panel, StdTuple2D<int>? frontier = null)
        {
            var map = new Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>>();
            if (panel == IntPtr.Zero)
                return map;
            var begin = Read<IntPtr>(IntPtr.Add(panel, PanelCandTableBeginOffset));
            var end = Read<IntPtr>(IntPtr.Add(panel, PanelCandTableEndOffset));
            if (begin == IntPtr.Zero || end == IntPtr.Zero)
                return map;
            if (begin == candTableCacheBegin && end == candTableCacheEnd && candTableCache != null)
            {
                // Self-heal a stale cache: the line's frontier node always belongs to its own
                // atlas's table, so a miss means the cache was filled from another atlas instance
                // (pointer reuse) or from a not-yet-populated load frame. Drop it and re-read —
                // at most once per table span.
                bool healedThisSpan = begin == candTableHealedBegin && end == candTableHealedEnd;
                if (frontier == null || healedThisSpan || candTableCache.ContainsKey(frontier.Value))
                    return candTableCache;
                candTableCache = null;
                candTableHealedBegin = begin;
                candTableHealedEnd = end;
            }
            long bytes = end.ToInt64() - begin.ToInt64();
            // The live table is ~4k nodes; allow up to 64k entries. Must be a whole number of entries.
            if (bytes <= 0 || bytes % CandTableEntryStride != 0 || bytes > CandTableEntryStride * 65536L)
                return map;
            int n = (int)(bytes / CandTableEntryStride);

            EnsureProcessHandle();
            byte[] buf = new byte[bytes];
            if (!ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemoryArray(Handle, begin, buf))
                return map;

            bool anyCands = false;
            for (int e = 0; e < n; e++)
            {
                int o = e * CandTableEntryStride;
                int nx = BitConverter.ToInt32(buf, o + 0);
                int ny = BitConverter.ToInt32(buf, o + 4);
                var cands = new List<StdTuple2D<int>>(CandTableMaxCandidates);
                for (int c = 0; c < CandTableMaxCandidates; c++)
                {
                    int co = o + 8 + c * 12;      // ints [2..], 3 per candidate (x,y,extra)
                    int cx = BitConverter.ToInt32(buf, co + 0);
                    int cy = BitConverter.ToInt32(buf, co + 4);
                    if (cx == 0 && cy == 0)
                        continue;                 // empty slot sentinel
                    cands.Add(new StdTuple2D<int> { X = cx, Y = cy });
                }
                if (cands.Count > 0)
                    anyCands = true;
                map[new StdTuple2D<int> { X = nx, Y = ny }] = cands;
            }
            // On an atlas-load frame the vector can exist while its entries are still zero-filled —
            // every slot parses as the (0,0) sentinel. Never cache that: the begin/end pointers
            // won't change afterwards, so the garbage would stick until a GH restart.
            if (anyCands)
            {
                candTableCacheBegin = begin;
                candTableCacheEnd = end;
                candTableCache = map;
            }
            return map;
        }

        // ── Ritual Rite-mod PREDICTION (reversed client-side roll). See obsidian poe2/Ritual.md ──
        // The game rolls each line node's Rite mods CLIENT-SIDE and deterministically. We reproduce
        // the roll so a candidate's mods can be shown BEFORE it is committed. Validated exact for the
        // first mod (single-mod nodes 6/6).

        // TinyMT32 exactly as the game uses it (mat1/mat2/tmat below). init_by_array over 4 u32 seed
        // words + an 8-step jump; then a tempered draw. Bit-exact vs TinyMT_seedAndJump (14156b620),
        // next32 (1404e16d0) and randBelow (1404e17a0). NOTE the state transition is the binary's form
        // (x>>1 / s3<<1), which differs from reference TinyMT (x<<1 / y>>1).
        private static class TinyMt32
        {
            private const uint MAT1 = 0x8f7011eeu, MAT2 = 0xfc78ff1fu, TMAT = 0x3793fdffu;

            // seed+jump; returns the 4-word state [s0,s1,s2,s3] (the binary's counter is unused here).
            public static uint[] Seed(uint w0, uint w1, uint w2, uint w3)
            {
                uint[] s = { 0x40336052u, 0xCFA3723Cu, 0x3CAC5F71u, 0x3793FDFFu }; // post-pre-step consts
                uint[] w = { w0, w1, w2, w3 };
                int r = 1;
                for (int i = 0; i < 4; i++)              // absorb 4 words (ini_func1)
                {
                    int a = (r + 1) & 3, b = r & 3, c = (r + 3) & 3;
                    uint x = s[a] ^ s[c] ^ s[b];
                    uint h = ((x >> 27) ^ x) * 0x19660Du;
                    s[a] += h;
                    uint h2 = h + w[i] + (uint)r;
                    s[(r + 2) & 3] += h2;
                    s[b] = h2;
                    r = a;
                }
                for (int k = 0; k < 3; k++)              // 3 mix rounds (ini_func1, no input)
                {
                    int a = (r + 1) & 3, b = r & 3, c = (r + 3) & 3;
                    uint x = s[a] ^ s[c] ^ s[b];
                    uint h = ((x >> 27) ^ x) * 0x19660Du;
                    uint h2 = h + (uint)r;
                    s[a] += h;
                    s[(r + 2) & 3] += h2;
                    s[b] = h2;
                    r = a;
                }
                for (int k = 0; k < 4; k++)              // 4 finalization blocks (ini_func2)
                {
                    int a = (r + 1) & 3, b = r & 3, c = (r + 3) & 3;
                    uint x = s[c] + s[a] + s[b];
                    x = ((x >> 27) ^ x) * 0x5D588B65u;
                    uint y = x - (uint)r;
                    s[a] ^= x;
                    s[(r + 2) & 3] ^= y;
                    s[b] = y;
                    r = a;
                }
                for (int k = 0; k < 8; k++) NextState(s); // jump
                return s;
            }

            private static void NextState(uint[] s)
            {
                uint x = (s[0] & 0x7fffffffu) ^ s[1] ^ s[2];
                uint t = s[3] ^ (s[3] << 1);
                x = (x >> 1) ^ x ^ t;
                uint mag = (x & 1) != 0 ? 0xffffffffu : 0u;
                uint oldS1 = s[1], oldS2 = s[2];
                s[0] = oldS1;
                s[1] = (mag & MAT1) ^ oldS2;
                s[2] = (mag & MAT2) ^ (x << 10) ^ t;
                s[3] = x;
            }

            // one tempered 32-bit output; advances the state (== next32 inner body).
            public static uint Draw(uint[] s)
            {
                uint oldS1 = s[1], oldS2 = s[2];
                uint x = (s[0] & 0x7fffffffu) ^ s[1] ^ s[2];
                uint t = s[3] ^ (s[3] << 1);
                x = (x >> 1) ^ x ^ t;
                uint mag = (x & 1) != 0 ? 0xffffffffu : 0u;
                uint newS2 = (mag & MAT2) ^ (x << 10) ^ t;
                s[0] = oldS1;
                s[1] = (mag & MAT1) ^ oldS2;
                s[2] = newS2;
                s[3] = x;
                uint v = (newS2 >> 8) + oldS1;
                uint magt = (v & 1) != 0 ? 0xffffffffu : 0u;
                return (magt & TMAT) ^ v ^ x;
            }

            // unbiased r in [0,n) with the binary's rejection (bits=32, mask=0xffffffff).
            public static uint RandBelow(uint[] s, uint n)
            {
                if (n <= 1) return 0;
                const uint M = 0xffffffffu;
                while (true)
                {
                    uint r = Draw(s);
                    if (M / n <= r / n && M % n != n - 1) continue;
                    return r % n;
                }
            }
        }

        private sealed class RitualRow
        {
            public int Row { get; set; }
            public int W { get; set; }       // weighting
            public int Cond { get; set; }    // ConditionStat FK (0 = none); binary id = Cond-1
            public int Stat { get; set; }    // granted Stat1 FK — 2nd-pick dup exclusion (0 = none)
            public string Text { get; set; }
        }
        private sealed class RitualPoolFile { public List<RitualRow> Rows { get; set; } }
        private static List<RitualRow> ritualPool;

        private void EnsureRitualPool()
        {
            if (ritualPool != null) return;
            try
            {
                var path = Path.Join(DllDirectory, "json", "ritualmods.json");
                ritualPool = File.Exists(path)
                    ? (JsonConvert.DeserializeObject<RitualPoolFile>(File.ReadAllText(path))?.Rows ?? new())
                    : new();
            }
            catch { ritualPool = new(); }
        }

        // Read the panel's active atlas stats (id -> value, value!=0 only). Chain from
        // ritualLineToggleNode: panel+0x320 -> +0x1b0 -> +0x3a20 -> vector [+0x408 begin, +0x410 end],
        // stride 0x28 (10 int32): stat id @ +0x00, value @ +0x08. Gates the reservoir pool and gives
        // the line length (5 + map_ritual_rite_additional_maps, binary id 0x670b).
        private static Dictionary<int, int> ReadRitualStats(IntPtr panel)
        {
            var stats = new Dictionary<int, int>();
            var o1 = Read<IntPtr>(IntPtr.Add(panel, 0x320));
            if (o1 == IntPtr.Zero) return stats;
            var o2 = Read<IntPtr>(IntPtr.Add(o1, 0x1b0));
            if (o2 == IntPtr.Zero) return stats;
            var holder = Read<IntPtr>(IntPtr.Add(o2, 0x3a20));
            if (holder == IntPtr.Zero) return stats;
            var begin = Read<IntPtr>(IntPtr.Add(holder, 0x408));
            var end = Read<IntPtr>(IntPtr.Add(holder, 0x410));
            if (begin == IntPtr.Zero || end == IntPtr.Zero) return stats;
            long bytes = end.ToInt64() - begin.ToInt64();
            if (bytes <= 0 || bytes % 0x28 != 0 || bytes > 0x28 * 8192L) return stats;
            int n = (int)(bytes / 0x28);
            EnsureProcessHandle();
            byte[] buf = new byte[bytes];
            if (!ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemoryArray(Handle, begin, buf))
                return stats;
            for (int e = 0; e < n; e++)
            {
                int id = BitConverter.ToInt32(buf, e * 0x28 + 0);
                int val = BitConverter.ToInt32(buf, e * 0x28 + 8);
                if (val != 0) stats[id] = val;
            }
            return stats;
        }

        // Whether a line node ALSO gets a second Rite mod: rand(100) < chance stat 0x670C
        // (map_ritual_rite_additional_modifier_chance_%), on a separate deterministic stream
        // seeded [lineId, committedCount, candIdx, salt] — the salt appears ONLY in this coin
        // flip, never in the mod-pick seed.
        private const int StatSecondModChance = 0x670c;
        private const uint SecondModCoinSalt = 0x91DA3AD9;
        private const string TwoModFilterOption = "[2 mods]";  // pseudo-entry in the reward dropdown

        private static bool PredictSecondModFlip(uint lineId, uint committedCount, uint candIdx, int chance)
        {
            if (chance <= 0)
                return false;
            if (chance >= 100)
                return true;
            var s = TinyMt32.Seed(lineId, committedCount, candIdx, SecondModCoinSalt);
            return TinyMt32.RandBelow(s, 100) < (uint)chance;
        }

        // One reservoir pass (seed modCount = 0 first mod / 1 second). The 2nd pass SKIPS —
        // no weight added, no draw — every row whose granted Stat the 1st pick already granted
        // (binary dup check FUN_14064cdc0 on the out-vector; currency trios share one stat so a
        // currency 1st mod blocks its whole trio). Validated 6/6 on logged two-mod nodes.
        private static RitualRow PredictModPass(uint lineId, uint committedCount, uint candIdx,
            uint modCount, List<RitualRow> pool, int grantedStat)
        {
            var s = TinyMt32.Seed(lineId, committedCount, candIdx, modCount);
            long total = 0; RitualRow sel = null;
            foreach (var row in pool)
            {
                if (grantedStat != 0 && row.Stat == grantedStat)
                    continue;
                total += row.W;
                if (TinyMt32.RandBelow(s, (uint)total) < (uint)row.W)
                    sel = row;
            }
            return sel;
        }

        // Game's AtlasPanel_ritualLineReachCheck (140b775f0), ported: a node may join the line
        // only if FROM it the line can still be extended to its full length through eligible
        // nodes (not committed / not already on the path / not blocked). The game refuses the
        // click otherwise — so dead-end branches must never be offered or rolled. `need` =
        // picks still required AFTER taking the node; first success short-circuits.
        private static bool RitualCanComplete(
            Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>> candTable,
            HashSet<StdTuple2D<int>> blocked,
            StdTuple2D<int> node,
            HashSet<StdTuple2D<int>> visited,
            int need)
        {
            if (need <= 0)
                return true;
            if (!candTable.TryGetValue(node, out var raw))
                return false;
            foreach (var c in raw)
            {
                if (blocked.Contains(c) || visited.Contains(c))
                    continue;
                visited.Add(c);
                bool ok = RitualCanComplete(candTable, blocked, c, visited, need - 1);
                visited.Remove(c);
                if (ok)
                    return true;
            }

            return false;
        }

        // Both Rite mods for a candidate: first pick, then the deterministic coin flip, then the
        // second pick with the stat-dup exclusion. Second is null on single-mod nodes.
        private static (string First, string Second) PredictMods(uint lineId, uint committedCount,
            uint candIdx, List<RitualRow> pool, int secondChance)
        {
            var first = PredictModPass(lineId, committedCount, candIdx, 0, pool, 0);
            if (first == null)
                return (null, null);
            if (!PredictSecondModFlip(lineId, committedCount, candIdx, secondChance))
                return (first.Text, null);
            var second = PredictModPass(lineId, committedCount, candIdx, 1, pool, first.Stat);
            return (first.Text, second?.Text);
        }

        // ── "Select N maps" pick counter ─────────────────────────────────────────────────
        // While drawing the ritual line the game shows a header with how many maps can still
        // be picked (the first pick — the start node — consumes one). Authoritative live value,
        // so it overrides the computed 5 + additional-maps stat when readable.
        // GameUi → [22] → [2] → [0], leaf fp 0x502EE1, wstring at +0x4C0 (found via UiExplorer).
        private static readonly int[] RitualPickCounterPath = { 22, 2, 0 };
        private const uint RitualPickCounterFp = 0x502EE1;

        // Reads the counter as the first integer in the label text (locale-independent).
        // False when the element is absent/hidden/moved (index path or fp drifted) or the
        // number is implausible — callers fall back to the stat-derived line length.
        private static bool TryReadRitualPickCounter(out int remaining)
        {
            remaining = 0;
            var addr = Core.States.InGameStateObject.GameUi.Address;
            if (addr == IntPtr.Zero)
                return false;
            foreach (var idx in RitualPickCounterPath)
            {
                addr = Read<UiElement>(addr).GetChildAddress(idx);
                if (addr == IntPtr.Zero)
                    return false;
            }

            var leaf = Read<UiElement>(addr);
            if ((leaf.Flags & ~IsVisibleMask) != (RitualPickCounterFp & ~IsVisibleMask)
                || (leaf.Flags & IsVisibleMask) == 0)
                return false;

            var text = ReadGameWString(IntPtr.Add(addr, TextElementTextOffset));
            if (string.IsNullOrEmpty(text))
                return false;
            int n = 0;
            bool seen = false;
            foreach (var ch in text)
            {
                if (ch >= '0' && ch <= '9') { n = (n * 10) + (ch - '0'); seen = true; }
                else if (seen) break;
            }

            if (!seen || n <= 0 || n > 30)
                return false;
            remaining = n;
            return true;
        }

        // Line-length atlas stat (binary id = tsv id - 1). map_ritual_rite_additional_maps.
        private const int StatAdditionalMaps = 0x670b;
        private const int RitualBaseLineLength = 5;   // AtlasPanel_ritualLineToggleNode: stat + 5
        private const int RitualMaxLookaheadDepth = 16;
        private const int RitualMaxPredictNodes = 4000;

        // Cache: predictions only change when the line state (id + committed set) changes.
        private string ritualPredSig;
        private Dictionary<StdTuple2D<int>, string> ritualPredCache = EmptyRitualPredictions;

        // Predict the Rite mods for EVERY node the ritual line can still reach from its current
        // frontier, up to the line's max length.
        // Each node's mod is rolled for the path that reaches it (committedCount = base + depth;
        // candIdx = its rank among the frontier's candidates minus the committed path). Returns
        // grid -> predicted first-mod text. Cached per line-state.
        private Dictionary<StdTuple2D<int>, string> BuildRitualPredictions(IntPtr panel)
        {
            if (panel == IntPtr.Zero) return EmptyRitualPredictions;
            EnsureRitualPool();
            if (ritualPool == null || ritualPool.Count == 0) return EmptyRitualPredictions;

            // Hover preview ONLY, and only BEFORE the first node is picked: once the line has a
            // start (committed, or clicked-but-unconfirmed pending), the planner window owns the
            // route display and the always-on green chain would just be noise on the atlas.
            var committed = ReadGridVector(IntPtr.Add(panel, PanelCommittedVecOffset));
            int committedReal = committed.Count;   // before a hypothetical start is inserted
            if (committed.Count > 0)
                return EmptyRitualPredictions;
            // Pre-click chain. The first click adds no randomness: lineId and the candidate
            // table exist before the line starts, and the start node itself is never rolled
            // (ritualLineToggleNode's empty-committed branch just adds it to pending). So the
            // whole chain from a hypothetical (hovered) start is already determined. Only while
            // the game is actually in ritual line mode.
            if (Read<byte>(IntPtr.Add(panel, PanelLineModeOffset)) == 0)
                return EmptyRitualPredictions;
            if (ReadGridVector(IntPtr.Add(panel, PanelPendingVecOffset)).Count > 0)
                return EmptyRitualPredictions;
            if (this.ritualHoverGrid is { } start)
                committed.Add(start);
            else
                return EmptyRitualPredictions;

            uint lineId = Read<uint>(IntPtr.Add(panel, PanelLineIdOffset));

            // Signature — reuse the cached chain unless the line changed.
            var sb = new StringBuilder();
            sb.Append(lineId);
            foreach (var g in committed) sb.Append(';').Append(g.X).Append(',').Append(g.Y);
            var sig = sb.ToString();
            if (sig == ritualPredSig && ritualPredCache != null)
                return ritualPredCache;

            var result = new Dictionary<StdTuple2D<int>, string>();
            var candTable = ReadCandidateTable(panel, committed[committed.Count - 1]);
            var stats = ReadRitualStats(panel);

            var pool = new List<RitualRow>(ritualPool.Count);
            foreach (var row in ritualPool)
            {
                if (row.W <= 0) continue;
                if (row.Cond == 0 || stats.ContainsKey(row.Cond) || stats.ContainsKey(row.Cond - 1))
                    pool.Add(row);
            }

            int addlMaps = stats.TryGetValue(StatAdditionalMaps, out var am) ? am
                         : stats.TryGetValue(StatAdditionalMaps + 1, out var am2) ? am2 : 0;
            int lineLen = RitualBaseLineLength + Math.Max(0, addlMaps);
            int secondChance = stats.TryGetValue(StatSecondModChance, out var sc) ? sc
                             : stats.TryGetValue(StatSecondModChance + 1, out var sc2) ? sc2 : 0;
            // The in-game "Select N maps" header is the authoritative remaining-picks count
            // (assumed to decrement as nodes commit — the roll log records it for verification);
            // when readable it overrides the stat-derived length.
            if (TryReadRitualPickCounter(out var picksLeft))
                lineLen = committedReal + picksLeft;
            int maxDepth = Math.Min(RitualMaxLookaheadDepth, Math.Max(0, lineLen - committed.Count));

            // Nodes the line can never be drawn onto — the game's own reach-check rule:
            // completed (widget state ∉ {0,1}) or special-category map (RitualSpecial: the
            // dat-row field the game tests; uniques/towers/hideouts/citadels/bosses). The
            // maps.json tags stay as a fallback for a cache built before the toggle came on.
            // Blocked nodes KEEP their slot in the candidate rank space — the validated candIdx
            // model ranks the raw table minus committed only — but they get no predicted label
            // and the chain is not extended through them.
            var blocked = new HashSet<StdTuple2D<int>>();
            foreach (var nd in nodeCache)
            {
                if (nd.State == AtlasNodeState.CompletedBase
                    || nd.RitualSpecial
                    || string.Equals(nd.MapInfo?.Type, "unique", StringComparison.OrdinalIgnoreCase)
                    || (nd.MapInfo?.HasTag("tower") ?? false)
                    || (nd.MapInfo?.HasTag("hideout") ?? false))
                    blocked.Add(nd.GridPosition);
            }

            if (pool.Count > 0 && maxDepth > 0)
            {
                var frontier = committed[committed.Count - 1];
                var visited = new HashSet<StdTuple2D<int>>(committed);   // never revisit committed
                int budget = RitualMaxPredictNodes;
                // BFS by depth so each node is reached via its shallowest (most direct) path, giving
                // the committedCount/candIdx of that path. Recomputed live as the line is drawn, so the
                // chosen path stays exact; branches are a guide.
                var queue = new Queue<(StdTuple2D<int> node, HashSet<StdTuple2D<int>> cset, int depth)>();
                queue.Enqueue((frontier, new HashSet<StdTuple2D<int>>(committed), 0));
                while (queue.Count > 0 && budget > 0)
                {
                    var (node, cset, depth) = queue.Dequeue();
                    if (depth >= maxDepth) continue;
                    if (!candTable.TryGetValue(node, out var raw) || raw.Count == 0) continue;
                    var cands = raw.Where(c => !cset.Contains(c))
                                   .OrderBy(c => c.X).ThenBy(c => c.Y).ToList();
                    uint cc = (uint)cset.Count;   // = committed.Count + depth
                    for (int i = 0; i < cands.Count; i++)
                    {
                        if (budget <= 0) break;
                        var cand = cands[i];
                        if (visited.Contains(cand)) continue;   // reached already via a shallower path
                        if (blocked.Contains(cand)) { visited.Add(cand); continue; }  // holds rank i, can't join
                        // Game reach check (ritualLineReachCheck 140b775f0): a node is clickable
                        // only if the line can still be COMPLETED through it — a dead-end branch
                        // is refused by the game and must not be labeled (its roll never happens).
                        var reachSet = new HashSet<StdTuple2D<int>>(cset) { cand };
                        if (!RitualCanComplete(candTable, blocked, cand, reachSet, maxDepth - depth - 1))
                            continue;
                        visited.Add(cand);
                        var (first, second) = PredictMods(lineId, cc, (uint)i, pool, secondChance);
                        if (!string.IsNullOrEmpty(first))
                        {
                            result[cand] = second == null ? first : first + "\n" + second;
                            budget--;
                        }
                        var childSet = new HashSet<StdTuple2D<int>>(cset) { cand };
                        queue.Enqueue((cand, childSet, depth + 1));
                    }
                }
            }

            ritualPredSig = sig;
            ritualPredCache = result;
            return result;
        }

        // ── "Head of the King Rewards" planner (ritual line mode, page mode 6) ────────────────────────
        // Enumerates every chain the ritual line can take from EVERY eligible start node at once
        // (or only from the committed frontier while a line is being drawn), with each node's
        // predicted first Rite mod (exact, same roll as BuildRitualPredictions but per-path).
        // Shown as a window with a persisted multi-select reward filter (a chain matches when ANY
        // selected reward is in it); a ticked chain draws a ray from the player to its start plus
        // the highlighted route with reward labels — that's how you find WHERE the rewards you
        // filtered for are. See obsidian poe2/Ritual.md.
        private sealed class PlannerChain
        {
            public string Key;                    // stable id: root-onward grids joined
            public List<StdTuple2D<int>> Nodes;   // root (start/frontier) + picked nodes
            public List<string> ShortMods;        // 1st mod per picked node (aligned with Nodes[i+1])
            public List<string> ShortMods2;       // 2nd mod per picked node (null = single-mod)
            public string PathLine;               // "Bastille  >  Headland  >  …"
            public string ModsLine;               // "+25% Tribute   -   Exalted Orbs x2 + Omen: … "
            public int Weight;                    // sum of user reward weights over the chain's mods
        }

        private readonly List<PlannerChain> plannerChains = new();
        private string plannerSig;
        private int plannerStartCount;                 // eligible start nodes in the last rebuild
        private bool plannerLineActive;                // committed non-empty: root = the line's frontier
        private readonly Dictionary<string, int> plannerSelected = new();  // chain key -> palette slot
        private int plannerEnumerated;                 // paths found (incl. beyond the caps)
        private bool plannerCapped;
        private static List<string> plannerRewardOptions;  // distinct ShortModLabel values of the pool
        // Reward-weight edits bump the version; the planner re-weighs + re-sorts its cached
        // chains when the versions diverge (so edits apply live without a full re-enumeration).
        private int plannerWeightsVersion;
        private int plannerChainsWeightsVersion = -1;

        private static readonly Vector4[] PlannerPalette =
        {
            new(1f, 0.85f, 0.2f, 1f),   // yellow
            new(1f, 0.5f, 0.15f, 1f),   // orange
            new(1f, 0.3f, 0.3f, 1f),    // red
            new(0.3f, 0.85f, 1f, 1f),   // cyan
            new(0.45f, 1f, 0.45f, 1f),  // green
            new(0.9f, 0.45f, 1f, 1f),   // violet
        };
        private const int PlannerMaxPaths = 8192;  // global enumeration cap, fair-shared across starts
        private const int PlannerMaxRows = 200;    // rows drawn per frame (matches beyond it still counted)

        // Compact reward label for chain rows / route pills ("4 Exalted Orbs" → "Exalted Orbs x4").
        // Pattern-based over the known RitualAtlasLineMods texts; unknown texts pass through.
        private static readonly Dictionary<string, string> shortModCache = new();

        private static string ShortModLabel(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            if (shortModCache.TryGetValue(text, out var cached))
                return cached;

            string r;
            var m = System.Text.RegularExpressions.Regex.Match(text, @"^(\d+) (.+?Orbs?.*)$");
            if (m.Success)
                r = $"{m.Groups[2].Value} x{m.Groups[1].Value}";
            else if (text.StartsWith("Omen of ", StringComparison.OrdinalIgnoreCase))
                r = "Omen: " + text["Omen of ".Length..];
            else if (text.StartsWith("Contains a very rare Unique", StringComparison.OrdinalIgnoreCase))
                r = "Very Rare Unique";
            else if (text.StartsWith("Contains ", StringComparison.OrdinalIgnoreCase))
                r = text["Contains ".Length..];
            else if (text.Contains("additional pack", StringComparison.OrdinalIgnoreCase))
                r = "+Monster Packs";
            else if (text.Contains("no Cost the first time", StringComparison.OrdinalIgnoreCase))
                r = "+Free Reroll";
            else if (text.Contains("additional Favour reroll", StringComparison.OrdinalIgnoreCase))
                r = "+1 Reroll";
            else if (text.Contains("reduced Tribute", StringComparison.OrdinalIgnoreCase))
                r = "-Reroll Cost";
            else if (text.Contains("increased Tribute", StringComparison.OrdinalIgnoreCase))
                r = "+25% Tribute";
            else if (text.Contains("increased number of Favours", StringComparison.OrdinalIgnoreCase))
                r = "+Favours";
            else
                r = text;
            shortModCache[text] = r;
            return r;
        }

        private string GridDisplayName(StdTuple2D<int> g)
        {
            foreach (var nd in nodeCache)
                if (nd.GridPosition.Equals(g))
                    return nd.Drawable ? nd.MapName : $"({g.X},{g.Y})";
            return $"({g.X},{g.Y})";
        }

        // Every distinct reward the pool can roll, as the short labels the rows display —
        // the option list for the filter dropdown. Built once (the json pool is static).
        private void EnsureRewardOptions()
        {
            if (plannerRewardOptions != null)
                return;
            EnsureRitualPool();
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in ritualPool)
                if (row.W > 0 && !string.IsNullOrEmpty(row.Text))
                    set.Add(ShortModLabel(row.Text));
            plannerRewardOptions = set.ToList();
            // Pseudo-entry: match chains containing a two-mod node (both mods are predicted).
            plannerRewardOptions.Insert(0, TwoModFilterOption);
        }

        // Settings-window table of per-reward weights (shown while the planner toggle is on).
        // The planner sorts its route list by the summed weight of each chain's mods, highest
        // first, so weighted rewards float the best routes to the top. 0 (the default) keeps a
        // reward neutral; negatives push routes down. Stored sparsely (only nonzero).
        private void DrawRewardWeightsTable()
        {
            EnsureRewardOptions();
            ImGui.Indent();
            ImGui.TextUnformatted(this.L("atlas.ritual_weights", "Reward weights"));
            ImGuiHelper.ToolTip(this.L("atlas.ritual_weights_hint",
                "Planner routes are sorted by the sum of these weights over the route's predicted " +
                "rewards, highest first. 0 = neutral; negative pushes a route down the list."));
            if (ImGui.BeginChild("##ritualWeights", new Vector2(0, 240), ImGuiChildFlags.Borders))
            {
                if (ImGui.BeginTable("##ritualWeightsTable", 2,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn(this.L("atlas.weights_reward_col", "Reward"), ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn(this.L("atlas.weights_weight_col", "Weight"), ImGuiTableColumnFlags.WidthFixed, 220f);
                    ImGui.TableHeadersRow();
                    foreach (var opt in plannerRewardOptions)
                    {
                        if (opt == TwoModFilterOption)
                            continue;   // filter pseudo-entry, not a rollable reward
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(opt);
                        ImGui.TableNextColumn();
                        int w = Settings.RitualRewardWeights.TryGetValue(opt, out var cur) ? cur : 0;
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.InputInt($"##rw_{opt}", ref w))
                        {
                            if (w == 0)
                                Settings.RitualRewardWeights.Remove(opt);
                            else
                                Settings.RitualRewardWeights[opt] = w;
                            plannerWeightsVersion++;
                        }
                    }

                    ImGui.EndTable();
                }
            }

            ImGui.EndChild();
            ImGui.Unindent();
        }

        // Chain weight = sum of the user's reward weights over every predicted mod on the chain
        // (both mods of a two-mod node count). Recomputed + re-sorted only when the weights or
        // the chain set change; ordering is weight DESC, then the path text for stability.
        private void SortPlannerChains()
        {
            var weights = Settings.RitualRewardWeights;
            foreach (var c in plannerChains)
            {
                int w = 0;
                for (int k = 0; k < c.ShortMods.Count; k++)
                {
                    if (weights.TryGetValue(c.ShortMods[k], out var w1))
                        w += w1;
                    if (c.ShortMods2[k] != null && weights.TryGetValue(c.ShortMods2[k], out var w2))
                        w += w2;
                }

                c.Weight = w;
            }

            plannerChains.Sort((a, b) => a.Weight != b.Weight
                ? b.Weight.CompareTo(a.Weight)
                : string.Compare(a.PathLine, b.PathLine, StringComparison.OrdinalIgnoreCase));
            plannerChainsWeightsVersion = plannerWeightsVersion;
        }

        // Enumerate all chains from every root. Cached by (lineId, depth, committed, roots);
        // rebuilt when the committed line or the eligible-start set changes.
        private void BuildPlannerChains(IntPtr panel)
        {
            if (panel == IntPtr.Zero)
                return;
            EnsureRitualPool();
            if (ritualPool == null || ritualPool.Count == 0)
                return;

            // Roots: while a line is being drawn its committed frontier is the only root; before
            // the first pick EVERY node the line could start from (accessible, not blocked) is a
            // root, so the window lists the whole atlas worth of options at once — no hover
            // needed, the selected row's ray shows where that start is.
            var committed = ReadGridVector(IntPtr.Add(panel, PanelCommittedVecOffset));
            int committedReal = committed.Count;
            plannerLineActive = committedReal > 0;

            // Ineligible nodes (same game-rule set as BuildRitualPredictions — completed state
            // or special-category dat row): they keep their candIdx rank but can't join the
            // line — nor start it. Also grid → display name.
            var blocked = new HashSet<StdTuple2D<int>>();
            var gridName = new Dictionary<StdTuple2D<int>, string>(nodeCache.Count);
            var roots = new List<StdTuple2D<int>>();
            foreach (var nd in nodeCache)
            {
                gridName[nd.GridPosition] = nd.Drawable ? nd.MapName : "???";
                if (nd.State == AtlasNodeState.CompletedBase
                    || nd.RitualSpecial
                    || string.Equals(nd.MapInfo?.Type, "unique", StringComparison.OrdinalIgnoreCase)
                    || (nd.MapInfo?.HasTag("tower") ?? false)
                    || (nd.MapInfo?.HasTag("hideout") ?? false))
                    blocked.Add(nd.GridPosition);
                else if (!plannerLineActive && nd.State == AtlasNodeState.AccessibleNow)
                    roots.Add(nd.GridPosition);
            }

            if (plannerLineActive)
                roots.Add(committed[^1]);
            roots.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            plannerStartCount = roots.Count;
            if (roots.Count == 0)
            {
                plannerChains.Clear();
                plannerSig = null;
                return;
            }

            int prefixCount = plannerLineActive ? committedReal : 1;
            uint lineId = Read<uint>(IntPtr.Add(panel, PanelLineIdOffset));

            var stats = ReadRitualStats(panel);
            int addl = stats.TryGetValue(StatAdditionalMaps, out var am) ? am
                     : stats.TryGetValue(StatAdditionalMaps + 1, out var am2) ? am2 : 0;
            int lineLen = RitualBaseLineLength + Math.Max(0, addl);
            if (TryReadRitualPickCounter(out var picksLeft))
                lineLen = committedReal + picksLeft;
            int maxDepth = Math.Max(0, lineLen - prefixCount);
            int secondChance = stats.TryGetValue(StatSecondModChance, out var sc) ? sc
                             : stats.TryGetValue(StatSecondModChance + 1, out var sc2) ? sc2 : 0;

            var sigSb = new StringBuilder();
            sigSb.Append(lineId).Append('#').Append(maxDepth);
            foreach (var g in committed)
                sigSb.Append(';').Append(g.X).Append(',').Append(g.Y);
            foreach (var g in roots)
                sigSb.Append('|').Append(g.X).Append(',').Append(g.Y);
            var sig = sigSb.ToString();
            if (sig == plannerSig)
                return;
            plannerSig = sig;
            plannerChains.Clear();
            plannerEnumerated = 0;
            plannerCapped = false;

            var candTable = ReadCandidateTable(panel,
                plannerLineActive ? committed[^1] : (StdTuple2D<int>?)null);

            var pool = new List<RitualRow>(ritualPool.Count);
            foreach (var row in ritualPool)
            {
                if (row.W <= 0) continue;
                if (row.Cond == 0 || stats.ContainsKey(row.Cond) || stats.ContainsKey(row.Cond - 1))
                    pool.Add(row);
            }

            if (pool.Count == 0 || maxDepth <= 0)
            {
                PrunePlannerSelection();
                return;
            }

            // Every start shares the same lineId + pool, and a roll depends only on
            // (committedCount, candIdx) — memoized, the whole enumeration rolls ≤ ~40 times.
            var rollMemo = new Dictionary<(uint cc, uint ci), (string First, string Second)>();
            (string First, string Second) Roll(uint cc, uint ci)
            {
                if (!rollMemo.TryGetValue((cc, ci), out var t))
                    rollMemo[(cc, ci)] = t = PredictMods(lineId, cc, ci, pool, secondChance);
                return t;
            }

            // Fair share of the global cap per start, so a branchy early start can't starve
            // the rest of the atlas out of the list.
            int perStart = Math.Max(32, PlannerMaxPaths / roots.Count);
            int startEmitted = 0;

            var path = new List<StdTuple2D<int>>(prefixCount + maxDepth);
            var mods = new List<(string First, string Second)>();
            var visited = new HashSet<StdTuple2D<int>>();

            void Emit()
            {
                // Game reach check (ritualLineReachCheck 140b775f0): a node is clickable only if
                // the line can still be completed through it — so a branch that dead-ends short
                // of full length can never be walked in-game and must not be listed.
                if (mods.Count < maxDepth)
                    return;
                plannerEnumerated++;
                if (plannerChains.Count >= PlannerMaxPaths || startEmitted >= perStart)
                {
                    plannerCapped = true;
                    return;
                }

                startEmitted++;

                var nodes = path.GetRange(prefixCount - 1, path.Count - prefixCount + 1);
                var keySb = new StringBuilder();
                var nameSb = new StringBuilder();
                foreach (var g in nodes)
                {
                    keySb.Append(g.X).Append(',').Append(g.Y).Append('|');
                    if (nameSb.Length > 0) nameSb.Append("  >  ");
                    nameSb.Append(gridName.TryGetValue(g, out var nm) ? nm : "???");
                }

                var shorts = new List<string>(mods.Count);
                var shorts2 = new List<string>(mods.Count);
                var modSb = new StringBuilder();
                for (int k = 0; k < mods.Count; k++)
                {
                    var s = ShortModLabel(mods[k].First);
                    var s2 = mods[k].Second == null ? null : ShortModLabel(mods[k].Second);
                    shorts.Add(s);
                    shorts2.Add(s2);
                    if (modSb.Length > 0) modSb.Append("   -   ");
                    modSb.Append(s);
                    if (s2 != null) modSb.Append(" + ").Append(s2);
                }

                plannerChains.Add(new PlannerChain
                {
                    Key = keySb.ToString(),
                    Nodes = nodes,
                    ShortMods = shorts,
                    ShortMods2 = shorts2,
                    PathLine = nameSb.ToString(),
                    ModsLine = modSb.ToString(),
                });
            }

            void Dfs(StdTuple2D<int> node, int depth)
            {
                if (depth >= maxDepth || plannerChains.Count >= PlannerMaxPaths
                    || startEmitted >= perStart)
                {
                    Emit();
                    return;
                }

                if (!candTable.TryGetValue(node, out var raw) || raw.Count == 0)
                {
                    Emit();
                    return;
                }

                var cands = raw.Where(c => !visited.Contains(c))
                               .OrderBy(c => c.X).ThenBy(c => c.Y).ToList();
                uint cc = (uint)(prefixCount + depth);
                bool any = false;
                for (int i = 0; i < cands.Count && plannerChains.Count < PlannerMaxPaths
                    && startEmitted < perStart; i++)
                {
                    var cand = cands[i];
                    if (blocked.Contains(cand))
                        continue;                 // holds rank i, but can't join the line
                    var roll = Roll(cc, (uint)i);
                    if (string.IsNullOrEmpty(roll.First))
                        continue;
                    any = true;
                    path.Add(cand);
                    mods.Add(roll);
                    visited.Add(cand);
                    Dfs(cand, depth + 1);
                    visited.Remove(cand);
                    mods.RemoveAt(mods.Count - 1);
                    path.RemoveAt(path.Count - 1);
                }

                if (!any)
                    Emit();
            }

            foreach (var root in roots)
            {
                path.Clear();
                mods.Clear();
                visited.Clear();
                if (plannerLineActive)
                {
                    path.AddRange(committed);
                    visited.UnionWith(committed);
                }
                else
                {
                    path.Add(root);
                    visited.Add(root);
                }

                startEmitted = 0;
                Dfs(root, 0);
                if (plannerChains.Count >= PlannerMaxPaths)
                    break;
            }

            this.SortPlannerChains();
            PrunePlannerSelection();
        }

        // Re-home selections whose chain key vanished. When the line advances ALONG a selected
        // route, the re-enumeration roots at the new frontier so the same remaining route gets a
        // shorter key (the old key minus its walked prefix) — carry the palette slot onto that
        // suffix chain instead of dropping it, so the highlight survives drawing the line.
        // Selections with no suffix heir (different start / route broken) are dropped.
        private void PrunePlannerSelection()
        {
            if (plannerSelected.Count == 0)
                return;
            var alive = new HashSet<string>(plannerChains.Count);
            foreach (var c in plannerChains)
                alive.Add(c.Key);
            var dead = plannerSelected.Keys.Where(k => !alive.Contains(k)).ToList();
            foreach (var k in dead)
            {
                var slot = plannerSelected[k];
                plannerSelected.Remove(k);

                string heir = null;
                foreach (var c in plannerChains)
                {
                    // Suffix match on whole "x,y|" tokens (guard against "12,3|" vs "2,3|").
                    if (c.Key.Length >= k.Length || !k.EndsWith(c.Key, StringComparison.Ordinal))
                        continue;
                    if (k[k.Length - c.Key.Length - 1] != '|')
                        continue;
                    if (plannerSelected.ContainsKey(c.Key))
                        continue;
                    if (heir == null || c.Key.Length > heir.Length)
                        heir = c.Key;
                }

                if (heir != null)
                    plannerSelected[heir] = slot;
            }
        }

        private int NextPaletteSlot()
        {
            var used = new HashSet<int>(plannerSelected.Values);
            for (int s = 0; ; s++)
                if (!used.Contains(s))
                    return s;
        }

        // Map overlay for the selected chains: a ray from the player marker to the chain's start,
        // the route polyline, and a reward pill at every picked node.
        private void DrawPlannerOverlay(ImDrawListPtr drawList, Vector2 playerLocation, float uiScale)
        {
            if (plannerSelected.Count == 0 || plannerChains.Count == 0)
                return;

            var needed = new HashSet<StdTuple2D<int>>();
            foreach (var c in plannerChains)
                if (plannerSelected.ContainsKey(c.Key))
                    foreach (var g in c.Nodes)
                        needed.Add(g);
            if (needed.Count == 0)
                return;

            var centers = new Dictionary<StdTuple2D<int>, Vector2>(needed.Count);
            foreach (var nd in nodeCache)
            {
                if (!needed.Contains(nd.GridPosition))
                    continue;
                var ub = Read<UiElementBaseOffset>(nd.Address);
                var sc = ComputeScalePair(in ub);
                var tl = GetLeafTopLeft(in ub);
                centers[nd.GridPosition] = tl + new Vector2(
                    ub.UnscaledSize.X * sc.X, ub.UnscaledSize.Y * sc.Y) * 0.5f;
            }

            drawList.ChannelsSetCurrent(ChannelLines);
            float th = MathF.Max(2f, 2.5f * uiScale);
            // The ray is a "where to go" pointer for far-off starts; once the start is near
            // (< 70% of the screen away — the route lines themselves are already in view)
            // it is just clutter, so only long rays draw.
            var disp = ImGui.GetIO().DisplaySize;
            float rayMinLen = 0.7f * MathF.Min(disp.X, disp.Y);
            foreach (var c in plannerChains)
            {
                if (!plannerSelected.TryGetValue(c.Key, out var slot))
                    continue;
                var col = ImGuiHelper.Color(PlannerPalette[slot % PlannerPalette.Length]);
                if (!centers.TryGetValue(c.Nodes[0], out var startC))
                    continue;
                if (Vector2.Distance(playerLocation, startC) >= rayMinLen)
                    drawList.AddLine(playerLocation, startC, col, th);   // ray to the chain's start

                // Ring on the start node — otherwise the route polyline has no readable direction.
                drawList.AddCircle(startC, 16f * uiScale, col, 0, th);
                drawList.AddCircle(startC, 20f * uiScale, col, 0, MathF.Max(1f, th * 0.5f));
                var prev = startC;
                for (int i = 1; i < c.Nodes.Count; i++)
                {
                    if (!centers.TryGetValue(c.Nodes[i], out var pc))
                        continue;
                    drawList.AddLine(prev, pc, col, th);
                    prev = pc;
                }
            }

            drawList.ChannelsSetCurrent(ChannelLabels);
            var pillBg = ImGuiHelper.Color(new Vector4(0.05f, 0.05f, 0.05f, 0.92f));
            foreach (var c in plannerChains)
            {
                if (!plannerSelected.TryGetValue(c.Key, out var slot))
                    continue;
                var colV = PlannerPalette[slot % PlannerPalette.Length];
                var col = ImGuiHelper.Color(colV);
                for (int i = 1; i < c.Nodes.Count; i++)
                {
                    if (!centers.TryGetValue(c.Nodes[i], out var pc))
                        continue;
                    var label = c.ShortMods2[i - 1] == null
                        ? c.ShortMods[i - 1]
                        : c.ShortMods[i - 1] + " + " + c.ShortMods2[i - 1];
                    var ts = ImGui.CalcTextSize(label);
                    var pad = new Vector2(4, 2) * uiScale;
                    var pos = new Vector2(pc.X - ts.X * 0.5f, pc.Y - ts.Y - 12f * uiScale);
                    drawList.AddRectFilled(pos - pad, pos + ts + pad, pillBg, 3f * uiScale);
                    drawList.AddRect(pos - pad, pos + ts + pad, col, 3f * uiScale);
                    drawList.AddText(pos, col, label);
                }
            }
        }

        // The planner window itself (normal-sized font — drawn outside the FontScaleScope).
        private void DrawPlannerWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(760, 500), ImGuiCond.FirstUseEver);
            bool open = true;
            if (!ImGui.Begin(this.Loc.Title("atlas.planner_title", "Head of the king Rewards", "AtlasRitualPlanner"), ref open))
            {
                ImGui.End();
                return;
            }

            if (!open)
                Settings.ShowRitualPlanner = false;   // X closes until re-enabled in settings

            // Persisted reward filter: a multi-select dropdown over every reward the pool can
            // roll; a chain matches when ANY selected reward is in it. Stored as '|'-joined
            // short labels so it survives restarts.
            EnsureRewardOptions();
            if (plannerChainsWeightsVersion != plannerWeightsVersion)
                this.SortPlannerChains();   // weights edited in settings — re-rank the cached chains
            var selected = new HashSet<string>(
                (Settings.RitualRewardFilter ?? string.Empty)
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
            string preview = selected.Count == 0
                ? this.L("atlas.planner_filter_hint", "filter by desired rewards (any match shows the path)…")
                : string.Join(", ", plannerRewardOptions.Where(selected.Contains));
            ImGui.SetNextItemWidth(MathF.Max(120f, ImGui.GetContentRegionAvail().X - 70f));
            bool filterChanged = false;
            if (ImGui.BeginCombo("##plannerFilter", preview, ImGuiComboFlags.HeightLargest))
            {
                foreach (var opt in plannerRewardOptions)
                {
                    bool on = selected.Contains(opt);
                    if (ImGui.Checkbox(opt, ref on))
                    {
                        if (on) selected.Add(opt);
                        else selected.Remove(opt);
                        filterChanged = true;
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.Button(this.L("atlas.planner_clear", "Clear")) && selected.Count > 0)
            {
                selected.Clear();
                filterChanged = true;
            }

            if (filterChanged)
                Settings.RitualRewardFilter = string.Join("|", plannerRewardOptions.Where(selected.Contains));

            // Root summary: the drawn line's frontier, or how many possible starts are listed.
            if (plannerLineActive && plannerChains.Count > 0)
                ImGui.TextUnformatted($"{this.L("atlas.planner_start", "Start:")} {GridDisplayName(plannerChains[0].Nodes[0])}");
            else
                ImGui.TextDisabled($"{this.L("atlas.planner_starts", "Possible starts:")} {plannerStartCount}");

            // Filter + count, then rows.
            var visible = new List<PlannerChain>(Math.Min(plannerChains.Count, PlannerMaxRows));
            int matchTotal = 0;
            foreach (var c in plannerChains)
            {
                // Ticked chains always stay listed — a route being walked must not drop out when
                // the reward that matched the filter was on an already-committed node.
                bool isSelected = plannerSelected.ContainsKey(c.Key);
                if (!isSelected && selected.Count > 0)
                {
                    bool wantTwo = selected.Contains(TwoModFilterOption);
                    bool ok = false;
                    for (int k = 0; k < c.ShortMods.Count && !ok; k++)
                        ok = selected.Contains(c.ShortMods[k])
                            || (c.ShortMods2[k] != null
                                && (wantTwo || selected.Contains(c.ShortMods2[k])));

                    if (!ok)
                        continue;
                }

                matchTotal++;
                if (visible.Count < PlannerMaxRows)
                    visible.Add(c);
                else if (isSelected)
                    visible.Add(c);   // never row-cap a ticked chain out of sight
            }

            var counts = $"{this.L("atlas.planner_shown", "Shown:")} {visible.Count}"
                + (matchTotal > visible.Count ? $" ({this.L("atlas.planner_of", "of")} {matchTotal})" : string.Empty)
                + $"  |  {this.L("atlas.planner_chains", "chains:")} {plannerChains.Count}"
                + (plannerCapped ? $" ({this.L("atlas.planner_capped", "capped")})" : string.Empty);
            ImGui.TextDisabled(counts);
            ImGui.Separator();

            ImGui.BeginChild("##plannerRows");
            var modColor = new Vector4(0.45f, 0.75f, 1f, 1f);
            for (int i = 0; i < visible.Count; i++)
            {
                var c = visible[i];
                bool sel = plannerSelected.ContainsKey(c.Key);
                if (ImGui.Checkbox($"##plannerSel{i}", ref sel))
                {
                    if (sel)
                        plannerSelected[c.Key] = NextPaletteSlot();
                    else
                        plannerSelected.Remove(c.Key);
                }

                ImGui.SameLine();
                if (plannerSelected.TryGetValue(c.Key, out var slot))
                {
                    ImGui.ColorButton($"##plannerCol{i}", PlannerPalette[slot % PlannerPalette.Length],
                        ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop, new Vector2(14, 14));
                    ImGui.SameLine();
                }

                ImGui.BeginGroup();
                ImGui.TextUnformatted(c.PathLine);
                if (c.Weight != 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"[{c.Weight:+0;-0}]");
                }

                ImGui.TextColored(modColor, c.ModsLine);
                ImGui.EndGroup();
                ImGui.Separator();
            }

            ImGui.EndChild();
            ImGui.End();
        }

        // RE ground-truth collector: snapshot the ritual atlas line to config/ritual_roll_log.jsonl.
        // One JSON line per distinct (lineId + committed grids + each line-node's rolled mod text)
        // state. Committed nodes carry posIdx = their index; the pending set are the next candidates
        // (posIdx = committed count). Only runs while a line exists, so idle frames cost one read.
        private void LogRitualSnapshot(IntPtr panel)
        {
            if (panel == IntPtr.Zero)
                return;

            var committed = ReadGridVector(IntPtr.Add(panel, PanelCommittedVecOffset));
            var pending = ReadGridVector(IntPtr.Add(panel, PanelPendingVecOffset));
            if (committed.Count == 0 && pending.Count == 0)
                return; // no active line — nothing to log

            uint lineId = Read<uint>(IntPtr.Add(panel, PanelLineIdOffset));

            // Precomputed next-candidate table: node(x,y) -> its raw ≤5 candidates. Lets the offline
            // solver reconstruct the exact candIdx (rank among a frontier's candidates), which the
            // clicked-only pending set can't (an unclicked candidate still shifts every rank).
            var candTable = ReadCandidateTable(panel,
                committed.Count > 0 ? committed[committed.Count - 1] : (StdTuple2D<int>?)null);
            List<int[]> CandsOf(StdTuple2D<int> g) =>
                candTable.TryGetValue(g, out var cs)
                    ? cs.Select(c => new[] { c.X, c.Y }).ToList()
                    : new List<int[]>();

            // grid → node address (from the already-built cache).
            var gridToAddr = new Dictionary<StdTuple2D<int>, IntPtr>(nodeCache.Count);
            foreach (var nd in nodeCache)
                gridToAddr[nd.GridPosition] = nd.Address;

            var entries = new List<object>();
            void Collect(List<StdTuple2D<int>> grids, string vecName, int basePos)
            {
                for (int i = 0; i < grids.Count; i++)
                {
                    var g = grids[i];
                    string text = null;
                    if (gridToAddr.TryGetValue(g, out var addr) && addr != IntPtr.Zero)
                    {
                        var child = Read<IntPtr>(IntPtr.Add(addr, RitualModsChildOffset));
                        if (child != IntPtr.Zero)
                            text = ReadGameWString(IntPtr.Add(child, TextElementTextOffset));
                    }
                    entries.Add(new
                    {
                        vec = vecName,
                        idx = i,
                        posIdx = basePos + i,
                        x = g.X,
                        y = g.Y,
                        text = string.IsNullOrWhiteSpace(text) ? null : text,
                        cands = CandsOf(g),
                    });
                }
            }

            Collect(committed, "committed", 0);
            Collect(pending, "pending", committed.Count);

            // Only snapshots where at least one node has rolled text are useful ground truth.
            if (!entries.Any(e => ((dynamic)e).text != null))
                return;

            // The frontier ritualLineToggleNode enumerates from = the LAST committed node; its raw
            // candidate set is what the next clicked node is ranked within.
            var frontierCands = committed.Count > 0 ? CandsOf(committed[committed.Count - 1])
                                                    : new List<int[]>();

            // "Select N maps" header — logged to verify it really decrements per committed pick
            // (the prediction depth override assumes it does).
            int? pickCounter = TryReadRitualPickCounter(out var pc) ? pc : null;

            var snapshot = new
            {
                lineId,
                committedCount = committed.Count,
                pendingCount = pending.Count,
                pickCounter,
                frontierCands,
                entries,
            };

            // Signature = lineId + every (posIdx,x,y,text): dedup identical states across frames.
            var sig = new StringBuilder();
            sig.Append(lineId).Append('|').Append(committed.Count).Append('|').Append(pickCounter ?? -1);
            foreach (dynamic e in entries)
                sig.Append(';').Append(e.posIdx).Append(',').Append(e.x).Append(',').Append(e.y)
                   .Append('=').Append((string)e.text ?? "");
            if (!ritualLogSeen.Add(sig.ToString()))
                return;

            try
            {
                var dir = Path.GetDirectoryName(RitualRollLogPathname);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                if (!ritualLogHeaderDone && !File.Exists(RitualRollLogPathname))
                    File.AppendAllText(RitualRollLogPathname,
                        "// Ritual Rite-mod roll ground-truth. One JSON snapshot per line state.\n");
                ritualLogHeaderDone = true;
                File.AppendAllText(RitualRollLogPathname,
                    JsonConvert.SerializeObject(snapshot) + "\n");
            }
            catch { /* logging must never break the overlay */ }
        }

        public static string ReadWideString(nint address, int stringLength)
        {
            if (address == IntPtr.Zero || stringLength <= 0)
                return string.Empty;

            EnsureProcessHandle();
            byte[] result = new byte[stringLength * 2];
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemoryArray(Handle, address, result);

            return Encoding.Unicode.GetString(result).Split('\0')[0];
        }

        static bool IsPrintableUnicode(string str)
        {
            if (string.IsNullOrEmpty(str))
                return false;

            if (str.All(ch => ch == '?' || char.IsWhiteSpace(ch)))
                return false;

            foreach (var rune in str.EnumerateRunes())
            {
                if (rune.Value == 0xFFFD)
                    return false;

                var cat = Rune.GetUnicodeCategory(rune);
                switch (cat)
                {
                    case UnicodeCategory.Control:
                    case UnicodeCategory.Format:
                    case UnicodeCategory.Surrogate:
                    case UnicodeCategory.PrivateUse:
                    case UnicodeCategory.OtherNotAssigned:
                        return false;
                }
            }

            return true;
        }

        private static string NormalizeName(string s) =>
            string.IsNullOrWhiteSpace(s)
                ? s
                : CollapseWhitespace(s.Replace('\u00A0', ' ').Trim());

        private static string CollapseWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;

            var sb = new StringBuilder(s.Length);
            bool prevSpace = false;
            foreach (var ch in s)
            {
                bool isSpace = char.IsWhiteSpace(ch);
                if (isSpace)
                {
                    if (!prevSpace) sb.Append(' ');
                }
                else
                {
                    sb.Append(ch);
                }
                prevSpace = isSpace;
            }

            return sb.ToString();
        }

        // Flags-fingerprint walk from GameUi to the atlas node-lists container — replaces the
        // fragile fixed-index walk (GetChild(25, 0, 6)) that broke between PoE2 patches. Path
        // verified live 2026-06 for build 0.5.x:
        //
        //   GameUi (fp 0x502EF0, ~123 children)
        //     └─ child fp 0x562EF5 — atlas panel container (~5 sibling matches, only the
        //                            IsVisible one continues the chain when atlas is open)
        //          └─ child fp 0x502EF1 — sub-container / gate (IsVisible toggles with panel)
        //               └─ child fp 0x502EF3 — node lists (direct parent of ~470 atlas nodes,
        //                                       each with fp 0x542EF3)
        //
        // Multiple siblings can match at each step (esp. step 0 has 5 candidates), so we
        // backtrack: try every matching child and recurse, keep the branch whose continuation
        // reaches a leaf with ≥ 1 atlas-node-fp child. Prefers visible candidates first so the
        // gate naturally picks the open instance.
        private const uint AtlasPanelFp = 0x00562EF5;
        private const uint AtlasGateFp = 0x00502EF1;
        private const uint AtlasNodeListFp = 0x00502EF3;
        // Controller layout only: a mid-container fp sitting between GameUi and the panel (it's also
        // the atlas map-node fp). Verified live for PoE2 0.5.x.
        private const uint AtlasMapNodeFp = 0x00542EF3;
        private const uint IsVisibleMask = 0x800u;

        // KB/Mouse: the panel is a DIRECT child of GameUi → Panel→Gate→NodeList (3 hops).
        // Controller: GameHelper auto-detects controller mode (InGameState.UiRootStructPtr == 0) and
        // swaps GameUi.Address to the gamepad UI manager (fp 0x502EF0); under it the SAME
        // Panel→Gate→NodeList triplet sits 3 levels deeper, reached by Gate→MapNode→Gate→Panel→
        // Gate→NodeList (6 hops, verified live 0.5.x). The fp tail is identical, so BOTH chains return
        // the same node-list container (fp 0x502EF3) the rest of the plugin treats as the panel address.
        private static readonly uint[] KbMouseChain = { AtlasPanelFp, AtlasGateFp, AtlasNodeListFp };
        private const int KbMouseGateStep = 1;            // the Gate, one level below the panel
        private static readonly uint[] ControllerChain =
            { AtlasGateFp, AtlasMapNodeFp, AtlasGateFp, AtlasPanelFp, AtlasGateFp, AtlasNodeListFp };
        private const int ControllerGateStep = 4;         // the Gate, one level below the panel

        private IntPtr GetAtlasPanelAddress()
        {
            var gameUi = Core.States.InGameStateObject.GameUi.Address;
            if (gameUi == IntPtr.Zero)
                return IntPtr.Zero;

            // Resolve via the active input layout first, then auto-fall back to the other so the panel
            // is found regardless. GH already auto-detects controller mode; the manual toggle force-ons
            // it as a safety override.
            bool controller = Core.GHSettings.EnableControllerMode || Settings.ControllerMode;
            var (primary, primaryGate, secondary, secondaryGate) = controller
                ? (ControllerChain, ControllerGateStep, KbMouseChain, KbMouseGateStep)
                : (KbMouseChain, KbMouseGateStep, ControllerChain, ControllerGateStep);

            var addr = WalkFp(gameUi, primary, primaryGate, 0);
            return addr != IntPtr.Zero ? addr : WalkFp(gameUi, secondary, secondaryGate, 0);
        }

        private UiElement GetAtlasPanelUi()
        {
            var addr = GetAtlasPanelAddress();
            return addr == IntPtr.Zero ? default : Read<UiElement>(addr);
        }

        private static IntPtr WalkFp(IntPtr parentAddr, uint[] fps, int gateStep, int step)
        {
            // Terminal step: the fp triplet Panel→Gate→NodeList is NOT unique to the endgame
            // atlas — the campaign world-map screen has a same-shaped visible branch whose leaf
            // holds a few non-node children, and reading atlas state (e.g. the ritual line-mode
            // byte at +0x637) off that stranger container yields garbage (planner window popping
            // up on an act map). A real atlas node list is recognized by its children.
            if (step == fps.Length)
                return HasAtlasNodeChild(parentAddr) ? parentAddr : IntPtr.Zero;

            var parent = Read<UiElement>(parentAddr);
            int n = parent.Length;
            if (n <= 0 || n > 5000)
                return IntPtr.Zero;

            uint target = fps[step] & ~IsVisibleMask;

            // Visible matches first, then non-visible — backtracking finds whichever branch
            // has a full continuation.
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantVisible = pass == 0;
                for (int i = 0; i < n; i++)
                {
                    var childAddr = parent.GetChildAddress(i);
                    if (childAddr == IntPtr.Zero)
                        continue;
                    var child = Read<UiElement>(childAddr);
                    uint f = child.Flags;
                    if ((f & ~IsVisibleMask) != target)
                        continue;
                    bool visible = (f & IsVisibleMask) != 0;
                    if (visible != wantVisible)
                        continue;
                    if (step == gateStep && !visible)
                        continue;

                    var deeper = WalkFp(childAddr, fps, gateStep, step + 1);
                    if (deeper != IntPtr.Zero)
                        return deeper;
                }
            }
            return IntPtr.Zero;
        }

        // True when the container holds at least one atlas map node (fp 0x542EF3) or mist node
        // (fp 0x442EF3) among its first children — the leaf check that tells the real endgame
        // atlas node list apart from same-fp-shaped containers on other world-map pages. The
        // real list is ~470+ nodes, so scanning a small prefix is enough (and a loading-frame
        // list with no nodes yet is correctly rejected until it fills).
        private static bool HasAtlasNodeChild(IntPtr containerAddr)
        {
            var container = Read<UiElement>(containerAddr);
            int n = Math.Min(container.Length, 64);
            for (int i = 0; i < n; i++)
            {
                var childAddr = container.GetChildAddress(i);
                if (childAddr == IntPtr.Zero)
                    continue;
                uint f = Read<uint>(IntPtr.Add(childAddr, 0x180)) & ~IsVisibleMask;
                if (f == (AtlasMapNodeFp & ~IsVisibleMask) || f == (AtlasMistNodeFp & ~IsVisibleMask))
                    return true;
            }

            return false;
        }

        private static bool InventoryPanel()
        {
            var uiElement = Read<UiElement>(Core.States.InGameStateObject.GameUi.Address);
            var invetoryPanel = uiElement.GetChild(33);

            return invetoryPanel.IsVisible;
        }

        private static void CategorizeContents(IEnumerable<string> raws,
            Dictionary<string, ContentInfo> tagMap,
            Dictionary<string, ContentInfo> plainMap,
            out List<ContentInfo> flags,
            out List<ContentInfo> contents)
        {
            flags = [];
            contents = [];
            foreach (var raw in raws)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var info = MatchContent(NormalizeName(raw), tagMap, plainMap);
                if (info is null || !info.Show)
                    continue;

                if (info.IsFlag) flags.Add(info);
                else contents.Add(info);
            }
        }

        public static List<string> GetContentName(UiElement nodeUi)
        {
            const int ContentOffset = 0x290;
            var result = new List<string>();

            nodeUi = nodeUi.GetChild(0);
            nodeUi = nodeUi.GetChild(0);

            var len = nodeUi.Length;
            if (len <= 0)
                return result;

            for (int i = 0; i < len; i++)
            {
                var childAddr = nodeUi.GetChildAddress(i);
                var contentPtr = Read<IntPtr>(childAddr + ContentOffset);
                if (contentPtr == IntPtr.Zero)
                    continue;

                var contentName = ReadWideString(contentPtr, 64);
                if (string.IsNullOrWhiteSpace(contentName))
                    continue;

                result.Add(contentName);
            }

            return result;
        }

        // Number of content markers on a node = children of the content container node[0][0]
        // (each child is one badge: Essence/Breach/Ritual/Boss…). Reliable for ALL nodes incl.
        // off-screen/hidden ones — the badge element always exists even when its icon sub-widgets
        // aren't built. The exact content TYPE is NOT persisted (rolled from a per-node seed), so
        // only the count is surfaced here. See docs/re-findings.md §2.7.
        public static int GetContentCount(UiElement nodeUi)
        {
            nodeUi = nodeUi.GetChild(0);   // node[0]
            nodeUi = nodeUi.GetChild(0);   // node[0][0] = content container
            var len = nodeUi.Length;
            return len > 0 ? len : 0;
        }

        // Per-node content token → content name. A token is a u32 = (HIGH16 weight × 0x40)
        // | (LOW16 effect-id): high words seen are ×1=0x0040, ×2=0x0080, ×3=0x00C0, ×5=0x0140,
        // ×10=0x0280, ×50=0x0C80, ×100=0x1900, ×1000=0xFA00 (and 0xE700 cluster, still unknown).
        // A content is identified by its DISTINCTIVE (usually high-weight) token; low-weight ×1/×2
        // tokens are often shared "building-block" effects (e.g. 0x..0A8C across all Azmeri content)
        // and are intentionally left unmapped to avoid mislabeling. So we key on the FULL u32.
        // Built empirically by visually correlating live tokens with content (re-findings §2.10.1).
        // Tokens confirmed STABLE across game restarts. Unknown tokens fall through to hex display.
        private static readonly Dictionary<uint, string> ContentTokenNames = new()
        {
            [0x00404C57] = "Powerful Map Boss",
            [0x004067C0] = "Grand Mirror",
            [0x0040686A] = "Delirium",
            [0x0040686B] = "Abyss",
            [0x0080686B] = "Abyss",                 // weight-2 variant
            [0x0040686C] = "Ritual",
            [0x0040686D] = "Vaal Beacons",
            [0x0040686E] = "Breach",
            // Atlas influence (biome) content
            [0x004064FF] = "Water Influence",
            [0x00406501] = "Grass Influence",
            [0x00406502] = "Forest Influence",
            [0x00406503] = "Swamp Influence",
            [0x00406504] = "Desert Influence",
            // Azmeri / Wildwood
            [0x19006351] = "Azmeri Bloodline",
            [0x00400890] = "Azmeri Bloodline",
            [0x004064DF] = "Azmeri Bloodline",
            [0xFA00610E] = "Azmeri Energisation",
            [0x0140_0A8C] = "Swarming Spirits",
            [0x19006630] = "Spirit Migration",
            [0x02806631] = "Spirit Migration",
            // Mods / modifiers
            [0x1900634C] = "Indomitable Essence",
            [0x00C01247] = "Indomitable Essence",
            [0x00C05E27] = "Scattered Stones",
            [0x00C06349] = "Power Struggle",
            [0x1900320E] = "Arcane Hordes",
            [0x0C8004D8] = "Affluent Armies",
            [0x19006202] = "Rites of the Rogues",
            [0x00800963] = "Rites of the Rogues",
            [0x00801282] = "Corrupted Mirage",
            [0x0040675E] = "Glimmering Mutation",
            [0x0040153B] = "Ancient Trove",
            [0x00400962] = "Ancient Trove",
            // Exceptional Find (distinctive + its 0x40-band sub-tokens)
            [0xFA00635D] = "Exceptional Find",
            [0x00406396] = "Exceptional Find",
            [0x00406397] = "Exceptional Find",
            [0x00406398] = "Exceptional Find",
            [0x00406399] = "Exceptional Find",
            [0x004065FF] = "Exceptional Find",
            // Known NON-content markers (mapped so they can be hidden, see render filter):
            [0x004065F0] = "(atlas skill point)",
            // Shared base tokens deliberately NOT mapped (ambiguous across contents):
            //   0x00800A8C / 0x00400A8C  — Azmeri base effect (Bloodline / Energisation / Spirit Migration)
            //   0xE700_5F0C / _5F0D / _5F0E — common cluster, still unidentified
        };

        // Resolve a content token to its display name; unknown tokens return a hex string (low 16
        // bits when in the 0x0040 band, otherwise the full u32) so they remain visible for labeling.
        public static string ResolveContentToken(uint token)
        {
            if (ContentTokenNames.TryGetValue(token, out var name))
                return name;
            return (token & 0xFFFF0000u) == 0x00400000u ? (token & 0xFFFF).ToString("X4") : token.ToString("X8");
        }

        // Read the per-node content tokens: the StdVector<u32> living directly on the atlas-node
        // UiElement at element+0x350 (begin) / +0x358 (end). Stable per content type (two
        // PowerfulMapBoss nodes give the identical vector). NOTE: populated only for VISIBLE
        // (rendered) nodes — culled/hidden nodes carry no tokens. See docs/re-findings.md §2.10.
        private const int ContentVecBeginOffset = 0x350;
        private const int ContentVecEndOffset = 0x358;
        private const int MaxContentTokens = 32;   // sanity cap (content lists are tiny)
        public static uint[] GetContentTokens(IntPtr nodeAddr)
        {
            if (nodeAddr == IntPtr.Zero)
                return System.Array.Empty<uint>();

            var begin = Read<IntPtr>(IntPtr.Add(nodeAddr, ContentVecBeginOffset));
            var end = Read<IntPtr>(IntPtr.Add(nodeAddr, ContentVecEndOffset));
            if (begin == IntPtr.Zero || end.ToInt64() <= begin.ToInt64())
                return System.Array.Empty<uint>();

            long bytes = end.ToInt64() - begin.ToInt64();
            int count = (int)(bytes / sizeof(uint));
            if (count <= 0 || count > MaxContentTokens)
                return System.Array.Empty<uint>();

            var tokens = new uint[count];
            for (int i = 0; i < count; i++)
                tokens[i] = Read<uint>(IntPtr.Add(begin, i * sizeof(uint)));
            return tokens;
        }

        // Read the class-2 (badge) content ids of a node: u32 at badge+0x188 for each badge child
        // under node[0][0] (the same container GetContentCount counts). The high word is a constant
        // 0x0002 category; the content type is the low 16 bits. Disjoint from the token vector
        // (a node carries EITHER tokens OR badges, never both). See docs/re-findings.md §2.10.3.
        private const int BadgeContentIdOffset = 0x188;
        private const int MaxBadges = 16;   // sanity cap (content lists are tiny)
        public static uint[] GetBadgeContentIds(UiElement nodeUi)
        {
            nodeUi = nodeUi.GetChild(0);   // node[0]
            nodeUi = nodeUi.GetChild(0);   // node[0][0] = content container
            var len = nodeUi.Length;
            if (len <= 0 || len > MaxBadges)
                return System.Array.Empty<uint>();

            var ids = new uint[len];
            for (int i = 0; i < len; i++)
            {
                var childAddr = nodeUi.GetChildAddress(i);
                if (childAddr == IntPtr.Zero)
                    continue;
                ids[i] = Read<uint>(IntPtr.Add(childAddr, BadgeContentIdOffset));
            }
            return ids;
        }

        // Resolve a node's tokens + badge ids into the final, de-duped display-name list. Run ONCE per
        // cache refresh (not per frame) so the per-frame draw path stays allocation-free. Non-content
        // markers (names wrapped in parentheses, e.g. atlas skill point) are filtered out here.
        private static readonly string[] NoContentNames = System.Array.Empty<string>();
        private static string[] BuildContentNames(uint[] tokens, uint[] badges, string internalId)
        {
            var seen = new List<string>(4);
            void Add(string s)
            {
                if (string.IsNullOrEmpty(s) || s[0] == '(')
                    return;
                if (!seen.Contains(s))
                    seen.Add(s);
            }

            if (tokens is { Length: > 0 })
                foreach (var t in tokens) Add(ResolveContentToken(t));
            if (badges is { Length: > 0 })
                foreach (var b in badges) Add(ResolveBadgeContent(b));

            // Map-type-inherent content fallback, derived from the persistent MapId (NOT the per-node
            // content widgets/vectors). The game culls a distant node's content badges AND its inline
            // content vectors, so far/fogged nodes — notably the whole sea cluster when the player is on
            // land — carry NO content client-side (see docs/re-findings-atlas.md §2.10.8b/c). Content that
            // is intrinsic to the MAP TYPE can still be recovered from the MapId, which IS persistent for
            // every node incl. fog. Matched by id prefix (exact — avoids spillover to the broader
            // "expedition"/"boss" maps.json tag sets which also cover doodads/other bosses):
            //   ExpeditionLogBook_* → Grand Expedition,   ExpeditionSubArea_* → Powerful Map Boss.
            // Drawn only on non-visible nodes (the icon draw is gated on !nodeVisible), so this never
            // duplicates the game's native icons on visible nodes.
            AddMapIdDerivedContent(internalId, Add);

            return seen.Count == 0 ? NoContentNames : seen.ToArray();
        }

        // Content names that are determined by the map TYPE (MapId), shown for fogged/distant nodes
        // whose per-node content data the client has culled. Names must match mapcontent.json entries so
        // DrawContentRow can resolve their icons (Grand Expedition → AtlasIconContentExpedition,
        // Powerful Map Boss → AtlasIconContentMapBoss).
        private static void AddMapIdDerivedContent(string internalId, Action<string> add)
        {
            if (string.IsNullOrEmpty(internalId))
                return;
            if (internalId.StartsWith("ExpeditionSubArea", StringComparison.OrdinalIgnoreCase))
                add("Powerful Map Boss");
            else if (internalId.StartsWith("ExpeditionLogBook", StringComparison.OrdinalIgnoreCase))
                add("Grand Expedition");
        }

        // Resolve a badge content id to its name. The low 16 bits are either an EndgameMapContent
        // row+100 (100-165, from mapcontent.json) or a Stats.dat row id for special map-state content
        // that has no table row (e.g. Grand Mirror = stat map_delirium_has_giga_mirror, seeded in
        // SeedSpecialBadges). Unknown ids fall through to "#<id>" so they stay VISIBLE for labeling
        // instead of being silently dropped — this is how new/unmapped specials get noticed. ""only for 0.
        // NOTE: an earlier `key > 1000` cutoff silently discarded every stat-id special (Grand Mirror
        // came through as 24919); removed. See docs/re-findings-atlas.md §2.10.6.
        public static string ResolveBadgeContent(uint id)
        {
            uint key = id & 0xFFFFu;
            if (key == 0)
                return string.Empty;
            if (BadgeContentNames.TryGetValue(key, out var name))
                return name;
            return "#" + key.ToString(CultureInfo.InvariantCulture);
        }

        // Find the route entry that should draw a line to a node carrying one of `contentNames`.
        // Scans content groups in order (group master toggle + per-entry toggle both required) and
        // returns the first match. Returns false when no enabled entry matches this node's content.
        private bool MatchContentRoute(in NodeData nd, out ContentRouteEntry match, out ContentGroupSettings matchGroup)
        {
            match = null;
            matchGroup = null;
            if (Settings?.ContentGroups is not { Count: > 0 })
                return false;

            var contentNames = nd.ContentNames;
            foreach (var grp in Settings.ContentGroups)
            {
                if (!grp.DrawPaths || grp.Contents is not { Count: > 0 })
                    continue;
                foreach (var entry in grp.Contents)
                {
                    if (!entry.DrawPath)
                        continue;

                    // Built-in entries match by map id/classification; user entries by node content.
                    if (!string.IsNullOrEmpty(entry.Match))
                    {
                        if (MatchMapTarget(entry.Match, nd.InternalId, nd.MapInfo))
                        {
                            match = entry;
                            matchGroup = grp;
                            return true;
                        }
                        continue;
                    }

                    if (string.IsNullOrEmpty(entry.ContentName) || contentNames is not { Length: > 0 })
                        continue;
                    foreach (var cn in contentNames)
                    {
                        if (string.Equals(cn, entry.ContentName, StringComparison.OrdinalIgnoreCase))
                        {
                            match = entry;
                            matchGroup = grp;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static ContentInfo MatchContent(string contentName,
            Dictionary<string, ContentInfo> tagMap,
            Dictionary<string, ContentInfo> plainMap)
        {
            if (string.IsNullOrWhiteSpace(contentName))
                return null;

            var normalized = contentName.Replace("\u00A0", " ").Trim();

            int lb = normalized.IndexOf('[');
            int rb = lb >= 0 ? normalized.IndexOf(']', lb + 1) : -1;
            if (lb >= 0 && rb > lb + 1)
            {
                var inside = normalized.Substring(lb + 1, rb - lb - 1);
                var pipe = inside.IndexOf('|');
                var tag = (pipe >= 0 ? inside[..pipe] : inside).Trim();

                if (tagMap.TryGetValue(tag, out var tagInfo))
                    return tagInfo;

                if (plainMap.TryGetValue(tag, out var tagAsPlain))
                    return tagAsPlain;
            }

            foreach (var map in plainMap)
            {
                if (normalized.Contains(map.Key, StringComparison.OrdinalIgnoreCase))
                    return map.Value;
            }

            foreach (var tag in tagMap)
            {
                if (normalized.Contains(tag.Key, StringComparison.OrdinalIgnoreCase))
                    return tag.Value;
            }

            return null;
        }

        private void ApplyBiomeOverrides()
        {
            foreach (var entry in Settings.BiomeOverrides)
            {
                if (Biomes.TryGetValue(entry.Key, out var info))
                {
                    var ov = entry.Value;
                    if (ov.BorderColor.HasValue)
                        info.BorderColor = [ov.BorderColor.Value.X, ov.BorderColor.Value.Y, ov.BorderColor.Value.Z, ov.BorderColor.Value.W];

                    if (ov.Show.HasValue)
                        info.Show = ov.Show.Value;
                }
            }
        }

        private void ApplyContentOverrides()
        {
            foreach (var entry in Settings.ContentOverrides)
            {
                if (MapTags.TryGetValue(entry.Key, out var info) ||
                    MapPlain.TryGetValue(entry.Key, out info))
                {
                    var ov = entry.Value;
                    if (ov.BackgroundColor.HasValue)
                        info.BackgroundColor = [ov.BackgroundColor.Value.X, ov.BackgroundColor.Value.Y, ov.BackgroundColor.Value.Z, ov.BackgroundColor.Value.W];

                    if (ov.FontColor.HasValue)
                        info.FontColor = [ov.FontColor.Value.X, ov.FontColor.Value.Y, ov.FontColor.Value.Z, ov.FontColor.Value.W];

                    if (ov.Show.HasValue)
                        info.Show = ov.Show.Value;

                    if (!string.IsNullOrEmpty(ov.Abbrev))
                        info.Abbrev = ov.Abbrev;
                }
            }
        }

        private static bool ColorsEqual(Vector4 a, Vector4 b, float eps = 0.001f)
        {
            return Math.Abs(a.X - b.X) < eps &&
                   Math.Abs(a.Y - b.Y) < eps &&
                   Math.Abs(a.Z - b.Z) < eps &&
                   Math.Abs(a.W - b.W) < eps;
        }

        private static RectangleF CalculateBounds(float range)
        {
            var baseBoundsTowers = new RectangleF(0, 0, ImGui.GetIO().DisplaySize.X, ImGui.GetIO().DisplaySize.Y);

            return RectangleF.Inflate(baseBoundsTowers, baseBoundsTowers.Width * (range - 1.0f), baseBoundsTowers.Height * (range - 1.0f));
        }

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
