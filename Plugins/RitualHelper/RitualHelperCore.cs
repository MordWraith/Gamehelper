// <copyright file="RitualHelperCore.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace RitualHelper
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Reflection;
    using System.Text.RegularExpressions;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameOffsets.Objects.States.InGameState;
    using GameOffsets.Objects.UiElement;
    using GameOffsets.Natives;
    using ImGuiNET;
    using Newtonsoft.Json;

    public sealed class RitualHelperCore : PCore<RitualHelperSettings>
    {
        private readonly Dictionary<string, List<string>> debugInventories = new();
        private readonly HashSet<string> alertedItemsThisSession = new(StringComparer.OrdinalIgnoreCase);
        private readonly CurrencyIconLoader currencyIcons = new();

        // Displayed-text std::wstring offset on a UiElement (used by the BFS fallback signature scan).
        private const int UiElementTextOffset = 0x390;

        // Signature strings that only render while the ritual tribute shop is open.
        private static readonly string[] RitualSignatureTexts = { "Rituals Remaining", "tribute to the king" };

        private MethodInfo? readUiOffsetMethod;
        private MethodInfo? readStdVectorMethod;
        private MethodInfo? readIntPtrMethod;
        private MethodInfo? readStdWStringStructMethod;
        private MethodInfo? readStdWStringMethod;
        private object? handleObj;
        private bool wasRitualWindowOpen;
        private Dictionary<string, string>? customNamesCache;
        private int selectedLeagueIndex = -1;
        private bool iconsReloadPending;
        private int? pendingSoundPlayback;
        private readonly List<PriceLabelDraw> cachedPriceLabels = new();
        private readonly Dictionary<string, double> sessionStablePriceChaos = new(StringComparer.OrdinalIgnoreCase);
        private object? uiParentsObj;
        private DateTime nextPriceRecomputeUtc = DateTime.MinValue;

        // BFS fallback: only runs when the fast index chain is structurally broken (post-patch).
        private IntPtr scannedGridAddr = IntPtr.Zero;
        private DateTime nextScanUtc = DateTime.MinValue;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        public override void DrawSettings()
        {
            try
            {
            LeagueProvider.EnsureLoaded();

            if (ImGui.BeginTabBar("##RitualHelperTabs"))
            {
                if (ImGui.BeginTabItem("General"))
                {
                    ImGui.Checkbox("Show Prices", ref this.Settings.ShowOverlay);

                    ImGui.Separator();
                    ImGui.Text("Display");
                    if (ImGui.RadioButton("Chaos (c)", this.Settings.DisplayCurrency == 2))
                        this.Settings.DisplayCurrency = 2;
                    ImGui.SameLine();
                    if (ImGui.RadioButton("Divine (D)", this.Settings.DisplayCurrency == 0))
                        this.Settings.DisplayCurrency = 0;
                    ImGui.SameLine();
                    if (ImGui.RadioButton("Exalted (Ex)", this.Settings.DisplayCurrency == 1))
                        this.Settings.DisplayCurrency = 1;

                    ImGui.SliderFloat("Font Scale", ref this.Settings.PriceFontScale, 0.1f, 2.0f);
                    ImGui.SliderFloat("Offset X", ref this.Settings.PriceOffsetX, -50f, 50f);
                    ImGui.SliderFloat("Offset Y", ref this.Settings.PriceOffsetY, -50f, 50f);
                    ImGui.ColorEdit4("Text Color", ref this.Settings.PriceTextColor);
                    ImGui.SliderFloat("Min display value (Ex)", ref this.Settings.MinDisplayExalted, 0f, 500f, "%.0f Ex");
                    ImGui.TextWrapped(
                        "Prices are shown as value + currency orb icon (no background box).");

                    ImGui.Separator();
                    ImGui.Text("Alert Sound");
                    ImGui.Checkbox("Enable alert", ref this.Settings.PlayValueAlert);
                    ImGui.SliderFloat("Alert from (Divine)", ref this.Settings.AlertMinDivine, 0.1f, 50f, "%.3f");

                    ImGui.Text("Sound");
                    if (ImGui.RadioButton("Asterisk", this.Settings.AlertSound == 0))
                        this.Settings.AlertSound = 0;
                    ImGui.SameLine();
                    if (ImGui.RadioButton("Exclamation", this.Settings.AlertSound == 1))
                        this.Settings.AlertSound = 1;
                    ImGui.SameLine();
                    if (ImGui.RadioButton("Hand", this.Settings.AlertSound == 2))
                        this.Settings.AlertSound = 2;
                    if (ImGui.RadioButton("Question", this.Settings.AlertSound == 3))
                        this.Settings.AlertSound = 3;
                    ImGui.SameLine();
                    if (ImGui.RadioButton("Beep", this.Settings.AlertSound == 4))
                        this.Settings.AlertSound = 4;

                    if (ImGui.Button("Test Sound"))
                        this.pendingSoundPlayback = this.Settings.AlertSound;

                    ImGui.Separator();
                    ImGui.TextWrapped(
                        "All items (currency and uniques) are named automatically from game memory. " +
                        "Prices come from poe2scout + poe.ninja; items missing there show no label (trade tools may differ).");
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Data Source"))
                {
                    this.DrawPriceSourceSelector();
                    this.DrawLeagueSelector();

                    ImGui.SliderInt("Refresh interval (min)", ref this.Settings.RefreshIntervalMin, 1, 120);
                    if (ImGui.Button("Refresh Prices Now"))
                    {
                        PoeNinjaPriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League ?? string.Empty, this.Settings.RefreshIntervalMin);
                        PoeNinjaPriceFetcher.ForceRefresh(this.DllDirectory);
                    }

                    ImGui.SameLine();
                    if (PoeNinjaPriceFetcher.IsFetching || LeagueProvider.IsLoading)
                    {
                        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.2f, 1f), "Loading...");
                    }
                    else if (PoeNinjaPriceFetcher.LastFetchUtc > DateTime.MinValue)
                    {
                        var mins = Math.Max(0, (int)(DateTime.UtcNow - PoeNinjaPriceFetcher.LastFetchUtc).TotalMinutes);
                        ImGui.TextColored(new Vector4(0.5f, 0.8f, 0.5f, 1f),
                            $"{PoeNinjaPriceFetcher.LoadedItemCount} items | {mins} min ago");
                    }

                    ImGui.TextWrapped(
                        "Prices are estimates from poe2scout / poe.ninja (not live trade). Uniques use the higher of scout and ninja. Refresh after league economy shifts.");

                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Advanced"))
                {
                    ImGui.Checkbox("Debug Mode (Show All Inventories)", ref this.Settings.DebugMode);
                    ImGui.Checkbox("Diagnose Pricing (label every tile)", ref this.Settings.DiagnosePricing);
                    ImGui.TextWrapped(
                        "When on, every ritual tile is labelled with its rarity, the base name read from memory, " +
                        "the final lookup name, and the internal id. Tiles with no price show a red 'NO PRICE'.");

                    ImGui.Separator();
                    ImGui.Checkbox("Force BFS window search (testing)", ref this.Settings.ForceBfsFallback);
                    ImGui.TextWrapped(
                        "Bypass the fast index chain and always locate the ritual window via the signature BFS " +
                        "fallback. Normally the fallback only engages if the index chain breaks after a patch.");
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            ImGui.Separator();
            ImGui.TextWrapped(
                "This plugin reads items from the Ritual shop (Favours window) and displays prices on each item.");
            }
            finally
            {
                this.FlushPendingSound();
            }
        }

        private void DrawLeagueSelector()
        {
            var leagues = LeagueProvider.Leagues;
            if (leagues.Count == 0)
            {
                ImGui.InputText("League", ref this.Settings.League, 64);
                return;
            }

            if (this.selectedLeagueIndex < 0 || this.selectedLeagueIndex >= leagues.Count ||
                !string.Equals(leagues[this.selectedLeagueIndex], this.Settings.League, StringComparison.OrdinalIgnoreCase))
            {
                this.selectedLeagueIndex = 0;
                for (var i = 0; i < leagues.Count; i++)
                {
                    if (string.Equals(leagues[i], this.Settings.League, StringComparison.OrdinalIgnoreCase))
                    {
                        this.selectedLeagueIndex = i;
                        break;
                    }
                }
            }

            ImGui.SetNextItemWidth(260f);
            if (ImGui.BeginCombo("League", leagues[this.selectedLeagueIndex]))
            {
                for (var i = 0; i < leagues.Count; i++)
                {
                    if (ImGui.Selectable(leagues[i], i == this.selectedLeagueIndex))
                    {
                        this.selectedLeagueIndex = i;
                        this.Settings.League = leagues[i];
                    }
                }

                ImGui.EndCombo();
            }
        }

        private void DrawPriceSourceSelector()
        {
            ImGui.Text("Price source:");
            var previousSource = this.Settings.PriceSource;

            if (ImGui.RadioButton("poe.ninja", this.Settings.PriceSource == PoeNinjaPriceFetcher.SourcePoeNinja))
            {
                this.Settings.PriceSource = PoeNinjaPriceFetcher.SourcePoeNinja;
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("poe2scout", this.Settings.PriceSource == PoeNinjaPriceFetcher.SourcePoe2Scout))
            {
                this.Settings.PriceSource = PoeNinjaPriceFetcher.SourcePoe2Scout;
            }

            if (this.Settings.PriceSource != previousSource)
            {
                PoeNinjaPriceFetcher.Configure(
                    this.Settings.PriceSource,
                    this.Settings.League ?? string.Empty,
                    this.Settings.RefreshIntervalMin);
                PoeNinjaPriceFetcher.ForceRefresh(this.DllDirectory, ignoreCooldown: true);
            }
        }

        private void NormalizePriceSourceSetting()
        {
            if (this.Settings.PriceSource == PoeNinjaPriceFetcher.SourcePoeNinja ||
                this.Settings.PriceSource == PoeNinjaPriceFetcher.SourcePoe2Scout)
            {
                return;
            }

            this.Settings.PriceSource = PoeNinjaPriceFetcher.SourcePoe2Scout;
        }

        public override void DrawUI()
        {
            try
            {
            if (!this.Settings.ShowOverlay && !this.Settings.DebugMode) return;
            if (Core.States.GameCurrentState != GameStateTypes.InGameState) return;

            PoeNinjaPriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League ?? string.Empty, this.Settings.RefreshIntervalMin);
            PoeNinjaPriceFetcher.RefreshIfNeeded();

            if (this.iconsReloadPending || !this.currencyIcons.TryGet("divine.png", out _, out _, out _))
            {
                this.currencyIcons.Reload();
                this.iconsReloadPending = false;
            }

            try
            {
                if (this.handleObj == null)
                {
                    PoeNinjaPriceFetcher.Initialize(this.DllDirectory);
                    this.currencyIcons.Initialize(this.DllDirectory);
                    this.iconsReloadPending = true;

                    var handleProp = typeof(GameProcess).GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic);
                    this.handleObj = handleProp?.GetValue(Core.Process);
                    if (this.handleObj == null) return;

                    var methods = this.handleObj.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var readMemMethod = System.Linq.Enumerable.First(methods, m => m.Name == "ReadMemory" && m.IsGenericMethod && m.GetParameters().Length == 1);
                    var readVectorMethod = System.Linq.Enumerable.First(methods, m => m.Name == "ReadStdVector" && m.IsGenericMethod);
                    this.readUiOffsetMethod = readMemMethod.MakeGenericMethod(typeof(UiElementBaseOffset));
                    this.readStdVectorMethod = readVectorMethod.MakeGenericMethod(typeof(IntPtr));
                    this.readIntPtrMethod = readMemMethod.MakeGenericMethod(typeof(IntPtr));
                    this.readStdWStringStructMethod = readMemMethod.MakeGenericMethod(typeof(StdWString));
                    this.readStdWStringMethod = System.Linq.Enumerable.First(
                        methods, m => m.Name == "ReadStdWString" && m.GetParameters().Length == 1);
                }

                var gameUiAddr = Core.States.InGameStateObject.GameUi.Address;
                var rootOffsetObj = this.readUiOffsetMethod!.Invoke(this.handleObj, new object[] { gameUiAddr });
                if (rootOffsetObj is not UiElementBaseOffset rootOffset)
                {
                    return;
                }

                var mainChildrenObj = this.readStdVectorMethod!.Invoke(this.handleObj, new object[] { rootOffset.ChildrensPtr });
                if (mainChildrenObj is not IntPtr[] mainChildren)
                {
                    return;
                }

                // FAST PATH: fixed index chain root.children[76].children[13] = ritual window.
                // Cheap but brittle to UI patches; if the chain is structurally broken we fall back
                // to the signature BFS (TryScanRitualWindowThrottled) below.
                var ritualWindowAddr = IntPtr.Zero;
                var fastChainValid = false;
                if (mainChildren.Length > 76 &&
                    this.readUiOffsetMethod.Invoke(this.handleObj, new object[] { mainChildren[76] }) is UiElementBaseOffset child76Offset &&
                    this.readStdVectorMethod.Invoke(this.handleObj, new object[] { child76Offset.ChildrensPtr }) is IntPtr[] child76Children &&
                    child76Children.Length > 13)
                {
                    ritualWindowAddr = child76Children[13];
                    fastChainValid = true;
                }

                var haveWindow = false;
                var ritualWindowOpen = false;
                UiElementBaseOffset ritualWindowOffset = default;

                if (!this.Settings.ForceBfsFallback && fastChainValid &&
                    this.readUiOffsetMethod.Invoke(this.handleObj, new object[] { ritualWindowAddr }) is UiElementBaseOffset fastOffset)
                {
                    ritualWindowOffset = fastOffset;
                    haveWindow = true;
                    ritualWindowOpen = UiElementBaseFuncs.IsVisibleChecker(fastOffset.Flags);
                }
                else if (this.TryScanRitualWindowThrottled(gameUiAddr, out var scanAddr) &&
                         this.readUiOffsetMethod.Invoke(this.handleObj, new object[] { scanAddr }) is UiElementBaseOffset scanOffset)
                {
                    // Fast chain broke (post-patch); the BFS located the reward grid by signature text.
                    ritualWindowOffset = scanOffset;
                    haveWindow = true;
                    ritualWindowOpen = true; // a grid with reward items was found -> shop is open
                }

                if (!ritualWindowOpen && this.wasRitualWindowOpen)
                {
                    this.alertedItemsThisSession.Clear();
                    this.sessionStablePriceChaos.Clear();
                    this.cachedPriceLabels.Clear();
                    this.nextPriceRecomputeUtc = DateTime.MinValue;
                }

                this.wasRitualWindowOpen = ritualWindowOpen;

                if (haveWindow && ritualWindowOpen && this.Settings.ShowOverlay)
                    this.DrawRitualPrices(ritualWindowOffset);
            }
            catch { }

            if (this.Settings.DebugMode)
            {
                this.UpdateDebugInventories();
                this.DrawDebugOverlay();
            }
            }
            finally
            {
                this.FlushPendingSound();
            }
        }

        private void DrawRitualPrices(UiElementBaseOffset ritualWindowOffset)
        {
            var fgDraw = ImGui.GetForegroundDrawList();
            var font = ImGui.GetFont();
            const uint shadowColor = 0xCC000000;

            var now = DateTime.UtcNow;
            var recomputeIntervalMs = Core.IsSettingsMenuOpen ? 200 : 120;
            if (now < this.nextPriceRecomputeUtc && this.cachedPriceLabels.Count > 0)
            {
                this.DrawPriceLabels(fgDraw, font, shadowColor, this.cachedPriceLabels);
                return;
            }

            this.nextPriceRecomputeUtc = now.AddMilliseconds(recomputeIntervalMs);

            var itemUiElementsObj = this.readStdVectorMethod!.Invoke(this.handleObj, new object[] { ritualWindowOffset.ChildrensPtr });
            if (itemUiElementsObj is not IntPtr[] itemUiElements)
            {
                return;
            }

            var priceLabels = this.cachedPriceLabels;
            priceLabels.Clear();

            this.uiParentsObj ??= PluginUiElementReflection.CreateParents();
            var parentsObj = this.uiParentsObj;

            for (var i = 0; i < itemUiElements.Length; i++)
            {
                var itemUiOffsetObj = this.readUiOffsetMethod!.Invoke(this.handleObj, new object[] { itemUiElements[i] });
                if (itemUiOffsetObj is not UiElementBaseOffset itemUiOffset)
                {
                    continue;
                }

                if (!UiElementBaseFuncs.IsVisibleChecker(itemUiOffset.Flags)) continue;

                object? uiElementObj = parentsObj == null
                    ? null
                    : PluginUiElementReflection.CreateUiElement(itemUiElements[i], parentsObj);

                var ptrObj = this.readIntPtrMethod!.Invoke(this.handleObj, new object[] { itemUiElements[i] + 0x4F8 });
                var ptr = ptrObj is IntPtr intPtr ? intPtr : IntPtr.Zero;
                var itemName = $"Item {i}";
                var internalNameOnly = string.Empty;
                var fullItemPath = string.Empty;
                var scoutText = string.Empty;
                var baseItemName = string.Empty;
                var artBasename = string.Empty;
                var itemRarity = Rarity.Normal;
                List<string>? memoryMods = null;

                if (ptr != IntPtr.Zero)
                {
                    var itemInstance = ItemModHelper.ReadFreshItem(ptr);
                    if (itemInstance != null && !string.IsNullOrEmpty(itemInstance.Path))
                    {
                        fullItemPath = itemInstance.Path;
                        var parts = itemInstance.Path.Split('/');
                        if (parts.Length > 0)
                        {
                            internalNameOnly = parts[^1];
                            itemName = this.GetPrettyName(internalNameOnly, out _);
                        }

                        if (itemInstance.TryGetComponent<Mods>(out var rarityMods))
                        {
                            itemRarity = rarityMods.Rarity;
                        }

                        // Prefer the item's rendered base-type name, read straight from the Base
                        // component (GameHelper Core). For non-uniques this IS the correct price key
                        // (e.g. "Greater Orb of Augmentation"). Uniques render only their base type,
                        // so they're resolved by icon art below instead.
                        if (itemInstance.TryGetComponent<Base>(out var baseComponent) &&
                            !string.IsNullOrWhiteSpace(baseComponent.BaseItemName))
                        {
                            baseItemName = baseComponent.BaseItemName.Trim();
                            if (itemRarity != Rarity.Unique)
                            {
                                itemName = baseItemName;
                            }
                        }

                        // Uniques: resolve the real unique name from the item's icon-art basename.
                        // Each unique has its own .dds, and the price index is keyed by that same
                        // basename (poe.ninja/poe2scout IconUrl), so this is unambiguous. (Base type /
                        // metadata id are shared across uniques, so they can't identify the unique.)
                        if (itemRarity == Rarity.Unique &&
                            itemInstance.TryGetComponent<RenderItem>(out var renderItem))
                        {
                            artBasename = ExtractArtBasename(renderItem.ResourcePath);

                            // GGG's .dds basename and the price DB disagree on a leading "The"
                            // (inconsistently, both directions), so try the key with and without it,
                            // and accept either an icon-map hit or a direct price-index hit.
                            foreach (var key in ArtKeyVariants(artBasename))
                            {
                                if (PoeNinjaPriceFetcher.TryResolveDisplayName(key, out var uniqueFromArt) &&
                                    !PoeNinjaPriceFetcher.IsGenericLookupName(uniqueFromArt))
                                {
                                    itemName = uniqueFromArt;
                                    break;
                                }

                                if (PoeNinjaPriceFetcher.HasPriceDataForName(key))
                                {
                                    itemName = key;
                                    break;
                                }
                            }
                        }

                        memoryMods = ItemModHelper.GetModLines(itemInstance);
                        if (memoryMods.Count > 0)
                        {
                            var baseTypeFromPath = this.InferBaseTypeFromMetadataPath(fullItemPath);
                            scoutText = this.BuildScoutText(itemName, baseTypeFromPath);
                        }
                    }
                }

                if (uiElementObj == null) continue;

                var screenPos = (Vector2)PluginUiElementReflection.UiElementPositionProperty!.GetValue(uiElementObj)!;
                var size = (Vector2)PluginUiElementReflection.UiElementSizeProperty!.GetValue(uiElementObj)!;

                var mods = memoryMods;
                itemName = this.ResolveItemDisplayName(itemName, internalNameOnly);
                var priceInfo = PoeNinjaPriceFetcher.GetPrice(itemName, mods, internalNameOnly, fullItemPath, scoutText);

                var diagFontSize = ImGui.GetFontSize() * this.Settings.PriceFontScale * 0.8f;
                if (priceInfo == null)
                {
                    // Only diagnose slots that actually hold an item — empty/stale tiles (e.g. when the
                    // window is closed but its element still reads visible) have no internal name.
                    if (this.Settings.DiagnosePricing && !string.IsNullOrEmpty(internalNameOnly))
                    {
                        priceLabels.Add(new PriceLabelDraw
                        {
                            ValueText = string.Empty,
                            IconFile = string.Empty,
                            DebugText = $"{itemRarity} NO PRICE\nbase:{(string.IsNullOrEmpty(baseItemName) ? "<none>" : baseItemName)}\nart:{(string.IsNullOrEmpty(artBasename) ? "<none>" : artBasename)}\nname:{itemName}\nint:{internalNameOnly}",
                            DebugPos = new Vector2(screenPos.X + 2f, screenPos.Y + 2f),
                            DebugFontSize = diagFontSize,
                        });
                    }

                    continue;
                }

                var priceChaos = this.StabilizeSessionPrice(internalNameOnly, priceInfo.PriceChaos);

                if (this.Settings.MinDisplayExalted > 0f)
                {
                    var (exValue, _) = PoeNinjaPriceFetcher.GetDisplayPrice(
                        new PoeNinjaPrice { PriceChaos = priceChaos }, 1);
                    if (exValue < this.Settings.MinDisplayExalted)
                        continue;
                }

                var (displayValue, displayCurrency) = PoeNinjaPriceFetcher.GetDisplayPrice(
                    new PoeNinjaPrice { PriceChaos = priceChaos },
                    this.Settings.DisplayCurrency);
                var divineValue = priceChaos / Math.Max(PoeNinjaPriceFetcher.GetChaosPerDivine(), 1.0);
                if (this.Settings.PlayValueAlert && divineValue >= this.Settings.AlertMinDivine)
                {
                    var alertKey = string.IsNullOrEmpty(internalNameOnly) ? itemName : internalNameOnly;
                    if (this.alertedItemsThisSession.Add(alertKey))
                        this.pendingSoundPlayback = this.Settings.AlertSound;
                }

                var fontSize = ImGui.GetFontSize() * this.Settings.PriceFontScale;
                var valueText = displayCurrency switch
                {
                    "divine" => displayValue.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                    "chaos" => displayValue.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                    _ => displayValue.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                };
                var textWidth = ImGui.CalcTextSize(valueText).X * this.Settings.PriceFontScale;
                var iconFile = displayCurrency switch
                {
                    "divine" => "divine.png",
                    "chaos" => "chaos.png",
                    _ => "exalted.png",
                };
                var iconWidth = fontSize;
                if (this.currencyIcons.TryGet(iconFile, out _, out var iconW, out var iconH) && iconH > 0)
                    iconWidth = fontSize * iconW / (float)iconH;

                var pos = new Vector2(
                    screenPos.X + this.Settings.PriceOffsetX,
                    screenPos.Y + size.Y - fontSize + this.Settings.PriceOffsetY);

                priceLabels.Add(new PriceLabelDraw
                {
                    Pos = pos,
                    IconFile = iconFile,
                    IconWidth = iconWidth,
                    IconHeight = fontSize,
                    TextWidth = textWidth,
                    ValueText = valueText,
                    TextColor = ImGui.ColorConvertFloat4ToU32(this.Settings.PriceTextColor),
                    FontSize = fontSize,
                    DebugText = this.Settings.DiagnosePricing
                        ? $"{itemRarity} OK\nbase:{(string.IsNullOrEmpty(baseItemName) ? "<none>" : baseItemName)}\nart:{(string.IsNullOrEmpty(artBasename) ? "<none>" : artBasename)}\nname:{itemName}\nint:{internalNameOnly}"
                        : null,
                    DebugPos = new Vector2(screenPos.X + 2f, screenPos.Y + 2f),
                    DebugFontSize = diagFontSize,
                });
            }

            this.DrawPriceLabels(fgDraw, font, shadowColor, priceLabels);
        }

        private void DrawPriceLabels(ImDrawListPtr fgDraw, ImFontPtr font, uint shadowColor, List<PriceLabelDraw> priceLabels)
        {
            foreach (var label in priceLabels)
            {
                if (!string.IsNullOrEmpty(label.ValueText))
                {
                    var textPos = label.Pos;
                    fgDraw.AddText(font, label.FontSize, textPos + new Vector2(1f, 1f), shadowColor, label.ValueText);
                    fgDraw.AddText(font, label.FontSize, textPos, label.TextColor, label.ValueText);

                    if (this.currencyIcons.TryGet(label.IconFile, out var texPtr, out _, out _))
                    {
                        var iconPos = label.Pos + new Vector2(label.TextWidth + 3f, 0f);
                        var iconMax = iconPos + new Vector2(label.IconWidth, label.IconHeight);
                        fgDraw.AddImage(texPtr, iconPos, iconMax);
                    }
                }

                if (!string.IsNullOrEmpty(label.DebugText))
                {
                    // 0=no-price (red), otherwise ok (green). Distinguished by the "NO PRICE" marker.
                    var diagColor = label.DebugText.Contains("NO PRICE", StringComparison.Ordinal)
                        ? 0xFF4040FFu
                        : 0xFF40FF40u;
                    fgDraw.AddText(font, label.DebugFontSize, label.DebugPos + new Vector2(1f, 1f), shadowColor, label.DebugText);
                    fgDraw.AddText(font, label.DebugFontSize, label.DebugPos, diagColor, label.DebugText);
                }
            }
        }

        private void FlushPendingSound()
        {
            if (!this.pendingSoundPlayback.HasValue) return;

            var soundType = this.pendingSoundPlayback.Value;
            this.pendingSoundPlayback = null;
            AlertSoundPlayer.Play(soundType);
        }

        // FALLBACK window finder, used only when the fast index chain is broken (post-patch) or when
        // ForceBfsFallback is set. The expensive BFS runs ONCE; the found grid address is then reused
        // every frame for as long as it keeps validating (no errors, still visible, still holds items).
        // Only when the cached address goes stale do we re-walk the tree (throttled).
        private bool TryScanRitualWindowThrottled(IntPtr gameUiRoot, out IntPtr gridAddr)
        {
            if (this.scannedGridAddr != IntPtr.Zero && this.IsValidRewardGrid(this.scannedGridAddr))
            {
                gridAddr = this.scannedGridAddr;
                return true;
            }

            // Cached path is gone/invalid (window closed, UI rebuilt, or a read failed): re-discover,
            // throttled so a closed-window poll doesn't re-walk the tree every frame.
            this.scannedGridAddr = IntPtr.Zero;
            var now = DateTime.UtcNow;
            if (now >= this.nextScanUtc)
            {
                this.nextScanUtc = now.AddMilliseconds(750);
                this.scannedGridAddr = this.FindRitualRewardGrid(gameUiRoot);
            }

            gridAddr = this.scannedGridAddr;
            return gridAddr != IntPtr.Zero;
        }

        // Cheap per-frame revalidation of a cached grid address: it must still read as a visible
        // UiElement that holds at least one item-slot tile (entity at +0x4F8). Any read failure => invalid.
        private bool IsValidRewardGrid(IntPtr gridAddr)
        {
            try
            {
                if (this.readUiOffsetMethod!.Invoke(this.handleObj, new object[] { gridAddr }) is not UiElementBaseOffset off ||
                    !UiElementBaseFuncs.IsVisibleChecker(off.Flags) ||
                    this.readStdVectorMethod!.Invoke(this.handleObj, new object[] { off.ChildrensPtr }) is not IntPtr[] tiles ||
                    tiles.Length is < 1 or > 16)
                {
                    return false;
                }

                foreach (var t in tiles)
                {
                    if (this.readIntPtrMethod!.Invoke(this.handleObj, new object[] { t + 0x4F8 }) is IntPtr p && p != IntPtr.Zero)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        // BFS the visible UI tree for the ritual-shop signature text, then walk up to the reward grid
        // (the container whose children are item-slot tiles). Mirrors POE2Radar's ReadRitualRewards.
        private IntPtr FindRitualRewardGrid(IntPtr gameUiRoot)
        {
            if (gameUiRoot == IntPtr.Zero || this.readUiOffsetMethod == null || this.readStdVectorMethod == null)
            {
                return IntPtr.Zero;
            }

            var queue = new Queue<IntPtr>();
            var visited = new HashSet<IntPtr>();
            queue.Enqueue(gameUiRoot);
            var sigEl = IntPtr.Zero;

            while (queue.Count > 0 && visited.Count < 20000)
            {
                var el = queue.Dequeue();
                if (el == IntPtr.Zero || !visited.Add(el)) continue;
                if (this.readUiOffsetMethod.Invoke(this.handleObj, new object[] { el }) is not UiElementBaseOffset off) continue;
                if (el != gameUiRoot && !UiElementBaseFuncs.IsVisibleChecker(off.Flags)) continue; // prune invisible subtrees

                if (this.readStdVectorMethod.Invoke(this.handleObj, new object[] { off.ChildrensPtr }) is IntPtr[] kids)
                {
                    foreach (var k in kids) queue.Enqueue(k);
                }

                if (sigEl == IntPtr.Zero && this.MatchesRitualSignature(el))
                {
                    sigEl = el;
                }
            }

            if (sigEl == IntPtr.Zero) return IntPtr.Zero;

            // Walk up from the signature element; at each ancestor look for the reward grid.
            var cur = sigEl;
            for (var up = 0; up < 8; up++)
            {
                var grid = this.FindRewardGridChild(cur);
                if (grid != IntPtr.Zero) return grid;
                if (this.readUiOffsetMethod.Invoke(this.handleObj, new object[] { cur }) is not UiElementBaseOffset o ||
                    o.ParentPtr == IntPtr.Zero)
                {
                    break;
                }

                cur = o.ParentPtr;
            }

            return IntPtr.Zero;
        }

        private bool MatchesRitualSignature(IntPtr element)
        {
            var text = this.ReadUiElementText(element);
            if (text.Length < 6) return false;
            foreach (var sig in RitualSignatureTexts)
            {
                if (text.Contains(sig, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        // Among a parent's direct children, the one whose own children are mostly item-slot tiles
        // (item entity at +0x4F8). Returns the best (>=2 tiles) or zero. Excludes the flask bar by
        // virtue of being reached from the shop-signature ancestor, not the HUD.
        private IntPtr FindRewardGridChild(IntPtr parent)
        {
            if (this.readUiOffsetMethod!.Invoke(this.handleObj, new object[] { parent }) is not UiElementBaseOffset poff ||
                this.readStdVectorMethod!.Invoke(this.handleObj, new object[] { poff.ChildrensPtr }) is not IntPtr[] children)
            {
                return IntPtr.Zero;
            }

            var best = IntPtr.Zero;
            var bestItems = 0;
            foreach (var c in children)
            {
                if (this.readUiOffsetMethod.Invoke(this.handleObj, new object[] { c }) is not UiElementBaseOffset coff ||
                    this.readStdVectorMethod.Invoke(this.handleObj, new object[] { coff.ChildrensPtr }) is not IntPtr[] tiles ||
                    tiles.Length is < 1 or > 16)
                {
                    continue;
                }

                var items = 0;
                foreach (var t in tiles)
                {
                    if (this.readIntPtrMethod!.Invoke(this.handleObj, new object[] { t + 0x4F8 }) is IntPtr p && p != IntPtr.Zero)
                    {
                        items++;
                    }
                }

                if (items >= 2 && items > bestItems && items * 2 >= tiles.Length)
                {
                    best = c;
                    bestItems = items;
                }
            }

            return best;
        }

        private string ReadUiElementText(IntPtr element)
        {
            try
            {
                var ws = this.readStdWStringStructMethod!.Invoke(this.handleObj, new object[] { element + UiElementTextOffset });
                if (ws == null) return string.Empty;
                return this.readStdWStringMethod!.Invoke(this.handleObj, new object[] { ws }) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private string BuildScoutText(string itemName, string? baseType)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return string.Empty;
            if (string.IsNullOrWhiteSpace(baseType)) return itemName.Trim();
            return $"{itemName.Trim()} {baseType.Trim()}";
        }

        // Keep the highest price seen this session for a given item so the label doesn't flicker
        // downward as the source data is re-fetched. Cleared when the ritual window closes.
        private double StabilizeSessionPrice(string internalNameOnly, double priceChaos)
        {
            if (string.IsNullOrWhiteSpace(internalNameOnly) || priceChaos <= 0)
            {
                return priceChaos;
            }

            if (this.sessionStablePriceChaos.TryGetValue(internalNameOnly, out var stable) && priceChaos < stable)
            {
                return stable;
            }

            this.sessionStablePriceChaos[internalNameOnly] = priceChaos;
            return priceChaos;
        }

        // "Art/2DItems/.../Uniques/Deidbell.dds" -> "Deidbell" (last path segment, no extension).
        // Matches the price index's IconUrl basename key.
        private static string ExtractArtBasename(string? artPath)
        {
            if (string.IsNullOrWhiteSpace(artPath)) return string.Empty;
            var slash = artPath.LastIndexOfAny(new[] { '/', '\\' });
            var file = slash >= 0 && slash < artPath.Length - 1 ? artPath[(slash + 1)..] : artPath;
            var dot = file.LastIndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }

        // GGG art basenames and the price DB's keys are inconsistent about a leading "The"
        // (e.g. art "TheEmptyRoar" vs "DarkDefiler"), so yield the key both with and without it.
        private static IEnumerable<string> ArtKeyVariants(string artBasename)
        {
            if (string.IsNullOrWhiteSpace(artBasename)) yield break;
            yield return artBasename;
            if (artBasename.StartsWith("The", StringComparison.OrdinalIgnoreCase) && artBasename.Length > 3)
                yield return artBasename[3..];
            else
                yield return "The" + artBasename;
        }

        private string InferBaseTypeFromMetadataPath(string metadataPath)
        {
            if (string.IsNullOrWhiteSpace(metadataPath))
            {
                return string.Empty;
            }

            var parts = metadataPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return string.Empty;
            }

            var parent = parts[^2];
            var spaced = Regex.Replace(parent, "([a-z])([A-Z])", "$1 $2");
            spaced = Regex.Replace(spaced, "([A-Z]+)([A-Z][a-z])", "$1 $2");
            return spaced.Trim();
        }

        private void EnsureNameCache()
        {
            if (this.customNamesCache != null) return;

            var dictionaryPath = Path.Combine(this.DllDirectory, "item_names.json");
            if (File.Exists(dictionaryPath))
            {
                try
                {
                    this.customNamesCache = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(dictionaryPath))
                        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    return;
                }
                catch { }
            }

            this.customNamesCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static bool TrySplitRuneforgeSuffix(string internalName, out string baseInternalName, out string suffix)
        {
            baseInternalName = internalName;
            suffix = string.Empty;

            if (internalName.EndsWith("Runeforged", StringComparison.OrdinalIgnoreCase))
            {
                baseInternalName = internalName[..^"Runeforged".Length];
                suffix = "Runeforged";
                return true;
            }

            if (internalName.EndsWith("Runemastered", StringComparison.OrdinalIgnoreCase))
            {
                baseInternalName = internalName[..^"Runemastered".Length];
                suffix = "Runemastered";
                return true;
            }

            if (internalName.EndsWith("reforged", StringComparison.OrdinalIgnoreCase))
            {
                baseInternalName = internalName[..^"reforged".Length];
                suffix = "Runeforged";
                return true;
            }

            return false;
        }

        private string GetPrettyName(string internalName, out bool isMapped)
        {
            isMapped = false;
            this.EnsureNameCache();
            TrySplitRuneforgeSuffix(internalName, out var baseInternalName, out var suffix);

            if (this.customNamesCache!.TryGetValue(baseInternalName, out var pretty))
            {
                isMapped = true;
                return string.IsNullOrEmpty(suffix) ? pretty : $"{pretty} {suffix}";
            }

            if (PoeNinjaPriceFetcher.TryResolveDisplayName(internalName, out var scoutName))
            {
                isMapped = true;
                return scoutName;
            }

            var clean = System.Text.RegularExpressions.Regex.Replace(internalName, "([A-Z])", " $1").Trim();
            clean = clean.Replace("Four ", string.Empty, StringComparison.Ordinal);
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\d+", string.Empty).Trim();
            if (PoeNinjaPriceFetcher.IsGenericLookupName(clean))
                return internalName;

            return clean;
        }

        private string ResolveItemDisplayName(string currentName, string internalNameOnly)
        {
            if (!string.IsNullOrWhiteSpace(currentName) &&
                !currentName.StartsWith("Item ", StringComparison.Ordinal) &&
                !PoeNinjaPriceFetcher.IsGenericLookupName(currentName))
            {
                return currentName;
            }

            if (!string.IsNullOrEmpty(internalNameOnly))
            {
                var mapped = this.GetPrettyName(internalNameOnly, out _);
                if (!string.IsNullOrWhiteSpace(mapped))
                    return mapped;
            }

            return currentName;
        }

        public override void OnDisable()
        {
            this.wasRitualWindowOpen = false;
            this.handleObj = null;
            this.uiParentsObj = null;
            this.cachedPriceLabels.Clear();
            this.alertedItemsThisSession.Clear();
        }

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingPathname))
            {
                try
                {
                    var content = File.ReadAllText(this.SettingPathname);
                    this.Settings = JsonConvert.DeserializeObject<RitualHelperSettings>(content) ?? new RitualHelperSettings();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RitualHelper] Failed to load settings: {ex.Message}");
                    this.Settings = new RitualHelperSettings();
                }
            }

            this.NormalizePriceSourceSetting();
            LeagueProvider.EnsureLoaded();
            PoeNinjaPriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League ?? string.Empty, this.Settings.RefreshIntervalMin);
            PoeNinjaPriceFetcher.Initialize(this.DllDirectory);
            this.currencyIcons.Initialize(this.DllDirectory);
            this.iconsReloadPending = true;
        }

        public override void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname) ?? string.Empty);
                File.WriteAllText(this.SettingPathname, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RitualHelper] Failed to save settings: {ex.Message}");
            }
        }

        private void UpdateDebugInventories()
        {
            try
            {
                var serverData = Core.States.InGameStateObject.CurrentAreaInstance.ServerDataObject;
                if (serverData == null || serverData.Address == IntPtr.Zero) return;

                var propInfo = typeof(ServerData).GetProperty("PlayerInventories", BindingFlags.Instance | BindingFlags.NonPublic);
                if (propInfo == null) return;

                var playerInventories = propInfo.GetValue(serverData) as Dictionary<InventoryName, IntPtr>;
                if (playerInventories == null) return;

                var handleProp = typeof(GameProcess).GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic);
                if (handleProp == null) return;
                var handleObj = handleProp.GetValue(Core.Process);
                if (handleObj == null) return;
                var handleType = handleObj.GetType();

                var readInvMethod = handleType.GetMethod("ReadMemory", BindingFlags.Instance | BindingFlags.NonPublic)?.MakeGenericMethod(typeof(InventoryStruct));
                var readVectorMethod = handleType.GetMethod("ReadStdVector", BindingFlags.Instance | BindingFlags.NonPublic)?.MakeGenericMethod(typeof(IntPtr));
                var readInvItemMethod = handleType.GetMethod("ReadMemory", BindingFlags.Instance | BindingFlags.NonPublic)?.MakeGenericMethod(typeof(InventoryItemStruct));
                if (readInvMethod == null || readVectorMethod == null || readInvItemMethod == null) return;

                this.debugInventories.Clear();
                foreach (var invKvp in playerInventories)
                {
                    if (invKvp.Value == IntPtr.Zero) continue;
                    var dbgInvObj = readInvMethod.Invoke(handleObj, new object[] { invKvp.Value });
                    if (dbgInvObj == null) continue;
                    var dbgInv = (InventoryStruct)dbgInvObj;
                    var dbgPtrArrayObj = readVectorMethod.Invoke(handleObj, new object[] { dbgInv.ItemList });
                    if (dbgPtrArrayObj is not IntPtr[] dbgArray || dbgArray.Length == 0) continue;

                    var itemNames = new List<string>();
                    foreach (var dbgInvItemPtr in dbgArray)
                    {
                        if (dbgInvItemPtr == IntPtr.Zero) continue;
                        var dbgInvItemObj = readInvItemMethod.Invoke(handleObj, new object[] { dbgInvItemPtr });
                        if (dbgInvItemObj == null) continue;
                        var dbgInvItem = (InventoryItemStruct)dbgInvItemObj;
                        if (dbgInvItem.Item == IntPtr.Zero) continue;
                        var itemInstance = Activator.CreateInstance(typeof(Item), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { dbgInvItem.Item }, null) as Item;
                        if (itemInstance != null && !string.IsNullOrEmpty(itemInstance.Path))
                            itemNames.Add(GetReadableName(itemInstance.Path));
                    }

                    if (itemNames.Count > 0)
                        this.debugInventories[$"{invKvp.Key} ({(int)invKvp.Key})"] = itemNames;
                }
            }
            catch { }
        }

        private void DrawDebugOverlay()
        {
            ImGui.SetNextWindowSize(new Vector2(400, 400), ImGuiCond.FirstUseEver);
            var flags = ImGuiWindowFlags.NoFocusOnAppearing;
            if (ImGui.Begin($"Ritual Debug Inventories##RitualHelperDebug", ref this.Settings.DebugMode, flags))
            {
                ImGui.TextWrapped(
                    "Showing all active ServerData PlayerInventories with >0 items.");

                if (ImGui.Button("Dump to inventory_dump.txt"))
                {
                    try
                    {
                        var dumpPath = Path.Join(this.DllDirectory, "inventory_dump.txt");
                        var lines = new List<string>();
                        foreach (var kvp in this.debugInventories)
                        {
                            lines.Add($"[{kvp.Key}] - {kvp.Value.Count} items:");
                            foreach (var itemName in kvp.Value)
                                lines.Add($"    - {itemName}");
                            lines.Add(string.Empty);
                        }

                        File.WriteAllLines(dumpPath, lines);
                    }
                    catch { }
                }

                ImGui.Separator();
                foreach (var kvp in this.debugInventories)
                {
                    if (ImGui.TreeNode($"{kvp.Key} ({kvp.Value.Count} items)"))
                    {
                        foreach (var itemName in kvp.Value)
                            ImGui.Text($"  - {itemName}");
                        ImGui.TreePop();
                    }
                }
            }

            ImGui.End();
        }

        private static string GetReadableName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "Unknown";
            var lastSlash = path.LastIndexOf('/');
            var name = lastSlash >= 0 && lastSlash < path.Length - 1 ? path[(lastSlash + 1)..] : path;
            var result = new System.Text.StringBuilder();
            for (var i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) result.Append(' ');
                result.Append(name[i]);
            }

            return result.ToString();
        }

        private struct PriceLabelDraw
        {
            public Vector2 Pos;
            public string IconFile;
            public float IconWidth;
            public float IconHeight;
            public string ValueText;
            public uint TextColor;
            public float FontSize;
            public float TextWidth;

            // Optional pricing-diagnostic overlay (set only when DiagnosePricing is on).
            public string? DebugText;
            public Vector2 DebugPos;
            public float DebugFontSize;
        }
    }
}
