namespace Atlas
{
    using GameHelper;
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

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct AtlasConnectionEdge
        {
            public int Unknown;
            public StdTuple2D<int> Source;
            public StdTuple2D<int> Target;
        }

        private string SettingPathname => Path.Join(DllDirectory, "config", "settings.txt");
        private string MapGroupsPathname => Path.Join(DllDirectory, "config", "mapgroups.json");
        private string NewGroupName = string.Empty;
        // Free-text filters for the "Add content…" / "Add map…" (content-route) / map-group pickers
        // (one combo open at a time).
        private string ContentAddFilter = string.Empty;
        private string MapAddFilter = string.Empty;
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
        }
        private readonly List<NodeData> nodeCache = new();
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

            LoadMapGroups();
            LoadBiomeMap();
            LoadContentMap();
            LoadMapContent();
            LoadMaps();
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

        public override void DrawSettings()
        {
            #region SettingsUI
            // Collapsed-by-default top section grouping the rarely-touched setup toggles
            // (input layout, font, map-name language). CollapsingHeader matches GameHelper's
            // General-tab section style (full-width bar).
            if (ImGui.CollapsingHeader("Settings"))
            {
                ImGui.SeparatorText("Input");
                if (ImGui.Checkbox("Controller Mode", ref Settings.ControllerMode))
                    nodeCache.Clear(); // re-resolve the panel on the other layout next frame
                ImGuiHelper.ToolTip("GameHelper auto-detects controller mode, so you normally don't need this. " +
                    "Tick it only to FORCE the controller Atlas layout if auto-detect ever fails. Either way the " +
                    "plugin falls back to the other layout when the selected one isn't found. In controller mode " +
                    "the overlay also stays visible while the inventory is open.");

                ImGui.SeparatorText("Font");
                if (ImGui.Checkbox("Universal font (render map names in any language)", ref Settings.UniversalFont))
                {
                    if (Settings.UniversalFont)
                        UniversalFont.Apply(DllDirectory);
                    else
                        UniversalFont.Restore();
                }
                ImGuiHelper.ToolTip("Loads the plugin's bundled DejaVuSans + GNU Unifont into the overlay so " +
                    "any-language map names render without configuring a font in GameHelper. Affects the whole overlay; " +
                    "turning it off restores GameHelper's configured font.");

                ImGui.SeparatorText("Map name language");
                if (ImGui.BeginCombo("Language", Settings.Language))
                {
                    foreach (var lang in AvailableLanguages)
                    {
                        bool selected = string.Equals(lang, Settings.Language, StringComparison.OrdinalIgnoreCase);
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
                ImGuiHelper.ToolTip("Display language for map-node names (from maps.json 'translates'). " +
                    "Changing it re-labels nodes immediately. Map Group names are matched in the selected language.");

                ImGui.SeparatorText("Draw Lines");
                if (ImGui.TreeNode("Draw Lines Settings"))
                {
                    ImGui.Checkbox("Shortest Path", ref Settings.RouteLinesThroughNodes);
                    ImGuiHelper.ToolTip("Route lines follow the shortest hop-path through the revealed atlas edges " +
                        "(from the nearest accessible node). When off, a straight line is drawn instead.");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150);
                    ImGui.SliderFloat("Path Thickness", ref Settings.PathLineThickness, 1.0f, 8.0f);
                    ImGui.SetNextItemWidth(150);
                    ImGui.SliderFloat("Arrow spacing", ref Settings.RouteArrowSpacing, 6.0f, 18.0f);
                    ImGuiHelper.ToolTip("Gap between the direction arrows drawn along a route (higher = more spread out).");
                    ImGui.SetNextItemWidth(150);
                    ImGui.SliderFloat("Search route range", ref Settings.DrawSearchInRange, 1.0f, 10.0f);
                    ImGui.TreePop();
                }

                ImGui.SeparatorText("Debug");
                ImGui.Checkbox("Show Node Index (debug/RE)", ref Settings.ShowNodeIndex);
                ImGuiHelper.ToolTip("DEBUG: draws each node's child-index (its number in the atlas-panel child list) as a badge " +
                    "to the left of the map name, so a node referenced by number is easy to locate on-screen.");
            }

            // Collapsed-by-default Display section: node-visibility filters, biome border, label
            // layout, and the content-icon overlay.
            if (ImGui.CollapsingHeader("Display"))
            {
                ImGui.SeparatorText("Atlas Settings");
                ImGui.Checkbox("Hide Completed Maps", ref Settings.HideCompletedMaps);
                ImGui.Checkbox("Hide Not Accessible Maps", ref Settings.HideNotAccessibleMaps);
                ImGui.Checkbox("Hide Available Maps", ref Settings.HideAvailableMaps);
                ImGuiHelper.ToolTip("Hide maps that are accessible/runnable right now. Route/search targets stay visible.");
                ImGui.Checkbox("Show Biome Border", ref Settings.ShowBiomeBorder);
                if (Settings.ShowBiomeBorder)
                    if (ImGui.TreeNode("Biome Settings"))
                    {
                        ImGui.SetNextItemWidth(180);
                        ImGui.SliderFloat("Biome Border Thickness", ref Settings.BiomeBorderThickness, 1.0f, 6.0f);

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
                                ColorSwatch($"Border Color##Biome{id}", ref border);
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

                if (ImGui.TreeNode("Layout Settings"))
                {
                    var nudge = Settings.AnchorNudge;
                    if (ImGui.SliderFloat2("Layout Nudge (px)", ref nudge, -60f, 60f))
                        Settings.AnchorNudge = nudge;
                    ImGui.SliderFloat("Scale Multiplier", ref Settings.ScaleMultiplier, 0.5f, 3.0f);
                    ImGui.TreePop();
                }

                ImGui.SeparatorText("Content Icons");
                ImGui.Checkbox("Show Content Icons", ref Settings.ShowContentIcons);
                ImGuiHelper.ToolTip("Draws each content as its in-game icon (from Plugins\\Atlas\\icons\\<name>.png) above the map " +
                    "name. Content without an icon file falls back to its text name. Icons are suppressed on visible nodes " +
                    "(the game already draws them there) and shown only on hidden ones.");
                if (Settings.ShowContentIcons)
                {
                    ImGui.SetNextItemWidth(180);
                    ImGui.SliderFloat("Content Icon Size", ref Settings.ContentIconSize, 16f, 64f);
                    var iconOffset = Settings.ContentIconOffset;
                    ImGui.SetNextItemWidth(180);
                    if (ImGui.SliderFloat2("Content Icon Offset (X,Y)", ref iconOffset, -64f, 64f))
                        Settings.ContentIconOffset = iconOffset;
                }

                if (ImGui.TreeNode("Map Styles##MapStyles"))
                {
                    ImGui.InputTextWithHint("##MapGroupName", "group name", ref Settings.GroupNameInput, 256);
                    ImGui.SameLine();
                    if (ImGui.Button("Add new map group"))
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
                            if (ImGui.Button($"Rename Group##{i}"))
                            {
                                NewGroupName = mapGroup.Name;
                                ImGui.OpenPopup($"RenamePopup##{i}");
                            }
                            ImGui.SameLine();
                            if (ImGui.Button($"Delete Group##{i}"))
                            {
                                DeleteMapGroup(i);
                            }
                            ImGui.SameLine();
                            ColorSwatch($"##MapGroupBackgroundColor{i}", ref mapGroup.BackgroundColor);
                            ImGui.SameLine();
                            ImGui.Text("Background Color");
                            ImGui.SameLine();
                            ColorSwatch($"##MapGroupFontColor{i}", ref mapGroup.FontColor);
                            ImGui.SameLine(); ImGui.Text("Font Color");

                            for (int j = 0; j < mapGroup.Maps.Count; j++)
                            {
                                var mapName = mapGroup.Maps[j];
                                if (ImGui.InputTextWithHint($"##MapName{i}-{j}", "map name", ref mapName, 256))
                                    mapGroup.Maps[j] = mapName;

                                ImGui.SameLine();
                                if (ImGui.Button($"Delete##MapNameDelete{i}-{j}"))
                                {
                                    mapGroup.Maps.RemoveAt(j);
                                    break;
                                }
                            }

                            if (ImGui.Button($"Add new map##AddNewMap{i}"))
                                mapGroup.Maps.Add(string.Empty);

                            // Pick a map from a filtered list instead of typing it. Stores the localized
                            // name (map styles match by the displayed name in the selected language); the
                            // filter narrows by localized or English name. Skips maps already in the group.
                            ImGui.SameLine();
                            ImGui.SetNextItemWidth(220);
                            if (ImGui.BeginCombo($"##MapGroupAdd{i}", "Add from list…"))
                            {
                                EnsureMapPickCache();
                                ImGui.SetNextItemWidth(-1);
                                ImGui.InputTextWithHint($"##MapGroupFilter{i}", "filter…", ref MapGroupAddFilter, 64);
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
                                ImGui.InputText("New Name", ref NewGroupName, 256);
                                if (ImGui.Button("OK"))
                                {
                                    mapGroup.Name = NewGroupName;
                                    ImGui.CloseCurrentPopup();
                                }
                                ImGui.SameLine();
                                if (ImGui.Button("Cancel"))
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

            ImGui.SeparatorText("Search Maps");
            ImGui.InputTextWithHint("Search Map", "You can search multiple maps at once using a comma separator ','", ref Settings.SearchQuery, 256);
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear"))
                Settings.SearchQuery = string.Empty;
            // Search routing is always on now (the old "Draw Lines to Search in range" toggle is hidden);
            // a non-empty Search query draws routes to the matching maps within range.
            Settings.DrawLinesSearchQuery = true;

            ImGui.SeparatorText("Target farming");
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
                ImGui.InputTextWithHint("##ContentGroupName", "group name", ref Settings.ContentGroupNameInput, 256);
                ImGui.SameLine();
                if (ImGui.Button("Add content group"))
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
                    string title = grp.Locked ? $"{grp.Name} (built-in)##ContentGroup{gi}" : $"{grp.Name}##ContentGroup{gi}";
                    // The built-in group stays expanded while it's the only group; once other groups
                    // exist it collapses by default but the user can still toggle it freely.
                    if (grp.Locked && Settings.ContentGroups.Count == 1)
                        ImGui.SetNextItemOpen(true, ImGuiCond.Always);
                    if (!ImGui.TreeNode(title))
                        continue;

                    bool drawPaths = grp.DrawPaths;
                    if (ImGui.Checkbox($"Draw paths##CG{gi}", ref drawPaths))
                        grp.DrawPaths = drawPaths;
                    ImGuiHelper.ToolTip("Master switch for this group: when off, no route is drawn for any of its content, " +
                        "but each entry keeps its own 'route' checkbox unchanged.");

                    // One line thickness for all entries in the group, shown right under "Draw paths".
                    ImGui.SetNextItemWidth(180);
                    float gth = grp.LineThickness;
                    if (ImGui.SliderFloat($"Line thickness##CGth{gi}", ref gth, 1f, 8f))
                        grp.LineThickness = gth;

                    // The built-in group can't be deleted and its content list is fixed.
                    if (!grp.Locked)
                    {
                        ImGui.SameLine();
                        if (ImGui.Button($"Delete group##CG{gi}"))
                        {
                            Settings.ContentGroups.RemoveAt(gi);
                            ImGui.TreePop();
                            break;
                        }

                        // Add-content combo (only content types not already in this group). Filter box
                        // narrows by content name OR description (in the selected UI language).
                        ImGui.SetNextItemWidth(220);
                        if (ImGui.BeginCombo($"##AddContent{gi}", "Add content…"))
                        {
                            ImGui.SetNextItemWidth(-1);
                            ImGui.InputTextWithHint($"##ContentFilter{gi}", "filter…", ref ContentAddFilter, 64);
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
                        if (ImGui.BeginCombo($"##AddMap{gi}", "Add map…"))
                        {
                            EnsureMapPickCache();
                            ImGui.SetNextItemWidth(-1);
                            ImGui.InputTextWithHint($"##MapFilter{gi}", "filter…", ref MapAddFilter, 64);
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
                        ImGuiHelper.ToolTip("Draw a route to the nearest node carrying this content.");

                        ImGui.SameLine();
                        var col = entry.LineColor;
                        ColorSwatch("##color", ref col);
                        entry.LineColor = col;

                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(60);
                        int hops = entry.MaxHops;
                        if (ImGui.DragInt("##hops", ref hops, 0.1f, 0, 1000))
                            entry.MaxHops = Math.Max(0, hops);
                        ImGuiHelper.ToolTip("Max hops to route through (0 = unlimited). A longer route is suppressed.");

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
            bool needNodeData = !allStatesHidden || doSearch || wantContentRoute
                || Settings.DrawLinesToUniqueMaps || Settings.PathToLineageMaps || Settings.PathToArbiterMaps;
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
                var pendingRoutes = new List<(List<StdTuple2D<int>> path, uint color, float thickness)>();

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

                    if (Settings.HideCompletedMaps && completed)
                        continue;
                    if (Settings.HideNotAccessibleMaps && notAccessible && !routeTarget)
                        continue;
                    if (Settings.HideAvailableMaps && available && !routeTarget)
                        continue;

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

                    if (Settings.ShowBiomeBorder && Biomes.TryGetValue(nd.BiomeId, out var biome) && biome.Show)
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
                    if (Settings.ShowNodeIndex)
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

                    // Per-node content shown ABOVE the map name. Two disjoint sources merge into one
                    // name list: the token vector (element+0x350, class-1: atlas/tower content) and the
                    // badge ids (badge+0x188, class-2: boss/corruption/unique). Each name draws as its
                    // in-game icon when available, else as a text chip. See re-findings §2.10.3.
                    if ((Settings.ShowContentTokens || Settings.ShowContentIcons)
                        && nd.ContentNames is { Length: > 0 })
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
        private void RefreshNodeCache(UiElement atlasUi, int atlasCount)
        {
            if (this.TryRefreshNodeCacheFromCore(atlasCount))
                return;
            this.RefreshNodeCacheSelf(atlasUi, atlasCount);
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
                var mapName = ResolveLocalizedName(internalId, mapInfo, Settings.Language);
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
                    var mapName = ResolveLocalizedName(internalId, mapInfo, Settings.Language);
                    newCache.Add(new NodeData
                    {
                        Address = (IntPtr)(nAddress.GetValue(map) ?? IntPtr.Zero),
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

            ApplyContentLanguage(Settings?.Language);
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
                return ResolveLocalizedName(id, GetMapInfo(id), Settings?.Language);
            }
            if (!string.IsNullOrEmpty(e.Match) && e.Match.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                return ResolveLocalizedMapName(e.Match[5..], Settings?.Language);
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
            var lang = Settings?.Language ?? string.Empty;
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
            if (step == fps.Length)
                return parentAddr;

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
