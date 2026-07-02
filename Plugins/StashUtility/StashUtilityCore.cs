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
        private object uiParentsObj;

        private readonly Dictionary<Type, MethodInfo> readMemoryMethods = new();
        private readonly Dictionary<Type, MethodInfo> readStdVectorMethods = new();
        private readonly Dictionary<Type, MethodInfo> tryReadMemoryMethods = new();

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

        public override void DrawSettings()
        {
            ImGui.Checkbox("Show Overlay When Game in Background", ref Settings.ShowOverlayInBackground);
            ImGuiHelper.ToolTip("If checked, the waystone highlights will remain visible even when the game window is in the background.");

            ImGui.Checkbox("Enable Debug Settings", ref Settings.EnableDebugProbe);
            ImGuiHelper.ToolTip("Enables advanced debugging options, interactive explorer, and hovered item inspector.");

            if (Settings.EnableDebugProbe)
            {
                debugHoveredCurrentElement = false;
                debugHoveredAllChildren = false;
                debugHoveredChildIndex = -1;

                if (ImGui.CollapsingHeader("UI Path Offsets (Debug Explorer)"))
                {
                    ImGui.SeparatorText("Path Navigation");

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

                    ImGui.InputText("Path Indices", ref Settings.PathString, 128);
                    ImGuiHelper.ToolTip("Comma-separated indices starting from the LeftPanel root. Edit manually or click through the explorer.");

                    if (ImGui.Button("Reset Path to Default"))
                    {
                        Settings.PathString = "2,0,0,0,1,1,45,0,1";
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Dump UI Tree to File"))
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

                    ImGui.SeparatorText("Interactive Explorer");

                    ImGui.InputText("Custom Root Address (Hex)", ref explorerRootAddressStr, 64);
                    ImGuiHelper.ToolTip("Leave empty to use gameUi.LeftPanel.Address as root.");

                    ImGui.Text("Breadcrumbs:");
                    ImGui.SameLine();
                    if (ImGui.Button("Root"))
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
                        ImGui.TextColored(new Vector4(1, 0, 0, 1), "Root address is null. (Game UI not loaded?)");
                    }
                    else if (!pathValid)
                    {
                        ImGui.TextColored(new Vector4(1, 0, 0, 1), $"Path resolution failed at index step {failedIndex}.");
                    }
                    else if (current != IntPtr.Zero)
                    {
                        var off = ReadMemory<UiElementBaseOffset>(current);
                        var stringId = ReadStdWString(off.StringIdPtr);
                        var isVis = UiElementBaseFuncs.IsVisibleChecker(off.Flags);

                        ImGui.TextColored(new Vector4(0, 1, 0, 1), "Path resolved successfully!");
                        ImGui.SameLine();
                        ImGui.SmallButton("Hover me to highlight active node in game");
                        if (ImGui.IsItemHovered())
                        {
                            debugHoveredCurrentElement = true;
                        }

                        ImGuiHelper.IntPtrToImGui("Active Node Addr", current);
                        ImGui.Text($"String ID: {stringId}  |  Visible: {isVis}");

                        var kids = ReadStdVector<IntPtr>(off.ChildrensPtr);
                        ImGui.SeparatorText($"Children ({kids.Length})");

                        ImGui.SmallButton("Hover me to highlight all children bounds");
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
                                    ImGui.Text($"[{j}] Null Pointer");
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

                if (ImGui.CollapsingHeader("Hovered Waystone Inspector (Debug)"))
                {
                    ImGui.Checkbox("Freeze Hovered Waystone", ref freezeHoveredWaystone);
                    ImGui.SameLine();
                    if (ImGui.Button("Clear"))
                    {
                        lastHoveredWaystone = null;
                    }

                    if (lastHoveredWaystone != null)
                    {
                        ImGui.SeparatorText("Item Details");
                        ImGuiHelper.IntPtrToImGui("Entity Address", lastHoveredWaystone.Address);
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

                        ImGui.SeparatorText("Advanced");
                        if (ImGui.Button("Dump Full Memory To File"))
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
                        ImGui.TextColored(new Vector4(1, 1, 0, 1), "Hover over a waystone in your Stash Tab to inspect it.");
                    }
                }
            }

            ImGui.Checkbox("Enable Waystone Manager", ref Settings.EnableWaystoneManager);
            ImGuiHelper.ToolTip("Enables or disables highlighting of waystones.");

            if (Settings.EnableWaystoneManager)
            {
                ImGui.Indent();
                if (ImGui.CollapsingHeader("Waystone Highlight Criteria (Normal)"))
                {
                    ImGui.SliderInt("Min Tier", ref Settings.MinTier, 1, 16);
                    ImGuiHelper.ToolTip("Minimum Waystone Tier to highlight (Normal rarity is always ignored if Hide Normal Waystones is checked).");

                    ImGui.Checkbox("Filter Revives", ref Settings.FilterMaxRevives);
                    if (Settings.FilterMaxRevives)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt("Max Revives Allowed##val", ref Settings.MaxRevivesAvailable, 0, 6);
                    }

                    ImGui.Checkbox("Filter Item Rarity", ref Settings.FilterMinItemRarity);
                    if (Settings.FilterMinItemRarity)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt("Min Item Rarity (%)##val", ref Settings.MinItemRarity, 0, 200);
                    }

                    ImGui.Checkbox("Filter Pack Size", ref Settings.FilterMinPackSize);
                    if (Settings.FilterMinPackSize)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt("Min Pack Size (%)##val", ref Settings.MinPackSize, 0, 100);
                    }

                    ImGui.Checkbox("Filter Monster Rarity", ref Settings.FilterMinMonsterRarity);
                    if (Settings.FilterMinMonsterRarity)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt("Min Monster Rarity (%)##val", ref Settings.MinMonsterRarity, 0, 100);
                    }

                    ImGui.Checkbox("Filter Monster Effectiveness", ref Settings.FilterMinMonsterEffectiveness);
                    if (Settings.FilterMinMonsterEffectiveness)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt("Min Monster Effectiveness (%)##val", ref Settings.MinMonsterEffectiveness, 0, 100);
                    }

                    ImGui.Checkbox("Filter Waystone Drop Chance", ref Settings.FilterMinWaystoneDropChance);
                    if (Settings.FilterMinWaystoneDropChance)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt("Min Waystone Drop Chance (%)##val", ref Settings.MinWaystoneDropChance, 0, 300);
                    }
                }

                if (ImGui.CollapsingHeader("Waystone Mod Filter Manager"))
                {
                    ImGui.Checkbox("Only Filter Bad Mods on Criteria-Meeting Waystones", ref Settings.FilterBadModsOnlyOnHighlighted);
                    ImGuiHelper.ToolTip("When checked, bad mod filtering is only applied to waystones that already meet the Tier and other active numerical criteria.");

                    if (ImGui.BeginCombo("Add Waystone Mod", "Select from database..."))
                    {
                        ImGui.InputTextWithHint("##searchWaystone", "Search database...", ref waystoneSearchTerm, 64);
                        var filtered = Data.ModDatabase.AllWaystoneMods
                            .Where(m => m.Name.Contains(waystoneSearchTerm, StringComparison.OrdinalIgnoreCase) ||
                                        m.Id.Contains(waystoneSearchTerm, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        foreach (var mod in filtered)
                        {
                            if (ImGui.Selectable($"{mod.Name}##{mod.Id}"))
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

                    DrawModListUI("BAD WAYSTONE MODS (RED HIGHLIGHT)", Settings.BadModPatterns, Settings.GoodModPatterns, new Vector4(1f, 0.4f, 0.4f, 1f), true);
                    DrawModListUI("GOOD WAYSTONE MODS (GREEN HIGHLIGHT)", Settings.GoodModPatterns, Settings.BadModPatterns, new Vector4(0.4f, 1f, 0.4f, 1f), false);
                }

                if (ImGui.CollapsingHeader("Waystone GREAT Highlight Conditions"))
                {
                    ImGui.Checkbox("Filter Great Item Rarity", ref Settings.FilterGreatRarity);
                    if (Settings.FilterGreatRarity)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt("Min Great Rarity (%)", ref Settings.MinGreatRarity, 0, 200);
                    }

                    ImGui.Checkbox("Filter Great Pack Size", ref Settings.FilterGreatPackSize);
                    if (Settings.FilterGreatPackSize)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt("Min Great Pack Size (%)", ref Settings.MinGreatPackSize, 0, 100);
                    }

                    ImGui.Checkbox("Filter Great Monster Rarity", ref Settings.FilterGreatMonstRarity);
                    if (Settings.FilterGreatMonstRarity)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt("Min Great Monster Rarity (%)", ref Settings.MinGreatMonstRarity, 0, 100);
                    }

                    ImGui.Checkbox("Filter Great Monster Effectiveness", ref Settings.FilterGreatEffect);
                    if (Settings.FilterGreatEffect)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt("Min Great Effectiveness (%)", ref Settings.MinGreatEffect, 0, 100);
                    }

                    ImGui.Checkbox("Filter Great Waystone Drop Chance", ref Settings.FilterGreatDropChance);
                    if (Settings.FilterGreatDropChance)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt("Min Great Drop Chance (%)", ref Settings.MinGreatDropChance, 0, 300);
                    }
                }
                ImGui.Unindent();
            }

            ImGui.Checkbox("Enable Tablet Manager", ref Settings.EnableTabletManager);
            ImGuiHelper.ToolTip("Enables or disables highlighting of precursor/breach tablets.");

            if (Settings.EnableTabletManager)
            {
                ImGui.Indent();
                if (ImGui.CollapsingHeader("Tablet Mod Filter Manager"))
                {
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
                            if (ImGui.BeginTabItem(kvp.Key))
                            {
                                var tabMods = Data.ModDatabase.AllTabletMods.Where(kvp.Value).ToList();
                                
                                ImGui.InputTextWithHint($"##search{kvp.Key}", $"Search {kvp.Key} Mods...", ref tabletSearchTerm, 64);
                                var filtered = tabMods.Where(m => m.Name.Contains(tabletSearchTerm, StringComparison.OrdinalIgnoreCase) || m.Id.Contains(tabletSearchTerm, StringComparison.OrdinalIgnoreCase)).ToList();

                                if (ImGui.BeginChild($"Child{kvp.Key}", new Vector2(0, 250), ImGuiChildFlags.Borders))
                                {
                                    foreach (var mod in filtered)
                                    {
                                        bool isGood = Settings.TabletGoodModPatterns.Contains(mod.Id);
                                        bool isBad = Settings.TabletBadModPatterns.Contains(mod.Id);
                                        bool isGod = Settings.TabletGodModPatterns.Contains(mod.Id);

                                        ImGui.TextWrapped(mod.Name.Replace("%", "%%"));

                                        if (ImGui.Checkbox($"Good##g_{mod.Id}", ref isGood))
                                        {
                                            if (isGood) { Settings.TabletGoodModPatterns.Add(mod.Id); Settings.TabletBadModPatterns.Remove(mod.Id); Settings.TabletGodModPatterns.Remove(mod.Id); }
                                            else { Settings.TabletGoodModPatterns.Remove(mod.Id); }
                                            SaveSettings();
                                        }
                                        ImGui.SameLine(100f);
                                        if (ImGui.Checkbox($"Bad##b_{mod.Id}", ref isBad))
                                        {
                                            if (isBad) { Settings.TabletBadModPatterns.Add(mod.Id); Settings.TabletGoodModPatterns.Remove(mod.Id); Settings.TabletGodModPatterns.Remove(mod.Id); }
                                            else { Settings.TabletBadModPatterns.Remove(mod.Id); }
                                            SaveSettings();
                                        }
                                        ImGui.SameLine(190f);
                                        if (ImGui.Checkbox($"God##god_{mod.Id}", ref isGod))
                                        {
                                            if (isGod) { Settings.TabletGodModPatterns.Add(mod.Id); Settings.TabletGoodModPatterns.Remove(mod.Id); Settings.TabletBadModPatterns.Remove(mod.Id); }
                                            else { Settings.TabletGodModPatterns.Remove(mod.Id); }
                                            SaveSettings();
                                        }
                                        ImGui.Separator();
                                    }
                                    ImGui.EndChild();
                                }
                                ImGui.EndTabItem();
                            }
                        }
                        ImGui.EndTabBar();
                    }
                }

                if (ImGui.CollapsingHeader("Tablet GREAT Highlight Conditions"))
                {
                    ImGui.Checkbox("Filter Tablet Great Status", ref Settings.FilterTabletGreat);
                    if (Settings.FilterTabletGreat)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(300f);
                        ImGui.SetNextItemWidth(150f);
                        ImGui.SliderInt("Min Good Tablet Mods Count", ref Settings.MinTabletGoodMods, 1, 5);
                    }
                    ImGui.SetNextItemWidth(150f);
                    ImGui.SliderInt("Min Good Mods To Ignore Bad", ref Settings.MinGoodModsToIgnoreBad, 1, 6);
                    ImGuiHelper.ToolTip("If a tablet has this many good mods, it will ignore any bad mods and still be highlighted as Good/Great.");
                }
                ImGui.Unindent();
            }

            if (ImGui.CollapsingHeader("Overlay Visual Settings"))
            {
                ImGui.Checkbox("Show Mod Highlight Border", ref Settings.ShowModBorder);
                ImGui.Checkbox("Show Rarity Corner Indicator", ref Settings.ShowRarityBorder);
                if (Settings.ShowRarityBorder)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150f);
                    ImGui.SliderFloat("Indicator Size##size", ref Settings.RarityIndicatorSize, 5f, 30f);
                }
                ImGui.Checkbox("Hide Normal (White) Waystones", ref Settings.HideNormalWaystones);
                ImGui.SliderFloat("Border Thickness", ref Settings.BorderThickness, 1f, 10f);
                ImGui.SliderFloat("Border Margin", ref Settings.BorderMargin, 0f, 10f);

                string[] borderStyles = { "Solid", "Dashed", "Dotted" };
                ImGui.Combo("Bad Highlight Border Style", ref Settings.FrameStyleBad, borderStyles, borderStyles.Length);
                ImGui.Combo("Good Highlight Border Style", ref Settings.FrameStyleGood, borderStyles, borderStyles.Length);

                string[] arrowPositions = { "Top-Left", "Top-Right", "Bottom-Left", "Bottom-Right" };
                ImGui.Combo("GREAT Arrow Position", ref Settings.GreatIndicatorPosition, arrowPositions, arrowPositions.Length);
                ImGui.SliderFloat("GREAT Arrow Size", ref Settings.GreatIndicatorSize, 5f, 40f);

                ImGui.SeparatorText("Colors");
                ImGui.ColorEdit4("Good Highlight Color", ref Settings.GoodColor);
                ImGui.ColorEdit4("Bad Highlight Color", ref Settings.BadColor);
                ImGui.ColorEdit4("GREAT Arrow Color", ref Settings.ColorGreat);
                ImGui.ColorEdit4("Rare Rarity Color", ref Settings.RareRarityColor);
                ImGui.ColorEdit4("Magic Rarity Color", ref Settings.MagicRarityColor);
                ImGui.ColorEdit4("Normal Rarity Color", ref Settings.NormalRarityColor);
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

        public override void DrawUI()
        {
            if (!Settings.EnableWaystoneManager && !Settings.EnableTabletManager && !Settings.EnableDebugProbe) return;

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

            if (Settings.EnableDebugProbe)
            {
                DrawDebugOverlay();
            }

            if (!Settings.EnableWaystoneManager && !Settings.EnableTabletManager) return;

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

            var stashTabsContainer = ResolvePath(gameUi.LeftPanel.Address, stashTabsContainerPath);
            if (stashTabsContainer != IntPtr.Zero)
            {
                var tabsOffsets = ReadMemory<UiElementBaseOffset>(stashTabsContainer);
                var tabs = ReadStdVector<IntPtr>(tabsOffsets.ChildrensPtr);

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
                            }
                        }

                        if (!processedAsFragmentTablets)
                        {
                            // 3. Otherwise, check if it's a normal/quad stash tab: activeTabAddr -> 0 -> 0 has slots directly
                            var normalGridRoot = ResolvePath(activeTabAddr, new int[] { 0, 0 });
                            if (normalGridRoot != IntPtr.Zero)
                            {
                                ProcessNormalTab(normalGridRoot);
                            }
                        }
                    }
                }
            }

            // 3. Process Character Inventory Panel if open: RightPanel -> 5 -> 36
            if (gameUi.RightPanel.Address != IntPtr.Zero && IsElementVisible(gameUi.RightPanel.Address))
            {
                var inventoryGridRoot = ResolvePath(gameUi.RightPanel.Address, new int[] { 5, 36 });
                if (inventoryGridRoot != IntPtr.Zero)
                {
                    ProcessNormalTab(inventoryGridRoot);
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
                        var el = PluginUiElementReflection.CreateUiElement(slot, this.uiParentsObj);
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
                    var el = PluginUiElementReflection.CreateUiElement(slot, this.uiParentsObj);
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
                var el = PluginUiElementReflection.CreateUiElement(slot, this.uiParentsObj);
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
                var el = PluginUiElementReflection.CreateUiElement(address, this.uiParentsObj);
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
                    var pathMatch = System.Text.RegularExpressions.Regex.Match(path, @"Waystone(\d+)");
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
            int revives = 0;

            int tabletGoodCount = 0;

            if (modsComponent != null)
            {
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

                    isGreat = activeFilters > 0 && candidate;
                }
                else if (isTablet)
                {
                    if (isGod || tabletGoodCount >= Settings.MinGoodModsToIgnoreBad)
                    {
                        isBad = false;
                        isGood = true;
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
                if (Settings.FilterMinPackSize && sumPackSize < Settings.MinPackSize) passesNumericalFilters = false;
                if (Settings.FilterMinMonsterRarity && sumMonstRarity < Settings.MinMonsterRarity) passesNumericalFilters = false;
                if (Settings.FilterMinMonsterEffectiveness && sumEffect < Settings.MinMonsterEffectiveness) passesNumericalFilters = false;
                if (Settings.FilterMinWaystoneDropChance && sumDropChance < Settings.MinWaystoneDropChance) passesNumericalFilters = false;
            }

            if (isTablet && !isBad && !isGood) return;

            if (isWaystone && Settings.FilterBadModsOnlyOnHighlighted && !passesNumericalFilters) return;

            if (!isBad && !passesNumericalFilters) return;

            float scale = size.X / 52.0f;
            float margin = Settings.BorderMargin * scale; // Adaptive margin for 4K scaling where bounding boxes overlap
            float activeBorderThickness = 0f;

            if (Settings.ShowModBorder && (isBad || isGood || passesNumericalFilters))
            {
                Vector4 borderCol = isBad ? Settings.BadColor : Settings.GoodColor;
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
                dl.AddTriangleFilled(topTip, topTip + new Vector2(-arrowSize / 2, arrowSize), topTip + new Vector2(arrowSize / 2, arrowSize), ImGuiHelper.Color(Settings.ColorGreat));
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

        private void DrawModListUI(string title, List<string> currentList, List<string> targetList, Vector4 color, bool isCurrentlyBad)
        {
            ImGui.TextColored(color, title);
            if (currentList.Count == 0) ImGui.TextDisabled("   (List empty)");

            for (int i = 0; i < currentList.Count; i++)
            {
                string id = currentList[i];
                var defW = Data.ModDatabase.AllWaystoneMods.FirstOrDefault(m => m.Id == id);
                var defT = Data.ModDatabase.AllTabletMods.FirstOrDefault(m => m.Id == id);
                string name = defW?.Name ?? defT?.Name ?? id;

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

                string moveLabel = isCurrentlyBad ? "Set GOOD" : "Set BAD";
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
                this.uiParentsObj = PluginUiElementReflection.CreateParents();
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
