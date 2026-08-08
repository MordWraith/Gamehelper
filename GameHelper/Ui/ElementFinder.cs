// <copyright file="ElementFinder.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Ui
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using ClickableTransparentOverlay.Win32;
    using Coroutine;
    using CoroutineEvents;
    using GameHelper.Cache;
    using ImGuiNET;
    using RemoteEnums;
    using RemoteObjects.UiElement;
    using Utils;

    /// <summary>
    ///     Finds UiElements under the cursor by recursively searching
    ///     from a user-provided root address. Collects the top 10 smallest
    ///     non-ancestor matches. Triggered by a configurable hotkey.
    /// </summary>
    public static class ElementFinder
    {
        private static readonly UiElementParents Parents = new(
            null,
            GameStateTypes.InGameState,
            GameStateTypes.EscapeState,
            "ElementFinder");

        private static readonly List<MatchEntry> Results = new();
        private static int lastSearchFrame = -1;
        private static string rootAddressHex = string.Empty;
        private static string lastError = string.Empty;

        /// <summary>
        ///     Initializes the co-routines.
        /// </summary>
        internal static void InitializeCoroutines()
        {
            CoroutineHandler.Start(ElementFinderCoroutine(), priority: UiRenderPriority.CoreWindows);
        }

        /// <summary>
        ///     Performs the recursive search starting from the user-provided root address,
        ///     collecting all elements whose bounding box contains the cursor.
        /// </summary>
        private static void PerformSearch()
        {
            Results.Clear();
            lastError = string.Empty;
            lastSearchFrame = ImGui.GetFrameCount();
            Core.GHSettings.ShowElementFinder = true;

            if (string.IsNullOrWhiteSpace(rootAddressHex))
            {
                lastError = "No root address set. Paste a hex address or click 'Use GameUi'.";
                return;
            }

            if (!long.TryParse(rootAddressHex.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var addr))
            {
                lastError = $"Invalid hex address: '{rootAddressHex}'";
                return;
            }

            var rootAddress = new System.IntPtr(addr);
            if (rootAddress == System.IntPtr.Zero)
            {
                lastError = "Root address is zero.";
                return;
            }

            Parents.UpdateAllParentsParallel();

            UiElementBase root;
            try
            {
                root = new UiElementBase(rootAddress, Parents);
            }
            catch (System.Exception ex)
            {
                lastError = $"Failed to read UiElement at 0x{addr:X}: {ex.Message}";
                return;
            }

            var mousePos = ImGui.GetMousePos();
            var rawMatches = new List<MatchEntry>();

            CollectMatches(mousePos, root, new List<int>(), rawMatches);

            if (rawMatches.Count == 0)
            {
                return;
            }

            // Remove any element whose path is a prefix of another match's path
            // (i.e. keep only the most specific / deepest matches).
            var filtered = rawMatches
                .Where(m => !rawMatches.Any(other => IsPathPrefixOf(m.Path, other.Path)))
                .ToList();

            // Sort by area ascending and take the top 10 smallest.
            Results.AddRange(filtered.OrderBy(m => m.Area).Take(10));
        }

        /// <summary>
        ///     Recursively walks the UiElement tree, collecting every element
        ///     whose bounding box contains the cursor.
        /// </summary>
        private static void CollectMatches(
            Vector2 mousePos,
            UiElementBase element,
            List<int> path,
            List<MatchEntry> matches)
        {
            for (var i = 0; i < element.TotalChildrens; i++)
            {
                var child = element[i];
                if (child == null)
                {
                    continue;
                }

                var childPath = new List<int>(path) { i };
                var pos = child.Position;
                var size = child.Size;

                if (mousePos.X >= pos.X && mousePos.X <= pos.X + size.X &&
                    mousePos.Y >= pos.Y && mousePos.Y <= pos.Y + size.Y)
                {
                    var area = size.X * size.Y;
                    if (area > 0)
                    {
                        matches.Add(new MatchEntry
                        {
                            Element = child,
                            Path = childPath,
                            Area = area,
                            ChainDisplay = FormatChain(child.Address, childPath),
                        });
                    }
                }

                if (child.TotalChildrens > 0)
                {
                    CollectMatches(mousePos, child, childPath, matches);
                }
            }
        }

        /// <summary>
        ///     Returns true if <paramref name="shorter"/> is a strict prefix
        ///     of <paramref name="longer"/> (i.e. shorter is an ancestor path).
        /// </summary>
        private static bool IsPathPrefixOf(List<int> shorter, List<int> longer)
        {
            if (shorter.Count >= longer.Count)
            {
                return false;
            }

            for (var i = 0; i < shorter.Count; i++)
            {
                if (shorter[i] != longer[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        ///     Formats the address and chain indices into a display string
        ///     like "ADDRESS[0][3][4]".
        /// </summary>
        private static string FormatChain(System.IntPtr address, List<int> chain)
        {
            var addr = address.ToInt64().ToString("X");
            var indices = string.Concat(chain.ConvertAll(i => $"[{i}]"));
            return $"{addr}{indices}";
        }

        /// <summary>
        ///     Draws the Element Finder window and handles the search hotkey.
        /// </summary>
        private static IEnumerator<Wait> ElementFinderCoroutine()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnRender);

                // Hotkey check — works even when the window is closed.
                if (Utils.IsKeyPressedAndNotTimeout(Core.GHSettings.ElementFinderHotKey))
                {
                    PerformSearch();
                }

                if (!Core.GHSettings.ShowElementFinder)
                {
                    continue;
                }

                if (ImGui.Begin("Element Finder", ref Core.GHSettings.ShowElementFinder))
                {
                    // Root address input.
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text("Root:");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
                    ImGui.InputText("##ef_root_addr", ref rootAddressHex, 32,
                        ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.EnterReturnsTrue);
                    ImGuiHelper.ToolTip("Hex address of the UiElement to start searching from.\n" +
                        "Paste an address from Data Visualization or UiExplorer.");

                    ImGui.SameLine();
                    if (ImGui.Button("Use GameUi"))
                    {
                        var guiAddr = Core.States.InGameStateObject.GameUi.Address;
                        if (guiAddr != System.IntPtr.Zero)
                        {
                            rootAddressHex = guiAddr.ToInt64().ToString("X");
                        }
                    }
                    ImGuiHelper.ToolTip("Auto-fill with the GameUi root address from Data Visualization.");

                    ImGui.SameLine();
                    if (ImGui.Button("Search"))
                    {
                        PerformSearch();
                    }

                    ImGui.Separator();

                    // Show last error if any.
                    if (!string.IsNullOrEmpty(lastError))
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.3f, 0.3f, 1f));
                        ImGui.TextWrapped(lastError);
                        ImGui.PopStyleColor();
                        ImGui.Separator();
                    }

                    ImGui.TextColored(
                        new Vector4(1f, 1f, 0f, 1f),
                        $"Press {Core.GHSettings.ElementFinderHotKey} to search under cursor");

                    // Flash a brief confirmation when a search was just triggered.
                    if (lastSearchFrame >= 0 && ImGui.GetFrameCount() - lastSearchFrame < 60)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(
                            new Vector4(0f, 1f, 0.5f, 1f),
                            Results.Count > 0
                                ? $"  ({Results.Count} found)"
                                : "  (nothing under cursor)");
                    }

                    ImGui.Separator();

                    if (Results.Count > 0)
                    {
                        var hilightColor = new Vector4(1f, 1f, 0f, 1f);

                        for (var i = 0; i < Results.Count; i++)
                        {
                            var match = Results[i];
                            var headerOpen = ImGui.CollapsingHeader(
                                $"#{i + 1}  {match.ChainDisplay}##ef_result_{i}",
                                ImGuiTreeNodeFlags.DefaultOpen);

                            if (ImGui.IsItemHovered())
                            {
                                ImGuiHelper.DrawRect(
                                    match.Element.Position,
                                    match.Element.Size,
                                    0,
                                    255,
                                    255);
                            }

                            if (headerOpen)
                            {
                                ImGuiHelper.IntPtrToImGui("Address:", match.Element.Address);

                                ImGui.Text("Chain:");
                                ImGui.SameLine();
                                ImGui.PushStyleColor(ImGuiCol.Text, hilightColor);
                                ImGui.Text(match.ChainDisplay);
                                ImGui.PopStyleColor();

                                ImGui.Text($"Position: {match.Element.Position}");
                                ImGui.Text($"Size: {match.Element.Size}  (area: {match.Area:F0})");
                                ImGui.Text($"IsVisible: {match.Element.IsVisible}");
                                ImGui.Text($"Children: {match.Element.TotalChildrens}");

                                if (ImGui.Button($"Explore in UiExplorer##ef_explore_{i}"))
                                {
                                    GameUiExplorer.AddUiElement(match.Element);
                                }
                            }
                        }
                    }
                    else
                    {
                        ImGui.TextColored(
                            new Vector4(0.5f, 0.5f, 0.5f, 1f),
                            "No results yet.");
                        ImGui.TextColored(
                            new Vector4(0.5f, 0.5f, 0.5f, 1f),
                            "Hover over the game and press the hotkey to search.");
                    }
                }

                ImGui.End();
            }
        }

        private sealed class MatchEntry
        {
            internal UiElementBase Element = null!;
            internal List<int> Path = null!;
            internal float Area;
            internal string ChainDisplay = string.Empty;
        }
    }
}
