namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using GameHelper;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameOffsets.Objects.UiElement;
    using ImGuiNET;
    using Newtonsoft.Json;

    // Monolith reward window. For each nearby runeshape monolith (the persistent
    // Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter device, which Radar also tracks),
    // resolves its anchor rune + hole position off the live RuneStation, filters the offline recipe
    // catalog (expedition2_recipes.json, built from game_dat/ by build_recipes_json.py) to the recipes
    // that monolith can roll, and prices each reward via poe.ninja.
    //
    // device→station path (no heap scan, persists out of the network bubble — docs/re-findings.md §6.10):
    //   device → StateMachine component (SM) → listener vector at SM+0x20 (begin/end)
    //          → for each node ptr N: station = *(N) − 0xA0, verified by *(station+0x10) == device addr
    //            (the sub-offset was 0x98 before the 2026-06-25 patch — see StationListenerSubOffset)
    // anchor (docs §6.8/6.10):
    //   station+0x28 = rune row ptr; index = (rowPtr − runeTableBase)/0x68 (runeTableBase = *(*(station+0x30+0x28)))
    //   (rune stride was 0x6c before the 2026-06-25 patch — see ExpeditionRuneStride)
    //   station+0x3c = anchor hole index (0-based)
    public sealed partial class RunecraftHelperCore
    {
        private const int StationOwnerOffset = 0x10;      // → device entity
        private const int StationAnchorRefOffset = 0x28;  // → Expedition2Runes row ptr
        private const int StationAnchorHolderOffset = 0x30;
        private const int StationHoleCountOffset = 0x38;   // → N (recipe hole count)
        private const int StationAnchorPosOffset = 0x3c;   // → anchor hole index (0..5)
        // → selected/locked recipe ROW pointer (in-memory Expedition2Recipes row). On a sealed
        // (rerolled) monolith this is the locked recipe; on a choosable one it tracks the current
        // highlight. Set by the station net-packet deserializer (Ghidra FUN_1417bb930, 2026-06-25:
        // opcode 0 full-state and opcode 2 selection both write +0x60). The row's language-independent
        // Id (UTF-16 at row+0x00, e.g. "4SlotArchaicRuneofControl1") matches the offline catalog id,
        // so we resolve it by Id and never need the per-area recipe table base.
        private const int StationSelectedRecipeOffset = 0x60;
        // Server-authoritative recipe-mode enum (RuneStation_DeserializeNetState op0 [0xb], Ghidra
        // 0x1417bb9b0): real Expedition dig monolith=1, unique=3 (also triggers the run-state buffs),
        // and the current league's standalone "additional" monolith=0. Used to drop the foreigner from
        // the route (see EnumerateMonoliths). NOT reward-based — it's the game's own monolith-type field.
        private const int StationRecipeModeOffset = 0x58;
        // → listener registered by the open Runeshape Combinations panel (an in-module vtable ptr). Set
        // only while THIS station's panel is open, null otherwise — the direct "which monolith is the
        // player browsing" signal (distance/SM-range don't discriminate; several monoliths cluster and
        // any can be opened from one spot). Verified 2026-06-25: open station +0xB8 = module ptr, the two
        // closed neighbours = 0. Used to pin the locked-recipe panel highlight to the right monolith.
        private const int StationPanelOpenOffset = 0xB8;
        private const int StateMachineListenerVecOffset = 0x20;
        // node[0] = station + 0xA0. Was 0x98 until the 2026-06-25 patch inserted an 8-byte field into
        // RuneStation between +0x48 and the listener sub-object (all earlier fields — owner +0x10,
        // anchor +0x28, holder +0x30, sockets +0x38, anchor-pos +0x3c — kept their offsets). Re-verified
        // live: sub − station == 0xA0, station+0x00 = in-module RuneStation vtable, station+0x10 = device.
        private const int StationListenerSubOffset = 0xA0;
        // Expedition2Runes row stride. Was 0x6c (108) until the 2026-06-25 patch shrank the .dat row to
        // 104 bytes (dump logs "Expedition2Runes ROWLEN 104!=schema 108"); the in-memory rune table
        // mirrors the .dat layout, so the live stride is now 0x68. Re-verified live: (rowPtr−tableBase)
        // = 0x9C0 = 24 × 0x68 exactly (0x6c would be misaligned).
        private const int ExpeditionRuneStride = 0x68;
        private const int RuneCount = 34;                  // Expedition2Runes rows 0..33

        // All 34 rune names, index-aligned with Expedition2Runes rows (fallback for runeNames, and the
        // source list for the glow-rune settings table). See obsidian poe2/expedition-runes.
        private static readonly string[] AllRuneNames =
        {
            "Fire", "Cold", "Lightning", "Tempest", "Momentum", "Bloodletting", "Stone", "Adaptive",
            "Arcane", "Toxic", "Electrocuting", "Protective", "Cyclonic", "Vision", "Tidal", "Rebirth",
            "Prismatic", "Gasp", "Moon", "Celestial", "Opulent", "Rage", "Wisdom", "Sky", "Earth", "Life",
            "Bond", "Ward", "Soul", "Death", "Oath", "Time", "Power", "Bait",
        };

        // Short monster-buff description per rune (from Mods.dat ExpeditionMonsterModRune* → Stats; see
        // obsidian poe2/expedition-runes). "special (server-side)" = effect is a monster skill not exposed
        // in the client .dat. Display-only.
        private static readonly Dictionary<string, string> RuneEffects = new(StringComparer.Ordinal)
        {
            ["Fire"] = "Monster damage gains as Fire",
            ["Cold"] = "Monster damage gains as Cold",
            ["Lightning"] = "Monster damage gains as Lightning",
            ["Tempest"] = "Hits Shock & Chill; monsters can't be Shocked",
            ["Momentum"] = "+Move speed, unslowable, slows you",
            ["Bloodletting"] = "Life leech + Corrupted Blood on hit",
            ["Stone"] = "Armour from Strength + huge Stun threshold",
            ["Adaptive"] = "Adapts resistance to the element that hits it",
            ["Arcane"] = "Part of max Life as Energy Shield",
            ["Toxic"] = "All damage can Poison + poison chance",
            ["Electrocuting"] = "Lightning damage + Electrocute",
            ["Protective"] = "special (server-side)",
            ["Cyclonic"] = "special (server-side)",
            ["Vision"] = "Reflects Curses, Shocks, Chills",
            ["Tidal"] = "special (server-side)",
            ["Rebirth"] = "Revive mechanic (server-side)",
            ["Prismatic"] = "Resist all elements + random-element damage",
            ["Gasp"] = "Volcanic: fire damage + Ignite",
            ["Moon"] = "special (server-side)",
            ["Celestial"] = "special (server-side)",
            ["Opulent"] = "Opulent reward mod (more loot)",
            ["Rage"] = "special (server-side)",
            ["Wisdom"] = "+Experience from kills",
            ["Sky"] = "special (server-side)",
            ["Earth"] = "special (server-side)",
            ["Life"] = "+Tankiness + Life regen",
            ["Bond"] = "On death: pass rare mod + heal a nearby rare",
            ["Ward"] = "Life as Ward; stronger while warded",
            ["Soul"] = "Union of Souls (shared)",
            ["Death"] = "special (server-side)",
            ["Oath"] = "special (server-side)",
            ["Time"] = "special (server-side)",
            ["Power"] = "special (server-side)",
            ["Bait"] = "filler (no monster buff)",
        };

        // Watch-table rows the user can toggle off but not delete (seeded on first use).
        private static readonly string[] DefaultGlowRuneNames = { "Time", "Death", "Bond", "Power", "Opulent" };

        // Rune display name for an Expedition2Runes index (json map first, static fallback).
        private string? RuneNameByIndex(int idx) =>
            this.runeNames.TryGetValue(idx, out var nm) ? nm
            : (idx >= 0 && idx < AllRuneNames.Length ? AllRuneNames[idx] : null);

        // Ensure the default glow-rune rows exist (add any missing default; never resets existing show/weight,
        // so a default that was toggled off stays off). Guarantees defaults can't be lost from the table.
        private void EnsureGlowRuneDefaults()
        {
            foreach (var name in DefaultGlowRuneNames)
                if (!this.Settings.GlowRunes.Exists(g => string.Equals(g.Rune, name, StringComparison.Ordinal)))
                    this.Settings.GlowRunes.Add(new GlowRuneEntry { Rune = name, Weight = 100f, Show = true });
        }

        private List<MonoRecipe> monolithRecipes = new();
        private readonly List<double> monoPriceScratch = new(); // per-reward totals → row-total colour median
        private Dictionary<int, string> runeNames = new();
        // (anchorRune, pos1based, size) → min area level at which that partial size is offered.
        // Built from Expedition2RunesWeights; gates which size<N recipes a monolith can roll.
        private Dictionary<long, int> partialMinLevel = new();
        private bool monolithLoadTried;

        private List<MonoView> monolithViews = new();
        private DateTime nextMonolithScanUtc = DateTime.MinValue;

        // ── recipe data (offline) ────────────────────────────────────────────
        private bool LoadMonolithData()
        {
            if (this.monolithRecipes.Count > 0) return true;
            if (this.monolithLoadTried) return false;
            this.monolithLoadTried = true;
            try
            {
                var path = Path.Join(this.DllDirectory, "expedition2_recipes.json");
                if (!File.Exists(path)) return false;
                var file = JsonConvert.DeserializeObject<MonoFile>(File.ReadAllText(path));
                if (file?.recipes == null) return false;
                this.monolithRecipes = file.recipes;
                this.runeNames = new Dictionary<int, string>();
                if (file.runes != null)
                    foreach (var kv in file.runes)
                        if (int.TryParse(kv.Key, out var k)) this.runeNames[k] = kv.Value;

                this.partialMinLevel = new Dictionary<long, int>();
                if (file.runeWeights != null)
                    foreach (var w in file.runeWeights)
                    {
                        long key = PartialKey(w.rune, w.pos, w.size);
                        if (!this.partialMinLevel.TryGetValue(key, out var cur) || w.minLevel < cur)
                            this.partialMinLevel[key] = w.minLevel;
                    }
                return this.monolithRecipes.Count > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RunecraftHelper] recipe json load failed: {ex.Message}");
                return false;
            }
        }

        // ── window ───────────────────────────────────────────────────────────
        // Entry point: loads the catalog, rescans nearby monoliths on a timer, then draws whichever of
        // the two windows are enabled — the rewards list (ShowMonolithRewards) and/or the per-monolith
        // debug dump (ShowWindow, repurposed). Both read the same scanned this.monolithViews.
        private void DrawMonolithRewards(bool combinationsPanelOpen)
        {
            if (!this.LoadMonolithData()) return;

            // Resolve reward names to the client's language from the in-memory BaseItemTypes table
            // (once per session). Must run before EnumerateMonoliths so AddCandidate can localize.
            this.BuildMetaToLocalNameIfNeeded();

            // Seed the default watched glow-runes if the table is empty / missing a default.
            this.EnsureGlowRuneDefaults();

            var now = DateTime.UtcNow;
            if (now >= this.nextMonolithScanUtc)
            {
                this.monolithViews = this.EnumerateMonoliths();
                this.nextMonolithScanUtc = now.AddMilliseconds(750);
            }

            // Map value labels: optionally suppressed while the Runeshape Combinations panel is open so
            // they don't clutter the map on top of the panel's own per-recipe overlay.
            bool drawMap = (this.Settings.DrawMonolithValueOnMap || this.Settings.ShowGlowRunes) &&
                !(this.Settings.HideMapValueWhenPanelOpen && combinationsPanelOpen);
            if (drawMap) this.DrawMonolithMapLabels();
            if (this.Settings.ShowMonolithRewards) this.DrawMonolithRewardsWindow();
            if (this.Settings.ShowWindow)
            {
                this.DrawMonolithDebugWindow();
                this.DrawOverlayRowsDebugWindow();
            }
        }

        // Camera rotation of the in-game map, mirrored from Radar.Helper.CameraAngle.
        private const double MapCameraAngle = 38.7 * Math.PI / 180.0;

        // Draw each monolith's best reward value (ex) on the in-game LARGE-map overlay, at the monolith's
        // projected map position — the same spot Radar paints the socket count. Radar can't be modified and
        // its Helper/settings aren't reachable, so the large-map projection (Radar.Helper.DeltaInWorldToMapDelta
        // + UpdateLargeMapDetails) is replicated here. Calibration baselines match Radar's defaults; the
        // MapValue* settings re-align if the user's Radar offsets/zoom differ. Foreground draw list with
        // absolute screen coords (largeMap.Center is screen-space).
        private void DrawMonolithMapLabels()
        {
            if (this.monolithViews.Count == 0) return;

            var gameUi = Core.States.InGameStateObject.GameUi;
            var largeMap = gameUi.LargeMap;
            if (largeMap == null || !largeMap.IsVisible || gameUi.WorldMapPanel.IsVisible) return;

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (area?.Player == null || !area.Player.TryGetComponent<Render>(out var playerRender)) return;
            var trackingPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);
            float trackingHeight = playerRender.TerrainHeight;

            // Diagonal length (UpdateLargeMapDetails): base-resolution diagonal scaled by the map's height.
            var baseRes = UiElementBaseFuncs.BaseResolution;
            double baseDiag = Math.Sqrt(((double)baseRes.X * baseRes.X) + ((double)baseRes.Y * baseRes.Y));
            double diag = baseDiag * largeMap.Size.Y / baseRes.Y;
            if (diag <= 0) return;

            // Helper.Scale for the large map: LargeMapScaleBaseline 0.187812, Radar default multiplier 1.
            float scale = this.Settings.MapValueScaleMultiplier * largeMap.Zoom * 0.187812f;
            if (scale <= 0) return;
            float mapScale = 240f / scale;
            float cos = (float)(diag * Math.Cos(MapCameraAngle) / mapScale);
            float sin = (float)(diag * Math.Sin(MapCameraAngle) / mapScale);

            // largeMapRealCenter: Center+Shift+DefaultShift + calibrated biases (0.6, 0.3) + user offsets.
            var center = largeMap.Center + largeMap.Shift + largeMap.DefaultShift;
            center.X += 0.6f + this.Settings.MapValueXOffset;
            center.Y += 0.3f + this.Settings.MapValueYOffset;

            var dl = ImGui.GetForegroundDrawList();
            var font = ImGui.GetFont();
            float ambient = ImGui.GetFontSize();
            float fontPx = ambient * 1.5f;
            float k = fontPx / ambient;

            // maxBest across visible monoliths — needed by the ColorMode=Relative header tint so the map
            // label colour matches the window header (which compares each monolith against the best on screen).
            double maxBest = 0;
            foreach (var mv in this.monolithViews)
                if (mv.Activated != 5 && mv.Best > maxBest) maxBest = mv.Best;

            // Plate behind the price: match the LootTracker map/hideout bars — the active theme's
            // WindowBg colour with its alpha overridden to 0.55 (LootTracker's SetNextWindowBgAlpha
            // default). Computed from the live style so it follows the theme like those bars do.
            var bgCol = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
            bgCol.W = MonolithMapBgAlpha;
            uint monoBg = ImGui.GetColorU32(bgCol);
            uint glowCol = ImGui.GetColorU32(new Vector4(1f, 0.80f, 0.30f, 1f));   // amber — glow-rune scouting labels

            bool showPrice = this.Settings.DrawMonolithValueOnMap;
            bool showGlow = this.Settings.ShowGlowRunes;

            foreach (var v in this.monolithViews)
            {
                if (!v.HasPos) continue;
                if (v.Activated == 5) continue;   // standalone foreigner already running — its price is moot, hide the label

                bool hasPrice = showPrice && v.Best > 0;
                bool hasGlow = showGlow && v.GlowRuneLabels.Count > 0;
                if (!hasPrice && !hasGlow) continue;

                // DeltaInWorldToMapDelta replicated (Radar.Helper).
                var delta = v.GridPos - trackingPos;
                float deltaZ = (v.TerrainHeight - trackingHeight) / 10.86957f;
                var fpos = new Vector2((delta.X - delta.Y) * cos, (deltaZ - (delta.X + delta.Y)) * sin);
                var screen = center + fpos;

                var pad = new Vector2(3f, 1f);
                float priceTopY = screen.Y + 6f;   // top of the price row

                if (hasPrice)
                {
                    // Same tint as the rewards-window header (shared helper); untinted → white on the map.
                    uint col = this.MonolithValueColor(v.Best, maxBest, out _);
                    var text = $"{v.Best:F0} ex";
                    var ts = ImGui.CalcTextSize(text) * k;
                    var at = new Vector2(screen.X - (ts.X * 0.5f), priceTopY);
                    dl.AddRectFilled(at - pad, at + ts + pad, monoBg, 2f);
                    dl.AddText(font, fontPx, at + new Vector2(1f, 1f), ColorShadow, text);
                    dl.AddText(font, fontPx, at, col, text);
                }

                if (hasGlow)
                {
                    // Watched rune name(s) stacked ABOVE the price row (amber). Multiple lines when a monolith
                    // has several tied-weight watched runes on its glowing sockets.
                    float lineH = fontPx + 3f;
                    float y = priceTopY - lineH;
                    for (int r = v.GlowRuneLabels.Count - 1; r >= 0; r--)
                    {
                        var name = v.GlowRuneLabels[r];
                        var ts = ImGui.CalcTextSize(name) * k;
                        var at = new Vector2(screen.X - (ts.X * 0.5f), y);
                        dl.AddRectFilled(at - pad, at + ts + pad, monoBg, 2f);
                        dl.AddText(font, fontPx, at + new Vector2(1f, 1f), ColorShadow, name);
                        dl.AddText(font, fontPx, at, glowCol, name);
                        y -= lineH;
                    }
                }
            }
        }

        private void DrawMonolithRewardsWindow()
        {
            if (this.monolithViews.Count == 0) return;

            // Auto-resize to content: the window height follows the number of monoliths and grows/shrinks
            // as recipe tables are expanded/collapsed. The constraint keeps width sane and caps height so a
            // long list can't run off-screen (it scrolls past the cap instead).
            ImGui.SetNextWindowSizeConstraints(new Vector2(260, 0), new Vector2(640, 900));
            if (ImGui.Begin("Monolith Rewards", ImGuiWindowFlags.AlwaysAutoResize))
            {
                float min = this.Settings.MonolithRewardsMinExalted;

                // Colour thresholds reuse the recipe-overlay logic (PickColor/ColorMode). In Relative
                // mode the row Total cells compare against the median over every priced reward (like the
                // recipe overlay). The HEADER, however, is coloured relative to the BEST monolith on
                // screen (see PickHeaderColor), not the median: the price distribution is bimodal (a
                // couple of huge monoliths among many cheap ones), so a median baseline drifts into the
                // cheap tail and an 8 ex monolith lights green next to a 387 ex one. Relative-to-max
                // keeps green for the genuine standouts at any distribution.
                bool colorize = this.Settings.ColorMode != RewardColorMode.Off;
                double median = 0, maxBest = 0;
                if (colorize && this.Settings.ColorMode == RewardColorMode.Relative)
                {
                    this.monoPriceScratch.Clear();
                    foreach (var mv in this.monolithViews)
                    {
                        double b = 0;
                        foreach (var c in mv.Candidates)
                            if (c.Priced)
                            {
                                var t = c.UnitEx * c.Count;
                                this.monoPriceScratch.Add(t);
                                if (t > b) b = t;
                            }
                        if (b > maxBest) maxBest = b;
                    }
                    median = MedianOfDoubles(this.monoPriceScratch);
                }

                foreach (var v in this.monolithViews)
                {
                    double best = 0;
                    foreach (var c in v.Candidates)
                        if (c.Priced) best = Math.Max(best, c.UnitEx * c.Count);

                    string hdr;
                    // Diagnostic marker: ▶ flags the monolith whose Combinations panel the plugin
                    // currently detects as open (station+0xB8). This is the gate for the in-panel gold
                    // price-box highlight, so it lets the user confirm detection at a glance.
                    string panelMark = v.PanelOpen ? "▶ " : string.Empty;
                    if (v.IsRerolled && v.Candidates.Count > 0)
                        // Sealed: recipe locked — show the one reward + its value, not "best of N".
                        hdr = $"{panelMark}[locked] {v.Candidates[0].Reward}  ·  {v.Distance:F0}  ·  {best:F0} ex###m{v.EntityId}";
                    else if (v.IsUnique)
                        hdr = $"{panelMark}Unique Monolith  ·  {v.HoleCount} holes  ·  {v.Distance:F0}  ·  best {best:F0} ex###m{v.EntityId}";
                    else if (v.AnchorIdx >= 0)
                        hdr = $"{panelMark}{v.AnchorName}  ·  hole {v.AnchorPos + 1}/{v.HoleCount}  ·  {v.Distance:F0}  ·  best {best:F0} ex###m{v.EntityId}";
                    else
                        hdr = $"{panelMark}(anchor ?)  ·  {v.HoleCount} holes  ·  {v.Distance:F0}###m{v.EntityId}";

                    // Header tint via the shared helper so it matches the map-overlay label exactly.
                    uint hdrColor = this.MonolithValueColor(best, maxBest, out bool colorHdr);
                    if (colorHdr) ImGui.PushStyleColor(ImGuiCol.Text, hdrColor);
                    bool open = ImGui.CollapsingHeader(hdr, ImGuiTreeNodeFlags.None);
                    if (colorHdr) ImGui.PopStyleColor();
                    if (!open)
                        continue;

                    if (v.AnchorIdx < 0 && !v.IsUnique && !(v.IsRerolled && v.Candidates.Count > 0))
                    {
                        ImGui.TextDisabled("  anchor not resolved (station unavailable)");
                        continue;
                    }

                    // Fixed table width gives the stretch column something to fill under AlwaysAutoResize
                    // (otherwise the window would collapse to the header-text width). Height 0 = auto.
                    if (ImGui.BeginTable($"t{v.EntityId}", 4,
                            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp,
                            new Vector2(430f, 0f)))
                    {
                        ImGui.TableSetupColumn("Reward", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("x", ImGuiTableColumnFlags.WidthFixed, 26f);
                        ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed, 58f);
                        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 62f);
                        ImGui.TableHeadersRow();

                        int shown = 0;
                        foreach (var c in v.Candidates)
                        {
                            double total = c.UnitEx * c.Count;
                            if (c.Priced && total < min) continue;     // below threshold
                            if (!c.Priced && min > 0) continue;        // hide unpriced when filtering

                            ImGui.TableNextRow();
                            ImGui.TableSetColumnIndex(0);
                            ImGui.Text(c.Reward);
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"[{c.Size}] {c.Runes}");
                            ImGui.TableSetColumnIndex(1);
                            ImGui.Text(c.Count.ToString());
                            ImGui.TableSetColumnIndex(2);
                            ImGui.Text(c.Priced ? c.UnitEx.ToString("F0") : "—");
                            ImGui.TableSetColumnIndex(3);
                            if (c.Priced && colorize)
                            {
                                ImGui.PushStyleColor(ImGuiCol.Text, this.PickColor(total, median));
                                ImGui.Text(total.ToString("F0"));
                                ImGui.PopStyleColor();
                            }
                            else
                            {
                                ImGui.Text(c.Priced ? total.ToString("F0") : "—");
                            }
                            shown++;
                        }

                        if (shown == 0)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableSetColumnIndex(0);
                            ImGui.TextDisabled("(nothing above threshold)");
                        }

                        ImGui.EndTable();
                    }
                }
            }

            ImGui.End();
        }

        // ── debug window (repurposes the "Show debug list window" toggle) ──────
        private int monolithDebugSel;

        // Lets a tester pick a nearby monolith and see every value the offer rule consumes, so a
        // game-vs-plugin recipe mismatch can be reported precisely. "gate" = why a recipe is offered:
        // N (size==hole count, always) or RW (partial enabled by Expedition2RunesWeights).
        private void DrawMonolithDebugWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(680, 460), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("Monolith Debug###RunecraftMonolithDebug"))
            {
                ImGui.End();
                return;
            }

            if (this.monolithViews.Count == 0)
            {
                ImGui.TextDisabled("No monoliths detected in this area.");
                ImGui.End();
                return;
            }

            var labels = new string[this.monolithViews.Count];
            for (int i = 0; i < labels.Length; i++)
            {
                var mv = this.monolithViews[i];
                labels[i] = mv.IsUnique
                    ? $"Unique  {mv.HoleCount}h  ({mv.Distance:F0})"
                    : mv.AnchorIdx >= 0
                        ? $"{mv.AnchorName}  hole {mv.AnchorPos + 1}/{mv.HoleCount}  ({mv.Distance:F0})"
                        : $"(anchor ?)  {mv.HoleCount}h  ({mv.Distance:F0})";
            }
            if (this.monolithDebugSel < 0 || this.monolithDebugSel >= labels.Length) this.monolithDebugSel = 0;
            ImGui.SetNextItemWidth(420f);
            ImGui.Combo("Monolith", ref this.monolithDebugSel, labels, labels.Length);

            var v = this.monolithViews[this.monolithDebugSel];
            var red = new Vector4(1f, 0.45f, 0.45f, 1f);
            var grey = new Vector4(0.6f, 0.6f, 0.6f, 1f);

            ImGui.Separator();
            if (v.IsUnique)
                ImGui.Text($"Unique monolith (no anchor) — offers all recipes with size <= N ({v.HoleCount}).");
            else if (v.AnchorIdx < 0)
            {
                ImGui.TextColored(red, "Anchor not resolved — no recipes.");
                if (!string.IsNullOrEmpty(v.StationDiag))
                    ImGui.TextColored(red, $"  why: {v.StationDiag}");
            }
            else
                ImGui.Text($"Anchor: {v.AnchorName} (idx {v.AnchorIdx})    p={v.AnchorPos}  (hole {v.AnchorPos + 1})");

            // N: station +0x38 vs StateMachine "sockets" — flag the under-read case.
            if (v.SocketsState >= 0 && v.SocketsState != v.HoleCount)
                ImGui.TextColored(red, $"N = {v.HoleCount}  (station +0x38)    sockets state = {v.SocketsState}   <- differ");
            else
                ImGui.Text($"N = {v.HoleCount}    sockets state = {v.SocketsState}");

            ImGui.Text($"Area level: {v.AreaLevel}");
            if (v.IsForeign)
                ImGui.TextColored(red, "FOREIGN monolith (recipe-mode 0) — standalone \"additional\", not part of the Expedition dig, excluded from the route");
            ImGui.TextColored(grey, $"device 0x{v.EntityId:X}   station 0x{v.StationAddr:X}   mode={v.RecipeMode}   activated={v.Activated}   glow={v.GlowCount}   +0x40={FmtI(v.Field40)}  +0x44={FmtI(v.Field44)}");
            if (!string.IsNullOrEmpty(v.SmStates))
                ImGui.TextColored(grey, $"SM states: {v.SmStates}");

            if (ImGui.Button("Copy report"))
                ImGui.SetClipboardText(BuildDebugReport(v));
            ImGui.SameLine();
            ImGui.TextDisabled($"{v.Candidates.Count} recipe(s) offered");

            ImGui.Separator();
            if (ImGui.BeginTable("mdbg", 8,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("row", ImGuiTableColumnFlags.WidthFixed, 44f);
                ImGui.TableSetupColumn("sz", ImGuiTableColumnFlags.WidthFixed, 26f);
                ImGui.TableSetupColumn("gate", ImGuiTableColumnFlags.WidthFixed, 40f);
                ImGui.TableSetupColumn("cat", ImGuiTableColumnFlags.WidthFixed, 28f);
                ImGui.TableSetupColumn("reward", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("FK / Id", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("lvl", ImGuiTableColumnFlags.WidthFixed, 56f);
                ImGui.TableSetupColumn("holes (anchor in [])", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                foreach (var c in v.Candidates)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0); ImGui.Text(c.Row.ToString());
                    ImGui.TableSetColumnIndex(1); ImGui.Text(c.Size.ToString());
                    ImGui.TableSetColumnIndex(2); ImGui.Text(c.Full ? "N" : "RW");
                    ImGui.TableSetColumnIndex(3); ImGui.Text(c.Category.ToString());
                    ImGui.TableSetColumnIndex(4); ImGui.Text(c.Reward);
                    ImGui.TableSetColumnIndex(5); ImGui.TextColored(grey, $"{c.RewardIdx} / {c.RewardId}");
                    ImGui.TableSetColumnIndex(6); ImGui.Text($"{c.MinLevel}-{c.MaxLevel}");
                    ImGui.TableSetColumnIndex(7); ImGui.Text(MarkAnchor(c.Runes, v.AnchorPos));
                }

                if (v.Candidates.Count == 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(4);
                    ImGui.TextDisabled("(no recipes)");
                }

                ImGui.EndTable();
            }

            ImGui.End();
        }

        // Standalone window dumping the live Runeshape Combinations panel rows exactly as the price OVERLAY
        // resolves them: parsed name → MetaId / DdsArt (from nameToArtId) → which price branch fired → ex.
        // Own window (not nested under the monolith debug) so it shows even when no monolith is detected and
        // isn't pushed off-screen by the scrolling candidates table. Driven by the same ShowWindow toggle.
        private void DrawOverlayRowsDebugWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(640, 320), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("Overlay Rows Debug###RunecraftOverlayRowsDebug"))
            {
                ImGui.End();
                return;
            }

            ImGui.Text($"Combinations panel rows: {this.recipes.Count}  (open the panel to populate)");
            if (this.recipes.Count > 0 && ImGui.BeginTable("ovrows", 6,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("x", ImGuiTableColumnFlags.WidthFixed, 24f);
                ImGui.TableSetupColumn("parsed name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("MetaId", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("DdsArt", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("ex", ImGuiTableColumnFlags.WidthFixed, 64f);
                ImGui.TableSetupColumn("branch", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                foreach (var r in this.recipes)
                {
                    var (price, branch) = this.TraceRecipePrice(in r);
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0); ImGui.Text(r.Count.ToString());
                    ImGui.TableSetColumnIndex(1); ImGui.Text(r.Name);
                    ImGui.TableSetColumnIndex(2); ImGui.Text(string.IsNullOrEmpty(r.MetaId) ? "—" : r.MetaId);
                    ImGui.TableSetColumnIndex(3); ImGui.Text(string.IsNullOrEmpty(r.DdsArt) ? "—" : r.DdsArt);
                    ImGui.TableSetColumnIndex(4); ImGui.Text(price > 0 ? price.ToString("0.###") : "—");
                    ImGui.TableSetColumnIndex(5); ImGui.Text(branch);
                }

                ImGui.EndTable();
            }

            ImGui.End();
        }

        private static string FmtI(int x) => x == int.MinValue ? "?" : x.ToString();

        // Mark the anchor hole in a " · "-joined rune list: anchor at pos 1 of "a · b · c" → "a · [b] · c".
        private static string MarkAnchor(string runes, int pos)
        {
            if (string.IsNullOrEmpty(runes) || pos < 0) return runes;
            var parts = runes.Split(" · ");
            if (pos >= parts.Length) return runes;
            parts[pos] = "[" + parts[pos] + "]";
            return string.Join(" · ", parts);
        }

        // Discord-pasteable plain-text dump of the selected monolith.
        private static string BuildDebugReport(MonoView v)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Monolith: {v.AnchorName} (idx {v.AnchorIdx})  p={v.AnchorPos} hole{v.AnchorPos + 1}  " +
                          $"N={v.HoleCount} (sockets={v.SocketsState})  areaLvl={v.AreaLevel}  mode={v.RecipeMode}{(v.IsForeign ? " FOREIGN(mode=0)" : "")}  activated={v.Activated}  glow={v.GlowCount}  +0x40={FmtI(v.Field40)} +0x44={FmtI(v.Field44)}");
            sb.AppendLine($"device 0x{v.EntityId:X}  station 0x{v.StationAddr:X}");
            if (!string.IsNullOrEmpty(v.SmStates))
                sb.AppendLine($"SM states: {v.SmStates}");
            if (v.AnchorIdx < 0 && !string.IsNullOrEmpty(v.StationDiag))
                sb.AppendLine($"resolve failed: {v.StationDiag}");
            sb.AppendLine($"offered {v.Candidates.Count}:");
            foreach (var c in v.Candidates)
                sb.AppendLine($"  row{c.Row} size{c.Size} [{(c.Full ? "N" : "RW")}] cat{c.Category} " +
                              $"{c.Reward} (FK{c.RewardIdx} {c.RewardId}) lvl{c.MinLevel}-{c.MaxLevel} | {MarkAnchor(c.Runes, v.AnchorPos)}");
            return sb.ToString();
        }

        // ── enumeration / resolution ──────────────────────────────────────────
        private List<MonoView> EnumerateMonoliths()
        {
            var list = new List<MonoView>();
            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (area == null) return list;
            int areaLevel = area.CurrentAreaLevel;

            Vector2 pg = default;
            bool havePlayer = false;
            if (area.Player != null && area.Player.TryGetComponent<Render>(out var ppr))
            {
                pg = new Vector2(ppr.GridPosition.X, ppr.GridPosition.Y);
                havePlayer = true;
            }

            foreach (var kv in area.AwakeEntities)
            {
                var e = kv.Value;
                // Validity gate: IsValid catches devices removed from the area; EntityState weeds out
                // unresolved/dead. NOTE: collecting a monolith does NOT clear these (the device persists
                // with IsValid + Life 100/100 — docs §6.11); the "collected" signal is the StateMachine
                // "activated" state checked below.
                if (e == null || !e.IsValid || e.EntityState == EntityStates.Useless) continue;
                var path = e.Path;
                if (string.IsNullOrEmpty(path) ||
                    path.IndexOf(MonolithDevicePath, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!e.TryGetComponent<StateMachine>(out var sm)) continue;

                var v = new MonoView { EntityId = e.Address.ToInt64(), AreaLevel = areaLevel };

                // Collected gate: the device persists (IsValid stays true, Life 100/100) after the player
                // collects a monolith, so the "still available" signal is its StateMachine "activated" state.
                // Observed values (live, docs §6.11): 0 = dormant/out of range, 1 = available; collected
                // monoliths jump to a high value (7 and 8 both seen post-collection). Live monoliths stay
                // low (0/1), so treat activated >= 7 as collected and hide it; 0/1 stay shown.
                bool collected = false;
                var smDump = new System.Text.StringBuilder();
                foreach (var s in sm.States)
                {
                    if (smDump.Length > 0) smDump.Append(", ");
                    smDump.Append(s.Name).Append('=').Append(s.Value);
                    if (string.Equals(s.Name, "sockets", StringComparison.OrdinalIgnoreCase))
                    {
                        v.SocketsState = (int)s.Value;
                        v.HoleCount = (int)s.Value;
                    }
                    else if (string.Equals(s.Name, "activated", StringComparison.OrdinalIgnoreCase))
                    {
                        v.Activated = (int)s.Value;
                        collected = s.Value >= 7;
                    }
                    else if (string.Equals(s.Name, "is_rerolled", StringComparison.OrdinalIgnoreCase))
                        v.IsRerolled = s.Value != 0;
                }
                v.SmStates = smDump.ToString();
                if (collected) continue;

                if (e.TryGetComponent<Render>(out var r))
                {
                    v.GridPos = new Vector2(r.GridPosition.X, r.GridPosition.Y);
                    v.TerrainHeight = r.TerrainHeight;
                    v.HasPos = true;
                    if (havePlayer)
                        v.Distance = Vector2.Distance(pg, v.GridPos);
                }

                if (this.TryResolveStation(sm, e.Address, out var station, out var sdiag))
                {
                    v.StationAddr = station.ToInt64();
                    // Read the station fields whenever it resolves — independent of the anchor. N (hole
                    // count) is authoritative from +0x38 (what the in-game offer builder uses); the
                    // StateMachine "sockets" state caps at 6 and under-reports >6-hole monoliths. These
                    // must run even when the anchor read fails (e.g. no rune socketed) so the debug still
                    // shows the true N and +0x40/+0x44.
                    if (this.TryReadI32(station + StationHoleCountOffset, out var nHoles) && nHoles > 0 && nHoles <= 16)
                        v.HoleCount = nHoles;
                    if (this.TryReadI32(station + 0x40, out var f40)) v.Field40 = f40;
                    if (this.TryReadI32(station + 0x44, out var f44)) v.Field44 = f44;

                    // Glow-socket vector size (station+0x40 std::vector<int32>). DEBUG ONLY — NOT a foreigner
                    // discriminator: both the dig monoliths and the standalone "additional" one have a non-empty
                    // glow vector (the standalone's glow just sits on a socket without rendering the gold ring).
                    IntPtr gFirst = this.ReadPtr(station + 0x40);
                    IntPtr gLast = this.ReadPtr(station + 0x48);
                    v.GlowCount = (gFirst != IntPtr.Zero && (long)gLast > (long)gFirst)
                        ? (int)(((long)gLast - (long)gFirst) / 4)
                        : 0;

                    // Foreigner discriminator (server-authoritative): station+0x58 = recipe-mode enum, written
                    // verbatim from the server net-state (RuneStation_DeserializeNetState op0 [0xb], Ghidra
                    // 0x1417bb9b0). Real Expedition dig monolith=1, unique=3; the current league's standalone
                    // "additional" monolith=0. Live-confirmed: dig 1297/1299 = mode 1, standalone 1383 = mode 0.
                    if (this.TryReadI32(station + StationRecipeModeOffset, out var rmode)) v.RecipeMode = rmode;

                    // Panel-open listener: an in-module vtable ptr only while THIS monolith's Combinations
                    // panel is open (null on the others). Direct signal of which monolith the player is
                    // browsing — used to anchor the locked-recipe highlight to the right one.
                    v.PanelOpen = IsExeAddr(this.ReadPtr(station + StationPanelOpenOffset));

                    // Anchor-less "unique" monolith: no pre-placed rune at station +0x28. Normal monoliths
                    // always carry one (docs §6.6), so a null anchor is the discriminator under which the
                    // game's offer builder runs its skip-anchor branch (offers all size<=N; docs §6.12).
                    if (this.ReadPtr(station + StationAnchorRefOffset) == IntPtr.Zero && v.HoleCount > 0)
                    {
                        v.IsUnique = true;
                        this.BuildCandidatesUnique(v, areaLevel);
                    }
                    else if (this.TryReadAnchor(station, out var aidx, out var apos, out var adiag))
                    {
                        v.AnchorIdx = aidx;
                        v.AnchorPos = apos;
                        v.AnchorName = this.runeNames.TryGetValue(aidx, out var nm) ? nm : $"#{aidx}";
                        this.BuildCandidates(v, areaLevel);
                    }
                    else
                    {
                        v.StationDiag = $"station resolved, anchor read failed: {adiag}";
                    }

                    // Sealed (rerolled) monolith: the reward is locked in — the player can no longer
                    // pick. Show ONLY the selected reward's value, not the max over the anchor
                    // candidates (which would over-report). The selection is the recipe row pointer at
                    // station+0x60; resolve it by language-independent Id against the offline catalog.
                    // Runs independent of the anchor read (it can fail on odd stations) — +0x60 alone
                    // identifies the locked recipe.
                    if (v.IsRerolled && this.TryReadSelectedRecipeId(station, out var selId))
                    {
                        v.SelectedRecipeId = selId;
                        var sel = this.monolithRecipes.Find(
                            r => string.Equals(r.id, selId, StringComparison.Ordinal));
                        if (sel != null)
                        {
                            v.Candidates.Clear();
                            this.AddCandidate(v, sel);
                            if (v.AnchorIdx < 0) v.StationDiag = string.Empty; // locked recipe is enough
                        }
                    }

                    // Watched runes sitting on glowing sockets (map scouting label). Runs after anchor +
                    // selected recipe are known, since it resolves each glow socket from those.
                    this.ResolveGlowRunes(v, station);
                }
                else
                {
                    v.StationDiag = sdiag;
                }

                double best = 0;
                foreach (var c in v.Candidates)
                    if (c.Priced) best = Math.Max(best, c.UnitEx * c.Count);
                v.Best = best;

                // Foreign standalone monolith (current "modified Expedition" league): NOT part of the explosive
                // dig — the player collects it by hand, so the chain planner must never anchor to it. The game's
                // own server-assigned recipe-mode enum (station+0x58) is the discriminator: dig monolith=1,
                // unique=3, the standalone "additional" monolith=0. Exclude ONLY mode 0 (so normal AND unique dig
                // monoliths still route). Earlier tries failed: glow vector is non-empty for both, and
                // StateMachine `activated` was 1 for both on some maps — see the socket-glow memory note.
                // Kept in the list for the debug dump; the route excludes it by EntityId (see ExpComputeRoute).
                if (v.RecipeMode == 0) v.IsForeign = true;

                list.Add(v);
            }

            list.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            return list;
        }

        // Walk the device StateMachine's listener vector to the RuneStation that registered on it.
        // `diag` records the failure step so community debug reports can pinpoint why big/odd monoliths
        // don't resolve (empty on success).
        private bool TryResolveStation(StateMachine sm, IntPtr deviceAddr, out IntPtr station, out string diag)
        {
            station = IntPtr.Zero;
            diag = string.Empty;
            if (sm == null || sm.Address == IntPtr.Zero) { diag = "SM component null"; return false; }
            if (!this.TryReadStdVector(sm.Address + StateMachineListenerVecOffset, out var first, out var last))
            {
                diag = "SM listener vector (+0x20) unreadable";
                return false;
            }

            long n = ((long)last - (long)first) / 8;
            if (n <= 0 || n > 256)
            {
                diag = $"listener vector size out of range (n={n})";
                return false;
            }

            for (long i = 0; i < n; i++)
            {
                var node = this.ReadPtr(first + (nint)(i * 8));
                if (node == IntPtr.Zero) continue;
                var sub = this.ReadPtr(node);            // = candidate station + 0x98
                if (sub == IntPtr.Zero) continue;
                var cand = sub - StationListenerSubOffset;
                if (this.ReadPtr(cand + StationOwnerOffset) == deviceAddr)
                {
                    station = cand;
                    return true;
                }
            }

            diag = $"no listener matched device ({n} checked)";

            return false;
        }

        // anchor rune index = (station+0x28 row ptr − table base)/0x6c; hole pos = station+0x3c.
        // `diag` records which step failed so debug reports distinguish "no rune socketed" from a
        // genuinely different station layout (empty on success).
        private bool TryReadAnchor(IntPtr station, out int idx, out int pos, out string diag)
        {
            idx = -1;
            pos = -1;
            diag = string.Empty;
            if (station == IntPtr.Zero) { diag = "station null"; return false; }

            this.TryReadI32(station + StationAnchorPosOffset, out pos);

            var rowPtr = this.ReadPtr(station + StationAnchorRefOffset);
            if (rowPtr == IntPtr.Zero) { diag = "rune row ptr null at +0x28 (no rune socketed?)"; return false; }

            // Rune-table base must be computed per station: the Expedition2Runes row array is
            // re-instantiated per area, so its base differs every map. Caching it across areas
            // makes rowPtr − base go negative on the next map (mirrors FUN_14179ed70 exactly).
            var holder = this.ReadPtr(station + StationAnchorHolderOffset);
            if (holder == IntPtr.Zero) { diag = "holder null at +0x30"; return false; }
            var p1 = this.ReadPtr(holder + 0x28);
            if (p1 == IntPtr.Zero) { diag = "rune table ptr null at holder+0x28"; return false; }
            long tableBase = this.ReadPtr(p1).ToInt64();
            if (tableBase == 0) { diag = "rune table base 0"; return false; }
            long delta = rowPtr.ToInt64() - tableBase;
            if (delta < 0 || delta % ExpeditionRuneStride != 0) { diag = $"rowPtr-base misaligned (delta=0x{delta:X})"; return false; }
            long i = delta / ExpeditionRuneStride;
            if (i < 0 || i >= RuneCount) { diag = $"rune index out of range (i={i}, pos={pos})"; return false; }

            idx = (int)i;
            return true;
        }

        // Recipes a monolith (anchor rune at hole p, N holes, current area level) can roll.
        // Rule decoded from the in-game offer builder FUN_141e32ab0 (docs/monolith-partial-recipes.md):
        //   runeIdx[p] == anchor  AND  size <= N
        //   AND  minLevel <= areaLevel <= maxLevel
        //   AND  ( size == N   OR   Expedition2RunesWeights permits (anchor, p+1, size) at this level )
        // There is NO category/theme filter — the "theme" is emergent from which rune sits at which hole.
        private void BuildCandidates(MonoView v, int areaLevel)
        {
            if (v.AnchorIdx < 0 || v.AnchorPos < 0 || v.HoleCount <= 0) return;
            foreach (var rec in this.monolithRecipes)
            {
                if (rec.runeIdx == null || rec.runeIdx.Count <= v.AnchorPos) continue;
                if (rec.size > v.HoleCount) continue;
                if (rec.runeIdx[v.AnchorPos] != v.AnchorIdx) continue;
                // Area-level gate: tiered rewards (e.g. Thaumaturgic Flux Levels 5..18) carry a
                // [minLevel, maxLevel] band in the .dat and only the tier covering the current area
                // level is offered. Untiered recipes use 1..100, so this never drops them.
                if (areaLevel > 0 && rec.maxLevel > 0 &&
                    (areaLevel < rec.minLevel || areaLevel > rec.maxLevel)) continue;
                // Partial-size gate: size==N is always offered; size<N only when Expedition2RunesWeights
                // has a (rune, position, size) row whose minLevel the area level meets.
                if (rec.size != v.HoleCount &&
                    !this.IsPartialAllowed(v.AnchorIdx, v.AnchorPos, rec.size, areaLevel)) continue;

                this.AddCandidate(v, rec);
            }

            v.Candidates.Sort((a, b) => (b.UnitEx * b.Count).CompareTo(a.UnitEx * a.Count));
        }

        // Recipes an anchor-less "unique" monolith offers. Decoded from the same offer builder
        // FUN_141e33b00 (docs/re-findings.md §6.12): when the station carries no pre-placed anchor
        // (station +0x28 == 0), the game calls it with the skip-anchor flag set (param_5 != 0), which
        // bypasses BOTH the runeIdx[p]==anchor match AND the size==N/partial gate. What remains is just
        //   recipe not disabled (+0x75==0, true for every row)  AND  size <= N  AND  level band passes.
        // So it offers the whole catalog that fits the holes — hence the very large list.
        private void BuildCandidatesUnique(MonoView v, int areaLevel)
        {
            if (v.HoleCount <= 0) return;
            foreach (var rec in this.monolithRecipes)
            {
                if (rec.size > v.HoleCount) continue;
                if (areaLevel > 0 && rec.maxLevel > 0 &&
                    (areaLevel < rec.minLevel || areaLevel > rec.maxLevel)) continue;
                this.AddCandidate(v, rec);
            }

            v.Candidates.Sort((a, b) => (b.UnitEx * b.Count).CompareTo(a.UnitEx * a.Count));
        }

        // Build a priced MonoCand from a recipe row and append it to the view. Shared by the anchored
        // (BuildCandidates) and anchor-less (BuildCandidatesUnique) offer paths.
        private void AddCandidate(MonoView v, MonoRecipe rec)
        {
            v.Offered.Add(rec);   // kept for the glow-rune scan (which runes offered recipes can place per socket)
            var c = new MonoCand
            {
                Size = rec.size,
                Count = Math.Max(1, rec.rewardCount),
                Runes = rec.runes != null ? string.Join(" · ", rec.runes) : string.Empty,
                Row = rec.row,
                Category = rec.category,
                RewardIdx = rec.reward?.idx ?? -1,
                RewardId = rec.reward?.id ?? string.Empty,
                MinLevel = rec.minLevel,
                MaxLevel = rec.maxLevel,
                Full = rec.size == v.HoleCount,
            };

            if (rec.reward != null && !string.IsNullOrEmpty(rec.reward.name))
            {
                // Display the client-language name (from the in-memory BaseItemTypes table, keyed by the
                // reward's full meta path) when available; fall back to the English catalog name. Pricing
                // still keys on the English name — poe.ninja types are English-only.
                c.Reward = (!string.IsNullOrEmpty(rec.reward.id) &&
                            this.metaToLocalName.TryGetValue(rec.reward.id, out var localName))
                    ? localName
                    : rec.reward.name;
                if (this.priceCache.TryGetExaltedPrice(rec.reward.name, out var u) && u > 0)
                {
                    c.UnitEx = u;
                    c.Priced = true;
                }
            }
            else
            {
                c.Reward = string.IsNullOrEmpty(rec.description) ? $"(unique) {rec.id}" : rec.description;
            }

            v.Candidates.Add(c);
        }

        // Build {BaseItemType.Id → localized Name} once per session so the rewards window can show reward
        // names in the client's language (the catalog json carries only English names). Rooted at the
        // global FileRoot registry (Core.CurrentAreaLoadedFiles.Address) — always available in-game, so it
        // works without the Combinations panel being open (unlike the price-overlay's nameToArtId, which is
        // built from a panel BFS root). Throttled while empty so the pointer walk doesn't run every frame.
        // FileRoot route + row layout: obsidian poe2/Loaders.md.
        private void BuildMetaToLocalNameIfNeeded()
        {
            if (this.metaToLocalName.Count > 0) return;
            var now = DateTime.UtcNow;
            if (now < this.metaToLocalNextTryUtc) return;
            this.metaToLocalNextTryUtc = now.AddSeconds(2);
            if (!this.EnsureProcess()) return;

            var fileRoot = Core.CurrentAreaLoadedFiles.Address;
            if (fileRoot == IntPtr.Zero) return;
            var bitTable = this.FindBaseItemTypesHandle(fileRoot, IntPtr.Zero);
            if (bitTable == IntPtr.Zero) return;

            var bitVec = this.ReadPtr(bitTable + TableRowsVectorOffset);
            var bitBegin = this.ReadPtr(bitVec);
            var bitEnd = this.ReadPtr(bitVec + 8);
            if (bitBegin == IntPtr.Zero || (long)bitEnd <= (long)bitBegin) return;
            long bitCount = ((long)bitEnd - (long)bitBegin) / BaseItemTypeStride;
            if (bitCount <= 0 || bitCount > 200000) return;

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            for (long j = 0; j < bitCount; j++)
            {
                var row = bitBegin + (nint)(j * BaseItemTypeStride);
                var id = this.ReadUtf16Z(this.ReadPtr(row + BaseItemTypeIdOffset), 128);
                if (id.Length == 0) continue;
                var name = this.ReadUtf16Z(this.ReadPtr(row + BaseItemTypeNameOffset), 64);
                if (name.Length < 2) continue;
                dict[id] = name.Trim();
            }

            if (dict.Count > 0) this.metaToLocalName = dict;
        }

        // True if Expedition2RunesWeights enables a partial recipe of `size` for anchor `idx` at hole
        // `pos` (0-based) given the current area level. areaLevel<=0 means level unknown → don't gate on it.
        private bool IsPartialAllowed(int idx, int pos, int size, int areaLevel)
        {
            if (!this.partialMinLevel.TryGetValue(PartialKey(idx, pos + 1, size), out var minL)) return false;
            return areaLevel <= 0 || areaLevel >= minL;
        }

        private static long PartialKey(int rune, int pos1Based, int size)
            => ((long)rune << 16) | ((long)pos1Based << 8) | (uint)size;

        // Header colour. Absolute mode defers to the shared PickColor (fixed ex thresholds). Relative
        // mode is judged against the BEST monolith on screen rather than a median: green only for the
        // top tier (≥0.5×max), red for the long cheap tail (≤0.2×max), yellow in between — robust to
        // the bimodal price spread where a median baseline would green-light cheap monoliths.
        // Single source of truth for a monolith's value tint, shared by the rewards-window header and the
        // map-overlay label so they always agree. A non-zero MonolithHighlightThreshold uses the absolute
        // scheme (green ≥ T, yellow 0.6×T..T, untinted below); otherwise the ColorMode-driven header tint.
        // `tinted` is false when no colour applies (caller leaves the default / neutral colour).
        private uint MonolithValueColor(double best, double maxBest, out bool tinted)
        {
            tinted = false;
            if (best <= 0) return ColorWhite;

            float threshold = this.Settings.MonolithHighlightThreshold;
            if (threshold > 0f)
            {
                if (best >= threshold) { tinted = true; return ColorGreen; }
                if (best >= 0.6f * threshold) { tinted = true; return ColorYellow; }
                return ColorWhite; // below 0.6×threshold → untinted
            }

            if (this.Settings.ColorMode != RewardColorMode.Off)
            {
                tinted = true;
                return this.PickHeaderColor(best, maxBest);
            }

            return ColorWhite;
        }

        private uint PickHeaderColor(double best, double maxBest)
        {
            if (this.Settings.ColorMode == RewardColorMode.Absolute)
                return this.PickColor(best, 0);
            if (this.Settings.ColorMode == RewardColorMode.Relative)
            {
                if (maxBest <= 0) return ColorWhite;
                double r = best / maxBest;
                if (r >= 0.5) return ColorGreen;
                if (r <= 0.2) return ColorRed;
                return ColorYellow;
            }
            return ColorWhite;
        }

        private static double MedianOfDoubles(List<double> v)
        {
            if (v.Count == 0) return 0;
            v.Sort();
            int n = v.Count;
            return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) * 0.5;
        }

        // Read the selected/locked recipe's language-independent Id off station+0x60 (see that const).
        // station+0x60 → in-memory Expedition2Recipes row → row+0x00 → UTF-16 Id ("4Slot…"). Returns
        // false if no recipe is selected (null ptr) or the Id can't be read. ReadPtr/ReadUtf16Z live in
        // the other partial of this class.
        private bool TryReadSelectedRecipeId(IntPtr station, out string id)
        {
            id = string.Empty;
            if (station == IntPtr.Zero) return false;
            var rowPtr = this.ReadPtr(station + StationSelectedRecipeOffset);
            if (rowPtr == IntPtr.Zero) return false;
            var idPtr = this.ReadPtr(rowPtr);
            if (idPtr == IntPtr.Zero) return false;
            var s = this.ReadUtf16Z(idPtr, 64);
            if (s.Length < 3) return false;
            id = s;
            return true;
        }

        // Populate v.GlowRuneLabels: the watched runes at this monolith's GLOWING socket positions, after the
        // weight rule (highest weight wins; ties show all). Glow sockets = station+0x40 vector<int32> indices.
        // Glow-socket position == rune position in a recipe, so no placed-rune memory read is needed:
        //   • a recipe is selected (station+0x60) → that recipe's rune at the socket;
        //   • else → scan the offered recipes (v.Offered) and collect every rune they could place at that
        //     socket, so an UNTOUCHED monolith still lights up if it can roll a watched rune there.
        private void ResolveGlowRunes(MonoView v, IntPtr station)
        {
            v.GlowRuneLabels.Clear();
            v.GlowSockets.Clear();
            if (!this.Settings.ShowGlowRunes || station == IntPtr.Zero) return;

            IntPtr gFirst = this.ReadPtr(station + 0x40);
            IntPtr gLast = this.ReadPtr(station + 0x48);
            if (gFirst == IntPtr.Zero || (long)gLast <= (long)gFirst) return;
            int cnt = (int)(((long)gLast - (long)gFirst) / 4);
            if (cnt <= 0 || cnt > 16) return;

            List<int>? selRuneIdx = null;
            if (this.TryReadSelectedRecipeId(station, out var selId))
                selRuneIdx = this.monolithRecipes
                    .Find(r => string.Equals(r.id, selId, StringComparison.Ordinal))?.runeIdx;

            // Distinct rune indices sitting at (or rollable at) the glowing socket positions.
            var runeIdxAtGlow = new List<int>();
            void Consider(int ri)
            {
                if (ri >= 0 && !runeIdxAtGlow.Contains(ri)) runeIdxAtGlow.Add(ri);
            }

            for (int i = 0; i < cnt; i++)
            {
                if (!this.TryReadI32(gFirst + (i * 4), out var socket) || socket < 0) continue;
                if (!v.GlowSockets.Contains(socket)) v.GlowSockets.Add(socket);  // raw index (for the panel rune overlay)

                if (selRuneIdx != null)
                {
                    // A recipe is selected → the socket holds exactly that recipe's rune.
                    if (socket < selRuneIdx.Count) Consider(selRuneIdx[socket]);
                    continue;
                }

                // No recipe selected → the socket has no fixed rune. Position == rune index in a recipe, so
                // enumerate what the monolith's OFFERED recipes could place there (anchor is fixed at its pos).
                if (socket == v.AnchorPos && v.AnchorIdx >= 0) Consider(v.AnchorIdx);
                foreach (var rec in v.Offered)
                    if (rec.runeIdx != null && socket < rec.runeIdx.Count) Consider(rec.runeIdx[socket]);
            }
            if (runeIdxAtGlow.Count == 0) return;

            // Match against the watch table (Show only); keep the highest weight, show all ties.
            float best = float.NegativeInfinity;
            var matched = new List<(string name, float w)>();
            foreach (var ri in runeIdxAtGlow)
            {
                var name = this.RuneNameByIndex(ri);
                if (name == null) continue;
                var e = this.Settings.GlowRunes.Find(
                    g => g.Show && string.Equals(g.Rune, name, StringComparison.Ordinal));
                if (e == null) continue;
                matched.Add((name, e.Weight));
                if (e.Weight > best) best = e.Weight;
            }

            foreach (var m in matched)
                if (m.w >= best && !v.GlowRuneLabels.Contains(m.name)) v.GlowRuneLabels.Add(m.name);
        }

        // For the open Combinations panel: the watched rune name a given recipe would place on the open
        // monolith's glowing socket(s), or empty. Used by DrawOverlay to label such recipe rows after the
        // price. Matches the visible recipe to the offline catalog by Id → runeIdx[glowSocket].
        private string GlowRuneLabelForRecipe(string recipeId)
        {
            if (!this.Settings.ShowGlowRunes || string.IsNullOrEmpty(recipeId)) return string.Empty;
            var open = this.monolithViews.Find(v => v.PanelOpen);
            if (open == null || open.GlowSockets.Count == 0) return string.Empty;
            var mr = this.monolithRecipes.Find(m => string.Equals(m.id, recipeId, StringComparison.Ordinal));
            if (mr?.runeIdx == null) return string.Empty;
            foreach (var g in open.GlowSockets)
            {
                if (g < 0 || g >= mr.runeIdx.Count) continue;
                var name = this.RuneNameByIndex(mr.runeIdx[g]);
                if (name != null &&
                    this.Settings.GlowRunes.Exists(e => e.Show && string.Equals(e.Rune, name, StringComparison.Ordinal)))
                    return name;
            }

            return string.Empty;
        }

        // Settings UI: the watched glow-rune table (show / weight / rune / effect) + add / remove. Drawn from
        // DrawSettings under the "Show glow runes" toggle. Defaults can be toggled off but not deleted.
        private void DrawGlowRuneTable()
        {
            var runes = this.Settings.GlowRunes;
            string? removeKey = null;
            if (ImGui.BeginTable("glowrunes", 5,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
                    new Vector2(0f, Math.Min(runes.Count + 1, 10) * ImGui.GetFrameHeightWithSpacing())))
            {
                ImGui.TableSetupColumn("Show", ImGuiTableColumnFlags.WidthFixed, 40f);
                ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 66f);
                ImGui.TableSetupColumn("Rune", ImGuiTableColumnFlags.WidthFixed, 92f);
                ImGui.TableSetupColumn("Effect", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##rm", ImGuiTableColumnFlags.WidthFixed, 22f);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                foreach (var g in runes)
                {
                    ImGui.TableNextRow();
                    ImGui.PushID(g.Rune);

                    ImGui.TableSetColumnIndex(0);
                    bool show = g.Show;
                    if (ImGui.Checkbox("##show", ref show)) g.Show = show;

                    ImGui.TableSetColumnIndex(1);
                    ImGui.SetNextItemWidth(60f);
                    float w = g.Weight;
                    if (ImGui.InputFloat("##w", ref w, 0f, 0f, "%.0f")) { if (w < 0f) w = 0f; g.Weight = w; }

                    ImGui.TableSetColumnIndex(2);
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(g.Rune);

                    ImGui.TableSetColumnIndex(3);
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextDisabled(RuneEffects.TryGetValue(g.Rune, out var eff) ? eff : string.Empty);

                    ImGui.TableSetColumnIndex(4);
                    if (Array.IndexOf(DefaultGlowRuneNames, g.Rune) < 0)   // defaults can't be removed
                    {
                        if (ImGui.SmallButton("×")) removeKey = g.Rune;
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove from table");
                    }

                    ImGui.PopID();
                }

                ImGui.EndTable();
            }

            if (removeKey != null)
                runes.RemoveAll(g => string.Equals(g.Rune, removeKey, StringComparison.Ordinal));

            // Add a rune not already in the table (all 34, with their effect).
            if (ImGui.BeginCombo("Add rune", "+ add…", ImGuiComboFlags.HeightLarge))
            {
                foreach (var name in AllRuneNames)
                {
                    if (runes.Exists(g => string.Equals(g.Rune, name, StringComparison.Ordinal))) continue;
                    var eff = RuneEffects.TryGetValue(name, out var e) ? e : string.Empty;
                    if (ImGui.Selectable($"{name}  —  {eff}"))
                        runes.Add(new GlowRuneEntry { Rune = name, Weight = 100f, Show = true });
                }

                ImGui.EndCombo();
            }
        }

        // Reward metaId of the locked recipe for the monolith whose Runeshape Combinations panel is
        // currently OPEN and which is SEALED — or empty otherwise. The open monolith is identified
        // directly by its station's panel-open listener (StationPanelOpenOffset, read during the scan),
        // NOT by distance/BFS: several monoliths can cluster and any be opened from one spot.
        // Identify the locked recipe of the monolith whose panel is open + sealed, and publish its
        // reward identity to the overlay matcher: metaId (primary) and name (fallback for runes whose
        // live MetaId reads blank). Clears both when no such monolith is found.
        private void ResolveLockedPanelReward()
        {
            this.lockedPanelMetaId = string.Empty;
            this.lockedPanelName = string.Empty;
            foreach (var v in this.monolithViews)
            {
                if (!v.PanelOpen || !v.IsRerolled || string.IsNullOrEmpty(v.SelectedRecipeId)) continue;
                var rec = this.monolithRecipes.Find(
                    r => string.Equals(r.id, v.SelectedRecipeId, StringComparison.Ordinal));
                var id = rec?.reward?.id;
                this.lockedPanelMetaId = string.IsNullOrEmpty(id) ? string.Empty : LastMetaSegment(id);
                this.lockedPanelName = rec?.reward?.name ?? string.Empty;
                return;
            }
        }

        private bool TryReadI32(IntPtr addr, out int val)
        {
            val = 0;
            if (addr == IntPtr.Zero) return false;
            var buf = new byte[4];
            if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out _)) return false;
            val = BitConverter.ToInt32(buf, 0);
            return true;
        }

        // ── models ────────────────────────────────────────────────────────────
        private sealed class MonoFile
        {
            public Dictionary<string, string>? runes { get; set; }
            public List<MonoRecipe>? recipes { get; set; }
            public List<RuneWeight>? runeWeights { get; set; }
        }

        private sealed class RuneWeight
        {
            public int rune { get; set; }
            public int pos { get; set; }   // 1-based anchor position
            public int size { get; set; }
            public int minLevel { get; set; }
        }

        private sealed class MonoRecipe
        {
            public int row { get; set; }
            public string id { get; set; } = string.Empty;
            public int size { get; set; }
            public int category { get; set; }
            public List<int>? runeIdx { get; set; }
            public List<string>? runes { get; set; }
            public MonoReward? reward { get; set; }
            public int rewardCount { get; set; }
            public string description { get; set; } = string.Empty;
            public int minLevel { get; set; }
            public int maxLevel { get; set; }
        }

        private sealed class MonoReward
        {
            public int idx { get; set; }
            public string id { get; set; } = string.Empty;
            public string name { get; set; } = string.Empty;
        }

        private sealed class MonoView
        {
            public long EntityId;          // device entity address
            public long StationAddr;       // resolved RuneStation address (0 if unresolved)
            public float Distance;
            public Vector2 GridPos;        // monolith grid position (for the radar-map projection)
            public float TerrainHeight;    // monolith terrain height (for the radar-map projection)
            public bool HasPos;            // GridPos/TerrainHeight were read
            public double Best;            // best priced reward total (header tint + map label)
            public int HoleCount;          // N — authoritative, from station +0x38
            public int SocketsState = -1;  // StateMachine "sockets" value (for debug; can under-read N)
            public int AreaLevel;
            public int Field40 = int.MinValue; // station +0x40 (debug; not used for offers)
            public int Field44 = int.MinValue; // station +0x44 (debug)
            public int GlowCount = -1;     // station+0x40 glow-socket vector size (debug; NOT the foreigner discriminator — both glow)
            public int Activated = -1;     // StateMachine "activated" (debug only; unreliable — was 1 for both dig & foreigner on some maps)
            public int RecipeMode = -1;    // station+0x58 server recipe-mode enum: dig=1, unique=3, standalone "additional"=0
            public bool IsForeign;         // RecipeMode==0 → standalone non-Expedition "additional" monolith (current league) → excluded from the route
            public int AnchorIdx = -1;
            public int AnchorPos = -1;
            public string AnchorName = "?";
            public bool IsUnique;          // anchor-less "unique" monolith: offers all size<=N (docs §6.12)
            public bool IsRerolled;        // sealed: SM "is_rerolled"=1 → recipe locked, show only selection
            public bool PanelOpen;         // this monolith's Combinations panel is open (station+0xB8 set)
            public string SelectedRecipeId = string.Empty; // locked recipe Id (station+0x60), when sealed
            public string StationDiag = string.Empty; // why station/anchor failed to resolve (debug)
            public string SmStates = string.Empty;     // all StateMachine states "name=value" (debug)
            public List<MonoCand> Candidates = new();
            public List<string> GlowRuneLabels = new(); // watched runes on glowing sockets to label on the map (weight-filtered)
            public List<int> GlowSockets = new();        // raw glowing socket indices (station+0x40); used by the panel rune overlay
            public List<MonoRecipe> Offered = new();     // recipes this monolith can roll (for glow-rune scan when no recipe is selected)
        }

        private sealed class MonoCand
        {
            public string Reward = string.Empty;
            public int Count;
            public int Size;
            public double UnitEx;
            public bool Priced;
            public string Runes = string.Empty;
            // debug detail
            public int Row;
            public int Category;
            public int RewardIdx = -1;
            public string RewardId = string.Empty;
            public int MinLevel;
            public int MaxLevel;
            public bool Full;             // true = size==N (always offered); false = partial via RunesWeights
        }
    }
}
