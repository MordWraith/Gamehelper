namespace StashUtility
{
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.RemoteObjects.UiElement;
    using GameHelper.Utils;
    using GameOffsets.Objects.UiElement;
    using GameOffsets.Natives;
    using ImGuiNET;
    using Newtonsoft.Json;
    using StashUtility.Models;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Reflection;
    using System.Runtime.InteropServices;

    public sealed class StashUtilityCore : PCore<StashUtilitySettings>
    {
        private object handleObj;
        private MethodInfo readStdWStringMethod;
        private object uiParentsObj;   // Parent cache for P1 / Single-Player slots
        private object uiParentsObjP2; // Separate parent cache for P2 slots (avoids cross-panel position corruption)
        private object currentUiParentsObj; // Points to either uiParentsObj or uiParentsObjP2 during processing
        private MethodInfo updateCacheMethod;

        private readonly Dictionary<Type, MethodInfo> readMemoryMethods = new();
        private readonly Dictionary<Type, MethodInfo> readStdVectorMethods = new();
        private readonly Dictionary<Type, MethodInfo> tryReadMemoryMethods = new();

        private struct CoopDebugInfo
        {
            public bool IsCoopMode;
            public IntPtr P1LeftPanel;
            public IntPtr P1StashTabsContainer;
            public IntPtr P1ActiveTab;
            public string P1Status;
            public List<string> P1TabsInfo;
            public IntPtr P2RightPanel;
            public IntPtr P2StashTabsContainer;
            public IntPtr P2ActiveTab;
            public string P2Status;
            public List<string> P2TabsInfo;
        }
        private CoopDebugInfo coopDebug = new() { P1TabsInfo = new(), P2TabsInfo = new() };

        private struct SinglePlayerDebugInfo
        {
            public IntPtr LeftPanel;
            public string StashTabsPath;
            public IntPtr StashTabsContainer;
            public IntPtr ActiveTab;
            public string Status;
        }
        private SinglePlayerDebugInfo spDebug = new();

        private readonly List<string> probeLog = new();
        private string waystoneSearchTerm = string.Empty;
        private string tabletSearchTerm = string.Empty;

        // Debug UI Path Explorer State
        private readonly List<int> currentDebugPath = new();
        private string explorerRootAddressStr = string.Empty;
        private bool debugHoveredCurrentElement = false;
        private bool debugHoveredAllChildren = false;
        private int debugHoveredChildIndex = -1;
        private IntPtr debugCurrentAddress = IntPtr.Zero;
        private readonly List<IntPtr> debugChildrenAddresses = new();

        // Debug Hovered Waystone Inspector State
        private Item lastHoveredWaystone = null;
        private bool freezeHoveredWaystone = false;

        private string SettingPathname => Path.Combine(DllDirectory, "config", "settings.txt");

        public override void OnDisable()
        {
        }

        public override void OnEnable(bool isGameOpened)
        {
            hasClearedDumpFile = false;
            if (File.Exists(SettingPathname))
            {
                try
                {
                    var content = File.ReadAllText(SettingPathname);
                    Settings = JsonConvert.DeserializeObject<StashUtilitySettings>(content) ?? new StashUtilitySettings();
                }
                catch
                {
                    // Fallback to defaults
                }
            }

            if (Settings.ScanEndOffset < 0x500)
            {
                Settings.ScanEndOffset = 0x600;
            }

            // Migration code: Convert old text patterns to new mod database IDs
            if (Settings.BadModPatterns.Count > 0)
            {
                for (int i = 0; i < Settings.BadModPatterns.Count; i++)
                {
                    var pattern = Settings.BadModPatterns[i];
                    var normalizedPat = NormalizeForMatching(pattern);
                    var match = Data.ModDatabase.AllWaystoneMods.FirstOrDefault(dbMod => 
                        NormalizeForMatching(dbMod.Name) == normalizedPat ||
                        dbMod.Id.Equals(pattern, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        Settings.BadModPatterns[i] = match.Id;
                    }
                }
            }
            if (Settings.GoodModPatterns.Count > 0)
            {
                for (int i = 0; i < Settings.GoodModPatterns.Count; i++)
                {
                    var pattern = Settings.GoodModPatterns[i];
                    var normalizedPat = NormalizeForMatching(pattern);
                    var match = Data.ModDatabase.AllWaystoneMods.FirstOrDefault(dbMod => 
                        NormalizeForMatching(dbMod.Name) == normalizedPat ||
                        dbMod.Id.Equals(pattern, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        Settings.GoodModPatterns[i] = match.Id;
                    }
                }
            }

            InitReflection();
        }

        public override void SaveSettings()
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingPathname);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var settingsData = JsonConvert.SerializeObject(Settings, Formatting.Indented);
                File.WriteAllText(SettingPathname, settingsData);
            }
            catch
            {
                // Ignored
            }
        }

        private void DrawMinMaxFilterTableRow(string label, string id, int maxSliderVal, ref bool filterMin, ref int minVal, ref bool filterMax, ref int maxVal)
        {
            ImGui.PushID(id);
            ImGui.TableNextRow();
            
            // Column 1: Label
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(PluginText.T($"stashutility.criteria.{id}", label));
            
            // Column 2: Min
            ImGui.TableNextColumn();
            ImGui.Checkbox(PluginText.T("stashutility.min", "Min"), ref filterMin);
            if (filterMin)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120f);
                ImGui.SliderInt("##minval", ref minVal, 0, maxSliderVal);
            }
            
            // Column 3: Max
            ImGui.TableNextColumn();
            ImGui.Checkbox(PluginText.T("stashutility.max", "Max"), ref filterMax);
            if (filterMax)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120f);
                ImGui.SliderInt("##maxval", ref maxVal, 0, maxSliderVal);
            }
            
            ImGui.PopID();
        }

        public override void DrawSettings()
        {
            ImGui.Checkbox(PluginText.T("stashutility.show_in_bg", "Show Overlay When Game in Background"), ref Settings.ShowOverlayInBackground);
            ImGuiHelper.ToolTip(PluginText.T("stashutility.show_in_bg_tooltip", "If checked, the waystone highlights will remain visible even when the game window is in the background."));

            ImGui.Checkbox(PluginText.T("stashutility.enable_merchant_purchase", "Enable Merchant Purchase Panel Overlay"), ref Settings.EnableMerchantPurchasePanel);
            ImGuiHelper.ToolTip(PluginText.T("stashutility.enable_merchant_purchase_tooltip", "When checked, waystones and tablets in the active merchant purchase panel tab will be highlighted based on your criteria."));

            ImGui.Checkbox(PluginText.T("stashutility.enable_debug", "Enable Debug Settings"), ref Settings.EnableDebugProbe);
            ImGuiHelper.ToolTip(PluginText.T("stashutility.enable_debug_tooltip", "Enables advanced debugging options, interactive explorer, and hovered item inspector."));

            if (Settings.EnableDebugProbe)
            {
                var settingsGameUi = Core.States.InGameStateObject.GameUi;
                if (settingsGameUi != null && settingsGameUi.Address != IntPtr.Zero)
                {
                    ImGui.SeparatorText("Active Stash Processing Debug Info");
                    bool isCoopMode = Core.GHSettings.EnableControllerMode && settingsGameUi.LeftPanel.Address != IntPtr.Zero;
                    if (isCoopMode)
                    {
                        ImGui.Text($"Coop Mode: TRUE");
                        ImGui.Text($"P1 LeftPanel: 0x{coopDebug.P1LeftPanel.ToInt64():X}");
                        ImGui.Text($"P1 StashTabsContainer: 0x{coopDebug.P1StashTabsContainer.ToInt64():X}");
                        ImGui.Text($"P1 ActiveTab: 0x{coopDebug.P1ActiveTab.ToInt64():X}");
                        ImGui.Text($"P1 Status: {coopDebug.P1Status}");
                        
                        if (coopDebug.P1TabsInfo != null && coopDebug.P1TabsInfo.Count > 0 && ImGui.TreeNode("P1 Tabs List Details"))
                        {
                            foreach (var info in coopDebug.P1TabsInfo)
                            {
                                ImGui.Text(info);
                            }
                            ImGui.TreePop();
                        }

                        ImGui.Separator();
                        ImGui.Text($"P2 RightPanel: 0x{coopDebug.P2RightPanel.ToInt64():X}");
                        ImGui.Text($"P2 StashTabsContainer: 0x{coopDebug.P2StashTabsContainer.ToInt64():X}");
                        ImGui.Text($"P2 ActiveTab: 0x{coopDebug.P2ActiveTab.ToInt64():X}");
                        ImGui.Text($"P2 Status: {coopDebug.P2Status}");

                        if (coopDebug.P2TabsInfo != null && coopDebug.P2TabsInfo.Count > 0 && ImGui.TreeNode("P2 Tabs List Details"))
                        {
                            foreach (var info in coopDebug.P2TabsInfo)
                            {
                                ImGui.Text(info);
                            }
                            ImGui.TreePop();
                        }
                    }
                    else
                    {
                        ImGui.Text($"Coop Mode: FALSE (Single Player)");
                        ImGui.Text($"LeftPanel: 0x{spDebug.LeftPanel.ToInt64():X}");
                        ImGui.Text($"StashTabsPath: {spDebug.StashTabsPath}");
                        ImGui.Text($"StashTabsContainer: 0x{spDebug.StashTabsContainer.ToInt64():X}");
                        ImGui.Text($"ActiveTab: 0x{spDebug.ActiveTab.ToInt64():X}");
                        ImGui.Text($"Status: {spDebug.Status}");
                    }
                    ImGui.Spacing();
                }

                debugHoveredCurrentElement = false;
                debugHoveredAllChildren = false;
                debugHoveredChildIndex = -1;

                if (ImGui.CollapsingHeader(PluginText.T("stashutility.debug.explorer_header", "UI Path Offsets (Debug Explorer)")))
                {
                    ImGui.SeparatorText(PluginText.T("stashutility.debug.path_nav", "Path Navigation"));

                    // Sync currentDebugPath with Settings.PathString
                    string expectedPathStr = string.Join(",", currentDebugPath);
                    if (Settings.PathString != expectedPathStr)
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(Settings.PathString))
                            {
                                currentDebugPath.Clear();
                            }
                            else
                            {
                                currentDebugPath.Clear();
                                currentDebugPath.AddRange(Settings.PathString
                                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .Select(int.Parse));
                            }
                        }
                        catch
                        {
                            // Invalid input, don't update list
                        }
                    }

                    ImGui.InputText(PluginText.T("stashutility.debug.path_indices", "Path Indices"), ref Settings.PathString, 128);
                    ImGuiHelper.ToolTip(PluginText.T("stashutility.debug.path_indices_tooltip", "Comma-separated indices starting from the LeftPanel root. Edit manually or click through the explorer."));

                    if (ImGui.Button(PluginText.T("stashutility.debug.reset_path", "Reset Path to Default")))
                    {
                        Settings.PathString = "2,0,0,0,1,1,45,0,1";
                    }
                    ImGui.SameLine();
                    if (ImGui.Button(PluginText.T("stashutility.debug.dump_tree", "Dump UI Tree to File")))
                    {
                        var gameUi = Core.States.InGameStateObject.GameUi;
                        if (gameUi != null && gameUi.Address != IntPtr.Zero)
                        {
                            if (this.handleObj == null)
                            {
                                InitReflection();
                            }
                            if (this.handleObj != null)
                            {
                                try
                                {
                                    var pathIndices = Settings.PathString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                        .Select(int.Parse)
                                        .ToArray();
                                    var resolved = ResolvePath(gameUi.LeftPanel.Address, pathIndices);
                                    if (resolved != IntPtr.Zero)
                                    {
                                        DumpUiTreeToFile(resolved);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[StashUtility] Dump error: {ex}");
                                }
                            }
                        }
                    }

                    ImGui.SeparatorText(PluginText.T("stashutility.debug.interactive_explorer", "Interactive Explorer"));

                    ImGui.InputText(PluginText.T("stashutility.debug.custom_root", "Custom Root Address (Hex)"), ref explorerRootAddressStr, 64);
                    ImGuiHelper.ToolTip(PluginText.T("stashutility.debug.custom_root_tooltip", "Leave empty to use gameUi.LeftPanel.Address as root."));

                    ImGui.Text(PluginText.T("stashutility.debug.breadcrumbs", "Breadcrumbs:"));
                    ImGui.SameLine();
                    if (ImGui.Button(PluginText.T("stashutility.debug.root", "Root")))
                    {
                        currentDebugPath.Clear();
                        Settings.PathString = string.Join(",", currentDebugPath);
                    }
                    for (int i = 0; i < currentDebugPath.Count; i++)
                    {
                        ImGui.SameLine();
                        ImGui.Text("->");
                        ImGui.SameLine();
                        if (ImGui.Button($"[{currentDebugPath[i]}]##path_{i}"))
                        {
                            var truncated = currentDebugPath.Take(i + 1).ToList();
                            currentDebugPath.Clear();
                            currentDebugPath.AddRange(truncated);
                            Settings.PathString = string.Join(",", currentDebugPath);
                        }
                    }

                    var root = GetExplorerRootAddress();
                    var current = root;
                    bool pathValid = true;
                    int failedIndex = -1;

                    for (int i = 0; i < currentDebugPath.Count; i++)
                    {
                        if (current == IntPtr.Zero)
                        {
                            pathValid = false;
                            failedIndex = i;
                            break;
                        }
                        var off = ReadMemory<UiElementBaseOffset>(current);
                        var kids = ReadStdVector<IntPtr>(off.ChildrensPtr);
                        var idx = currentDebugPath[i];
                        if (idx < 0 || idx >= kids.Length)
                        {
                            pathValid = false;
                            failedIndex = i;
                            break;
                        }
                        current = kids[idx];
                    }

                    this.debugCurrentAddress = current;

                    if (root == IntPtr.Zero)
                    {
                        ImGui.TextColored(new Vector4(1, 0, 0, 1), PluginText.T("stashutility.debug.root_null", "Root address is null. (Game UI not loaded?)"));
                    }
                    else if (!pathValid)
                    {
                        ImGui.TextColored(new Vector4(1, 0, 0, 1), PluginText.F("stashutility.debug.path_failed", "Path resolution failed at index step {0}.", failedIndex));
                    }
                    else if (current != IntPtr.Zero)
                    {
                        var off = ReadMemory<UiElementBaseOffset>(current);
                        var stringId = ReadStdWString(off.StringIdPtr);
                        var isVis = UiElementBaseFuncs.IsVisibleChecker(off.Flags);

                        ImGui.TextColored(new Vector4(0, 1, 0, 1), PluginText.T("stashutility.debug.path_success", "Path resolved successfully!"));
                        ImGui.SameLine();
                        ImGui.SmallButton(PluginText.T("stashutility.debug.hover_active", "Hover me to highlight active node in game"));
                        if (ImGui.IsItemHovered())
                        {
                            debugHoveredCurrentElement = true;
                        }

                        ImGuiHelper.IntPtrToImGui(PluginText.T("stashutility.debug.active_node_addr", "Active Node Addr"), current);
                        ImGui.Text(PluginText.F("stashutility.debug.string_id_visible", "String ID: {0}  |  Visible: {1}", stringId, isVis));

                        var kids = ReadStdVector<IntPtr>(off.ChildrensPtr);
                        ImGui.SeparatorText(PluginText.F("stashutility.debug.children_count", "Children ({0})", kids.Length));

                        ImGui.SmallButton(PluginText.T("stashutility.debug.hover_all_children", "Hover me to highlight all children bounds"));
                        if (ImGui.IsItemHovered())
                        {
                            debugHoveredAllChildren = true;
                        }

                        if (ImGui.BeginChild("ExplorerChildrenList", new Vector2(0, 200), ImGuiChildFlags.Borders))
                        {
                            debugChildrenAddresses.Clear();
                            for (int j = 0; j < kids.Length; j++)
                            {
                                var childAddr = kids[j];
                                debugChildrenAddresses.Add(childAddr);
                                if (childAddr == IntPtr.Zero)
                                {
                                    ImGui.Text(PluginText.F("stashutility.debug.null_pointer", "[{0}] Null Pointer", j));
                                    continue;
                                }

                                var childOff = ReadMemory<UiElementBaseOffset>(childAddr);
                                var childId = ReadStdWString(childOff.StringIdPtr);
                                var childVis = UiElementBaseFuncs.IsVisibleChecker(childOff.Flags);

                                if (childVis)
                                {
                                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0, 1, 0, 1));
                                }
                                else
                                {
                                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1));
                                }

                                if (ImGui.Selectable($"[{j}] Addr: 0x{childAddr.ToInt64():X} | ID: {childId}##child_{j}"))
                                {
                                    currentDebugPath.Add(j);
                                    Settings.PathString = string.Join(",", currentDebugPath);
                                }
                                if (ImGui.IsItemHovered())
                                {
                                    debugHoveredChildIndex = j;
                                }

                                ImGui.PopStyleColor();
                            }
                        }
                        ImGui.EndChild();
                    }
                }

                if (ImGui.CollapsingHeader(PluginText.T("stashutility.debug.waystone_inspector_header", "Hovered Waystone Inspector (Debug)")))
                {
                    ImGui.Checkbox(PluginText.T("stashutility.debug.freeze_waystone", "Freeze Hovered Waystone"), ref freezeHoveredWaystone);
                    ImGui.SameLine();
                    if (ImGui.Button(PluginText.T("stashutility.debug.clear", "Clear")))
                    {
                        lastHoveredWaystone = null;
                    }

                    if (lastHoveredWaystone != null)
                    {
                        ImGui.SeparatorText(PluginText.T("stashutility.debug.item_details", "Item Details"));
                        ImGuiHelper.IntPtrToImGui(PluginText.T("stashutility.debug.entity_address", "Entity Address"), lastHoveredWaystone.Address);
                        ImGuiHelper.DisplayTextAndCopyOnClick($"Path: {lastHoveredWaystone.Path}", lastHoveredWaystone.Path);

                        if (lastHoveredWaystone.TryGetComponent<Base>(out var baseComp))
                        {
                            ImGuiHelper.DisplayTextAndCopyOnClick($"Base Name: {baseComp.BaseItemName}", baseComp.BaseItemName);
                            ImGuiHelper.DisplayTextAndCopyOnClick($"Internal Name: {baseComp.InternalName}", baseComp.InternalName);
                        }

                        if (lastHoveredWaystone.TryGetComponent<Mods>(out var modsComp))
                        {
                            ImGui.Text($"Rarity: {modsComp.Rarity}");

                            if (modsComp.ImplicitMods.Count > 0 && ImGui.TreeNode("Implicit Mods"))
                            {
                                foreach (var mod in modsComp.ImplicitMods)
                                {
                                    ImGui.Text($"{mod.name}: ({mod.values.value0}, {mod.values.value1})");
                                }
                                ImGui.TreePop();
                            }
                            if (modsComp.ExplicitMods.Count > 0 && ImGui.TreeNode("Explicit Mods"))
                            {
                                foreach (var mod in modsComp.ExplicitMods)
                                {
                                    ImGui.Text($"{mod.name}: ({mod.values.value0}, {mod.values.value1})");
                                }
                                ImGui.TreePop();
                            }
                            if (modsComp.EnchantMods.Count > 0 && ImGui.TreeNode("Enchant Mods"))
                            {
                                foreach (var mod in modsComp.EnchantMods)
                                {
                                    ImGui.Text($"{mod.name}: ({mod.values.value0}, {mod.values.value1})");
                                }
                                ImGui.TreePop();
                            }
                            var statsFromMods = GetStatsFromMods(modsComp);
                            if (statsFromMods.Count > 0)
                            {
                                ImGuiHelper.StatsWidget(statsFromMods, "Stats From Mods");
                            }
                        }

                        if (lastHoveredWaystone.TryGetComponent<ObjectMagicProperties>(out var omp))
                        {
                            if (omp.ModStats.Count > 0)
                            {
                                ImGuiHelper.StatsWidget(omp.ModStats, "Stats From Magic Properties");
                            }
                        }

                        if (lastHoveredWaystone.TryGetComponent<Mods>(out var modsCompForDebug))
                        {
                            var allRawMods = modsCompForDebug.ImplicitMods.Concat(modsCompForDebug.ExplicitMods).Concat(modsCompForDebug.EnchantMods).ToList();
                            if (allRawMods.Count > 0 && ImGui.TreeNode("Raw Game Memory Mods (For Matching)"))
                            {
                                foreach (var mod in allRawMods)
                                {
                                    ImGui.Text($"Raw ID: {mod.name}");
                                }
                                ImGui.TreePop();
                            }
                        }

                        ImGui.SeparatorText(PluginText.T("stashutility.debug.advanced", "Advanced"));
                        if (ImGui.Button(PluginText.T("stashutility.debug.dump_full_memory", "Dump Full Memory To File")))
                        {
                            DumpAllWaystonesMemory(lastHoveredWaystone);
                        }

                        var field = typeof(Entity).GetField("componentAddresses", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (field != null)
                        {
                            if (field.GetValue(lastHoveredWaystone) is System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr> dict)
                            {
                                if (ImGui.TreeNode("All Components (Raw Addresses)"))
                                {
                                    foreach (var kv in dict)
                                    {
                                        ImGuiHelper.IntPtrToImGui(kv.Key, kv.Value);
                                    }
                                    ImGui.TreePop();
                                }
                            }
                        }
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(1, 1, 0, 1), PluginText.T("stashutility.debug.hover_hint", "Hover over a waystone in your Stash Tab to inspect it."));
                    }
                }
            }

            ImGui.Checkbox(PluginText.T("stashutility.enable_waystone_manager", "Enable Waystone Manager"), ref Settings.EnableWaystoneManager);
            ImGuiHelper.ToolTip(PluginText.T("stashutility.enable_waystone_manager_tooltip", "Enables or disables highlighting of waystones."));

            if (Settings.EnableWaystoneManager)
            {
                ImGui.Indent();
                if (ImGui.CollapsingHeader(PluginText.T("stashutility.waystone_criteria_header", "Waystone Highlight Criteria (Normal)")))
                {
                    ImGui.SliderInt(PluginText.T("stashutility.min_tier", "Min Tier"), ref Settings.MinTier, 1, 16);
                    ImGuiHelper.ToolTip(PluginText.T("stashutility.min_tier_tooltip", "Minimum Waystone Tier to highlight (Normal rarity is always ignored if Hide Normal Waystones is checked)."));

                    ImGui.Checkbox(PluginText.T("stashutility.filter_revives", "Filter Revives"), ref Settings.FilterMaxRevives);
                    if (Settings.FilterMaxRevives)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt(PluginText.T("stashutility.max_revives", "Max Revives Allowed##val"), ref Settings.MaxRevivesAvailable, 0, 5);
                    }

                    if (ImGui.BeginTable("FiltersTable", 3, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX))
                    {
                        ImGui.TableSetupColumn(PluginText.T("stashutility.table.criteria", "Criteria"), ImGuiTableColumnFlags.WidthFixed, 180f);
                        ImGui.TableSetupColumn(PluginText.T("stashutility.table.min_limit", "Minimum Limit"), ImGuiTableColumnFlags.WidthFixed, 190f);
                        ImGui.TableSetupColumn(PluginText.T("stashutility.table.max_limit", "Maximum Limit"), ImGuiTableColumnFlags.WidthFixed, 190f);

                        DrawMinMaxFilterTableRow("Explicit Mods", "ExplicitMods", 10, ref Settings.FilterMinExplicitMods, ref Settings.MinExplicitMods, ref Settings.FilterMaxExplicitMods, ref Settings.MaxExplicitMods);
                        DrawMinMaxFilterTableRow("Item Rarity", "ItemRarity", 200, ref Settings.FilterMinItemRarity, ref Settings.MinItemRarity, ref Settings.FilterMaxItemRarity, ref Settings.MaxItemRarity);
                        DrawMinMaxFilterTableRow("Pack Size", "PackSize", 100, ref Settings.FilterMinPackSize, ref Settings.MinPackSize, ref Settings.FilterMaxPackSize, ref Settings.MaxPackSize);
                        DrawMinMaxFilterTableRow("Monster Rarity", "MonsterRarity", 100, ref Settings.FilterMinMonsterRarity, ref Settings.MinMonsterRarity, ref Settings.FilterMaxMonsterRarity, ref Settings.MaxMonsterRarity);
                        DrawMinMaxFilterTableRow("Monster Effectiveness", "MonsterEffectiveness", 100, ref Settings.FilterMinMonsterEffectiveness, ref Settings.MinMonsterEffectiveness, ref Settings.FilterMaxMonsterEffectiveness, ref Settings.MaxMonsterEffectiveness);
                        DrawMinMaxFilterTableRow("Waystone Drop Chance", "DropChance", 300, ref Settings.FilterMinWaystoneDropChance, ref Settings.MinWaystoneDropChance, ref Settings.FilterMaxWaystoneDropChance, ref Settings.MaxWaystoneDropChance);

                        ImGui.EndTable();
                    }
                }

                if (ImGui.CollapsingHeader(PluginText.T("stashutility.waystone_mod_manager_header", "Waystone Mod Filter Manager")))
                {
                    ImGui.Checkbox(PluginText.T("stashutility.filter_bad_mods_only_on_highlighted", "Only Filter Bad Mods on Criteria-Meeting Waystones"), ref Settings.FilterBadModsOnlyOnHighlighted);
                    ImGuiHelper.ToolTip(PluginText.T("stashutility.filter_bad_mods_tooltip", "When checked, bad mod filtering is only applied to waystones that already meet the Tier and other active numerical criteria."));

                    if (ImGui.BeginCombo(PluginText.T("stashutility.add_waystone_mod", "Add Waystone Mod"), PluginText.T("stashutility.select_from_database", "Select from database...")))
                    {
                        ImGui.InputTextWithHint("##searchWaystone", PluginText.T("stashutility.search_database", "Search database..."), ref waystoneSearchTerm, 64);
                        var filtered = Data.ModDatabase.AllWaystoneMods
                            .Where(m => ModName(m).Contains(waystoneSearchTerm, StringComparison.OrdinalIgnoreCase) ||
                                        m.Name.Contains(waystoneSearchTerm, StringComparison.OrdinalIgnoreCase) ||
                                        m.Id.Contains(waystoneSearchTerm, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        foreach (var mod in filtered)
                        {
                            if (ImGui.Selectable($"{ModName(mod)}##{mod.Id}"))
                            {
                                if (!Settings.BadModPatterns.Contains(mod.Id) && !Settings.GoodModPatterns.Contains(mod.Id))
                                {
                                    Settings.BadModPatterns.Add(mod.Id);
                                    SaveSettings();
                                }
                            }
                        }
                        ImGui.EndCombo();
                    }

                    DrawModListUI(PluginText.T("stashutility.bad_mods_title", "BAD WAYSTONE MODS (RED HIGHLIGHT)"), Settings.BadModPatterns, Settings.GoodModPatterns, new Vector4(1f, 0.4f, 0.4f, 1f), true);
                    DrawModListUI(PluginText.T("stashutility.good_mods_title", "GOOD WAYSTONE MODS (GREEN HIGHLIGHT)"), Settings.GoodModPatterns, Settings.BadModPatterns, new Vector4(0.4f, 1f, 0.4f, 1f), false);
                }

                if (ImGui.CollapsingHeader(PluginText.T("stashutility.waystone_great_conditions_header", "Waystone GREAT Highlight Conditions")))
                {
                    ImGui.Checkbox(PluginText.T("stashutility.filter_great_explicit_mods", "Filter Great Min Explicit Mods Count"), ref Settings.FilterGreatExplicitMods);
                    if (Settings.FilterGreatExplicitMods)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt(PluginText.T("stashutility.min_great_explicit_mods", "Min Great Explicit Mods"), ref Settings.MinGreatExplicitMods, 0, 10);
                    }

                    ImGui.Checkbox(PluginText.T("stashutility.filter_great_max_explicit_mods", "Filter Great Max Explicit Mods Count"), ref Settings.FilterGreatMaxExplicitMods);
                    if (Settings.FilterGreatMaxExplicitMods)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt(PluginText.T("stashutility.max_great_explicit_mods", "Max Great Explicit Mods"), ref Settings.MaxGreatExplicitMods, 0, 10);
                    }

                    ImGui.Checkbox(PluginText.T("stashutility.filter_great_rarity", "Filter Great Item Rarity"), ref Settings.FilterGreatRarity);
                    if (Settings.FilterGreatRarity)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt(PluginText.T("stashutility.min_great_rarity", "Min Great Rarity (%)"), ref Settings.MinGreatRarity, 0, 200);
                    }

                    ImGui.Checkbox(PluginText.T("stashutility.filter_great_pack_size", "Filter Great Pack Size"), ref Settings.FilterGreatPackSize);
                    if (Settings.FilterGreatPackSize)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt(PluginText.T("stashutility.min_great_pack_size", "Min Great Pack Size (%)"), ref Settings.MinGreatPackSize, 0, 100);
                    }

                    ImGui.Checkbox(PluginText.T("stashutility.filter_great_monster_rarity", "Filter Great Monster Rarity"), ref Settings.FilterGreatMonstRarity);
                    if (Settings.FilterGreatMonstRarity)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt(PluginText.T("stashutility.min_great_monster_rarity", "Min Great Monster Rarity (%)"), ref Settings.MinGreatMonstRarity, 0, 100);
                    }

                    ImGui.Checkbox(PluginText.T("stashutility.filter_great_monster_effectiveness", "Filter Great Monster Effectiveness"), ref Settings.FilterGreatEffect);
                    if (Settings.FilterGreatEffect)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt(PluginText.T("stashutility.min_great_monster_effectiveness", "Min Great Effectiveness (%)"), ref Settings.MinGreatEffect, 0, 100);
                    }

                    ImGui.Checkbox(PluginText.T("stashutility.filter_great_waystone_drop_chance", "Filter Great Waystone Drop Chance"), ref Settings.FilterGreatDropChance);
                    if (Settings.FilterGreatDropChance)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt(PluginText.T("stashutility.min_great_waystone_drop_chance", "Min Great Drop Chance (%)"), ref Settings.MinGreatDropChance, 0, 300);
                    }
                }
                ImGui.Unindent();
            }

            ImGui.Checkbox(PluginText.T("stashutility.enable_tablet_manager", "Enable Tablet Manager"), ref Settings.EnableTabletManager);
            ImGuiHelper.ToolTip(PluginText.T("stashutility.enable_tablet_manager_tooltip", "Enables or disables highlighting of precursor/breach tablets."));

            if (Settings.EnableTabletManager)
            {
                ImGui.Indent();
                if (ImGui.CollapsingHeader(PluginText.T("stashutility.tablet_mod_manager_header", "Tablet Mod Filter Manager")))
                {
                    ImGui.SetNextItemWidth(150f);
                    if (ImGui.SliderInt(PluginText.T("stashutility.min_tablet_mods_to_highlight", "Min Good Mods To Highlight"), ref Settings.MinTabletGoodModsToHighlight, 1, 5)) SaveSettings();
                    
                    ImGui.SetNextItemWidth(150f);
                    if (ImGui.SliderInt(PluginText.T("stashutility.min_good_mods_to_ignore_bad", "Min Good Mods To Ignore Bad"), ref Settings.MinGoodModsToIgnoreBad, 0, 6)) SaveSettings();
                    ImGuiHelper.ToolTip(PluginText.T("stashutility.min_good_mods_to_ignore_bad_tooltip", "If a tablet has this many good mods, it will ignore any bad mods and still be highlighted as Good/Great."));

                    if (ImGui.Checkbox(PluginText.T("stashutility.filter_tablet_great", "Filter Tablet Great Status"), ref Settings.FilterTabletGreat)) SaveSettings();
                    if (Settings.FilterTabletGreat)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        if (ImGui.SliderInt(PluginText.T("stashutility.min_good_tablet_mods", "Min Good Tablet Mods Count"), ref Settings.MinTabletGoodMods, 1, 5)) SaveSettings();
                    }
                    ImGui.Spacing();

                    if (ImGui.BeginTabBar("TabletMechanicsTabs"))
                    {
                        var categories = new Dictionary<string, Func<Models.TabletMod, bool>>
                        {
                            { "Breach", m => m.Id.Contains("Breach", StringComparison.OrdinalIgnoreCase) },
                            { "Expedition", m => m.Id.Contains("Expedition", StringComparison.OrdinalIgnoreCase) },
                            { "Delirium", m => m.Id.Contains("Delirium", StringComparison.OrdinalIgnoreCase) },
                            { "Abyss", m => m.Id.Contains("Abyss", StringComparison.OrdinalIgnoreCase) },
                            { "Incursion", m => m.Id.Contains("Incursion", StringComparison.OrdinalIgnoreCase) },
                            { "Ritual", m => m.Id.Contains("Ritual", StringComparison.OrdinalIgnoreCase) },
                            { "General", m => !m.Id.Contains("Breach", StringComparison.OrdinalIgnoreCase) && !m.Id.Contains("Expedition", StringComparison.OrdinalIgnoreCase) && !m.Id.Contains("Delirium", StringComparison.OrdinalIgnoreCase) && !m.Id.Contains("Abyss", StringComparison.OrdinalIgnoreCase) && !m.Id.Contains("Incursion", StringComparison.OrdinalIgnoreCase) && !m.Id.Contains("Ritual", StringComparison.OrdinalIgnoreCase) }
                        };

                        foreach (var kvp in categories)
                        {
                            if (ImGui.BeginTabItem(PluginText.T($"stashutility.tablet.category.{kvp.Key}", kvp.Key)))
                            {
                                var tabMods = Data.ModDatabase.AllTabletMods.Where(kvp.Value).ToList();
                                
                                ImGui.InputTextWithHint($"##search{kvp.Key}", PluginText.F("stashutility.tablet.search_category", "Search {0} Mods...", kvp.Key), ref tabletSearchTerm, 64);
                                var filtered = tabMods.Where(m => ModName(m).Contains(tabletSearchTerm, StringComparison.OrdinalIgnoreCase) || m.Name.Contains(tabletSearchTerm, StringComparison.OrdinalIgnoreCase) || m.Id.Contains(tabletSearchTerm, StringComparison.OrdinalIgnoreCase)).ToList();

                                if (ImGui.BeginChild($"Child{kvp.Key}", new Vector2(0, 250), ImGuiChildFlags.Borders))
                                {
                                    foreach (var mod in filtered)
                                    {
                                        bool isGood = Settings.TabletGoodModPatterns.Contains(mod.Id);
                                        bool isBad = Settings.TabletBadModPatterns.Contains(mod.Id);
                                        bool isGod = Settings.TabletGodModPatterns.Contains(mod.Id);

                                        ImGui.TextWrapped(ModName(mod).Replace("%", "%%"));

                                        if (ImGui.Checkbox(PluginText.Label("stashutility.tablet.good", "Good", $"g_{mod.Id}"), ref isGood))
                                        {
                                            if (isGood) { Settings.TabletGoodModPatterns.Add(mod.Id); Settings.TabletBadModPatterns.Remove(mod.Id); Settings.TabletGodModPatterns.Remove(mod.Id); }
                                            else { Settings.TabletGoodModPatterns.Remove(mod.Id); }
                                            SaveSettings();
                                        }
                                        ImGui.SameLine(100f);
                                        if (ImGui.Checkbox(PluginText.Label("stashutility.tablet.bad", "Bad", $"b_{mod.Id}"), ref isBad))
                                        {
                                            if (isBad) { Settings.TabletBadModPatterns.Add(mod.Id); Settings.TabletGoodModPatterns.Remove(mod.Id); Settings.TabletGodModPatterns.Remove(mod.Id); }
                                            else { Settings.TabletBadModPatterns.Remove(mod.Id); }
                                            SaveSettings();
                                        }
                                        ImGui.SameLine(190f);
                                        if (ImGui.Checkbox(PluginText.Label("stashutility.tablet.god", "God", $"god_{mod.Id}"), ref isGod))
                                        {
                                            if (isGod) { Settings.TabletGodModPatterns.Add(mod.Id); Settings.TabletGoodModPatterns.Remove(mod.Id); Settings.TabletBadModPatterns.Remove(mod.Id); }
                                            else { Settings.TabletGodModPatterns.Remove(mod.Id); }
                                            SaveSettings();
                                        }
                                        ImGuiHelper.ToolTip(PluginText.T("stashutility.tablet.god_tooltip", "A God mod immediately flags the tablet as Good/Great, ignoring any Bad mods."));
                                        ImGui.Separator();
                                    }
                                }
                                ImGui.EndChild();
                                ImGui.EndTabItem();
                            }
                        }
                        ImGui.EndTabBar();
                    }
                }

                ImGui.Unindent();
            }

            if (ImGui.CollapsingHeader(PluginText.T("stashutility.visual_settings_header", "Overlay Visual Settings")))
            {
                ImGui.Checkbox(PluginText.T("stashutility.show_waystone_mod_border", "Show Waystone Mod Highlight Border"), ref Settings.ShowModBorder);
                ImGui.Checkbox(PluginText.T("stashutility.show_tablet_mod_border", "Show Tablet Mod Highlight Border"), ref Settings.ShowTabletModBorder);
                ImGui.Checkbox(PluginText.T("stashutility.show_rarity_border", "Show Rarity Corner Indicator"), ref Settings.ShowRarityBorder);
                if (Settings.ShowRarityBorder)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150f);
                    ImGui.SliderFloat(PluginText.T("stashutility.rarity_indicator_size", "Indicator Size##size"), ref Settings.RarityIndicatorSize, 5f, 30f);
                }
                ImGui.Checkbox(PluginText.T("stashutility.hide_normal_waystones", "Hide Normal (White) Waystones"), ref Settings.HideNormalWaystones);
                ImGui.SliderFloat(PluginText.T("stashutility.border_thickness", "Border Thickness"), ref Settings.BorderThickness, 1f, 10f);
                ImGui.SliderFloat(PluginText.T("stashutility.border_margin", "Border Margin"), ref Settings.BorderMargin, 0f, 10f);

                string[] borderStyles = { 
                    PluginText.T("stashutility.style_solid", "Solid"), 
                    PluginText.T("stashutility.style_dashed", "Dashed"), 
                    PluginText.T("stashutility.style_dotted", "Dotted") 
                };
                ImGui.Combo(PluginText.T("stashutility.bad_border_style", "Bad Highlight Border Style"), ref Settings.FrameStyleBad, borderStyles, borderStyles.Length);
                ImGui.Combo(PluginText.T("stashutility.good_border_style", "Good Highlight Border Style"), ref Settings.FrameStyleGood, borderStyles, borderStyles.Length);

                string[] arrowPositions = { 
                    PluginText.T("stashutility.pos_top_left", "Top-Left"), 
                    PluginText.T("stashutility.pos_top_right", "Top-Right"), 
                    PluginText.T("stashutility.pos_bottom_left", "Bottom-Left"), 
                    PluginText.T("stashutility.pos_bottom_right", "Bottom-Right") 
                };
                ImGui.Combo(PluginText.T("stashutility.great_arrow_position", "GREAT Arrow Position"), ref Settings.GreatIndicatorPosition, arrowPositions, arrowPositions.Length);
                ImGui.SliderFloat(PluginText.T("stashutility.great_arrow_size", "GREAT Arrow Size"), ref Settings.GreatIndicatorSize, 5f, 40f);

                ImGui.SeparatorText(PluginText.T("stashutility.colors_section", "Colors"));
                if (ImGui.ColorEdit4(PluginText.T("stashutility.good_color", "Waystone Good Highlight Color"), ref Settings.GoodColor)) SaveSettings();
                if (ImGui.ColorEdit4(PluginText.T("stashutility.bad_color", "Waystone Bad Highlight Color"), ref Settings.BadColor)) SaveSettings();
                if (ImGui.ColorEdit4(PluginText.T("stashutility.tablet_good_color", "Tablet Good Highlight Color"), ref Settings.TabletGoodColor)) SaveSettings();
                if (ImGui.ColorEdit4(PluginText.T("stashutility.tablet_bad_color", "Tablet Bad Highlight Color"), ref Settings.TabletBadColor)) SaveSettings();
                if (ImGui.ColorEdit4(PluginText.T("stashutility.waystone_great_color", "Waystone GREAT Arrow Color"), ref Settings.ColorGreat)) SaveSettings();
                if (ImGui.ColorEdit4(PluginText.T("stashutility.tablet_great_color", "Tablet GREAT Arrow Color"), ref Settings.TabletColorGreat)) SaveSettings();
                ImGui.ColorEdit4(PluginText.T("stashutility.rare_color", "Rare Rarity Color"), ref Settings.RareRarityColor);
                ImGui.ColorEdit4(PluginText.T("stashutility.magic_color", "Magic Rarity Color"), ref Settings.MagicRarityColor);
                ImGui.ColorEdit4(PluginText.T("stashutility.normal_color", "Normal Rarity Color"), ref Settings.NormalRarityColor);
            }
        }

        private IntPtr GetExplorerRootAddress()
        {
            if (string.IsNullOrWhiteSpace(explorerRootAddressStr))
            {
                var gameUi = Core.States.InGameStateObject.GameUi;
                return gameUi != null ? gameUi.LeftPanel.Address : IntPtr.Zero;
            }

            string cleanAddr = explorerRootAddressStr.Trim();
            if (cleanAddr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                cleanAddr = cleanAddr.Substring(2);
            }

            if (IntPtr.TryParse(cleanAddr, System.Globalization.NumberStyles.HexNumber, null, out var addr))
            {
                return addr;
            }

            return IntPtr.Zero;
        }

        private bool IsElementVisible(IntPtr address)
        {
            if (address == IntPtr.Zero) return false;
            var current = address;
            int depth = 0;
            while (current != IntPtr.Zero && depth < 20)
            {
                var off = ReadMemory<UiElementBaseOffset>(current);
                if (off.Self != IntPtr.Zero && off.Self != current)
                {
                    return false;
                }
                if (!UiElementBaseFuncs.IsVisibleChecker(off.Flags))
                {
                    return false;
                }
                current = off.ParentPtr;
                depth++;
            }
            return true;
        }

        private bool IsTabVisible(IntPtr address)
        {
            if (address == IntPtr.Zero) return false;
            var off = ReadMemory<UiElementBaseOffset>(address);
            if (off.Self != IntPtr.Zero && off.Self != address)
            {
                return false;
            }
            return UiElementBaseFuncs.IsVisibleChecker(off.Flags);
        }

        private bool IsActiveTab(IntPtr tab, out string tabType)
        {
            // IMPORTANT: Do NOT call CreateUiElement anywhere in this method.
            // CreateUiElement calls parents.AddIfNotExists() on its parent pointer, which contaminates
            // the shared uiParentsObj parent cache. When item slots are later created with CreateUiElement
            // they walk the poisoned cache and compute wrong absolute positions => overlay offset.
            // We check visibility and size exclusively through raw memory (UiElementBaseOffset).

            tabType = "Unknown";
            if (tab == IntPtr.Zero) return false;

            // First check if the tab itself is visible
            if (!IsElementVisible(tab)) return false;

            // Helper: check if a resolved address is visible with non-zero UnscaledSize.X
            bool HasNonZeroSize(IntPtr addr)
            {
                if (addr == IntPtr.Zero) return false;
                var off = ReadMemory<UiElementBaseOffset>(addr);
                return UiElementBaseFuncs.IsVisibleChecker(off.Flags) && off.UnscaledSize.X > 0f;
            }

            // 1. Check Waystone tab: tab -> 0 -> 1 has 16 children (tiers)
            var child01 = ResolvePath(tab, new int[] { 0, 1 });
            if (child01 != IntPtr.Zero)
            {
                var off01 = ReadMemory<UiElementBaseOffset>(child01);
                var kids01 = ReadStdVector<IntPtr>(off01.ChildrensPtr);
                if (kids01.Length == 16 && HasNonZeroSize(child01))
                {
                    tabType = "Waystone";
                    return true;
                }
            }

            // 2. Check Fragment tab: tab -> 0 -> 0 -> 0 -> 1 has 6 children (pages)
            var childFrag = ResolvePath(tab, new int[] { 0, 0, 0, 1 });
            if (childFrag != IntPtr.Zero)
            {
                var offFrag = ReadMemory<UiElementBaseOffset>(childFrag);
                var kidsFrag = ReadStdVector<IntPtr>(offFrag.ChildrensPtr);
                if (kidsFrag.Length == 6 && HasNonZeroSize(childFrag))
                {
                    tabType = "Fragment";
                    return true;
                }
            }

            // 4. Check normal grid: tab -> 0 -> 0 visible and non-zero size
            var child00 = ResolvePath(tab, new int[] { 0, 0 });
            if (child00 != IntPtr.Zero && HasNonZeroSize(child00))
            {
                tabType = "Normal/Quad (0->0)";
                return true;
            }

            // 5. In coop mode, a normal grid might be flatter: tab -> 0 directly
            var child0 = ResolvePath(tab, new int[] { 0 });
            if (child0 != IntPtr.Zero && HasNonZeroSize(child0))
            {
                var off0 = ReadMemory<UiElementBaseOffset>(child0);
                var slots = ReadStdVector<IntPtr>(off0.ChildrensPtr);
                if (slots.Length > 0 && slots.Any(k => k != IntPtr.Zero && GetItemAddressFromElement(k) != IntPtr.Zero))
                {
                    tabType = "Normal/Quad (Flat 0)";
                    return true;
                }
            }

            return false;
        }

        public override void DrawUI()
        {
            if (!Settings.EnableWaystoneManager && !Settings.EnableTabletManager && !Settings.EnableDebugProbe && !Settings.EnableMerchantPurchasePanel) return;

            if (!Settings.ShowOverlayInBackground && !Core.Process.Foreground)
            {
                return;
            }

            if (this.handleObj == null)
            {
                InitReflection();
                if (this.handleObj == null) return;
            }

            var gameUi = Core.States.InGameStateObject.GameUi;
            if (gameUi == null || gameUi.Address == IntPtr.Zero) return;

            // Update the parent caches every frame to refresh stale parent positions/coordinates.
            // This is extremely fast (parallel memory reads) and avoids GC allocations from clearing the cache.
            if (this.updateCacheMethod != null)
            {
                if (this.uiParentsObj != null) this.updateCacheMethod.Invoke(this.uiParentsObj, null);
                if (this.uiParentsObjP2 != null) this.updateCacheMethod.Invoke(this.uiParentsObjP2, null);
            }

            if (Settings.EnableDebugProbe)
            {
                DrawDebugOverlay();
            }

            if (Settings.EnableWaystoneManager || Settings.EnableTabletManager)
            {
                bool isCoopMode = Core.GHSettings.EnableControllerMode && gameUi.LeftPanel.Address != IntPtr.Zero;

                if (isCoopMode)
                {
                    coopDebug.IsCoopMode = true;
                    coopDebug.P1LeftPanel = gameUi.LeftPanel.Address;
                    coopDebug.P2RightPanel = gameUi.RightPanel.Address;

                    // Player 1 (LeftPanel)
                    if (gameUi.LeftPanel.Address != IntPtr.Zero && IsElementVisible(gameUi.LeftPanel.Address))
                    {
                        this.currentUiParentsObj = this.uiParentsObj;

                        // 1. Player 1 inventory: LeftPanel -> 3 -> 0 -> 0 -> 1 -> 0 -> 2
                        // Check inventory panel container (3 -> 0 -> 0 -> 1) is visible first
                        var invPanelContainerP1 = ResolvePath(gameUi.LeftPanel.Address, new int[] { 3, 0, 0, 1 });
                        var inventoryGridRoot = (invPanelContainerP1 != IntPtr.Zero && IsElementVisible(invPanelContainerP1))
                            ? ResolvePath(invPanelContainerP1, new int[] { 0, 2 })
                            : IntPtr.Zero;
                        if (inventoryGridRoot != IntPtr.Zero)
                        {
                            ProcessNormalTab(inventoryGridRoot);
                        }

                        // 2. Player 1 stash tabs: LeftPanel -> 3 -> 0 -> 0 -> 0 -> 2
                        // Check the stash panel container (3 -> 0 -> 0 -> 0) is visible before processing stash tabs
                        var stashPanelContainerP1 = ResolvePath(gameUi.LeftPanel.Address, new int[] { 3, 0, 0, 0 });
                        var stashTabsContainer = (stashPanelContainerP1 != IntPtr.Zero && IsElementVisible(stashPanelContainerP1))
                            ? ResolvePath(stashPanelContainerP1, new int[] { 2 })
                            : IntPtr.Zero;
                        if (stashTabsContainer != IntPtr.Zero)
                        {
                            ProcessCoopStashTabs(stashTabsContainer, 1);
                        }
                        else
                        {
                            coopDebug.P1StashTabsContainer = IntPtr.Zero;
                            coopDebug.P1Status = stashPanelContainerP1 == IntPtr.Zero
                                ? "Stash panel container path resolution failed (LeftPanel -> 3 -> 0 -> 0 -> 0)"
                                : "Stash panel is not visible (closed)";
                        }
                    }
                    else
                    {
                        coopDebug.P1Status = gameUi.LeftPanel.Address == IntPtr.Zero ? "LeftPanel address is null" : "LeftPanel is not visible";
                    }

                    // Player 2 (RightPanel)
                    if (gameUi.RightPanel.Address != IntPtr.Zero && IsElementVisible(gameUi.RightPanel.Address))
                    {
                        this.currentUiParentsObj = this.uiParentsObjP2;

                        // 1. Player 2 inventory: RightPanel -> 3 -> 0 -> 0 -> 1 -> 0 -> 2
                        // Check inventory panel container (3 -> 0 -> 0 -> 1) is visible first
                        var invPanelContainerP2 = ResolvePath(gameUi.RightPanel.Address, new int[] { 3, 0, 0, 1 });
                        var inventoryGridRoot = (invPanelContainerP2 != IntPtr.Zero && IsElementVisible(invPanelContainerP2))
                            ? ResolvePath(invPanelContainerP2, new int[] { 0, 2 })
                            : IntPtr.Zero;
                        if (inventoryGridRoot != IntPtr.Zero)
                        {
                            ProcessNormalTab(inventoryGridRoot);
                        }

                        // 2. Player 2 stash tabs: RightPanel -> 3 -> 0 -> 0 -> 0 -> 2
                        // Check the stash panel container (3 -> 0 -> 0 -> 0) is visible before processing stash tabs
                        var stashPanelContainerP2 = ResolvePath(gameUi.RightPanel.Address, new int[] { 3, 0, 0, 0 });
                        var stashTabsContainerP2 = (stashPanelContainerP2 != IntPtr.Zero && IsElementVisible(stashPanelContainerP2))
                            ? ResolvePath(stashPanelContainerP2, new int[] { 2 })
                            : IntPtr.Zero;
                        if (stashTabsContainerP2 != IntPtr.Zero)
                        {
                            ProcessCoopStashTabs(stashTabsContainerP2, 2);
                        }
                        else
                        {
                            coopDebug.P2StashTabsContainer = IntPtr.Zero;
                            coopDebug.P2Status = stashPanelContainerP2 == IntPtr.Zero
                                ? "Stash panel container path resolution failed (RightPanel -> 3 -> 0 -> 0 -> 0)"
                                : "Stash panel is not visible (closed)";
                        }
                    }
                    else
                    {
                        coopDebug.P2Status = gameUi.RightPanel.Address == IntPtr.Zero ? "RightPanel address is null" : "RightPanel is not visible";
                    }
                }
                else
                {
                    coopDebug.IsCoopMode = false;
                    spDebug.LeftPanel = gameUi.LeftPanel.Address;
                    this.currentUiParentsObj = this.uiParentsObj;

                    // Resolve Path
                    int[] pathIndices;
                    try
                    {
                        pathIndices = Settings.PathString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(int.Parse)
                            .ToArray();
                    }
                    catch
                    {
                        lock (probeLog)
                        {
                            probeLog.Clear();
                            probeLog.Add("Error: Path String is not in a valid format (must be comma-separated integers)");
                        }
                        return;
                    }

                    int[] stashTabsContainerPath;
                    if (pathIndices.Length >= 12 && pathIndices[pathIndices.Length - 4] == 0 && pathIndices[pathIndices.Length - 3] == 0 && pathIndices[pathIndices.Length - 2] == 0 && pathIndices[pathIndices.Length - 1] == 1)
                    {
                        // Fragment stash tab layout (e.g. 2,0,0,0,0,1,1,40,0,0,0,1)
                        stashTabsContainerPath = pathIndices.Take(pathIndices.Length - 5).ToArray();
                    }
                    else if (pathIndices.Length >= 9 && pathIndices[pathIndices.Length - 2] == 0 && pathIndices[pathIndices.Length - 1] == 1)
                    {
                        // Waystone stash tab layout (e.g. 2,0,0,0,1,1,45,0,1)
                        stashTabsContainerPath = pathIndices.Take(pathIndices.Length - 3).ToArray();
                    }
                    else
                    {
                        // Fallback / default behavior
                        stashTabsContainerPath = pathIndices.Length >= 6 
                            ? pathIndices.Take(6).ToArray() 
                            : new int[] { 2, 0, 0, 0, 1, 1 };
                    }

                    spDebug.StashTabsPath = string.Join(",", stashTabsContainerPath);
                    var stashTabsContainer = ResolvePath(gameUi.LeftPanel.Address, stashTabsContainerPath);
                    if (stashTabsContainer == IntPtr.Zero)
                    {
                        // Fallback 1: Try default waystone/normal stash tabs container path (2,0,0,0,1,1)
                        var fbPath1 = new int[] { 2, 0, 0, 0, 1, 1 };
                        stashTabsContainer = ResolvePath(gameUi.LeftPanel.Address, fbPath1);
                        if (stashTabsContainer != IntPtr.Zero)
                        {
                            stashTabsContainerPath = fbPath1;
                            spDebug.StashTabsPath = string.Join(",", fbPath1) + " (Fallback Waystone/Normal)";
                        }
                        else
                        {
                            // Fallback 2: Try default fragment stash tabs container path (2,0,0,0,0,1,1)
                            var fbPath2 = new int[] { 2, 0, 0, 0, 0, 1, 1 };
                            stashTabsContainer = ResolvePath(gameUi.LeftPanel.Address, fbPath2);
                            if (stashTabsContainer != IntPtr.Zero)
                            {
                                stashTabsContainerPath = fbPath2;
                                spDebug.StashTabsPath = string.Join(",", fbPath2) + " (Fallback Fragment)";
                            }
                        }
                    }

                    if (stashTabsContainer != IntPtr.Zero)
                    {
                        ProcessStashTabs(stashTabsContainer);
                    }
                    else
                    {
                        spDebug.StashTabsContainer = IntPtr.Zero;
                        spDebug.Status = $"StashTabsContainer path resolution failed for path: {spDebug.StashTabsPath}";
                    }

                    // 3. Process Character Inventory Panel if open: RightPanel -> 5 -> 36
                    if (gameUi.RightPanel.Address != IntPtr.Zero && IsElementVisible(gameUi.RightPanel.Address))
                    {
                        var inventoryPanelSP = ResolvePath(gameUi.RightPanel.Address, new int[] { 5 });
                        var inventoryGridRoot = (inventoryPanelSP != IntPtr.Zero && IsElementVisible(inventoryPanelSP))
                            ? ResolvePath(inventoryPanelSP, new int[] { 36 })
                            : IntPtr.Zero;
                        if (inventoryGridRoot != IntPtr.Zero)
                        {
                            ProcessNormalTab(inventoryGridRoot);
                        }
                    }
                }
            }

            // 4. Process Merchant Purchase Panel if open
            if (Settings.EnableMerchantPurchasePanel)
            {
                var merchantPanelParent = ResolvePath(gameUi.Address, new int[] { 80, 8, 1 });
                if (merchantPanelParent != IntPtr.Zero)
                {
                    var parentOff = ReadMemory<UiElementBaseOffset>(merchantPanelParent);
                    var tabs = ReadStdVector<IntPtr>(parentOff.ChildrensPtr);

                    IntPtr activeTabAddr = IntPtr.Zero;
                    foreach (var tab in tabs)
                    {
                        if (tab == IntPtr.Zero) continue;
                        if (IsElementVisible(tab))
                        {
                            activeTabAddr = tab;
                            break;
                        }
                    }

                    if (activeTabAddr != IntPtr.Zero)
                    {
                        var itemsContainer = ResolvePath(activeTabAddr, new int[] { 0 });
                        if (itemsContainer != IntPtr.Zero)
                        {
                            ProcessNormalTab(itemsContainer);
                        }
                    }
                }
            }
        }

        private void ProcessWaystoneTab(IntPtr[] tierKids)
        {
            foreach (var tierKid in tierKids)
            {
                if (tierKid == IntPtr.Zero) continue;

                var tierOff = ReadMemory<UiElementBaseOffset>(tierKid);
                if (!UiElementBaseFuncs.IsVisibleChecker(tierOff.Flags)) continue;

                // Go down: tierKid -> 0 -> 1
                var tierKidKids = ReadStdVector<IntPtr>(tierOff.ChildrensPtr);
                if (tierKidKids.Length <= 0 || tierKidKids[0] == IntPtr.Zero) continue;

                var child0Off = ReadMemory<UiElementBaseOffset>(tierKidKids[0]);
                var child0Kids = ReadStdVector<IntPtr>(child0Off.ChildrensPtr);
                if (child0Kids.Length <= 1 || child0Kids[1] == IntPtr.Zero) continue;

                var pagesContainer = child0Kids[1];
                var pagesContainerOff = ReadMemory<UiElementBaseOffset>(pagesContainer);
                var pages = ReadStdVector<IntPtr>(pagesContainerOff.ChildrensPtr);

                foreach (var page in pages)
                {
                    if (page == IntPtr.Zero) continue;

                    var pageOff = ReadMemory<UiElementBaseOffset>(page);
                    if (!UiElementBaseFuncs.IsVisibleChecker(pageOff.Flags)) continue;

                    var pageKids = ReadStdVector<IntPtr>(pageOff.ChildrensPtr);
                    if (pageKids.Length == 0) continue;

                    var slotContainer = pageKids[0];
                    if (slotContainer == IntPtr.Zero) continue;

                    var containerOff = ReadMemory<UiElementBaseOffset>(slotContainer);
                    var slots = ReadStdVector<IntPtr>(containerOff.ChildrensPtr);

                    foreach (var slot in slots)
                    {
                        if (slot == IntPtr.Zero) continue;

                        var slotOff = ReadMemory<UiElementBaseOffset>(slot);
                        if (!UiElementBaseFuncs.IsVisibleChecker(slotOff.Flags)) continue;

                        // Retrieve screen bounds for slot
                        var el = PluginUiElementReflection.CreateUiElement(slot, this.currentUiParentsObj);
                        if (el == null) continue;

                        var pos = (Vector2)PluginUiElementReflection.UiElementPositionProperty!.GetValue(el)!;
                        var size = (Vector2)PluginUiElementReflection.UiElementSizeProperty!.GetValue(el)!;
                        if (size.X <= 0f || pos == Vector2.Zero) continue;

                        // Scan slot for item entity
                        var itemAddr = GetItemAddressFromElement(slot);
                        if (itemAddr != IntPtr.Zero)
                        {
                            var item = ReadFreshItem(itemAddr);
                            if (item != null)
                            {
                                EvaluateAndHighlightItem(item, pos, size);
                            }
                        }
                    }
                }
            }
        }

        private void ProcessFragmentTabletsTab(IntPtr[] pages)
        {
            foreach (var page in pages)
            {
                if (page == IntPtr.Zero) continue;

                var pageOff = ReadMemory<UiElementBaseOffset>(page);
                if (!UiElementBaseFuncs.IsVisibleChecker(pageOff.Flags)) continue;

                // From page, go -> 0 -> 0 to get the slots container
                var slotContainer = ResolvePath(page, new int[] { 0, 0 });
                if (slotContainer == IntPtr.Zero) continue;

                var containerOff = ReadMemory<UiElementBaseOffset>(slotContainer);
                var slots = ReadStdVector<IntPtr>(containerOff.ChildrensPtr);

                foreach (var slot in slots)
                {
                    if (slot == IntPtr.Zero) continue;

                    var slotOff = ReadMemory<UiElementBaseOffset>(slot);
                    if (!UiElementBaseFuncs.IsVisibleChecker(slotOff.Flags)) continue;

                    // Retrieve screen bounds for slot
                    var el = PluginUiElementReflection.CreateUiElement(slot, this.currentUiParentsObj);
                    if (el == null) continue;

                    var pos = (Vector2)PluginUiElementReflection.UiElementPositionProperty!.GetValue(el)!;
                    var size = (Vector2)PluginUiElementReflection.UiElementSizeProperty!.GetValue(el)!;
                    if (size.X <= 0f || pos == Vector2.Zero) continue;

                    // Scan slot for item entity
                    var itemAddr = GetItemAddressFromElement(slot);
                    if (itemAddr != IntPtr.Zero)
                    {
                        var item = ReadFreshItem(itemAddr);
                        if (item != null)
                        {
                            EvaluateAndHighlightItem(item, pos, size);
                        }
                    }
                }
            }
        }

        private void ProcessNormalTab(IntPtr gridRoot)
        {
            var gridOffsets = ReadMemory<UiElementBaseOffset>(gridRoot);
            var slots = ReadStdVector<IntPtr>(gridOffsets.ChildrensPtr);

            foreach (var slot in slots)
            {
                if (slot == IntPtr.Zero) continue;

                var slotOff = ReadMemory<UiElementBaseOffset>(slot);
                if (!UiElementBaseFuncs.IsVisibleChecker(slotOff.Flags)) continue;

                // Retrieve screen bounds for slot
                var el = PluginUiElementReflection.CreateUiElement(slot, this.currentUiParentsObj);
                if (el == null) continue;

                var pos = (Vector2)PluginUiElementReflection.UiElementPositionProperty!.GetValue(el)!;
                var size = (Vector2)PluginUiElementReflection.UiElementSizeProperty!.GetValue(el)!;
                if (size.X <= 0f || pos == Vector2.Zero) continue;

                // Scan slot for item entity
                var itemAddr = GetItemAddressFromElement(slot);
                if (itemAddr != IntPtr.Zero)
                {
                    var item = ReadFreshItem(itemAddr);
                    if (item != null)
                    {
                        EvaluateAndHighlightItem(item, pos, size);
                    }
                }
            }
        }

        private void ProcessStashTabs(IntPtr stashTabsContainer)
        {
            spDebug.StashTabsContainer = stashTabsContainer;
            spDebug.ActiveTab = IntPtr.Zero;
            spDebug.Status = "No active tab found";

            if (stashTabsContainer == IntPtr.Zero) return;

            var tabsOffsets = ReadMemory<UiElementBaseOffset>(stashTabsContainer);
            var tabs = ReadStdVector<IntPtr>(tabsOffsets.ChildrensPtr);

            IntPtr activeTabAddr = IntPtr.Zero;
            int activeTabIdx = -1;
            string activeTabType = "None";

            // 1. Try to find the tab that is visible and has active/non-zero size children
            for (int i = 0; i < tabs.Length; i++)
            {
                var tab = tabs[i];
                if (tab == IntPtr.Zero) continue;
                if (IsActiveTab(tab, out string detectedType))
                {
                    activeTabAddr = tab;
                    activeTabIdx = i;
                    activeTabType = detectedType;
                    break;
                }
            }

            // 2. Fallback to old behavior if no active tab found by size/structure
            if (activeTabAddr == IntPtr.Zero)
            {
                for (int i = 0; i < tabs.Length; i++)
                {
                    var tab = tabs[i];
                    if (tab == IntPtr.Zero) continue;
                    if (IsElementVisible(tab))
                    {
                        activeTabAddr = tab;
                        activeTabIdx = i;
                        activeTabType = "Fallback (Visible)";
                        break;
                    }
                }
            }

            spDebug.ActiveTab = activeTabAddr;
            if (activeTabAddr != IntPtr.Zero)
            {
                spDebug.Status = $"Active tab index {activeTabIdx} ({activeTabType}) at 0x{activeTabAddr.ToInt64():X}";
            }

            if (activeTabAddr != IntPtr.Zero)
            {
                // 1. Check if it's the Waystone stash tab: activeTabAddr -> 0 -> 1 has 16 children (tiers)
                bool processedAsWaystone = false;
                var waystonesTabRoot = ResolvePath(activeTabAddr, new int[] { 0, 1 });
                if (waystonesTabRoot != IntPtr.Zero)
                {
                    var waystoneOffsets = ReadMemory<UiElementBaseOffset>(waystonesTabRoot);
                    var waystoneKids = ReadStdVector<IntPtr>(waystoneOffsets.ChildrensPtr);
                    if (waystoneKids.Length == 16)
                    {
                        ProcessWaystoneTab(waystoneKids);
                        processedAsWaystone = true;
                        spDebug.Status += " | Processed: Waystone Tab";
                    }
                }

                if (!processedAsWaystone)
                {
                    // 2. Check if it's a Fragment stash tab with tablets: activeTabAddr -> 0 -> 0 -> 0 -> 1 has 6 children (pages)
                    bool processedAsFragmentTablets = false;
                    var fragmentTabletsRoot = ResolvePath(activeTabAddr, new int[] { 0, 0, 0, 1 });
                    if (fragmentTabletsRoot != IntPtr.Zero)
                    {
                        var fragmentOffsets = ReadMemory<UiElementBaseOffset>(fragmentTabletsRoot);
                        var fragmentKids = ReadStdVector<IntPtr>(fragmentOffsets.ChildrensPtr);
                        if (fragmentKids.Length == 6)
                        {
                            ProcessFragmentTabletsTab(fragmentKids);
                            processedAsFragmentTablets = true;
                            spDebug.Status += " | Processed: Fragment Tab";
                        }
                    }

                    if (!processedAsFragmentTablets)
                    {
                        // 3. Otherwise, check if it's a normal/quad stash tab: activeTabAddr -> 0 -> 0 has slots directly
                        var normalGridRoot = ResolvePath(activeTabAddr, new int[] { 0, 0 });
                        if (normalGridRoot != IntPtr.Zero)
                        {
                            ProcessNormalTab(normalGridRoot);
                            spDebug.Status += " | Processed: Normal/Quad Tab (0->0)";
                        }
                        else
                        {
                            spDebug.Status += " | Failed: Not Waystone/Fragment, and 0->0 resolved to null";
                        }
                    }
                }
            }
        }


        private void ProcessCoopStashTabs(IntPtr stashTabsContainer, int playerNumber)
        {
            if (playerNumber == 1)
            {
                coopDebug.P1StashTabsContainer = stashTabsContainer;
                coopDebug.P1ActiveTab = IntPtr.Zero;
                coopDebug.P1Status = "No active tab found";
            }
            else
            {
                coopDebug.P2StashTabsContainer = stashTabsContainer;
                coopDebug.P2ActiveTab = IntPtr.Zero;
                coopDebug.P2Status = "No active tab found";
            }

            if (stashTabsContainer == IntPtr.Zero) return;

            var tabsOffsets = ReadMemory<UiElementBaseOffset>(stashTabsContainer);
            var tabs = ReadStdVector<IntPtr>(tabsOffsets.ChildrensPtr);

            var debugTabsList = new List<string>();
            IntPtr activeTabAddr = IntPtr.Zero;
            int activeTabIdx = -1;

            for (int i = 0; i < tabs.Length; i++)
            {
                var tab = tabs[i];
                if (tab == IntPtr.Zero)
                {
                    debugTabsList.Add($"[{i}] Null");
                    continue;
                }

                var off = ReadMemory<UiElementBaseOffset>(tab);
                bool localVis = UiElementBaseFuncs.IsVisibleChecker(off.Flags);
                bool fullVis = IsElementVisible(tab);

                var el = PluginUiElementReflection.CreateUiElement(tab, this.currentUiParentsObj);
                Vector2 size = Vector2.Zero;
                Vector2 pos = Vector2.Zero;
                if (el != null)
                {
                    size = (Vector2)PluginUiElementReflection.UiElementSizeProperty!.GetValue(el)!;
                    pos = (Vector2)PluginUiElementReflection.UiElementPositionProperty!.GetValue(el)!;
                }

                debugTabsList.Add($"[{i}] 0x{tab.ToInt64():X} | LVis:{localVis} | FVis:{fullVis} | Sz:{(int)size.X}x{(int)size.Y} | Pos:{(int)pos.X},{(int)pos.Y}");

                // Try to find the active tab using IsActiveTab helper
            }

            string activeTabType = "None";

            // 1. Try to find the tab that is visible and has active/non-zero size children
            for (int i = 0; i < tabs.Length; i++)
            {
                var tab = tabs[i];
                if (tab == IntPtr.Zero) continue;
                if (IsActiveTab(tab, out string detectedType))
                {
                    activeTabAddr = tab;
                    activeTabIdx = i;
                    activeTabType = detectedType;
                    break;
                }
            }

            // 2. Fallback to old behavior if no active tab found by size/structure
            if (activeTabAddr == IntPtr.Zero)
            {
                for (int i = 0; i < tabs.Length; i++)
                {
                    var tab = tabs[i];
                    if (tab == IntPtr.Zero) continue;
                    var off = ReadMemory<UiElementBaseOffset>(tab);
                    if (UiElementBaseFuncs.IsVisibleChecker(off.Flags))
                    {
                        activeTabAddr = tab;
                        activeTabIdx = i;
                        activeTabType = "Fallback (Visible)";
                        break;
                    }
                }
            }

            if (playerNumber == 1)
            {
                coopDebug.P1TabsInfo = debugTabsList;
                coopDebug.P1ActiveTab = activeTabAddr;
                if (activeTabAddr != IntPtr.Zero)
                {
                    coopDebug.P1Status = $"Active tab index {activeTabIdx} ({activeTabType}) at 0x{activeTabAddr.ToInt64():X}";
                }
            }
            else
            {
                coopDebug.P2TabsInfo = debugTabsList;
                coopDebug.P2ActiveTab = activeTabAddr;
                if (activeTabAddr != IntPtr.Zero)
                {
                    coopDebug.P2Status = $"Active tab index {activeTabIdx} ({activeTabType}) at 0x{activeTabAddr.ToInt64():X}";
                }
            }

            if (activeTabAddr != IntPtr.Zero)
            {
                // 1. Check if the active tab itself, activeTabAddr -> 0, or activeTabAddr -> 0 -> 0 contains item slots directly.
                // In coop/controller mode, POE2 often flattens the UI tree layout, rendering item slots directly under the active tab node.
                bool processedAsGrid = false;

                // Let's check activeTabAddr itself first
                var selfOffsets = ReadMemory<UiElementBaseOffset>(activeTabAddr);
                var selfKids = ReadStdVector<IntPtr>(selfOffsets.ChildrensPtr);
                if (selfKids.Length > 0 && selfKids.Any(k => k != IntPtr.Zero && GetItemAddressFromElement(k) != IntPtr.Zero))
                {
                    ProcessNormalTab(activeTabAddr);
                    processedAsGrid = true;
                    if (playerNumber == 1) coopDebug.P1Status += " | Processed: Flat Grid (Self)";
                    else coopDebug.P2Status += " | Processed: Flat Grid (Self)";
                }

                // Next check activeTabAddr -> 0
                if (!processedAsGrid)
                {
                    var coopGridRoot = ResolvePath(activeTabAddr, new int[] { 0 });
                    if (coopGridRoot != IntPtr.Zero)
                    {
                        var gridOffsets = ReadMemory<UiElementBaseOffset>(coopGridRoot);
                        var slots = ReadStdVector<IntPtr>(gridOffsets.ChildrensPtr);
                        if (slots.Length > 0 && slots.Any(k => k != IntPtr.Zero && GetItemAddressFromElement(k) != IntPtr.Zero))
                        {
                            ProcessNormalTab(coopGridRoot);
                            processedAsGrid = true;
                            if (playerNumber == 1) coopDebug.P1Status += " | Processed: Flat Grid (Self->0)";
                            else coopDebug.P2Status += " | Processed: Flat Grid (Self->0)";
                        }
                    }
                }

                // Next check activeTabAddr -> 0 -> 0
                if (!processedAsGrid)
                {
                    var normalGridRoot = ResolvePath(activeTabAddr, new int[] { 0, 0 });
                    if (normalGridRoot != IntPtr.Zero)
                    {
                        var gridOffsets = ReadMemory<UiElementBaseOffset>(normalGridRoot);
                        var slots = ReadStdVector<IntPtr>(gridOffsets.ChildrensPtr);
                        if (slots.Length > 0 && slots.Any(k => k != IntPtr.Zero && GetItemAddressFromElement(k) != IntPtr.Zero))
                        {
                            ProcessNormalTab(normalGridRoot);
                            processedAsGrid = true;
                            if (playerNumber == 1) coopDebug.P1Status += " | Processed: Flat Grid (Self->0->0)";
                            else coopDebug.P2Status += " | Processed: Flat Grid (Self->0->0)";
                        }
                    }
                }

                if (!processedAsGrid)
                {
                    // 2. If not a flattened grid, check nested/special structures
                    // Check if it's the Waystone stash tab: activeTabAddr -> 0 -> 1 has 16 children (tiers)
                    bool processedAsWaystone = false;
                    var waystonesTabRoot = ResolvePath(activeTabAddr, new int[] { 0, 1 });
                    if (waystonesTabRoot != IntPtr.Zero)
                    {
                        var waystoneOffsets = ReadMemory<UiElementBaseOffset>(waystonesTabRoot);
                        var waystoneKids = ReadStdVector<IntPtr>(waystoneOffsets.ChildrensPtr);
                        if (waystoneKids.Length == 16)
                        {
                            ProcessWaystoneTab(waystoneKids);
                            processedAsWaystone = true;
                            if (playerNumber == 1) coopDebug.P1Status += " | Processed: Waystone Tab (16 tiers)";
                            else coopDebug.P2Status += " | Processed: Waystone Tab (16 tiers)";
                        }
                    }

                    if (!processedAsWaystone)
                    {
                        // Check if it's a Fragment stash tab with tablets in coop mode: activeTabAddr -> 0 -> 0 -> 0 -> 1 has 6 children (pages)
                        var fragmentTabletsRoot = ResolvePath(activeTabAddr, new int[] { 0, 0, 0, 1 });
                        if (fragmentTabletsRoot != IntPtr.Zero)
                        {
                            var fragmentOffsets = ReadMemory<UiElementBaseOffset>(fragmentTabletsRoot);
                            var fragmentKids = ReadStdVector<IntPtr>(fragmentOffsets.ChildrensPtr);
                            if (fragmentKids.Length == 6)
                            {
                                ProcessFragmentTabletsTab(fragmentKids);
                                if (playerNumber == 1) coopDebug.P1Status += " | Processed: Fragment Tab (6 pages)";
                                else coopDebug.P2Status += " | Processed: Fragment Tab (6 pages)";
                            }
                            else
                            {
                                if (playerNumber == 1) coopDebug.P1Status += $" | Failed: Path 0->0->0->1 resolved but has {fragmentKids.Length} children instead of 6";
                                else coopDebug.P2Status += $" | Failed: Path 0->0->0->1 resolved but has {fragmentKids.Length} children instead of 6";
                            }
                        }
                        else
                        {
                            if (playerNumber == 1) coopDebug.P1Status += " | Failed: Not grid, and no nested 0->0->0->1 child found";
                            else coopDebug.P2Status += " | Failed: Not grid, and no nested 0->0->0->1 child found";
                        }
                    }
                }
            }
        }


        private void DrawDebugOverlay()
        {
            if (debugHoveredCurrentElement && debugCurrentAddress != IntPtr.Zero)
            {
                DrawDebugRect(debugCurrentAddress, new Vector4(1f, 1f, 0f, 1f), -1);
            }

            if (debugHoveredAllChildren)
            {
                for (int i = 0; i < debugChildrenAddresses.Count; i++)
                {
                    DrawDebugRect(debugChildrenAddresses[i], new Vector4(1f, 0f, 0f, 1f), i);
                }
            }
            else if (debugHoveredChildIndex >= 0 && debugHoveredChildIndex < debugChildrenAddresses.Count)
            {
                DrawDebugRect(debugChildrenAddresses[debugHoveredChildIndex], new Vector4(0f, 1f, 1f, 1f), debugHoveredChildIndex);
            }
        }

        private void DrawDebugRect(IntPtr address, Vector4 color, int indexLabel)
        {
            if (address == IntPtr.Zero) return;
            try
            {
                var el = PluginUiElementReflection.CreateUiElement(address, this.currentUiParentsObj ?? this.uiParentsObj);
                if (el != null)
                {
                    var pos = (Vector2)PluginUiElementReflection.UiElementPositionProperty!.GetValue(el)!;
                    var size = (Vector2)PluginUiElementReflection.UiElementSizeProperty!.GetValue(el)!;
                    if (size.X > 0f && pos != Vector2.Zero)
                    {
                        ImGui.GetForegroundDrawList().AddRect(
                            pos,
                            pos + size,
                            ImGuiHelper.Color(color),
                            0f,
                            ImDrawFlags.RoundCornersNone,
                            3.0f
                        );

                        if (indexLabel >= 0)
                        {
                            ImGui.GetForegroundDrawList().AddText(pos, ImGuiHelper.Color(color), $"{indexLabel}");
                        }
                    }
                }
            }
            catch
            {
                // Ignored
            }
        }

        private void EvaluateAndHighlightItem(Item item, Vector2 pos, Vector2 size)
        {
            if (item == null) return;

            if (!item.TryGetComponent<Base>(out var baseComponent)) return;

            var name = baseComponent.BaseItemName;
            var path = item.Path;

            bool isWaystone = (!string.IsNullOrEmpty(name) && name.Contains("Waystone")) || path.Contains("MapKey") || path.Contains("Waystone");
            bool isTablet = (!string.IsNullOrEmpty(name) && name.Contains("Tablet")) || path.Contains("TowerAugment") || path.Contains("Tablet");

            if (!isWaystone && !isTablet) return;

            if (isWaystone && !Settings.EnableWaystoneManager) return;
            if (isTablet && !Settings.EnableTabletManager) return;

            if (Settings.EnableDebugProbe && !freezeHoveredWaystone)
            {
                var mousePos = ImGui.GetMousePos();
                if (mousePos.X >= pos.X && mousePos.X <= pos.X + size.X &&
                    mousePos.Y >= pos.Y && mousePos.Y <= pos.Y + size.Y)
                {
                    lastHoveredWaystone = item;
                }
            }

            var rarity = GameHelper.RemoteEnums.Rarity.Normal;
            if (item.TryGetComponent<Mods>(out var modsComponent))
            {
                rarity = modsComponent.Rarity;
            }

            if (isWaystone && Settings.HideNormalWaystones && rarity == GameHelper.RemoteEnums.Rarity.Normal) return;

            int tier = 0;
            if (isWaystone)
            {
                var tierMatch = System.Text.RegularExpressions.Regex.Match(name, @"Tier\s*(\d+)");
                if (tierMatch.Success && int.TryParse(tierMatch.Groups[1].Value, out var parsedTier))
                {
                    tier = parsedTier;
                }
                else
                {
                    var pathMatch = System.Text.RegularExpressions.Regex.Match(path, @"MapKeyTier(\d+)");
                    if (pathMatch.Success) int.TryParse(pathMatch.Groups[1].Value, out tier);
                }

                if (tier < Settings.MinTier) return;
            }

            bool isBad = false;
            bool isGood = false;
            bool isGreat = false;
            bool isGod = false;

            int sumRarity = 0;
            int sumPackSize = 0;
            int sumMonstRarity = 0;
            int sumEffect = 0;
            int sumDropChance = 0;
            int revives = 5;
            int explicitModsCount = 0;

            int tabletGoodCount = 0;

            if (modsComponent != null)
            {
                explicitModsCount = modsComponent.ExplicitMods.Count;
                revives = Math.Max(0, Math.Min(5, 6 - explicitModsCount));
                var statsFromMods = GetStatsFromMods(modsComponent);
                bool hasMemoryStats = statsFromMods.Count > 0;
                if (statsFromMods.TryGetValue((GameStats)8210, out var rawDropChance)) sumDropChance = rawDropChance;
                if (statsFromMods.TryGetValue((GameStats)8206, out var rawRarity)) sumRarity = rawRarity;
                if (statsFromMods.TryGetValue((GameStats)8207, out var rawPackSize)) sumPackSize = rawPackSize;
                if (statsFromMods.TryGetValue((GameStats)8208, out var rawMonsterRarity)) sumMonstRarity = rawMonsterRarity;
                if (statsFromMods.TryGetValue((GameStats)8209, out var rawMonsterEffectiveness)) sumEffect = rawMonsterEffectiveness;

                int quality = 0;
                var field = typeof(Entity).GetField("componentAddresses", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                {
                    var dict = field.GetValue(item) as System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr>;
                    if (dict != null && dict.TryGetValue("Quality", out var qualityAddr) && qualityAddr != IntPtr.Zero)
                    {
                        TryReadMemory<int>(qualityAddr + 0x18, out quality);
                    }
                }
                sumDropChance += quality;

                var allRawMods = new List<(string name, (float v0, float v1) vals)>();
                allRawMods.AddRange(modsComponent.ImplicitMods);
                allRawMods.AddRange(modsComponent.ExplicitMods);
                allRawMods.AddRange(modsComponent.EnchantMods);

                foreach (var mod in allRawMods)
                {
                    if (string.IsNullOrEmpty(mod.name)) continue;

                    string lowerName = mod.name.ToLowerInvariant();
                    float rawVal = float.IsNaN(mod.vals.v0) ? 0f : mod.vals.v0;
                    if (rawVal == 0f) rawVal = float.IsNaN(mod.vals.v1) ? 0f : mod.vals.v1;
                    int intVal = (int)rawVal;

                    if (lowerName.Contains("revive"))
                    {
                        revives = intVal;
                    }
                    else if (sumPackSize == 0 && (lowerName.Contains("packsize") || lowerName.Contains("pack_size") || lowerName.Contains("pack")))
                    {
                        sumPackSize = intVal;
                    }

                    if (isWaystone)
                    {
                        var def = Data.ModDatabase.AllWaystoneMods
                            .OrderByDescending(d => d.Id.Length)
                            .FirstOrDefault(d => mod.name.Contains(d.Id, StringComparison.OrdinalIgnoreCase));

                        if (def != null)
                        {
                            if (!hasMemoryStats)
                            {
                                sumRarity += def.ItemRarity;
                                sumPackSize += def.PackSize;
                                sumMonstRarity += def.MonsterRarity;
                                sumEffect += def.MonsterEffectiveness;
                                sumDropChance += def.WaystoneDropChance;
                            }

                            if (Settings.BadModPatterns.Contains(def.Id))
                            {
                                isBad = true;
                            }
                            if (Settings.GoodModPatterns.Contains(def.Id))
                            {
                                isGood = true;
                            }
                        }
                    }
                    else if (isTablet)
                    {
                        var def = Data.ModDatabase.AllTabletMods
                            .OrderByDescending(d => d.Id.Length)
                            .FirstOrDefault(d => mod.name.Contains(d.Id, StringComparison.OrdinalIgnoreCase));

                        if (def != null)
                        {
                            if (Settings.TabletGodModPatterns.Contains(def.Id))
                            {
                                isGod = true;
                            }
                            if (Settings.TabletBadModPatterns.Contains(def.Id))
                            {
                                isBad = true;
                            }
                            if (Settings.TabletGoodModPatterns.Contains(def.Id))
                            {
                                isGood = true;
                                tabletGoodCount++;
                            }
                        }
                    }
                }

                if (isWaystone)
                {
                    bool candidate = true;
                    int activeFilters = 0;

                    if (Settings.FilterGreatRarity) { activeFilters++; if (sumRarity < Settings.MinGreatRarity) candidate = false; }
                    if (Settings.FilterGreatPackSize) { activeFilters++; if (sumPackSize < Settings.MinGreatPackSize) candidate = false; }
                    if (Settings.FilterGreatMonstRarity) { activeFilters++; if (sumMonstRarity < Settings.MinGreatMonstRarity) candidate = false; }
                    if (Settings.FilterGreatEffect) { activeFilters++; if (sumEffect < Settings.MinGreatEffect) candidate = false; }
                    if (Settings.FilterGreatDropChance) { activeFilters++; if (sumDropChance < Settings.MinGreatDropChance) candidate = false; }
                    if (Settings.FilterGreatExplicitMods) { activeFilters++; if (explicitModsCount < Settings.MinGreatExplicitMods) candidate = false; }
                    if (Settings.FilterGreatMaxExplicitMods) { activeFilters++; if (explicitModsCount > Settings.MaxGreatExplicitMods) candidate = false; }

                    isGreat = activeFilters > 0 && candidate;
                }
                else if (isTablet)
                {
                    if (isGod || tabletGoodCount >= Settings.MinGoodModsToIgnoreBad)
                    {
                        isBad = false;
                        isGood = true;
                    }

                    if (!isGod && tabletGoodCount < Settings.MinTabletGoodModsToHighlight)
                    {
                        isGood = false;
                    }

                    if (Settings.FilterTabletGreat)
                    {
                        isGreat = isGod || tabletGoodCount >= Settings.MinTabletGoodMods;
                    }
                }
            }

            bool passesNumericalFilters = true;
            if (isWaystone)
            {
                if (Settings.FilterMaxRevives && revives > Settings.MaxRevivesAvailable) passesNumericalFilters = false;
                if (Settings.FilterMinItemRarity && sumRarity < Settings.MinItemRarity) passesNumericalFilters = false;
                if (Settings.FilterMaxItemRarity && sumRarity > Settings.MaxItemRarity) passesNumericalFilters = false;
                if (Settings.FilterMinPackSize && sumPackSize < Settings.MinPackSize) passesNumericalFilters = false;
                if (Settings.FilterMaxPackSize && sumPackSize > Settings.MaxPackSize) passesNumericalFilters = false;
                if (Settings.FilterMinMonsterRarity && sumMonstRarity < Settings.MinMonsterRarity) passesNumericalFilters = false;
                if (Settings.FilterMaxMonsterRarity && sumMonstRarity > Settings.MaxMonsterRarity) passesNumericalFilters = false;
                if (Settings.FilterMinMonsterEffectiveness && sumEffect < Settings.MinMonsterEffectiveness) passesNumericalFilters = false;
                if (Settings.FilterMaxMonsterEffectiveness && sumEffect > Settings.MaxMonsterEffectiveness) passesNumericalFilters = false;
                if (Settings.FilterMinWaystoneDropChance && sumDropChance < Settings.MinWaystoneDropChance) passesNumericalFilters = false;
                if (Settings.FilterMaxWaystoneDropChance && sumDropChance > Settings.MaxWaystoneDropChance) passesNumericalFilters = false;
                if (Settings.FilterMinExplicitMods && explicitModsCount < Settings.MinExplicitMods) passesNumericalFilters = false;
                if (Settings.FilterMaxExplicitMods && explicitModsCount > Settings.MaxExplicitMods) passesNumericalFilters = false;
            }

            if (isTablet && !isBad && !isGood) return;

            if (isWaystone && Settings.FilterBadModsOnlyOnHighlighted && !passesNumericalFilters) return;

            if (!isBad && !passesNumericalFilters) return;

            float scale = size.X / 52.0f;
            float margin = Settings.BorderMargin * scale; // Adaptive margin for 4K scaling where bounding boxes overlap
            float activeBorderThickness = 0f;

            bool showModBorder = (isWaystone && Settings.ShowModBorder) || (isTablet && Settings.ShowTabletModBorder);

            if (showModBorder && (isBad || isGood || passesNumericalFilters))
            {
                Vector4 borderCol = isTablet 
                    ? (isBad ? Settings.TabletBadColor : Settings.TabletGoodColor)
                    : (isBad ? Settings.BadColor : Settings.GoodColor);
                float thickness = isBad ? Settings.BorderThickness : Math.Max(1.5f, Settings.BorderThickness - 0.5f);
                int style = isBad ? Settings.FrameStyleBad : Settings.FrameStyleGood;

                activeBorderThickness = thickness;
                float halfThickness = thickness / 2.0f;
                float inset = margin + halfThickness;

                AddStyledRect(ImGui.GetBackgroundDrawList(), pos + new Vector2(inset, inset), pos + size - new Vector2(inset, inset), ImGuiHelper.Color(borderCol), thickness, style);
            }

            if ((isWaystone || isTablet) && Settings.ShowRarityBorder)
            {
                Vector4 rarityCol = rarity switch
                {
                    GameHelper.RemoteEnums.Rarity.Normal => Settings.NormalRarityColor,
                    GameHelper.RemoteEnums.Rarity.Magic => Settings.MagicRarityColor,
                    GameHelper.RemoteEnums.Rarity.Rare => Settings.RareRarityColor,
                    GameHelper.RemoteEnums.Rarity.Unique => new Vector4(1f, 0.5f, 0f, 1f),
                    _ => Settings.NormalRarityColor
                };

                float offset = margin + activeBorderThickness;
                float sizeVal = Settings.RarityIndicatorSize;
                var p0 = pos + new Vector2(size.X - offset - sizeVal, offset);
                var p1 = pos + new Vector2(size.X - offset, offset);
                var p2 = pos + new Vector2(size.X - offset, offset + sizeVal);
                ImGui.GetBackgroundDrawList().AddTriangleFilled(p0, p1, p2, ImGuiHelper.Color(rarityCol));
            }

            if (isGreat)
            {
                float arrowSize = Settings.GreatIndicatorSize * scale;
                float padding = 4.0f * scale;
                float borderPad = activeBorderThickness > 0 ? activeBorderThickness : 0f;
                float totalPadX = margin + borderPad + padding;
                float totalPadY = margin + borderPad + padding;

                Vector2 topTip = Settings.GreatIndicatorPosition switch
                {
                    0 => pos + new Vector2(totalPadX + (arrowSize / 2), totalPadY), // Top-Left
                    1 => pos + new Vector2(size.X - totalPadX - (arrowSize / 2), totalPadY + (Settings.ShowRarityBorder ? Settings.RarityIndicatorSize : 0f)), // Top-Right
                    2 => pos + new Vector2(totalPadX + (arrowSize / 2), size.Y - totalPadY - arrowSize), // Bottom-Left
                    3 => pos + new Vector2(size.X - totalPadX - (arrowSize / 2), size.Y - totalPadY - arrowSize), // Bottom-Right
                    _ => pos + new Vector2(totalPadX + (arrowSize / 2), totalPadY)
                };

                var dl = ImGui.GetBackgroundDrawList();
                Vector4 arrowCol = isTablet ? Settings.TabletColorGreat : Settings.ColorGreat;
                dl.AddTriangleFilled(topTip, topTip + new Vector2(-arrowSize / 2, arrowSize), topTip + new Vector2(arrowSize / 2, arrowSize), ImGuiHelper.Color(arrowCol));
                dl.AddTriangle(topTip, topTip + new Vector2(-arrowSize / 2, arrowSize), topTip + new Vector2(arrowSize / 2, arrowSize), 0xFF000000, Math.Max(1.0f, 1.5f * scale));
            }
        }

        private void AddStyledRect(ImDrawListPtr dl, Vector2 min, Vector2 max, uint color, float thickness, int style)
        {
            if (style == 0)
            {
                dl.AddRect(min, max, color, 3.0f, ImDrawFlags.RoundCornersAll, thickness);
                return;
            }

            float step = (style == 1) ? 10.0f : 4.0f;
            float space = (style == 1) ? 5.0f : 4.0f;

            DrawDashedLine(dl, new Vector2(min.X, min.Y), new Vector2(max.X, min.Y), color, thickness, step, space);
            DrawDashedLine(dl, new Vector2(max.X, min.Y), new Vector2(max.X, max.Y), color, thickness, step, space);
            DrawDashedLine(dl, new Vector2(max.X, max.Y), new Vector2(min.X, max.Y), color, thickness, step, space);
            DrawDashedLine(dl, new Vector2(min.X, max.Y), new Vector2(min.X, min.Y), color, thickness, step, space);
        }

        private void DrawDashedLine(ImDrawListPtr dl, Vector2 start, Vector2 end, uint color, float thickness, float step, float space)
        {
            Vector2 diff = end - start;
            float fullLen = diff.Length();
            if (fullLen <= 0) return;
            Vector2 dir = diff / fullLen;

            float currentLen = 0;
            while (currentLen < fullLen)
            {
                float lineLen = Math.Min(step, fullLen - currentLen);
                dl.AddLine(start + dir * currentLen, start + dir * (currentLen + lineLen), color, thickness);
                currentLen += lineLen + space;
            }
        }

        private string ModName(WaystoneMod mod) =>
            mod != null ? PluginText.T($"mod.waystone.{mod.Id}", mod.Name) : string.Empty;

        private string ModName(TabletMod mod) =>
            mod != null ? PluginText.T($"mod.tablet.{mod.Id}", mod.Name) : string.Empty;

        private void DrawModListUI(string title, List<string> currentList, List<string> targetList, Vector4 color, bool isCurrentlyBad)
        {
            ImGui.TextColored(color, title);
            if (currentList.Count == 0) ImGui.TextDisabled(PluginText.T("stashutility.list_empty", "   (List empty)"));

            for (int i = 0; i < currentList.Count; i++)
            {
                string id = currentList[i];
                var defW = Data.ModDatabase.AllWaystoneMods.FirstOrDefault(m => m.Id == id);
                var defT = Data.ModDatabase.AllTabletMods.FirstOrDefault(m => m.Id == id);
                string name = ModName(defW);
                if (name.Length == 0) name = ModName(defT);
                if (name.Length == 0) name = id;

                ImGui.PushID(title + id);
                if (ImGui.Button("X"))
                {
                    currentList.RemoveAt(i);
                    SaveSettings();
                    ImGui.PopID();
                    break;
                }
                ImGui.SameLine();
                ImGui.TextUnformatted(name);
                ImGui.SameLine();

                string moveLabel = isCurrentlyBad 
                    ? PluginText.T("stashutility.set_good", "Set GOOD") 
                    : PluginText.T("stashutility.set_bad", "Set BAD");
                if (ImGui.SmallButton(moveLabel))
                {
                    targetList.Add(id);
                    currentList.RemoveAt(i);
                    SaveSettings();
                    ImGui.PopID();
                    break;
                }
                ImGui.PopID();
            }
        }

        private List<string> GetWaystoneModLines(Item item)
        {
            var lines = new List<string>();
            if (item == null) return lines;

            if (item.TryGetComponent<Mods>(out var modsComponent))
            {
                AddModGroup(lines, modsComponent.ImplicitMods);
                AddModGroup(lines, modsComponent.ExplicitMods);
                AddModGroup(lines, modsComponent.EnchantMods);
            }

            if (item.TryGetComponent<ObjectMagicProperties>(out var magicProps))
            {
                AddModGroup(lines, magicProps.Mods);
            }

            return lines;
        }

        private void AddModGroup(List<string> lines, List<(string name, (float value0, float value1) values)> mods)
        {
            foreach (var (name, values) in mods)
            {
                var formatted = FormatModLine(name, values);
                if (!string.IsNullOrWhiteSpace(formatted))
                {
                    lines.Add(formatted);
                }
            }
        }

        private string FormatModLine(string template, (float value0, float value1) values)
        {
            if (string.IsNullOrWhiteSpace(template)) return string.Empty;

            var translatedTemplate = TranslateModName(template);
            var line = translatedTemplate;

            if (!float.IsNaN(values.value0))
            {
                float val0 = values.value0;
                if (val0 < 0 && translatedTemplate != template && (translatedTemplate.Contains("less", StringComparison.OrdinalIgnoreCase) || translatedTemplate.Contains("reduced", StringComparison.OrdinalIgnoreCase)))
                {
                    val0 = Math.Abs(val0);
                }

                line = line.Replace("{0}", FormatNumber(val0), StringComparison.Ordinal);
                if (!float.IsNaN(values.value1))
                {
                    float val1 = values.value1;
                    if (val1 < 0 && translatedTemplate != template && (translatedTemplate.Contains("less", StringComparison.OrdinalIgnoreCase) || translatedTemplate.Contains("reduced", StringComparison.OrdinalIgnoreCase)))
                    {
                        val1 = Math.Abs(val1);
                    }
                    line = line.Replace("{1}", FormatNumber(val1), StringComparison.Ordinal);
                }
            }

            return line.Trim();
        }

        private string FormatNumber(float value)
        {
            if (Math.Abs(value - MathF.Round(value)) < 0.001f)
            {
                return ((int)MathF.Round(value)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        private Dictionary<GameStats, int> GetStatsFromMods(Mods modsComponent)
        {
            var stats = new Dictionary<GameStats, int>();
            if (modsComponent == null || modsComponent.Address == IntPtr.Zero)
            {
                return stats;
            }

            try
            {
                var data = ReadMemory<GameOffsets.Objects.Components.ModsOffsets>(modsComponent.Address);
                var mystats = ReadStdVector<GameOffsets.Objects.Components.StatArrayStruct>(data.Details0.StatsFromMods);
                foreach (var newStat in mystats)
                {
                    stats[(GameStats)newStat.key] = newStat.value;
                }
            }
            catch
            {
                // Ignored
            }

            return stats;
        }

        private static int ExtractFirstNumber(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var match = System.Text.RegularExpressions.Regex.Match(text, @"\d+");
            if (match.Success && int.TryParse(match.Value, out var val))
            {
                return val;
            }
            return 0;
        }

        private IntPtr GetItemAddressFromElement(IntPtr elementAddr)
        {
            if (elementAddr == IntPtr.Zero) return IntPtr.Zero;

            for (int offset = Settings.ScanStartOffset; offset + 8 <= Settings.ScanEndOffset; offset += 8)
            {
                if (TryReadMemory<IntPtr>(elementAddr + offset, out var cand))
                {
                    if (IsValidItemEntity(cand))
                    {
                        return cand;
                    }
                }
            }
            return IntPtr.Zero;
        }

        private bool IsValidItemEntity(IntPtr address)
        {
            if (address == IntPtr.Zero || (ulong)address.ToInt64() < 0x10000 || (ulong)address.ToInt64() > 0x7FFFFFFFFFFF)
            {
                return false;
            }

            try
            {
                if (!TryReadMemory<IntPtr>(address + 0x08, out var detailsPtr))
                {
                    return false;
                }
                if (detailsPtr == IntPtr.Zero || (ulong)detailsPtr.ToInt64() < 0x10000 || (ulong)detailsPtr.ToInt64() > 0x7FFFFFFFFFFF)
                {
                    return false;
                }

                if (!TryReadMemory<StdWString>(detailsPtr + 0x08, out var nativeContainer))
                {
                    return false;
                }
                var path = ReadStdWString(nativeContainer);
                if (path.StartsWith("Metadata/Items", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch
            {
                // Ignored
            }
            return false;
        }

        private Item ReadFreshItem(IntPtr itemAddress)
        {
            if (itemAddress == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Activator.CreateInstance(
                    typeof(Item),
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new object[] { itemAddress },
                    null) as Item;
            }
            catch
            {
                return null;
            }
        }

        private IntPtr ResolvePath(IntPtr root, int[] path)
        {
            if (root == IntPtr.Zero)
            {
                if (Settings.EnableDebugProbe)
                {
                    lock (probeLog)
                    {
                        probeLog.Clear();
                        probeLog.Add("Root address is Zero");
                    }
                }
                return IntPtr.Zero;
            }

            var current = root;

            if (Settings.EnableDebugProbe)
            {
                lock (probeLog)
                {
                    probeLog.Clear();
                    probeLog.Add($"Root: 0x{current.ToInt64():X}");
                }
            }

            for (int i = 0; i < path.Length; i++)
            {
                var idx = path[i];
                var off = ReadMemory<UiElementBaseOffset>(current);
                var kids = ReadStdVector<IntPtr>(off.ChildrensPtr);

                if (Settings.EnableDebugProbe)
                {
                    lock (probeLog)
                    {
                        probeLog.Add($"Step {i} [Index {idx}]: parent 0x{current.ToInt64():X} has {kids.Length} children");
                    }
                }

                if (idx < 0 || idx >= kids.Length)
                {
                    if (Settings.EnableDebugProbe)
                    {
                        lock (probeLog)
                        {
                            probeLog.Add($"Error: Index {idx} out of range (0..{kids.Length - 1})");
                        }
                    }
                    return IntPtr.Zero;
                }

                current = kids[idx];
                if (current == IntPtr.Zero)
                {
                    if (Settings.EnableDebugProbe)
                    {
                        lock (probeLog)
                        {
                            probeLog.Add($"Error: Child at index {idx} is null");
                        }
                    }
                    return IntPtr.Zero;
                }
            }

            if (Settings.EnableDebugProbe)
            {
                lock (probeLog)
                {
                    probeLog.Add($"Success: Resolved to 0x{current.ToInt64():X}");
                }
            }

            return current;
        }

        private bool InitReflection()
        {
            try
            {
                var handleProp = Core.Process.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                this.handleObj = handleProp?.GetValue(Core.Process);
                if (this.handleObj == null) return false;

                var methods = this.handleObj.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                this.readStdWStringMethod = methods.First(m => m.Name == "ReadStdWString" && m.GetParameters().Length == 1);
                this.uiParentsObj          = PluginUiElementReflection.CreateParents("p1");
                this.uiParentsObjP2        = PluginUiElementReflection.CreateParents("p2");
                this.currentUiParentsObj   = this.uiParentsObj;
                
                var uiParentsType = PluginUiElementReflection.UiElementParentsType;
                if (uiParentsType != null)
                {
                    this.updateCacheMethod = uiParentsType.GetMethod("UpdateAllParentsParallel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                
                return true;
            }
            catch
            {
                return false;
            }
        }

        private T ReadMemory<T>(IntPtr address) where T : unmanaged
        {
            if (!readMemoryMethods.TryGetValue(typeof(T), out var method))
            {
                var genericMethod = this.handleObj.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .First(m => m.Name == "ReadMemory" && m.IsGenericMethod && m.GetParameters().Length == 1);
                method = genericMethod.MakeGenericMethod(typeof(T));
                readMemoryMethods[typeof(T)] = method;
            }
            return (T)method.Invoke(this.handleObj, new object[] { address });
        }

        private bool TryReadMemory<T>(IntPtr address, out T result) where T : unmanaged
        {
            try
            {
                if (!tryReadMemoryMethods.TryGetValue(typeof(T), out var method))
                {
                    var genericMethod = this.handleObj.GetType()
                        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                        .First(m => m.Name == "TryReadMemory" && m.IsGenericMethod && m.GetParameters().Length == 2);
                    genericMethod = genericMethod.MakeGenericMethod(typeof(T));
                    tryReadMemoryMethods[typeof(T)] = genericMethod;
                }

                var args = new object[] { address, default(T) };
                var success = (bool)method.Invoke(this.handleObj, args);
                result = (T)args[1];
                return success;
            }
            catch
            {
                result = default;
                return false;
            }
        }

        private T[] ReadStdVector<T>(StdVector nativeContainer) where T : unmanaged
        {
            if (!readStdVectorMethods.TryGetValue(typeof(T), out var method))
            {
                var genericMethod = this.handleObj.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .First(m => m.Name == "ReadStdVector" && m.IsGenericMethod);
                method = genericMethod.MakeGenericMethod(typeof(T));
                readStdVectorMethods[typeof(T)] = method;
            }
            return (T[])method.Invoke(this.handleObj, new object[] { nativeContainer });
        }

        private string ReadStdWString(StdWString nativeContainer)
        {
            try
            {
                return this.readStdWStringMethod!.Invoke(this.handleObj, new object[] { nativeContainer }) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }


        private void DrawStringList(List<string> list)
        {
            int toRemove = -1;
            for (int i = 0; i < list.Count; i++)
            {
                var val = list[i];
                ImGui.PushID(i);
                if (ImGui.InputText("##val", ref val, 128))
                {
                    list[i] = val;
                }
                ImGui.SameLine();
                if (ImGui.Button("Remove"))
                {
                    toRemove = i;
                }
                ImGui.PopID();
            }

            if (toRemove >= 0)
            {
                list.RemoveAt(toRemove);
            }

        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private void DumpUiTreeToFile(IntPtr address)
        {
            if (address == IntPtr.Zero) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"UI Tree Dump from root: 0x{address.ToInt64():X}");
            DumpUiTreeRecursive(address, "", 0, sb);
            
            try
            {
                var dir = Path.Combine(DllDirectory, "config");
                Directory.CreateDirectory(dir);
                var filepath = Path.Combine(dir, "ui_tree_dump.txt");
                File.WriteAllText(filepath, sb.ToString());
                Console.WriteLine($"[StashUtility] Dumped UI tree to {filepath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StashUtility] Failed to dump UI tree: {ex}");
            }
        }

        private void DumpUiTreeRecursive(IntPtr address, string prefix, int depth, System.Text.StringBuilder sb)
        {
            if (address == IntPtr.Zero || depth > 8) return;

            var off = ReadMemory<UiElementBaseOffset>(address);
            var kids = ReadStdVector<IntPtr>(off.ChildrensPtr);
            
            sb.AppendLine($"{prefix}Addr: 0x{address.ToInt64():X}, Vis: {UiElementBaseFuncs.IsVisibleChecker(off.Flags)}, Kids: {kids.Length}, Size: <{off.UnscaledSize.X},{off.UnscaledSize.Y}>");

            // Look for any string starting with "Metadata/" by dereferencing pointers
            for (int offset = 0; offset + 8 <= 0x800; offset += 8)
            {
                if (TryReadMemory<IntPtr>(address + offset, out var cand))
                {
                    if (cand != IntPtr.Zero && (ulong)cand.ToInt64() >= 0x10000 && (ulong)cand.ToInt64() <= 0x7FFFFFFFFFFF)
                    {
                        if (TryReadMemory<IntPtr>(cand + 0x08, out var detailsPtr))
                        {
                            if (detailsPtr != IntPtr.Zero && (ulong)detailsPtr.ToInt64() >= 0x10000 && (ulong)detailsPtr.ToInt64() <= 0x7FFFFFFFFFFF)
                            {
                                if (TryReadMemory<StdWString>(detailsPtr + 0x08, out var nativeContainer))
                                {
                                    var path = ReadStdWString(nativeContainer);
                                    if (path != null && path.StartsWith("Metadata/", StringComparison.Ordinal))
                                    {
                                        sb.AppendLine($"{prefix}  ==> Found Metadata Pointer at offset 0x{offset:X}: Entity=0x{cand.ToInt64():X}, Details=0x{detailsPtr.ToInt64():X}, Path={path}");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            for (int i = 0; i < kids.Length; i++)
            {
                DumpUiTreeRecursive(kids[i], prefix + $"  [{i}] ", depth + 1, sb);
            }
        }

        private static bool hasClearedDumpFile = false;
        private void DumpAllWaystonesMemory(Item item)
        {
            if (item == null) return;
            try
            {
                var dir = Path.Combine(DllDirectory, "config");
                Directory.CreateDirectory(dir);
                var dumpPath = Path.Combine(dir, "waystone_memory_dump.txt");
                if (!hasClearedDumpFile)
                {
                    if (File.Exists(dumpPath)) File.Delete(dumpPath);
                    hasClearedDumpFile = true;
                }

                var lines = new List<string>();
                lines.Add($"=== WAYSTONE MEMORY DUMP: {DateTime.Now} ===");

                if (item.TryGetComponent<Base>(out var baseComponent))
                {
                    lines.Add($"Item Name: {baseComponent.BaseItemName}");
                }
                lines.Add($"Item Path: {item.Path}");

                // Reflect componentAddresses to see what components are actually there
                var field = typeof(Entity).GetField("componentAddresses", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                {
                    var dict = field.GetValue(item) as System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr>;
                    if (dict != null)
                    {
                        lines.Add("Available Components:");
                        foreach (var kv in dict)
                        {
                            lines.Add($"  - {kv.Key} at 0x{kv.Value.ToInt64():X}");
                        }

                        if (dict.TryGetValue("Map", out var mapAddr) && mapAddr != IntPtr.Zero)
                        {
                            lines.Add("Map Component Hex Dump (0x80 bytes):");
                            for (int offset = 0; offset < 0x80; offset += 8)
                            {
                                TryReadMemory<long>(mapAddr + offset, out var val64);
                                TryReadMemory<int>(mapAddr + offset, out var val32_1);
                                TryReadMemory<int>(mapAddr + offset + 4, out var val32_2);
                                lines.Add($"  +0x{offset:X2}: {val64:X16} | int32: {val32_1}, {val32_2}");
                            }

                            TryReadMemory<IntPtr>(mapAddr + 0x10, out var detailsAddr);
                            lines.Add($"Map Component Details Pointer: 0x{detailsAddr.ToInt64():X}");
                            if (detailsAddr != IntPtr.Zero)
                            {
                                lines.Add("Map Component Details Hex Dump (0x80 bytes):");
                                for (int offset = 0; offset < 0x80; offset += 8)
                                {
                                    TryReadMemory<long>(detailsAddr + offset, out var val64);
                                    TryReadMemory<int>(detailsAddr + offset, out var val32_1);
                                    TryReadMemory<int>(detailsAddr + offset + 4, out var val32_2);
                                    lines.Add($"  Details +0x{offset:X2}: {val64:X16} | int32: {val32_1}, {val32_2}");
                                }
                            }
                        }

                        if (dict.TryGetValue("LocalStats", out var localStatsAddr) && localStatsAddr != IntPtr.Zero)
                        {
                            lines.Add("LocalStats Component Hex Dump (0x80 bytes):");
                            for (int offset = 0; offset < 0x80; offset += 8)
                            {
                                TryReadMemory<long>(localStatsAddr + offset, out var val64);
                                TryReadMemory<int>(localStatsAddr + offset, out var val32_1);
                                TryReadMemory<int>(localStatsAddr + offset + 4, out var val32_2);
                                lines.Add($"  +0x{offset:X2}: {val64:X16} | int32: {val32_1}, {val32_2}");
                            }
                        }

                        if (dict.TryGetValue("Quality", out var qualityAddr) && qualityAddr != IntPtr.Zero)
                        {
                            lines.Add("Quality Component Hex Dump (0x40 bytes):");
                            for (int offset = 0; offset < 0x40; offset += 8)
                            {
                                TryReadMemory<long>(qualityAddr + offset, out var val64);
                                TryReadMemory<int>(qualityAddr + offset, out var val32_1);
                                TryReadMemory<int>(qualityAddr + offset + 4, out var val32_2);
                                lines.Add($"  +0x{offset:X2}: {val64:X16} | int32: {val32_1}, {val32_2}");
                            }
                        }
                    }
                }

                if (item.TryGetComponent<Mods>(out var modsComp))
                {
                    lines.Add($"Rarity: {modsComp.Rarity}");
                    lines.Add("Implicit Mods:");
                    foreach (var m in modsComp.ImplicitMods)
                    {
                        lines.Add($"  - {m.name}: values = ({m.values.value0}, {m.values.value1})");
                    }
                    lines.Add("Explicit Mods:");
                    foreach (var m in modsComp.ExplicitMods)
                    {
                        lines.Add($"  - {m.name}: values = ({m.values.value0}, {m.values.value1})");
                    }
                    lines.Add("Mods StatsFromMods:");
                    var statsFromMods = GetStatsFromMods(modsComp);
                    foreach (var stat in statsFromMods)
                    {
                        lines.Add($"  - {stat.Key} ({(int)stat.Key}) = {stat.Value}");
                    }
                }

                if (item.TryGetComponent<ObjectMagicProperties>(out var omp))
                {
                    lines.Add("ObjectMagicProperties ModStats:");
                    foreach (var stat in omp.ModStats)
                    {
                        lines.Add($"  - {stat.Key} ({(int)stat.Key}) = {stat.Value}");
                    }
                }

                lines.Add("=========================================\n");

                File.AppendAllLines(dumpPath, lines);
            }
            catch
            {
            }
        }

        private static Dictionary<string, string> MapModTranslations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private class ModsData
        {
            [JsonProperty("DatabaseMods")]
            public List<string> DatabaseMods { get; set; } = new();

            [JsonProperty("Translations")]
            public Dictionary<string, string> Translations { get; set; } = new();
        }

        private static string TranslateModName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return string.Empty;

            // Strip trailing numbers/digits (e.g. MapPlayerRecoveryRate3 -> MapPlayerRecoveryRate)
            string cleanKey = rawName;
            while (cleanKey.Length > 0 && char.IsDigit(cleanKey[cleanKey.Length - 1]))
            {
                cleanKey = cleanKey.Substring(0, cleanKey.Length - 1);
            }

            if (MapModTranslations.TryGetValue(cleanKey, out var translated))
            {
                return translated;
            }

            // Fallback: just return the rawName
            return rawName;
        }

        private static readonly System.Text.RegularExpressions.Regex RangeRegex = 
            new System.Text.RegularExpressions.Regex(@"\([^)]*\)", System.Text.RegularExpressions.RegexOptions.Compiled);
        
        private static readonly System.Text.RegularExpressions.Regex DigitsRegex = 
            new System.Text.RegularExpressions.Regex(@"\d+", System.Text.RegularExpressions.RegexOptions.Compiled);
        
        private static readonly System.Text.RegularExpressions.Regex CleanRegex = 
            new System.Text.RegularExpressions.Regex(@"[^a-zA-Z%\s]", System.Text.RegularExpressions.RegexOptions.Compiled);
        
        private static readonly System.Text.RegularExpressions.Regex SpacesRegex = 
            new System.Text.RegularExpressions.Regex(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

        private string NormalizeForMatching(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            
            // Remove ranges in parentheses like (36-40) or (-8--6)
            var result = RangeRegex.Replace(input, "");
            
            // Remove digits
            result = DigitsRegex.Replace(result, "");
            
            // Remove everything except letters, % and whitespace
            result = CleanRegex.Replace(result, "");
            
            // Normalize spaces to single spaces and lowercase
            result = SpacesRegex.Replace(result, " ").Trim().ToLowerInvariant();
            
            return result;
        }
    }
}
