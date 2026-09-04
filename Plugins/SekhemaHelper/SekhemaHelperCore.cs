namespace SekhemaHelper
{
    using GameHelper;
    using GameHelper.Localization;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Text;

    public sealed class SekhemaHelperCore : PCore<SekhemaHelperSettings>
    {
        private string SettingPathname => Path.Join(DllDirectory, "config", "settings.txt");

        // UI localization via GameHelper's per-plugin dictionaries (Localization\<lang>.json next to the
        // dll). Lazy so it's ready even if a Draw* runs before OnEnable. English literal fallback at each
        // call site keeps the plugin working with no dictionaries present.
        private PluginLocalization loc;
        private PluginLocalization Loc => this.loc ??= new PluginLocalization(this.DllDirectory);
        private string L(string key, string fallback) => this.Loc.T(key, fallback);
        private string LF(string key, string fallback, params object[] args) => this.Loc.F(key, fallback, args);

        private WeightCalculator weightCalculator;
        private bool dumpRequested;
        private bool scanHonourRequested;
        private bool scanHonourByValueRequested;
        private int honourScanCur;
        private int honourScanMax;

        // --- UITree navigation: index path primary, Flags-fingerprint fallback (docs/uitree-guide.md §2b) ---
        // Child indices jump on game patches; each element's Flags field (+0x168 on 0.5.5) encodes its stable
        // "role". Ideally we'd navigate purely by fp (like RunecraftHelper.WalkFp), but here the
        // fingerprints are GENERIC containers (0x..F1 at every level) and the terminals are WEAK (water
        // = "any numeric text", honour = "any bar geometry"), so an fp-walk could backtrack into the
        // wrong sibling. So the index path stays AUTHORITATIVE while it resolves a terminal-valid leaf,
        // and the harvested fp chain is the RECOVERY path used only when the index path breaks (a patch
        // shifted indices) — the stable Flags still find the element though it moved. Re-harvest the
        // chains after a client patch with the "Dump UI fingerprints" debug button.
        private const int UiChildrenFirstOffset = 0x10;   // UiElementBase.ChildrensPtr.First (StdVector @ +0x10).
        // 0.5.5: everything in UiElementBase AFTER ParentPtr moved -0x18 (Flags 0x180 -> 0x168,
        // UnscaledSize 0x288 -> 0x270); ParentPtr itself and the children vector are unchanged. These are
        // the same values GameOffsets.UiElementBaseOffset now carries -- this plugin keeps private copies
        // only to read a raw child address without materialising a whole element, so they must be kept in
        // step with the core by hand. A stale Flags word is silent: every fp compare simply misses.
        private const int UiElementFlagsOffset = 0x168;    // UiElementBaseOffset.Flags (uint).
        private const int UiParentOffset = 0xB8;           // UiElementBaseOffset.ParentPtr (unchanged).
        private const int UiUnscaledSizeXOffset = 0x270;   // UiElementBaseOffset.UnscaledSize.X (float).
        private const uint IsVisibleMask = 0x800;          // Flags bit 0x0B — masked before fp compare.

        // Live Sacred Water, read from the Trial map panel's water counter text leaf.
        // Index path SekhemasTrialMapPanel -> [1][0][0][1]; displayed value is the std::wstring at
        // leaf+0x490 (docs §4.7.9). -1 = not yet read (rule degrades via WeightCalculator.WaterKnown).
        // Fp chain harvested live 2026-06-23 (config\ui_fp_dump.txt); IsVisible masked at compare time.
        private static int liveWater = -1;
        private static readonly int[] WaterIndexPath = { 1, 0, 0, 1 };
        private static readonly uint[] WaterFpChain = { 0x00502EF1, 0x00502EF1, 0x00502EF3, 0x00502EE1 };
        // 0.5.5: 0x4C0 -> 0x490. A derived TEXT element lost 0x30 in total -- 0x18 from UiElementBase's
        // tail plus another 0x18 of the text class's own. Measured by reading a known string back, not
        // by shifting the old value.
        private const int WaterTextWStringOffset = 0x490;

        // Live key counts (Bronze/Silver/Gold), read from the same map-panel water+keys container as the
        // water counter: SekhemasTrialMapPanel -> [1][0] is the container, where child[0]=water and
        // child[1|2|3] are the three key tiers; each value leaf is child[N][1] with the displayed number
        // as a std::wstring at leaf+0x490 (docs re-findings-sekhema §4.6). Read while the map is open,
        // then cached so the HUD can show them when the map is closed (like water). -1 = not yet read.
        private static int liveBronze = -1, liveSilver = -1, liveGold = -1;
        // Map-OPEN source: from SekhemasTrialMapPanel (read after the visibility gate, like water).
        private static readonly int[] BronzeKeyIndexPath = { 1, 0, 1, 1 };
        private static readonly int[] SilverKeyIndexPath = { 1, 0, 2, 1 };
        private static readonly int[] GoldKeyIndexPath = { 1, 0, 3, 1 };
        // Map-CLOSED source: the bottom HUD panel GameUi.Address -> [13] (the same container that holds
        // the honour bar at [13][5]); key tiers at [13][1|2|3][1], value wstring @ +0x490. Captured live
        // 2026-06-25 (chest room: GameUi -> [13] -> [1|2|3] -> [1]). Read before the gate so keys show
        // while the map is closed; values are read fresh here (no staleness in a chest room).
        private static readonly int[] HudBronzeKeyIndexPath = { 13, 1, 1 };
        private static readonly int[] HudSilverKeyIndexPath = { 13, 2, 1 };
        private static readonly int[] HudGoldKeyIndexPath = { 13, 3, 1 };

        // Live Honour as a percentage, derived from the honour bar's fill width (docs §4.7.10).
        // GameUi.Address is uiManagerPtr (the panel resolves as GameUi.Address -> [84][0], with no
        // leading [1]). Index path GameUi.Address -> [13][5][1] = the FILL element; its parent (+0xB8)
        // is the FRAME. honour% = fill.UnscaledSize.X / frame.UnscaledSize.X * 100 (scale cancels; bar
        // fill = cur/max). The honour subtree keeps valid geometry even when the map hides the bar
        // (verified: [13] flags 0x..F1 with IsVisible cleared, still frameW=680/fillW=660.5). -1 =
        // unreadable (HonourKnown=false → threshold rules skip). Fp chain harvested live 2026-06-23.
        private static double liveHonourPct = -1;
        private static readonly int[] HonourIndexPath = { 13, 5, 1 };
        private static readonly uint[] HonourFpChain = { 0x005026F1, 0x00502EF1, 0x00502EF7 };
        private bool dumpUiFpRequested;
        private bool dumpFloorRequested;
        // Node->physical-room mapping research (FloorResearch): collect ground-truth across many floors.
        private bool researchNewFloorRequested;
        private bool researchRecordRoomRequested;
        private bool researchSaveFloorRequested;

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(SettingPathname))
            {
                var content = File.ReadAllText(SettingPathname);
                var opts = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
                Settings = JsonConvert.DeserializeObject<SekhemaHelperSettings>(content, opts) ?? Settings;
            }

            // Migration: profiles saved before AfflictionWeights was (re)introduced deserialize it as
            // null. Backfill from the default table so room-affliction weighting works without losing
            // the user's other tuned weights.
            foreach (var profile in Settings.Profiles.Values)
                profile.AfflictionWeights ??= ProfileContent.CreateDefaultProfile().AfflictionWeights;

            this.weightCalculator = new WeightCalculator(Settings);
        }

        public override void OnDisable() => Mem.Close();

        public override void SaveSettings()
        {
            var dir = Path.GetDirectoryName(SettingPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(SettingPathname, JsonConvert.SerializeObject(Settings, Formatting.Indented));
        }

        public override void DrawSettings()
        {
            // ---- Profile ----
            ImGui.SeparatorText(this.L("sk.profile", "Profile"));
            if (ImGui.BeginCombo(this.L("sk.active_profile", "Active Profile"), Settings.CurrentProfile))
            {
                foreach (var name in Settings.Profiles.Keys)
                {
                    bool sel = name == Settings.CurrentProfile;
                    if (ImGui.Selectable(name, sel))
                        Settings.CurrentProfile = name;
                    if (sel)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            // ---- Weights — {profile} (Reset + Room types / Afflictions / Rewards) ----
            DrawWeightSettings();

            ImGui.Checkbox(this.L("sk.debug", "Debug (show weights)"), ref Settings.DebugEnable);
            if (Settings.DebugEnable)
            {
                // Debug/RE dump tooling below is developer-facing (references config file paths) — left in English.
                ColorSwatch("Debug Text Color", ref Settings.TextColor);
                ColorSwatch("Debug Background", ref Settings.BackgroundColor);
                // Force the Death-crystal route through an explicit crystal-id set (ignores active/collected
                // state + room filter), to reproduce a routing bug with a known crystal set.
                ImGui.InputText("Force Crystal IDs (route override)", ref Settings.HazardDebugCrystalIds, 128);
                ImGuiHelper.ToolTip("Comma/space separated crystal entity ids (read from the yellow/grey id labels). Empty = normal room route.");
                ImGui.Checkbox("Paint Walkable Grid (where can I walk)", ref Settings.HazardDebugDrawWalkable);
                ImGuiHelper.ToolTip("Green = cells the game marks walkable, around the player. Shows why an A* leg goes straight (player on a non-walkable cell).");
                if (Settings.HazardDebugDrawWalkable)
                    ImGui.SliderFloat("Walkable Paint Radius", ref Settings.HazardDebugWalkableRadius, 50f, 1200f, "%.0f");
                // Dump every room's content FK pairs (+ the FloorData content vector) to
                // config\fk_dump.txt on the next frame the Trial map is open.
                if (ImGui.Button("Dump FK -> config\\fk_dump.txt"))
                    this.dumpRequested = true;
                if (ImGui.Button("Dump Honour candidates -> config\\honour_scan.txt"))
                    this.scanHonourRequested = true;
                ImGui.InputInt("Honour current", ref this.honourScanCur);
                ImGui.InputInt("Honour max", ref this.honourScanMax);
                if (ImGui.Button("Locate Honour by value -> config\\honour_byvalue.txt") &&
                    this.honourScanCur > 0 && this.honourScanMax > 0)
                    this.scanHonourByValueRequested = true;
                if (ImGui.Button("Dump UI fingerprints -> config\\ui_fp_dump.txt"))
                    this.dumpUiFpRequested = true;

                // Whole-floor read (SanctumPlugin picks + placement records): draw every room (incl.
                // UI-hidden) on the large map, and dump the floor + raw door bytes for edge-graph RE.
                ImGui.Checkbox("Draw floor plots (all rooms on large map)", ref Settings.DebugDrawFloorPlots);
                ImGuiHelper.ToolTip("Reads the client's terrain layout: every room's type+reward at its world position, incl. rooms the UI hides. Open the large map to see it.");
                if (ImGui.Button("Dump floor -> config\\sekhema_floor_dump.txt"))
                    this.dumpFloorRequested = true;

                // --- Node->physical-room mapping research (open the Trial map for each action) ---
                ImGui.SeparatorText("Room-mapping research");
                ImGuiHelper.ToolTip("Collect ground-truth to reverse the server's (layer,room)->physical-room assignment. Keep the Trial map OPEN when pressing these.\n1) enter a fresh floor -> New floor snapshot\n2) in each room you visit -> Record current room\n3) at floor end -> Save floor\nRepeat for 8-15 floors; data lands in config\\sekhema_research\\dataset.jsonl.");
                if (ImGui.Button("1) New floor snapshot"))
                    this.researchNewFloorRequested = true;
                ImGui.SameLine();
                if (ImGui.Button("2) Record current room"))
                    this.researchRecordRoomRequested = true;
                ImGui.SameLine();
                if (ImGui.Button("3) Save floor -> dataset.jsonl"))
                    this.researchSaveFloorRequested = true;
                ImGui.TextDisabled(FloorResearch.Status);
            }

            // ---- Display ----
            ImGui.SeparatorText(this.L("sk.display", "Display"));
            ImGui.Checkbox(this.L("sk.draw_best_path", "Draw Best Path"), ref Settings.DrawBestPath);
            ImGui.SliderFloat(this.L("sk.frame_thickness", "Frame Thickness"), ref Settings.FrameThickness, 1f, 10f);
            ColorSwatch(this.L("sk.best_path_color", "Best Path Color"), ref Settings.BestPathColor);

            // ---- Overlay POI (Portals / Levers / Crystals) ----
            if (ImGui.CollapsingHeader(this.Loc.Title("sk.overlay_poi", "Overlay POI", "sk_overlay_poi")))
            {
                ImGui.Indent();

                ImGui.Checkbox(this.L("sk.show_portals", "Show Portals (Ritual)"), ref Settings.ShowPortals);
                ImGuiHelper.ToolTip(this.L("sk.show_portals_hint", "Mark ACTIVE hazard Portals on the large map. Removed once a portal closes."));
                if (Settings.ShowPortals)
                    ColorSwatch(this.L("sk.portal_color", "Portal Color"), ref Settings.PortalColor);

                ImGui.Checkbox(this.L("sk.show_levers", "Show Levers (Gauntlet)"), ref Settings.ShowLevers);
                ImGuiHelper.ToolTip(this.L("sk.show_levers_hint", "Mark the un-activated Sanctum lever on the large map. Removed once pulled."));
                if (Settings.ShowLevers)
                    ColorSwatch(this.L("sk.lever_color", "Lever Color"), ref Settings.LeverColor);

                if (Settings.ShowPortals || Settings.ShowLevers)
                    ImGui.SliderFloat(this.L("sk.poi_marker_radius", "POI Marker Radius"), ref Settings.RoomObjectMarkerRadius, 4f, 20f);

                ImGui.Spacing();
                ImGui.Checkbox(this.L("sk.show_crystals", "Show Crystals (Escape)"), ref Settings.DrawHazardRoute);
                ImGuiHelper.ToolTip(this.L("sk.show_crystals_hint", "Death-crystal (HourglassLethal) collection route + markers on the map overlay."));
                if (Settings.DrawHazardRoute)
                {
                    ImGui.Checkbox(this.L("sk.follow_walkable", "Follow Walkable Terrain (A*)"), ref Settings.HazardWalkableRoute);
                    ImGuiHelper.ToolTip(this.L("sk.follow_walkable_hint", "On: route follows the walkable path like Radar. Off: straight lines."));
                    ColorSwatch(this.L("sk.route_line_color", "Route Line Color"), ref Settings.HazardRouteColor);
                    ImGui.SameLine();
                    ColorSwatch(this.L("sk.crystal_marker_color", "Crystal Marker Color"), ref Settings.HazardMarkerColor);
                    ImGui.SliderFloat(this.L("sk.route_thickness", "Route Thickness"), ref Settings.HazardRouteThickness, 1f, 8f);
                    ImGui.SliderFloat(this.L("sk.marker_radius", "Marker Radius"), ref Settings.HazardMarkerRadius, 3f, 20f);
                    ImGui.SliderFloat(this.L("sk.max_grid_distance", "Max Grid Distance (0 = all)"), ref Settings.HazardMaxGridDistance, 0f, 500f, "%.0f");
                    ImGui.SliderInt(this.L("sk.room_id_gap", "Room ID Gap (0 = off)"), ref Settings.HazardIdGroupGap, 0, 200);
                    ImGuiHelper.ToolTip(this.L("sk.room_id_gap_hint", "Crystals of one room share contiguous entity ids; a larger gap = another room. Only the player's room is routed."));
                    ImGui.SliderFloat(this.L("sk.room_margin", "Room Margin (in-room gate)"), ref Settings.HazardRoomMargin, 0f, 800f, "%.0f");
                    ImGuiHelper.ToolTip(this.L("sk.room_margin_hint", "Route shows only when the player is within the crystal room's bounding box + this margin. Prevents it appearing from an adjacent room."));
                }

                ImGui.Unindent();
            }

            ImGui.Separator();

            // ---- Final-room chest priority ----
            ImGui.Checkbox(this.L("sk.mark_best_chests", "Mark Best Chests by Keys"), ref Settings.DrawChestPriority);
            ImGuiHelper.ToolTip(this.L("sk.mark_best_chests_hint", "In the reward room, marks the best chests on the large map. For each key\n" +
                "tier it highlights the top-N chests by content priority, where N = your live\n" +
                "Bronze/Silver/Gold key count."));
            if (Settings.DrawChestPriority)
            {
                ColorSwatch(this.L("sk.selected_marker_color", "Selected Marker Color"), ref Settings.ChestMarkerColor);
                ImGui.SliderFloat(this.L("sk.chest_marker_radius", "Chest Marker Radius"), ref Settings.ChestMarkerRadius, 4f, 24f);
                Settings.ChestPriorityOrder ??= ChestPriority.DefaultOrder();
                Settings.ChestDisabledContent ??= new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                if (ImGui.TreeNode(this.Loc.Title("sk.content_priority", "Content priority (top = best)", "sk_content_priority")))
                {
                    ImGui.TextDisabled(this.L("sk.content_priority_hint", "Tick a type to track it; higher in the list = higher priority.\n" +
                        "Un-tick types you don't want marked (to track only a few, un-tick the rest).\n" +
                        "Use the arrows to reorder."));
                    var order = Settings.ChestPriorityOrder;
                    var off = Settings.ChestDisabledContent;
                    int moveFrom = -1, moveTo = -1;
                    if (ImGui.BeginTable("chestPrio", 2,
                        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit))
                    {
                        ImGui.TableSetupColumn("##move", ImGuiTableColumnFlags.WidthFixed, 64f);
                        ImGui.TableSetupColumn(this.L("sk.col_content", "Content"), ImGuiTableColumnFlags.WidthStretch);
                        for (int i = 0; i < order.Count; i++)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableSetColumnIndex(0);
                            ImGui.PushID(i);
                            if (ImGui.ArrowButton("up", ImGuiDir.Up) && i > 0)
                            {
                                moveFrom = i; moveTo = i - 1;
                            }
                            ImGui.SameLine();
                            if (ImGui.ArrowButton("down", ImGuiDir.Down) && i < order.Count - 1)
                            {
                                moveFrom = i; moveTo = i + 1;
                            }
                            ImGui.TableSetColumnIndex(1);
                            bool enabled = !off.Contains(order[i]);
                            if (ImGui.Checkbox($"{i + 1}.  {order[i]}", ref enabled))
                            {
                                if (enabled) off.Remove(order[i]);
                                else off.Add(order[i]);
                            }
                            ImGui.PopID();
                        }
                        ImGui.EndTable();
                    }
                    // Apply at most one move per frame (after the loop, so the table isn't mutated mid-draw).
                    if (moveFrom >= 0 && moveTo >= 0)
                    {
                        var item = order[moveFrom];
                        order.RemoveAt(moveFrom);
                        order.Insert(moveTo, item);
                    }
                    if (ImGui.Button(this.L("sk.reset_chest_priority", "Reset chest priority order")))
                    {
                        Settings.ChestPriorityOrder = ChestPriority.DefaultOrder();
                        Settings.ChestDisabledContent = ChestPriority.DefaultDisabled();
                    }
                    ImGui.TreePop();
                }
            }
        }

        // Edit the active profile's weights in place. Everything lives in Settings.Profiles, which is
        // serialized to config\settings.txt, so edits persist and load with the profile; the
        // WeightCalculator reads the active profile every frame, so changes take effect live.
        private void DrawWeightSettings()
        {
            if (!Settings.Profiles.TryGetValue(Settings.CurrentProfile, out var profile) || profile == null)
                return;

            ImGui.SeparatorText(this.LF("sk.weights", "Weights — {0}", Settings.CurrentProfile));
            ImGui.TextDisabled(this.L("sk.weights_hint", "Higher = more desirable. Drag to adjust (Ctrl+click to type). Saved to config."));

            if (ImGui.Button(this.L("sk.reset_profile", "Reset this profile to defaults")))
            {
                Settings.Profiles[Settings.CurrentProfile] = Settings.CurrentProfile == "No-Hit"
                    ? ProfileContent.CreateNoHitProfile()
                    : ProfileContent.CreateDefaultProfile();
                return;
            }

            DrawWeightGroup(this.L("sk.group_room_types", "Room types"), "roomtypes", profile.RoomTypeWeights);

            // Resource-aware reward suppression. SOFT: lowers the reward so an equally-good alternative is
            // preferred, but the path still routes through it when every other option is worse. Lives on
            // Settings (not per-profile). Grouped under Room types per the menu layout.
            ImGui.Indent();
            ImGui.Checkbox(this.L("sk.avoid_merchant", "Avoid Merchant when water below"), ref Settings.SuppressMerchantLowWater);
            if (Settings.SuppressMerchantLowWater)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(130f);
                ImGui.SliderInt("##merchantwater", ref Settings.MerchantWaterThreshold, 100, 1000);
            }
            ImGui.Checkbox(this.L("sk.avoid_honour_restore", "Avoid honour restore when honour above"), ref Settings.SuppressHonourRestoreHighPct);
            if (Settings.SuppressHonourRestoreHighPct)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(130f);
                ImGui.SliderInt("##honourpct", ref Settings.HonourRestoreThresholdPct, 30, 100, "%d%%");
            }
            ImGui.Unindent();

            DrawWeightGroup(this.L("sk.group_afflictions", "Afflictions"), "afflictions", profile.AfflictionWeights);
            DrawWeightGroup(this.L("sk.group_rewards", "Rewards"), "rewards", profile.RewardWeights);
        }

        // One collapsible group of {name → weight} sliders. Edits the dictionary value in place.
        // `title` is the (localized) display label; `id` is a stable, language-independent ImGui id so
        // the collapse state and per-slider ids don't reset when the UI language changes.
        private static void DrawWeightGroup(string title, string id, Dictionary<string, float> weights)
        {
            if (weights == null || weights.Count == 0)
                return;
            if (!ImGui.CollapsingHeader($"{title} ({weights.Count})###{id}"))
                return;

            ImGui.PushID(id);
            ImGui.Indent();
            var keys = new List<string>(weights.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                float v = weights[key];
                if (ImGui.DragFloat(key, ref v, 10f, -1_000_000f, 1_000_000f, "%.0f"))
                    weights[key] = v;
            }
            ImGui.Unindent();
            ImGui.PopID();
        }

        public override void DrawUI()
        {
            // Draw while the game OR GameHelper itself is foreground (so config/debug is visible too).
            bool ghForeground = Process.GetCurrentProcess().MainWindowHandle == GetForegroundWindow();
            if (!Core.Process.Foreground && !ghForeground)
                return;

            var drawList = ImGui.GetBackgroundDrawList();

            // "Death crystal" (HourglassLethal) collection route — drawn on the map overlay (large map
            // + minimap) like Radar, independent of the Trial map panel.
            if (this.scanHonourRequested)
            {
                this.scanHonourRequested = false;
                HonourReader.DumpCandidates(Path.Join(DllDirectory, "config", "honour_scan.txt"));
            }

            if (this.scanHonourByValueRequested)
            {
                this.scanHonourByValueRequested = false;
                HonourReader.ScanForPair(this.honourScanCur, this.honourScanMax,
                    Path.Join(DllDirectory, "config", "honour_byvalue.txt"));
            }

            // The route draws into its own transparent culling windows (like Radar), not this draw
            // list, so the in-game map texture can't hide it. It manages its own ImGui.Begin/End.
            HazardRoute.Draw(Settings);

            // Room interactables (Portals / Lever) on the large map, coloured by used/activated state
            // (§12). Self-gates on large-map visibility; independent of the Trial map panel below.
            RoomObjects.Draw(Settings);

            // Whole-floor plots (all rooms incl. UI-hidden) on the large map — debug overlay + dump.
            FloorReader.Draw(Settings);
            if (this.dumpFloorRequested)
            {
                this.dumpFloorRequested = false;
                FloorReader.Dump(Path.Join(DllDirectory, "config", "sekhema_floor_dump.txt"));
            }

            var gameUi = Core.States.InGameStateObject.GameUi;

            var panel = gameUi?.SekhemasTrialMapPanel;
            if (panel == null)
            {
                DebugHud(drawList, "panel = null (not in Trial?)");
                return;
            }

            // Honour% from the bottom-HUD bar fill geometry (docs §4.7.10) — read here, BEFORE the
            // map-visibility gate: when the map opens the honour bar relocates to the top and the
            // bottom bar at [1][13][5] is hidden, so we'd read nothing. Reading while the map is
            // closed (bar present) and caching the last value is correct — honour can't change while
            // you're paused on the map. Replaces the retired HonourReader heap scan.
            var uiHonourPct = ReadHonourPctFromUi(gameUi.Address);
            if (uiHonourPct >= 0)
                liveHonourPct = uiHonourPct;
            // Key counts from the bottom HUD panel (works while the map is CLOSED, e.g. chest room).
            // Read before the visibility gate; cached so they persist on screen.
            CacheKeysFrom(gameUi.Address, HudBronzeKeyIndexPath, HudSilverKeyIndexPath, HudGoldKeyIndexPath);
            // Final-room chest priority: mark the best openable chests on the large map using the live
            // key budget (§11.3/§11.5). Self-gates on large-map visibility; uses this frame's key counts.
            ChestPriority.Draw(Settings, liveBronze, liveSilver, liveGold);
            // Harvest fp chains (debug): dump the learned chains + a live walk. Honour only exists while
            // the map is CLOSED and water only while it's OPEN, so the chains are learned in-memory frame
            // by frame — walk around (learns honour) then open the map (learns water), then dump both.
            if (this.dumpUiFpRequested)
            {
                this.dumpUiFpRequested = false;
                DumpUiFingerprints(panel.Address, gameUi.Address);
            }
            if (Settings.DebugEnable)
                DrawResourcesHud(drawList);
            if (!panel.IsVisible)
            {
                DebugHud(drawList, $"panel 0x{panel.Address.ToInt64():X} not visible (map closed)");
                return;
            }

            var floor = SekhemaReader.Read(panel);
            DebugHud(drawList, $"panel 0x{panel.Address.ToInt64():X}  {floor.Status}");
            if (!floor.IsValid)
                return;

            if (this.dumpRequested)
            {
                this.dumpRequested = false;
                DumpFk(floor);
            }

            ClassifyRooms(floor);

            // Room-mapping research harness (ground-truth collection across floors). Handled here so the
            // classified `floor` (revealed ids) and the open-map FloorData are both available.
            if (this.researchNewFloorRequested)
            {
                this.researchNewFloorRequested = false;
                FloorResearch.NewFloor(floor);
            }
            if (this.researchRecordRoomRequested)
            {
                this.researchRecordRoomRequested = false;
                FloorResearch.RecordRoom(floor);
            }
            if (this.researchSaveFloorRequested)
            {
                this.researchSaveFloorRequested = false;
                FloorResearch.SaveFloor(Path.Join(DllDirectory, "config", "sekhema_research"));
            }

            this.weightCalculator ??= new WeightCalculator(Settings);
            // Live water + key counts from the visible map panel's counter text (docs §4.7.9 / §4.6).
            // Cached so the HUD can keep showing them once the map closes.
            var uiWater = ReadSacredWaterFromUi(panel.Address);
            if (uiWater >= 0)
                liveWater = uiWater;
            CacheKeysFrom(panel.Address, BronzeKeyIndexPath, SilverKeyIndexPath, GoldKeyIndexPath);

            // Player defensive stats for the dynamic affliction model (a "no Armour" curse hurts an
            // armour build far more than an evasion one). Best-effort; 0 when unknown → dynamic terms
            // collapse and the static affliction weight is used. NOT the removed runState scan.
            ReadPlayerStats(out var evasion, out var es, out var armour, out var life, out var qotf);
            this.weightCalculator.Evasion = evasion;
            this.weightCalculator.EnergyShield = es;
            this.weightCalculator.Armour = armour;
            this.weightCalculator.Life = life;
            this.weightCalculator.HasQueenOfTheForest = qotf;

            // Live resources for resource-aware reward suppression (Merchant vs water, honour restore vs
            // honour%). -1 stays unknown → the calculator skips those rules.
            this.weightCalculator.Water = liveWater;
            this.weightCalculator.HonourPct = liveHonourPct;

            // Weights per room.
            var weights = new Dictionary<(int, int), double>();
            var debugTexts = new Dictionary<(int, int), string>();
            for (int l = 0; l < floor.Layers.Count; l++)
                for (int r = 0; r < floor.Layers[l].Count; r++)
                {
                    var (w, dbg) = this.weightCalculator.Calculate(floor.Layers[l][r]);
                    weights[(l, r)] = w;
                    debugTexts[(l, r)] = dbg;
                }

            if (Settings.DebugEnable)
            {
                foreach (var layer in floor.Layers)
                    foreach (var room in layer)
                    {
                        if (!room.HasWidget)
                            continue;
                        var txt = $"W:{weights[(room.Layer, room.Index)]:F0}\n{debugTexts[(room.Layer, room.Index)]}";
                        drawList.AddText(room.ScreenPos, ImGuiHelper.Color(Settings.TextColor), txt);
                    }
            }

            if (Settings.DrawBestPath)
            {
                var path = PathFinder.FindBestPath(floor, weights);
                uint color = ImGuiHelper.Color(Settings.BestPathColor);
                foreach (var (l, r) in path)
                {
                    if (l == floor.PlayerLayer && r == floor.PlayerRoom)
                        continue;
                    var room = floor.Get(l, r);
                    if (room == null || !room.HasWidget)
                        continue;
                    drawList.AddRect(room.ScreenPos, room.ScreenPos + room.ScreenSize,
                        color, 0f, ImDrawFlags.None, Settings.FrameThickness);
                }
            }
        }

        // Player defensive stats for WeightCalculator's dynamic affliction model. Read from the player's
        // Stats component (standard GameHelper entity component) — NOT the runState active-effect scan.
        private static void ReadPlayerStats(out int evasion, out int energyShield, out int armour, out int life, out bool qotf)
        {
            evasion = 0;
            energyShield = 0;
            armour = 0;
            life = 0;
            qotf = false;
            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            if (player == null || !player.TryGetComponent<Stats>(out var stats))
                return;
            // Confirmed live 2026-06-10 (Research --stats vs character sheet):
            //  - effective Evasion / Max ES match StatsChangedByItems (NOT the sum of both dicts).
            //    The items layer is the player's OWN gear/passive total; the buff layer can carry
            //    transient externals (e.g. another player's ES aura), which must NOT count for a
            //    solo Sekhema run, so we read the items layer here.
            //  - the Queen-of-the-Forest stat is granted via the buff/action layer
            //    (key 9490 movement_speed_is_only_base_…_per_x_evasion_rating: items=0, buffs>0 when worn).
            // Armour (235) and Max Life (239) added for the relevance-based affliction model
            // (see WeightCalculator.DynamicAffliction). Stat ids verified vs game_dat/Stats.tsv.
            evasion = ItemStat(stats, GameStats.evasion_rating);
            energyShield = ItemStat(stats, GameStats.maximum_energy_shield);
            armour = ItemStat(stats, GameStats.armour);
            life = ItemStat(stats, GameStats.maximum_life);
            qotf = StatValue(stats, GameStats.movement_speed_is_only_base_positive_1percentage_per_x_evasion_rating) > 0;
        }

        // Effective value from the items layer (matches the in-game character sheet).
        private static int ItemStat(Stats stats, GameStats key)
            => stats.StatsChangedByItems != null && stats.StatsChangedByItems.TryGetValue(key, out var v) ? v : 0;

        // Sum across both stat dicts (used for buff/action-granted stats like QotF).
        private static int StatValue(Stats stats, GameStats key)
        {
            int v = 0;
            if (stats.StatsChangedByItems != null && stats.StatsChangedByItems.TryGetValue(key, out var a))
                v += a;
            if (stats.StatsChangedByBuffAndActions != null && stats.StatsChangedByBuffAndActions.TryGetValue(key, out var b))
                v += b;
            return v;
        }

        // Classify each revealed room from the FloorData content vector (FloorData+0x18, stride 0x40),
        // each entry keyed by (layer,room) in its first two bytes and carrying up to 3 content FK
        // pairs {row, table} at +0x08 + k*0x10:
        //   SanctumRooms.dat  -> fight room "Caverns_<TYPE>_NN" (type) OR "Caverns_Treasure..." (reward)
        //
        // This reads the game-data structure directly (verified live 2026-06-20: ids like
        // "Caverns_Arena_02" resolve here). It replaces the old per-widget read at widget+0x4D8, which a
        // client patch left empty — every room then fell back to base weight, so editing the weight
        // tables had no effect on the chosen path. Only revealed rooms have entries in this vector;
        // deeper rooms stay unclassified (weight = base).
        private static void ClassifyRooms(SekhemaFloor floor)
        {
            if (floor.FloorDataAddr == IntPtr.Zero)
                return;
            var first = Mem.Read<IntPtr>(floor.FloorDataAddr + 0x18);
            var last = Mem.Read<IntPtr>(floor.FloorDataAddr + 0x20);
            if (first == IntPtr.Zero || last.ToInt64() <= first.ToInt64())
                return;
            long count = (last.ToInt64() - first.ToInt64()) / 0x40;
            for (long i = 0; i < count && i < 512; i++)
            {
                var e = first + (int)(i * 0x40);
                int layer = Mem.Read<byte>(e + 0x00);
                int idx = Mem.Read<byte>(e + 0x01);
                var room = floor.Get(layer, idx);
                if (room == null)
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    var rowPtr = Mem.Read<IntPtr>(e + 0x08 + k * 0x10);
                    var tablePtr = Mem.Read<IntPtr>(e + 0x10 + k * 0x10);
                    if (rowPtr == IntPtr.Zero || tablePtr == IntPtr.Zero)
                        continue;
                    var tpath = Mem.ReadWideString(Mem.Read<IntPtr>(tablePtr + 0x08), 96);
                    if (string.IsNullOrEmpty(tpath))
                        continue;

                    if (tpath.IndexOf("SanctumPersistentEffects", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // Room-imposed affliction (display name @ row+0x28). FloorData-derived, not the
                        // player's active-effect state — feeds AfflictionWeight in WeightCalculator.
                        var name = Mem.ReadWideString(Mem.Read<IntPtr>(rowPtr + 0x28), 48);
                        if (!string.IsNullOrEmpty(name))
                            room.Affliction = name;
                    }
                    else if (tpath.IndexOf("SanctumRooms", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var id = Mem.ReadWideString(Mem.Read<IntPtr>(rowPtr + 0x00), 64);
                        if (string.IsNullOrEmpty(id))
                            continue;
                        if (id.IndexOf("Treasure", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var rw = MapReward(id);
                            if (rw != null)
                                room.Reward = rw;
                        }
                        else
                        {
                            var t = ExtractRoomType(id);
                            if (t != null)
                                room.RoomType = t;
                        }
                    }
                }
            }
        }

        // Map the SanctumRooms.Id token (parts[1]) to the in-game DISPLAY name the player sees, which
        // is also what the legacy PathfindSanctum profile keys on. Resolved offline (2026-06-10) by
        // joining SanctumRooms.RoomType -> SanctumRoomTypes display-name array (dump_dat.py):
        //   token   RoomType      UI display ("… Trial")
        //   Arena   TimerArena    Hourglass
        //   Ritual  PortalArena   Ritual
        //   Lair    Lair          Chalice
        //   Explore Explore       Escape
        //   Gauntlet Gauntlet     Gauntlet   (identity)
        //   Boss    Boss          (per-floor boss name)  (identity token kept)
        private static readonly Dictionary<string, string> TokenToDisplay = new(StringComparer.Ordinal)
        {
            ["Arena"] = "Hourglass",
            ["Lair"] = "Chalice",
            ["Explore"] = "Escape",
            // Ritual/Gauntlet/Boss tokens already match their display short name.
        };

        // "Caverns_Gauntlet_02" -> "Gauntlet"; "Caverns_Arena_03" -> "Hourglass" (UI display name).
        private static string ExtractRoomType(string id)
        {
            var parts = id.Split('_');
            if (parts.Length < 2)
                return null;
            var tok = parts[1];
            return TokenToDisplay.TryGetValue(tok, out var t) ? t : tok;
        }

        // Map a "<Region>_Treasure..." reward-room id to a ProfileContent reward name.
        // Region prefix is the floor tier (Caverns/Ruins/Depths/...); the token after "Treasure" is the kind.
        // Confirmed kinds (FOUND.md, all 4 floors): Key{Bronze,Silver,Gold}, Chest{Bronze,Silver,Gold},
        // Water{Minor,Major}, Merchant_01, LegendWater, LegendPledge, LegendHonor, LegendBoon,
        // LegendCurse, LegendRandom. Unknown -> null (base weight).
        private static string MapReward(string id)
        {
            string s = id.ToLowerInvariant();
            if (s.Contains("key"))
                return s.Contains("gold") ? "Gold Key" : s.Contains("silver") ? "Silver Key" : "Bronze Key";
            if (s.Contains("chest") || s.Contains("cache"))
                return s.Contains("gold") ? "Golden Cache" : s.Contains("silver") ? "Silver Cache" : "Bronze Cache";
            if (s.Contains("water") || s.Contains("fountain"))
                return (s.Contains("legend") || s.Contains("major") || s.Contains("large")) ? "Large Fountain" : "Fountain";
            if (s.Contains("merchant"))
                return "Merchant";
            if (s.Contains("pledge"))
                return "Pledge to Kochai";
            // The client carries no deity for the honour-shrine reward (PoE1 had Halani/Galai/...);
            // it is a single generic "Honour" reward in PoE2 0.5.x.
            if (s.Contains("honor") || s.Contains("honour"))
                return "Honour";
            if (s.Contains("boon"))
                return "Boon";
            if (s.Contains("curse"))
                return "Curse";
            if (s.Contains("random"))
                return "Random";
            return null;
        }

        // Debug recon: dump each room's content FK pairs (row, table, table-path) + any readable
        // strings in the FK row, to config\fk_dump.txt. Used to map FK -> names (room type /
        // affliction / reward) so weights can be wired. Content block lives at widget+0x4D8.
        private void DumpFk(SekhemaFloor floor)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"FloorData=0x{floor.FloorDataAddr.ToInt64():X} mapElement=0x{floor.MapElement.ToInt64():X} " +
                          $"player=({floor.PlayerLayer},{floor.PlayerRoom})");
            foreach (var layer in floor.Layers)
            {
                foreach (var room in layer)
                {
                    if (!room.HasWidget)
                        continue;
                    sb.AppendLine($"== ({room.Layer},{room.Index}) widget=0x{room.WidgetAddr.ToInt64():X} " +
                                  $"chosen={room.IsChosen} conn=[{string.Join(",", room.NextConnections)}] ==");
                    // Room-struct FK (FloorData room +0x28/+0x30) — available for ALL rooms incl. hidden?
                    {
                        var rrow = Mem.Read<IntPtr>(room.Address + 0x28);
                        var rtab = Mem.Read<IntPtr>(room.Address + 0x30);
                        if (rrow != IntPtr.Zero || rtab != IntPtr.Zero)
                        {
                            var tp = rtab != IntPtr.Zero ? Mem.ReadWideString(Mem.Read<IntPtr>(rtab + 0x08), 80) : "";
                            var id = rrow != IntPtr.Zero ? Mem.ReadWideString(Mem.Read<IntPtr>(rrow + 0x00), 48) : "";
                            sb.AppendLine($"  roomStruct+0x28: table=\"{tp}\" id=\"{id}\"");
                        }
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        var slot = room.WidgetAddr + 0x4D8 + k * 0x10;
                        var rowPtr = Mem.Read<IntPtr>(slot);
                        var tablePtr = Mem.Read<IntPtr>(slot + 8);
                        if (rowPtr == IntPtr.Zero && tablePtr == IntPtr.Zero)
                            continue;
                        string tpath = tablePtr != IntPtr.Zero
                            ? Mem.ReadWideString(Mem.Read<IntPtr>(tablePtr + 0x08), 96) : "";
                        sb.AppendLine($"  FK[{k}] row=0x{rowPtr.ToInt64():X} table=0x{tablePtr.ToInt64():X} \"{tpath}\"");
                        if (rowPtr != IntPtr.Zero)
                            for (int off = 0; off <= 0xC0; off += 8)
                            {
                                var p = Mem.Read<IntPtr>(rowPtr + off);
                                if (p == IntPtr.Zero)
                                    continue;
                                var s = Mem.ReadWideString(p, 48);
                                if (Printable(s))
                                    sb.AppendLine($"    row+0x{off:X}: \"{s}\"");
                            }
                    }
                }
            }
            // Also dump the FloorData content vector (+0x18, stride 0x40, keyed by layer/room):
            // if hidden cells have entries here, we can classify ahead of reveal.
            sb.AppendLine();
            sb.AppendLine("== FloorData content vector (FloorData+0x18) ==");
            var cFirst = Mem.Read<IntPtr>(floor.FloorDataAddr + 0x18);
            var cLast = Mem.Read<IntPtr>(floor.FloorDataAddr + 0x20);
            long ccount = (cFirst != IntPtr.Zero && cLast.ToInt64() > cFirst.ToInt64())
                ? (cLast.ToInt64() - cFirst.ToInt64()) / 0x40 : 0;
            sb.AppendLine($"  entries={ccount}");
            for (long i = 0; i < ccount && i < 512; i++)
            {
                var e = cFirst + (int)(i * 0x40);
                byte el = Mem.Read<byte>(e + 0x00);
                byte er = Mem.Read<byte>(e + 0x01);
                sb.Append($"  [{i,3}] key=({el},{er})");
                for (int k = 0; k < 3; k++)
                {
                    var row = Mem.Read<IntPtr>(e + 0x08 + k * 0x10);
                    var table = Mem.Read<IntPtr>(e + 0x10 + k * 0x10);
                    if (row == IntPtr.Zero || table == IntPtr.Zero)
                        continue;
                    var tp = Mem.ReadWideString(Mem.Read<IntPtr>(table + 0x08), 80);
                    int slash = tp.LastIndexOf('/');
                    var tshort = slash >= 0 ? tp.Substring(slash + 1) : tp;
                    var id = Mem.ReadWideString(Mem.Read<IntPtr>(row + 0x00), 48);
                    var nm = Mem.ReadWideString(Mem.Read<IntPtr>(row + 0x28), 48);
                    sb.Append($"  FK{k}[{tshort}] id=\"{id}\" n28=\"{nm}\"");
                }
                sb.AppendLine();
            }

            var dir = Path.GetDirectoryName(SettingPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Join(dir, "fk_dump.txt"), sb.ToString());
        }

        private static bool Printable(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 2)
                return false;
            foreach (var c in s)
                if (c < 0x20 || c > 0x7E)
                    return false;
            return true;
        }

        // Resolve the Sacred Water counter leaf by Flags-fingerprint walk (docs §2b), then read the
        // displayed std::wstring at +0x490 and parse it (strips thousands separators, e.g. "6 267").
        // Returns -1 if the leaf can't be resolved or the text isn't numeric. Panel must be populated.
        private static int ReadSacredWaterFromUi(IntPtr panelAddr)
        {
            var leaf = ResolveLeaf(panelAddr, WaterIndexPath, WaterFpChain, IsWaterLeaf);
            return leaf == IntPtr.Zero ? -1 : ParseDigits(Mem.ReadStdWString(leaf + WaterTextWStringOffset));
        }

        // Read one key-tier counter by its index path (Bronze/Silver/Gold share the water leaf layout:
        // displayed number is the std::wstring at leaf+0x490). -1 if unresolved/non-numeric.
        private static int ReadKeyFromUi(IntPtr root, int[] indexPath)
        {
            var leaf = ResolveByIndex(root, indexPath, out _);
            return leaf == IntPtr.Zero ? -1 : ParseDigits(Mem.ReadStdWString(leaf + WaterTextWStringOffset));
        }

        // Read the three key tiers from a root + paths; update each cached value only on a valid read
        // (so a closed/absent panel leaves the last-known count on screen instead of blanking it).
        private static void CacheKeysFrom(IntPtr root, int[] bronze, int[] silver, int[] gold)
        {
            int b = ReadKeyFromUi(root, bronze);
            int s = ReadKeyFromUi(root, silver);
            int g = ReadKeyFromUi(root, gold);
            if (b >= 0) liveBronze = b;
            if (s >= 0) liveSilver = s;
            if (g >= 0) liveGold = g;
        }

        // Terminal validator: a water leaf is one whose +0x490 std::wstring parses to a number.
        private static bool IsWaterLeaf(IntPtr leaf) =>
            ParseDigits(Mem.ReadStdWString(leaf + WaterTextWStringOffset)) >= 0;

        // Strip thousands separators (game draws "6 267" with a narrow space) and parse. -1 if no digits.
        private static int ParseDigits(string text)
        {
            if (string.IsNullOrEmpty(text))
                return -1;
            var digits = new StringBuilder(text.Length);
            foreach (var ch in text)
                if (ch >= '0' && ch <= '9')
                    digits.Append(ch);
            return digits.Length > 0 && int.TryParse(digits.ToString(), out var v) ? v : -1;
        }

        // Honour percentage from the honour bar fill geometry (docs §4.7.10). Resolve the FILL leaf by
        // Flags-fingerprint walk, take its parent (+0xB8) as the FRAME: the game sizes the fill to
        // cur/max of the frame width, so fill.X / frame.X == honour fraction (verified 666.18/680.0 =
        // 0.97968 = 6267/6397). UnscaledSize ratio is scale-invariant. -1 if unreadable.
        private static double ReadHonourPctFromUi(IntPtr root)
        {
            var fill = ResolveLeaf(root, HonourIndexPath, HonourFpChain, IsHonourFill);
            return fill == IntPtr.Zero ? -1 : HonourPctFromFill(fill);
        }

        // Terminal validator: an honour fill is a leaf whose parent gives a sane fill/frame width ratio.
        private static bool IsHonourFill(IntPtr fill) => HonourPctFromFill(fill) >= 0;

        private static double HonourPctFromFill(IntPtr fill)
        {
            var frame = Mem.Read<IntPtr>(fill + UiParentOffset);
            if (frame == IntPtr.Zero)
                return -1;
            float frameW = Mem.Read<float>(frame + UiUnscaledSizeXOffset);
            float fillW = Mem.Read<float>(fill + UiUnscaledSizeXOffset);
            if (!float.IsFinite(frameW) || !float.IsFinite(fillW) || frameW <= 1f || fillW < 0f)
                return -1;
            double pct = fillW / frameW * 100.0;
            if (pct < 0 || pct > 105)
                return -1;
            return pct > 100 ? 100 : pct;
        }

        // ── UITree leaf resolution: index primary, Flags-fingerprint fallback (docs §2b) ─────────
        // The index path is authoritative while it lands on a terminal-valid leaf (indices are correct
        // until a client patch shifts them). When it breaks, fall back to the harvested fp chain: the
        // stable Flags role-bits still locate the element though it moved. Fp is the recovery path, not
        // the primary, because these fingerprints are generic and the terminals weak (see field notes).
        private static IntPtr ResolveLeaf(IntPtr root, int[] indexPath, uint[] fpChain, Func<IntPtr, bool> terminalValid)
        {
            if (root == IntPtr.Zero)
                return IntPtr.Zero;

            var byIdx = ResolveByIndex(root, indexPath, out _);
            if (byIdx != IntPtr.Zero && terminalValid(byIdx))
                return byIdx;

            if (fpChain.Length == indexPath.Length)
            {
                var byFp = WalkFp(root, fpChain, 0, terminalValid);
                if (byFp != IntPtr.Zero)
                    return byFp;
            }
            return IntPtr.Zero;
        }

        // Backtracking Flags-fingerprint walk (mirrors RunecraftHelper.WalkFp). At each step, scan the
        // parent's children for ones whose Flags (IsVisible masked) match chain[step] — visible
        // candidates first — and recurse until a branch reaches a leaf the terminal validator accepts.
        private static IntPtr WalkFp(IntPtr parent, uint[] chain, int step, Func<IntPtr, bool> terminalValid)
        {
            if (step == chain.Length)
                return terminalValid(parent) ? parent : IntPtr.Zero;
            if (!TryChildren(parent, out var first, out var count))
                return IntPtr.Zero;

            uint target = chain[step] & ~IsVisibleMask;
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantVisible = pass == 0;
                for (long i = 0; i < count; i++)
                {
                    var child = Mem.Read<IntPtr>(first + (int)(i * IntPtr.Size));
                    if (child == IntPtr.Zero)
                        continue;
                    uint flags = Mem.Read<uint>(child + UiElementFlagsOffset);
                    if ((flags & ~IsVisibleMask) != target)
                        continue;
                    if (((flags & IsVisibleMask) != 0) != wantVisible)
                        continue;
                    var deeper = WalkFp(child, chain, step + 1, terminalValid);
                    if (deeper != IntPtr.Zero)
                        return deeper;
                }
            }
            return IntPtr.Zero;
        }

        // Resolve via the legacy index path, recording each hop's Flags so the chain can be learned.
        private static IntPtr ResolveByIndex(IntPtr root, int[] path, out uint[] chain)
        {
            chain = Array.Empty<uint>();
            var cur = root;
            var rec = new uint[path.Length];
            for (int i = 0; i < path.Length; i++)
            {
                if (!TryChildren(cur, out var first, out var count) || path[i] >= count)
                    return IntPtr.Zero;
                cur = Mem.Read<IntPtr>(first + path[i] * IntPtr.Size);
                if (cur == IntPtr.Zero)
                    return IntPtr.Zero;
                rec[i] = Mem.Read<uint>(cur + UiElementFlagsOffset);
            }
            chain = rec;
            return cur;
        }

        // Read an element's children StdVector (First @ +0x10, Last @ +0x18). Count = (Last-First)/8.
        private static bool TryChildren(IntPtr elem, out IntPtr first, out long count)
        {
            first = IntPtr.Zero;
            count = 0;
            if (elem == IntPtr.Zero)
                return false;
            first = Mem.Read<IntPtr>(elem + UiChildrenFirstOffset);
            var last = Mem.Read<IntPtr>(elem + UiChildrenFirstOffset + 8);
            if (first == IntPtr.Zero || last.ToInt64() <= first.ToInt64())
                return false;
            count = (last.ToInt64() - first.ToInt64()) / IntPtr.Size;
            return count > 0 && count <= 4000;
        }

        // Debug recon: dump the resolved Flags chains for the water/honour leaves (raw + masked) plus
        // each hop's address/flags/size, to config\ui_fp_dump.txt. Run it in a Trial (water needs the
        // map open) to harvest the fingerprints, so they can be baked as static constants (docs §2b).
        private void DumpUiFingerprints(IntPtr panelAddr, IntPtr gameUiAddr)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"panel=0x{panelAddr.ToInt64():X} gameUi=0x{gameUiAddr.ToInt64():X}");
            sb.AppendLine();
            sb.AppendLine("== Sacred Water (panel -> [1][0][0][1]) ==");
            DumpChain(sb, panelAddr, WaterIndexPath, WaterFpChain, leaf =>
                $"text=\"{Mem.ReadStdWString(leaf + WaterTextWStringOffset)}\"");
            sb.AppendLine();
            sb.AppendLine("== Honour fill (gameUi -> [13][5][1]) ==");
            DumpChain(sb, gameUiAddr, HonourIndexPath, HonourFpChain, fill =>
            {
                var frame = Mem.Read<IntPtr>(fill + UiParentOffset);
                return $"frameW={Mem.Read<float>(frame + UiUnscaledSizeXOffset):F1} " +
                       $"fillW={Mem.Read<float>(fill + UiUnscaledSizeXOffset):F1} pct={HonourPctFromFill(fill):F1}";
            });

            var dir = Path.GetDirectoryName(SettingPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Join(dir, "ui_fp_dump.txt"), sb.ToString());
        }

        private static void DumpChain(StringBuilder sb, IntPtr root, int[] path, uint[] bakedChain, Func<IntPtr, string> describeLeaf)
        {
            sb.AppendLine($"  baked chain: [{string.Join(", ", Array.ConvertAll(bakedChain, f => $"0x{f:X8}"))}]");
            var cur = root;
            for (int i = 0; i < path.Length; i++)
            {
                if (!TryChildren(cur, out var first, out var count) || path[i] >= count)
                {
                    sb.AppendLine($"  [{path[i]}] <unresolved> (count={count})");
                    return;
                }
                cur = Mem.Read<IntPtr>(first + path[i] * IntPtr.Size);
                uint flags = Mem.Read<uint>(cur + UiElementFlagsOffset);
                sb.AppendLine($"  [{path[i]}] 0x{cur.ToInt64():X} flags=0x{flags:X8} masked=0x{flags & ~IsVisibleMask:X8} " +
                              $"sizeX={Mem.Read<float>(cur + UiUnscaledSizeXOffset):F1}");
            }
            sb.AppendLine($"  leaf: {describeLeaf(cur)}");
        }

        // Minimal resources HUD (when Debug is on): just the live Sacred Water + Honour readout.
        private static void DrawResourcesHud(ImDrawListPtr drawList)
        {
            var waterStr = liveWater >= 0 ? liveWater.ToString() : "?";
            var honourStr = liveHonourPct >= 0 ? $"{liveHonourPct:F0}%" : "?";
            string K(int v) => v >= 0 ? v.ToString() : "?";
            var text = $"water {waterStr}, honour {honourStr}, keys {K(liveBronze)}/{K(liveSilver)}/{K(liveGold)} (B/S/G)";
            var pos = new Vector2(20f, 160f);
            var size = ImGui.CalcTextSize(text);
            drawList.AddRectFilled(pos - new Vector2(4, 2), pos + size + new Vector2(4, 2),
                ImGuiHelper.Color(new Vector4(0f, 0f, 0f, 0.8f)));
            drawList.AddText(pos, ImGuiHelper.Color(new Vector4(0.5f, 1f, 0.6f, 1f)), text);
        }

        private void DebugHud(ImDrawListPtr drawList, string text)
        {
            if (!Settings.DebugEnable)
                return;
            var pos = new Vector2(20f, 120f);
            var size = ImGui.CalcTextSize(text);
            drawList.AddRectFilled(pos - new Vector2(4, 2), pos + size + new Vector2(4, 2),
                ImGuiHelper.Color(new Vector4(0f, 0f, 0f, 0.8f)));
            drawList.AddText(pos, ImGuiHelper.Color(new Vector4(1f, 0.9f, 0.2f, 1f)), "SekhemaHelper: " + text);
        }

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        private static void ColorSwatch(string label, ref Vector4 color)
        {
            if (ImGui.ColorButton(label, color))
                ImGui.OpenPopup(label);
            ImGui.SameLine();
            ImGui.Text(label);
            if (ImGui.BeginPopup(label))
            {
                ImGui.ColorPicker4(label, ref color);
                ImGui.EndPopup();
            }
        }
    }
}
