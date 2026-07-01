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
        private object? uiParentsObj;
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
            this.uiParentsObj = null;
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
            changed |= ImGui.Checkbox(this.PluginText.Label("settings.show_stash_prices", "Show stash item prices", "StashValueShowStashPrices"), ref this.Settings.ShowOverlay);
            changed |= ImGui.Checkbox(this.PluginText.Label("settings.show_inventory_prices", "Show inventory item prices", "StashValueShowInventoryPrices"), ref this.Settings.ShowInventoryOverlay);
            changed |= ImGui.Checkbox(this.PluginText.Label("settings.hide_price_on_hover", "Hide price when hovering item", "StashValueHidePriceOnHover"), ref this.Settings.HidePriceOnHover);

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
            changed |= ImGui.SliderFloat($"{this.PluginText.F("settings.min_price_threshold", "Min Price Threshold ({0})", currencyLabel)}##threshold", ref this.Settings.MinValueEx, 0.0f, maxThreshold, $"%.2f {currencyLabel}");

            changed |= ImGui.Checkbox(this.PluginText.Label("settings.show_debug_info", "Show Debug Info (Draw Boxes & Diagnostics)", "StashValueShowDebugInfo"), ref this.Settings.ShowDebugInfo);

            ImGui.Separator();
            ImGui.Text(this.PluginText.T("section.display_currency", "Display Currency"));
            if (ImGui.RadioButton(this.PluginText.Label("currency.chaos", "Chaos", "StashValueCurrencyChaos"), this.Settings.DisplayCurrency == 2)) { this.Settings.DisplayCurrency = 2; changed = true; }
            ImGui.SameLine();
            if (ImGui.RadioButton(this.PluginText.Label("currency.exalted", "Exalted", "StashValueCurrencyExalted"), this.Settings.DisplayCurrency == 1)) { this.Settings.DisplayCurrency = 1; changed = true; }
            ImGui.SameLine();
            if (ImGui.RadioButton(this.PluginText.Label("currency.divine", "Divine", "StashValueCurrencyDivine"), this.Settings.DisplayCurrency == 0)) { this.Settings.DisplayCurrency = 0; changed = true; }

            changed |= ImGui.SliderFloat(this.PluginText.Label("settings.font_scale", "Font Scale", "StashValueFontScale"), ref this.Settings.PriceFontScale, 0.5f, 2f, "%.2f");
            changed |= ImGui.SliderFloat(this.PluginText.Label("settings.horizontal_offset", "Horizontal Offset", "StashValueHorizontalOffset"), ref this.Settings.PriceOffsetX, -50f, 50f);
            changed |= ImGui.SliderFloat(this.PluginText.Label("settings.vertical_offset", "Vertical Offset", "StashValueVerticalOffset"), ref this.Settings.PriceOffsetY, -50f, 50f);
            changed |= ImGui.ColorEdit4(this.PluginText.Label("settings.text_color", "Text Color", "StashValueTextColor"), ref this.Settings.TextColor);

            ImGui.Separator();
            ImGui.Text(this.PluginText.T("section.price_source", "Price Source"));
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

            changed |= ImGui.InputText(this.PluginText.Label("settings.league", "League", "StashValueLeague"), ref this.Settings.League, 64);
            changed |= ImGui.SliderInt(this.PluginText.Label("settings.refresh_interval", "Refresh interval (min)", "StashValueRefreshInterval"), ref this.Settings.RefreshIntervalMin, 1, 120);

            if (changed)
            {
                this.SaveSettings();
            }

            if (ImGui.Button(this.PluginText.Label("button.refresh_prices_now", "Refresh prices now", "StashValueRefreshPricesNow")))
            {
                PoeNinjaPriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League ?? string.Empty, this.Settings.RefreshIntervalMin);
                PoeNinjaPriceFetcher.ForceRefresh(this.DllDirectory, ignoreCooldown: true);
            }

            ImGui.SameLine();
            if (PoeNinjaPriceFetcher.IsFetching)
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.2f, 1f), this.PluginText.T("status.loading", "Loading..."));
            }
            else if (PoeNinjaPriceFetcher.LastFetchUtc > DateTime.MinValue)
            {
                var mins = Math.Max(0, (int)(DateTime.UtcNow - PoeNinjaPriceFetcher.LastFetchUtc).TotalMinutes);
                ImGui.TextColored(new Vector4(0.5f, 0.8f, 0.5f, 1f), this.PluginText.F("status.loaded_items", "{0} items | {1} min ago", PoeNinjaPriceFetcher.LoadedItemCount, mins));
            }
        }

        private void DrawDebugWindow()
        {
            ImGui.Begin(this.PluginText.Title("debug.window_title", "StashValue Debugger", "StashValueDebugger"));
            var gameUi = Core.States.InGameStateObject.GameUi;
            if (gameUi != null)
            {
                ImGui.Text(this.PluginText.F("debug.left_panel", "LeftPanel: {0} | Visible: {1}", gameUi.LeftPanel.Address.ToString("X"), gameUi.LeftPanel.IsVisible));
                ImGui.Text(this.PluginText.F("debug.right_panel", "RightPanel: {0} | Visible: {1}", gameUi.RightPanel.Address.ToString("X"), gameUi.RightPanel.IsVisible));
            }
            else
            {
                ImGui.Text(this.PluginText.T("debug.game_ui_null", "GameUi is NULL"));
            }

            var serverData = Core.States.InGameStateObject.CurrentAreaInstance.ServerDataObject;
            if (serverData != null)
            {
                var propInfo = typeof(ServerData).GetProperty("PlayerInventories", BindingFlags.Instance | BindingFlags.NonPublic);
                var playerInventories = propInfo?.GetValue(serverData) as Dictionary<InventoryName, IntPtr>;
                ImGui.Text(this.PluginText.F("debug.player_inventories", "PlayerInventories: {0}", playerInventories != null ? this.PluginText.T("debug.found", "Found") : this.PluginText.T("debug.not_found", "Not Found")));
                if (playerInventories != null)
                {
                    foreach (var kvp in playerInventories)
                    {
                        ImGui.Text(this.PluginText.F("debug.inventory", "Inv {0} ({1}): {2}", kvp.Key, (int)kvp.Key, kvp.Value.ToString("X")));
                    }
                    if (this.stashInventory != null)
                    {
                        ImGui.Text(this.PluginText.F("debug.stash_inventory_address", "Stash Inventory Address: {0}", this.stashInventory.Address.ToString("X")));
                        ImGui.Text(this.PluginText.F("debug.stash_inventory_items", "Stash Inventory Items: {0}", this.stashInventory.Items.Count));
                    }
                }
            }
            else
            {
                ImGui.Text(this.PluginText.T("debug.server_data_null", "ServerData is NULL"));
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
                leftSlots = this.ScanSlots(gameUi.LeftPanel.Address, out leftHovered);
            }

            if (gameUi.RightPanel.IsVisible)
            {
                rightSlots = this.ScanSlots(gameUi.RightPanel.Address, out rightHovered);
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

        private List<SlotInfo> ScanSlots(IntPtr panelAddr, out bool panelHovered)
        {
            panelHovered = false;
            var slots = new List<SlotInfo>();
            if (panelAddr == IntPtr.Zero) return slots;

            var queue = new Queue<IntPtr>();
            var visited = new HashSet<IntPtr>();
            queue.Enqueue(panelAddr);

            this.uiParentsObj ??= PluginUiElementReflection.CreateParents();
            if (this.uiParentsObj == null) return slots;

            var mousePos = ImGui.GetIO().MousePos;

            while (queue.Count > 0 && visited.Count < 5000)
            {
                var el = queue.Dequeue();
                if (el == IntPtr.Zero || !visited.Add(el)) continue;

                if (this.readUiOffsetMethod!.Invoke(this.handleObj, new object[] { el }) is not UiElementBaseOffset off) continue;
                if (!UiElementBaseFuncs.IsVisibleChecker(off.Flags)) continue;

                if (this.readStdVectorMethod!.Invoke(this.handleObj, new object[] { off.ChildrensPtr }) is IntPtr[] kids)
                {
                    foreach (var k in kids) queue.Enqueue(k);
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
                    var uiElement = PluginUiElementReflection.CreateUiElement(el, this.uiParentsObj);
                    if (uiElement != null)
                    {
                        var pos = (Vector2)PluginUiElementReflection.UiElementPositionProperty!.GetValue(uiElement)!;
                        var size = (Vector2)PluginUiElementReflection.UiElementSizeProperty!.GetValue(uiElement)!;
                        if (pos != Vector2.Zero && size.X > 0f)
                        {
                            // Trigger hover check if mouse is on this item slot (even if below threshold, since it has a tooltip)
                            if (mousePos.X >= pos.X && mousePos.X <= pos.X + size.X &&
                                mousePos.Y >= pos.Y && mousePos.Y <= pos.Y + size.Y)
                            {
                                panelHovered = true;
                            }

                            // Cache the display price text if it passes pricing threshold
                            if (this.TryPriceItem(item, out var valueText))
                            {
                                slots.Add(new SlotInfo { El = el, Ptr = ptr, Pos = pos, Size = size, ValueText = valueText });
                            }
                        }
                    }
                }
                catch
                {
                }
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
