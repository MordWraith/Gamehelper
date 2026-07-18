// <copyright file="StashValueCore.cs" company="zx0CF1">
// Copyright (c) zx0CF1. All rights reserved.
// </copyright>

namespace StashValue
{
    // Adapted and inspired by LootValue (GameHelper2 upstream) and RitualHelper (by caio).
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Reflection;
    using System.Text.RegularExpressions;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameOffsets.Natives;
    using GameOffsets.Objects.UiElement;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    ///     StashValue plugin — prices items inside the player's stash when opened
    ///     and overlays their values directly on the stash item slots.
    /// </summary>
    public sealed class StashValueCore : PCore<StashValueSettings>
    {
        private object? handleObj;
        private MethodInfo? readUiOffsetMethod;
        private MethodInfo? readStdVectorMethod;
        private MethodInfo? readIntPtrMethod;

        private Inventory? stashInventory;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingPathname))
            {
                try
                {
                    this.Settings = JsonConvert.DeserializeObject<StashValueSettings>(File.ReadAllText(this.SettingPathname)) ?? new StashValueSettings();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[StashValue] Failed to load settings: {ex.Message}");
                    this.Settings = new StashValueSettings();
                }
            }

            PoeNinjaPriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League ?? string.Empty, this.Settings.RefreshIntervalMin);
            PoeNinjaPriceFetcher.Initialize(this.DllDirectory);
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
            this.handleObj = null;
            this.readUiOffsetMethod = null;
            this.readStdVectorMethod = null;
            this.readIntPtrMethod = null;
            this.stashInventory = null;
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname) ?? string.Empty);
                File.WriteAllText(this.SettingPathname, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StashValue] Failed to save settings: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            var changed = false;
            changed |= ImGui.Checkbox("Show stash item prices", ref this.Settings.ShowOverlay);
            changed |= ImGui.Checkbox("Show inventory item prices", ref this.Settings.ShowInventoryOverlay);
            changed |= ImGui.Checkbox("Hide price when hovering item", ref this.Settings.HidePriceOnHover);

            var maxThreshold = this.Settings.DisplayCurrency switch
            {
                0 => 100f,   // Divine
                1 => 200f,   // Exalted
                _ => 1000f   // Chaos
            };
            var currencyLabel = this.Settings.DisplayCurrency switch
            {
                0 => "div",
                1 => "ex",
                _ => "c"
            };
            this.Settings.MinValueEx = Math.Clamp(this.Settings.MinValueEx, 0.0f, maxThreshold);
            changed |= ImGui.SliderFloat($"Min Price Threshold ({currencyLabel})##threshold", ref this.Settings.MinValueEx, 0.0f, maxThreshold, $"%.2f {currencyLabel}");

            changed |= ImGui.Checkbox("Show Debug Info (Draw Boxes & Diagnostics)", ref this.Settings.ShowDebugInfo);

            ImGui.Separator();
            ImGui.Text("Display Currency");
            if (ImGui.RadioButton("Chaos", this.Settings.DisplayCurrency == 2)) { this.Settings.DisplayCurrency = 2; changed = true; }
            ImGui.SameLine();
            if (ImGui.RadioButton("Exalted", this.Settings.DisplayCurrency == 1)) { this.Settings.DisplayCurrency = 1; changed = true; }
            ImGui.SameLine();
            if (ImGui.RadioButton("Divine", this.Settings.DisplayCurrency == 0)) { this.Settings.DisplayCurrency = 0; changed = true; }

            changed |= ImGui.SliderFloat("Font Scale", ref this.Settings.PriceFontScale, 0.5f, 2f, "%.2f");
            changed |= ImGui.SliderFloat("Horizontal Offset", ref this.Settings.PriceOffsetX, -50f, 50f);
            changed |= ImGui.SliderFloat("Vertical Offset", ref this.Settings.PriceOffsetY, -50f, 50f);
            changed |= ImGui.ColorEdit4("Text Color", ref this.Settings.TextColor);

            ImGui.Separator();
            ImGui.Text("Price Source");
            if (ImGui.RadioButton("poe2scout", this.Settings.PriceSource == PoeNinjaPriceFetcher.SourcePoe2Scout))
            {
                this.Settings.PriceSource = PoeNinjaPriceFetcher.SourcePoe2Scout;
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("poe.ninja", this.Settings.PriceSource == PoeNinjaPriceFetcher.SourcePoeNinja))
            {
                this.Settings.PriceSource = PoeNinjaPriceFetcher.SourcePoeNinja;
                changed = true;
            }

            changed |= ImGui.InputText("League", ref this.Settings.League, 64);
            changed |= ImGui.SliderInt("Refresh interval (min)", ref this.Settings.RefreshIntervalMin, 1, 120);

            if (changed)
            {
                this.SaveSettings();
            }

            if (ImGui.Button("Refresh prices now"))
            {
                PoeNinjaPriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League ?? string.Empty, this.Settings.RefreshIntervalMin);
                PoeNinjaPriceFetcher.ForceRefresh(this.DllDirectory, ignoreCooldown: true);
            }

            ImGui.SameLine();
            if (PoeNinjaPriceFetcher.IsFetching)
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.2f, 1f), "Loading...");
            }
            else if (PoeNinjaPriceFetcher.LastFetchUtc > DateTime.MinValue)
            {
                var mins = Math.Max(0, (int)(DateTime.UtcNow - PoeNinjaPriceFetcher.LastFetchUtc).TotalMinutes);
                ImGui.TextColored(new Vector4(0.5f, 0.8f, 0.5f, 1f), $"{PoeNinjaPriceFetcher.LoadedItemCount} items | {mins} min ago");
            }
        }

        private void DrawDebugWindow()
        {
            ImGui.Begin("StashValue Debugger");
            var gameUi = Core.States.InGameStateObject.GameUi;
            if (gameUi != null)
            {
                ImGui.Text($"LeftPanel: {gameUi.LeftPanel.Address.ToString("X")} | Visible: {gameUi.LeftPanel.IsVisible}");
                ImGui.Text($"RightPanel: {gameUi.RightPanel.Address.ToString("X")} | Visible: {gameUi.RightPanel.IsVisible}");
            }
            else
            {
                ImGui.Text("GameUi is NULL");
            }

            var serverData = Core.States.InGameStateObject.CurrentAreaInstance.ServerDataObject;
            if (serverData != null)
            {
                var propInfo = typeof(ServerData).GetProperty("PlayerInventories", BindingFlags.Instance | BindingFlags.NonPublic);
                var playerInventories = propInfo?.GetValue(serverData) as Dictionary<InventoryName, IntPtr>;
                ImGui.Text($"PlayerInventories: {(playerInventories != null ? "Found" : "Not Found")}");
                if (playerInventories != null)
                {
                    foreach (var kvp in playerInventories)
                    {
                        ImGui.Text($"Inv {kvp.Key} ({(int)kvp.Key}): {kvp.Value.ToString("X")}");
                    }
                    if (this.stashInventory != null)
                    {
                        ImGui.Text($"Stash Inventory Address: {this.stashInventory.Address.ToString("X")}");
                        ImGui.Text($"Stash Inventory Items: {this.stashInventory.Items.Count}");
                    }
                }
            }
            else
            {
                ImGui.Text("ServerData is NULL");
            }
            ImGui.End();
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState) return;

            PoeNinjaPriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League ?? string.Empty, this.Settings.RefreshIntervalMin);
            PoeNinjaPriceFetcher.RefreshIfNeeded();

            if (this.Settings.ShowDebugInfo)
            {
                this.DrawDebugWindow();
            }

            if (!this.Settings.ShowOverlay && !this.Settings.ShowInventoryOverlay && !this.Settings.ShowDebugInfo)
            {
                return;
            }

            var gameUi = Core.States.InGameStateObject.GameUi;
            if (gameUi == null || gameUi.Address == IntPtr.Zero)
            {
                return;
            }

            if (!this.EnsureReflection()) return;

            var leftSlots = new List<SlotInfo>();
            var rightSlots = new List<SlotInfo>();
            var leftHovered = false;
            var rightHovered = false;

            if (gameUi.LeftPanel.IsVisible)
            {
                leftSlots = this.ScanSlots(gameUi.LeftPanel.Address, gameUi.LeftPanel.Position, gameUi.LeftPanel.Size, out leftHovered);
            }

            if (gameUi.RightPanel.IsVisible)
            {
                rightSlots = this.ScanSlots(gameUi.RightPanel.Address, gameUi.RightPanel.Position, gameUi.RightPanel.Size, out rightHovered);
            }

            var globalAnyHovered = leftHovered || rightHovered;
            var hideOnHoverActive = this.Settings.HidePriceOnHover && globalAnyHovered;

            if (gameUi.LeftPanel.IsVisible)
            {
                this.DrawSlots(leftSlots, drawDebugBoxes: this.Settings.ShowDebugInfo, drawPrices: this.Settings.ShowOverlay, hideOnHoverActive: hideOnHoverActive);
            }

            if (gameUi.RightPanel.IsVisible && (this.Settings.ShowInventoryOverlay || this.Settings.ShowDebugInfo))
            {
                this.DrawSlots(rightSlots, drawDebugBoxes: this.Settings.ShowDebugInfo, drawPrices: this.Settings.ShowInventoryOverlay, hideOnHoverActive: hideOnHoverActive);
            }
        }

        private bool EnsureReflection()
        {
            if (this.handleObj != null) return true;
            var handleProp = typeof(GameProcess).GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic);
            this.handleObj = handleProp?.GetValue(Core.Process);
            if (this.handleObj == null) return false;

            var methods = this.handleObj.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var readMem = methods.First(m => m.Name == "ReadMemory" && m.IsGenericMethod && m.GetParameters().Length == 1);
            var readVec = methods.First(m => m.Name == "ReadStdVector" && m.IsGenericMethod);
            this.readUiOffsetMethod = readMem.MakeGenericMethod(typeof(UiElementBaseOffset));
            this.readStdVectorMethod = readVec.MakeGenericMethod(typeof(IntPtr));
            this.readIntPtrMethod = readMem.MakeGenericMethod(typeof(IntPtr));
            return true;
        }

        private struct SlotInfo
        {
            public IntPtr El;
            public IntPtr Ptr;
            public Vector2 Pos;
            public Vector2 Size;
            public string ValueText;
        }

        private List<SlotInfo> ScanSlots(IntPtr panelAddr, Vector2 panelPos, Vector2 panelSize, out bool panelHovered)
        {
            panelHovered = false;
            var candidates = new List<SlotInfo>();
            if (panelAddr == IntPtr.Zero) return candidates;

            var queue = new Queue<(IntPtr Address, IntPtr Parent)>();
            var visited = new HashSet<IntPtr>();
            queue.Enqueue((panelAddr, IntPtr.Zero));

            var mousePos = ImGui.GetIO().MousePos;

            while (queue.Count > 0 && visited.Count < 5000)
            {
                var (el, parent) = queue.Dequeue();
                if (el == IntPtr.Zero || !visited.Add(el)) continue;

                if (this.readUiOffsetMethod!.Invoke(this.handleObj, new object[] { el }) is not UiElementBaseOffset off) continue;
                if (!UiElementBaseFuncs.IsVisibleChecker(off.Flags)) continue;

                if (this.readStdVectorMethod!.Invoke(this.handleObj, new object[] { off.ChildrensPtr }) is IntPtr[] kids)
                {
                    foreach (var k in kids) queue.Enqueue((k, el));
                }

                // Check if it's an item slot UI element
                var ptrObj = this.readIntPtrMethod!.Invoke(this.handleObj, new object[] { el + 0x4F8 });
                var ptr = ptrObj is IntPtr intPtr ? intPtr : IntPtr.Zero;
                if (ptr == IntPtr.Zero) continue;

                // Validate that it is a real item in the PoE item structure
                var item = ItemModHelper.ReadFreshItem(ptr);
                if (item == null || string.IsNullOrEmpty(item.Path) || !item.Path.StartsWith("Metadata/Items", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (PluginUiElementReflection.TryGetAbsoluteRect(el, out var pos, out var size))
                    {
                        // Premium stash tabs often store the item pointer on an inner
                        // bookkeeping element. Its immediate parent is the visible cell.
                        if (parent != IntPtr.Zero &&
                            PluginUiElementReflection.TryGetAbsoluteRect(parent, out var parentPos, out var parentSize) &&
                            parentSize.X >= 20f && parentSize.Y >= 20f &&
                            ((parentSize.X <= 160f && parentSize.Y <= 256f) ||
                             (parentSize.X <= 256f && parentSize.Y <= 160f)))
                        {
                            pos = parentPos;
                            size = parentSize;
                        }

                        // Cache the display price text if it passes pricing threshold.
                        if (this.TryPriceItem(item, out var valueText))
                        {
                            candidates.Add(new SlotInfo { El = el, Ptr = ptr, Pos = pos, Size = size, ValueText = valueText });
                        }
                    }
                }
                catch
                {
                }
            }

            // Premium tabs expose ghost copies of the same item. Keep the copy
            // physically located inside the open panel, then deduplicate by item.
            var panelMax = panelPos + panelSize;
            var slots = new List<SlotInfo>();
            foreach (var group in candidates.GroupBy(x => x.Ptr))
            {
                var slot = group.FirstOrDefault(x =>
                {
                    var center = x.Pos + (x.Size * 0.5f);
                    return center.X >= panelPos.X && center.X <= panelMax.X &&
                           center.Y >= panelPos.Y && center.Y <= panelMax.Y;
                });
                if (slot.Ptr == IntPtr.Zero)
                {
                    continue;
                }

                if (mousePos.X >= slot.Pos.X && mousePos.X <= slot.Pos.X + slot.Size.X &&
                    mousePos.Y >= slot.Pos.Y && mousePos.Y <= slot.Pos.Y + slot.Size.Y)
                {
                    panelHovered = true;
                }

                slots.Add(slot);
            }

            return slots;
        }

        private void DrawSlots(List<SlotInfo> slots, bool drawDebugBoxes, bool drawPrices, bool hideOnHoverActive)
        {
            var fg = ImGui.GetForegroundDrawList();
            var font = ImGui.GetFont();
            var baseFontSize = ImGui.GetFontSize();
            var fontSize = baseFontSize * this.Settings.PriceFontScale;

            foreach (var slot in slots)
            {
                if (drawDebugBoxes)
                {
                    fg.AddRect(slot.Pos, slot.Pos + slot.Size, 0xFFFF00FFu, 0f, ImDrawFlags.None, 2f);
                    fg.AddText(font, fontSize, slot.Pos, 0xFFFFFFFFu, $"E: {slot.Ptr.ToString("X")}");
                }

                if (drawPrices)
                {
                    if (hideOnHoverActive)
                    {
                        continue;
                    }

                    var valueText = slot.ValueText;
                    if (!string.IsNullOrEmpty(valueText))
                    {
                        try
                        {
                            var textWidth = ImGui.CalcTextSize(valueText).X * this.Settings.PriceFontScale;
                            var drawPos = new Vector2(
                                slot.Pos.X + this.Settings.PriceOffsetX,
                                slot.Pos.Y + slot.Size.Y - fontSize + this.Settings.PriceOffsetY);

                            // Draw background chip
                            fg.AddRectFilled(
                                drawPos - new Vector2(3f, 1f),
                                drawPos + new Vector2(textWidth + 3f, fontSize + 1f),
                                0xB0000000u,
                                3f);

                            // Draw shadowed text
                            fg.AddText(font, fontSize, drawPos + new Vector2(1f, 1f), 0xCC000000u, valueText);
                            fg.AddText(font, fontSize, drawPos, ImGui.ColorConvertFloat4ToU32(this.Settings.TextColor), valueText);
                        }
                        catch
                        {
                            // Stale/freed element
                        }
                    }
                }
            }
        }

        private bool TryPriceItem(Item item, out string valueText)
        {
            valueText = string.Empty;

            var rarity = Rarity.Normal;
            if (item.TryGetComponent<Mods>(out var mods)) rarity = mods.Rarity;

            var baseName = item.TryGetComponent<Base>(out var baseComp) ? baseComp.BaseItemName?.Trim() ?? string.Empty : string.Empty;
            var artBasename = item.TryGetComponent<RenderItem>(out var renderItem) ? ExtractArtBasename(renderItem.ResourcePath) : string.Empty;
            var fullItemPath = item.Path ?? string.Empty;
            var internalName = fullItemPath.Contains('/') ? fullItemPath[(fullItemPath.LastIndexOf('/') + 1)..] : fullItemPath;

            var itemName = baseName;
            if (rarity == Rarity.Unique && !string.IsNullOrEmpty(artBasename))
            {
                foreach (var key in ArtKeyVariants(artBasename))
                {
                    if (PoeNinjaPriceFetcher.TryResolveDisplayName(key, out var uniqueName) &&
                        !PoeNinjaPriceFetcher.IsGenericLookupName(uniqueName))
                    {
                        itemName = uniqueName;
                        break;
                    }

                    if (PoeNinjaPriceFetcher.HasPriceDataForName(key))
                    {
                        itemName = key;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(itemName)) return false;

            var modLines = ItemModHelper.GetModLines(item);
            var price = PoeNinjaPriceFetcher.GetPrice(itemName, modLines, internalName, fullItemPath);
            if (price == null) return false;

            var stack = item.TryGetComponent<Stack>(out var stackComp) && stackComp.Count > 1 ? stackComp.Count : 1;
            var priceChaos = price.PriceChaos * stack;

            var priced = new PoeNinjaPrice { PriceChaos = priceChaos };
            var (displayValue, displayCurrency) = PoeNinjaPriceFetcher.GetDisplayPrice(priced, this.Settings.DisplayCurrency);

            if (this.Settings.MinValueEx > 0f && displayValue < this.Settings.MinValueEx)
            {
                return false;
            }

            valueText = FormatValue(displayValue, displayCurrency);
            return true;
        }

        private static string FormatValue(double value, string currency) => currency switch
        {
            "divine" => value.ToString("0.00", CultureInfo.InvariantCulture) + " div",
            "chaos" => value.ToString("0.#", CultureInfo.InvariantCulture) + " c",
            _ => value.ToString("0.#", CultureInfo.InvariantCulture) + " ex",
        };

        private static string ExtractArtBasename(string? artPath)
        {
            if (string.IsNullOrWhiteSpace(artPath)) return string.Empty;
            var slash = artPath.LastIndexOfAny(new[] { '/', '\\' });
            var file = slash >= 0 && slash < artPath.Length - 1 ? artPath[(slash + 1)..] : artPath;
            var dot = file.LastIndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }

        private static IEnumerable<string> ArtKeyVariants(string artBasename)
        {
            if (string.IsNullOrWhiteSpace(artBasename)) yield break;
            yield return artBasename;
            if (artBasename.StartsWith("The", StringComparison.OrdinalIgnoreCase) && artBasename.Length > 3)
                yield return artBasename[3..];
            else
                yield return "The" + artBasename;
        }
    }

    /// <summary>
    ///     Module initializer to dynamically resolve GameHelper vs GameHelper.App assembly references.
    /// </summary>
#pragma warning disable CA2255
    public static class StashValueModuleInitializer
    {
        /// <summary>
        ///     Initializes the assembly resolution redirection.
        /// </summary>
        [System.Runtime.CompilerServices.ModuleInitializer]
        public static void Initialize()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var requestedName = new System.Reflection.AssemblyName(args.Name).Name;
                if (requestedName == "GameHelper" || requestedName == "GameHelper.App")
                {
                    var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    return assemblies.FirstOrDefault(a => 
                        a.GetName().Name == "GameHelper" || a.GetName().Name == "GameHelper.App");
                }
                return null;
            };
        }
    }
#pragma warning restore CA2255
}
