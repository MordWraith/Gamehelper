using System;
using System.Numerics;
using GameHelper.Localization;
using GameHelper.RemoteEnums;
using ImGuiNET;
using AuraTracker;

namespace AuraTracker.render;

internal sealed class SettingsUiRenderer
{
    private readonly string versionLabel;

    public SettingsUiRenderer(string version)
    {
        versionLabel = $"AuraTracker v{version} by Skrip";
    }

    public void Draw(AuraTrackerSettings settings, PluginLocalization text)
    {
        if (ImGui.CollapsingHeader(text.Title("section.general", "General", "AuraTrackerGeneral")))
        {
            if (ImGui.BeginTable("at_general", 2))
            {
                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("settings.draw_when_backgrounded", "Draw when game is backgrounded", "AuraTrackerDrawWhenBackgrounded"), ref settings.DrawWhenGameInBackground);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(180);
                ImGui.DragFloat(text.Label("settings.screen_range", "Screen Range (px)", "AuraTrackerScreenRange"), ref settings.ScreenRangePx, 5f, 100f, 3000f);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(180);
                ImGui.DragInt(text.Label("settings.max_enemies", "Max Enemies in List", "AuraTrackerMaxEnemies"), ref settings.MaxEnemies, 1f, 1, 12);

                ImGui.TableNextColumn();
                var rarityNames = AuraTrackerLocalization.RarityNames(text);
                int curIdx = (int)settings.MinRarityToShow;
                if (ImGui.Combo(text.Label("settings.min_rarity", "Min Rarity To Show", "AuraTrackerMinRarity"), ref curIdx, rarityNames, rarityNames.Length))
                {
                    settings.MinRarityToShow = (Rarity)Math.Clamp(curIdx, 0, 3);
                }

                ImGui.EndTable();
            }
        }

        if (ImGui.CollapsingHeader(text.Title("section.filters", "Filters", "AuraTrackerFilters")))
        {
            ImGui.Checkbox(text.Label("settings.only_beasts", "Only beasts (tamable)", "AuraTrackerOnlyBeasts"), ref settings.OnlyBeasts);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    text.T("settings.only_beasts.tooltip", "Wild / tamable beast monsters (Spirit Walker, Tame Beast). " +
                    "Matches the game's Beast monster category."));
            }

            ImGui.Spacing();
            ImGui.Checkbox(text.Label("settings.filter_by_auras", "Filter by auras / buffs", "AuraTrackerFilterByAuras"), ref settings.EnableAuraFilter);
            if (settings.EnableAuraFilter)
            {
                ImGui.Indent();
                ImGui.Checkbox(text.Label("settings.require_all_auras", "Require ALL listed auras", "AuraTrackerRequireAllAuras"), ref settings.AuraFilterMatchAll);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        text.T("settings.require_all_auras.tooltip", "Off: monster matches if it has any listed aura. On: monster must have every listed aura."));
                }

                ImGui.TextWrapped(
                    text.T("settings.aura_filter_help", "Match against the chip label (e.g. \"Frenzy\", \"Empowering\"). Case-insensitive substring."));

                for (int i = 0; i < settings.AuraFilters.Count; i++)
                {
                    ImGui.PushID(i);
                    ImGui.SetNextItemWidth(-70);
                    string pattern = settings.AuraFilters[i] ?? string.Empty;
                    if (ImGui.InputText("##aura", ref pattern, 128))
                    {
                        settings.AuraFilters[i] = pattern;
                    }

                    ImGui.SameLine();
                    if (ImGui.Button(text.Label("button.remove", "Remove", "AuraTrackerRemoveAuraFilter")))
                    {
                        settings.AuraFilters.RemoveAt(i);
                        ImGui.PopID();
                        i--;
                        continue;
                    }

                    ImGui.PopID();
                }

                if (ImGui.Button(text.Label("button.add_aura_filter", "Add aura filter", "AuraTrackerAddAuraFilter")))
                {
                    settings.AuraFilters.Add(string.Empty);
                }

                ImGui.Unindent();
            }
        }

        if (ImGui.CollapsingHeader(text.Title("section.list_layout", "List Layout", "AuraTrackerLayout")))
        {
            if (ImGui.BeginTable("at_layout", 2))
            {
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(220);
                ImGui.DragFloat2(text.Label("settings.left_anchor", "Left Anchor (x,y)", "AuraTrackerLeftAnchor"), ref settings.LeftAnchor, 1f, -4000, 4000);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(180);
                ImGui.DragFloat(text.Label("settings.entry_spacing", "Entry Spacing (px)", "AuraTrackerEntrySpacing"), ref settings.EntrySpacing, 0.5f, 0f, 80f);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(180);
                ImGui.DragFloat(text.Label("settings.bar_buff_spacing", "Bar->Buff Spacing (px)", "AuraTrackerBarBuffSpacing"), ref settings.BarToBuffSpacing, 0.5f, 0f, 40f);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(220);
                ImGui.DragFloat(text.Label("settings.panel_width", "Panel Width (px)", "AuraTrackerPanelWidth"), ref settings.PanelWidth, 1f, 120f, 1600f);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(220);
                ImGui.DragFloat(text.Label("settings.max_list_height", "Max List Height (px, 0 = overlay)", "AuraTrackerMaxListHeight"), ref settings.MaxListHeight, 5f, 0f, 8000f);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(220);
                ImGui.DragFloat(text.Label("settings.right_safe_margin", "Right Safe Margin (px)", "AuraTrackerRightSafeMargin"), ref settings.PanelRightSafeMargin, 0.5f, 0f, 120f);

                ImGui.EndTable();
            }
        }

        if (ImGui.CollapsingHeader(text.Title("section.bar_buffs", "Bar & Buffs", "AuraTrackerBar")))
        {
            if (ImGui.BeginTable("at_bar", 2))
            {
                ImGui.TableNextColumn();
                ImGui.ColorEdit4(text.Label("settings.bar_background", "Bar Background", "AuraTrackerBarBackground"), ref settings.BarBg);

                ImGui.TableNextColumn();
                ImGui.ColorEdit4(text.Label("settings.hp_fill", "HP Fill", "AuraTrackerHpFill"), ref settings.BarHpFill);

                ImGui.TableNextColumn();
                ImGui.ColorEdit4(text.Label("settings.es_fill", "ES Fill", "AuraTrackerEsFill"), ref settings.BarEsFill);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(180);
                ImGui.DragFloat2(text.Label("settings.bar_size", "Bar Size (w,h)", "AuraTrackerBarSize"), ref settings.BarSize, 1f, 80, 600);

                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("settings.hp_text_percent", "HP Text Shows Percent (instead of absolute)", "AuraTrackerHpTextPercent"), ref settings.ShowHpPercent);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(180);
                ImGui.DragFloat(text.Label("settings.buff_padding", "Buff Padding (px)", "AuraTrackerBuffPadding"), ref settings.BuffPad, 0.5f, 0f, 16f);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(180);
                ImGui.DragInt(text.Label("settings.max_buffs_per_enemy", "Max Buffs/Enemy", "AuraTrackerMaxBuffsPerEnemy"), ref settings.MaxBuffsPerEnemy, 1f, 1, 30);

                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("settings.show_buff_durations", "Show Buff Durations", "AuraTrackerShowBuffDurations"), ref settings.ShowDurations);

                ImGui.TableNextColumn();
                ImGui.SliderFloat(text.Label("settings.buff_bg_alpha", "Buff BG Alpha", "AuraTrackerBuffBgAlpha"), ref settings.BuffBgAlpha, 0.0f, 1.0f);

                ImGui.TableNextColumn();
                ImGui.SliderFloat(text.Label("settings.buff_text_scale", "Buff Text Scale", "AuraTrackerBuffTextScale"), ref settings.BuffTextScale, 0.5f, 2.0f);

                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("settings.show_dps_label", "Show DPS Label", "AuraTrackerShowDpsLabel"), ref settings.ShowDps);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(180);
                ImGui.DragFloat(text.Label("settings.dps_smoothing", "DPS Smoothing (s)", "AuraTrackerDpsSmoothing"), ref settings.DpsSmoothingSeconds, 0.05f, 0.1f, 5f);

                ImGui.TableNextColumn();
                ImGui.ColorEdit4(text.Label("settings.dps_text_color", "DPS Text Color", "AuraTrackerDpsTextColor"), ref settings.DpsTextColor);

                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("settings.show_overall_dps_header", "Show Overall DPS Header", "AuraTrackerShowOverallDpsHeader"), ref settings.ShowOverallDps);

                ImGui.EndTable();
            }
        }

        if (ImGui.CollapsingHeader(text.Title("section.chip_overrides", "Chip Color Overrides", "AuraTrackerChips")))
        {
            ImGui.SetNextItemWidth(150);
            ImGui.DragInt(text.Label("settings.chip_color_seed", "Chip Color Seed", "AuraTrackerChipColorSeed"), ref settings.ChipColorSeed, 1, 0, 1000);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    text.T("settings.chip_color_seed.tooltip", "Set the seed used for randomizing buff chip background colors.\nSame seed yields same color mapping each launch."));
            }

            ImGui.TextWrapped(
                text.T("settings.chip_override_help", "Add entries that match the chip's base text (without stacks or timer), e.g. \"Archnemesis\". The specified color overrides the random chip color. Alpha is ignored."));

            for (int i = 0; i < settings.ChipOverrides.Count; i++)
            {
                var item = settings.ChipOverrides[i];
                ImGui.PushID(i);

                if (ImGui.BeginTable("ovr_row", 3, ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn(text.T("table.text", "Text"), ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn(text.T("table.color", "Color"), ImGuiTableColumnFlags.WidthFixed, 180);
                    ImGui.TableSetupColumn(text.T("table.delete", "Del"), ImGuiTableColumnFlags.WidthFixed, 60);

                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    var match = item.Match ?? string.Empty;
                    if (ImGui.InputText("##txt", ref match, 256))
                    {
                        item.Match = match;
                    }

                    ImGui.TableNextColumn();
                    var rgb = new Vector3(item.Color.X, item.Color.Y, item.Color.Z);
                    if (ImGui.ColorEdit3("##col", ref rgb, ImGuiColorEditFlags.NoInputs))
                    {
                        item.Color = new Vector4(rgb.X, rgb.Y, rgb.Z, 1f);
                    }

                    ImGui.TableNextColumn();
                    if (ImGui.Button(text.Label("button.remove", "Remove", "AuraTrackerRemoveChipOverride")))
                    {
                        settings.ChipOverrides.RemoveAt(i);
                        ImGui.EndTable();
                        ImGui.PopID();
                        i--;
                        continue;
                    }

                    ImGui.EndTable();
                }

                settings.ChipOverrides[i] = item;
                ImGui.PopID();
            }

            if (ImGui.Button(text.Label("button.add_override", "Add Override", "AuraTrackerAddOverride")))
            {
                settings.ChipOverrides.Add(new AuraTrackerSettings.ChipColorOverride { Match = string.Empty, Color = new Vector4(1, 1, 1, 1) });
            }
        }

        if (ImGui.CollapsingHeader(text.Title("section.visuals", "Visuals", "AuraTrackerVisuals")))
        {
            if (ImGui.BeginTable("at_fx", 2))
            {
                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("settings.panel_shadow", "Panel Shadow", "AuraTrackerPanelShadow"), ref settings.FancyPanelShadow);
                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("settings.rarity_stripe", "Rarity Stripe", "AuraTrackerRarityStripe"), ref settings.FancyRarityStripe);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(200);
                ImGui.DragFloat(text.Label("settings.shadow_size", "Shadow Size", "AuraTrackerShadowSize"), ref settings.PanelShadowSize, 0.5f, 0f, 40f);
                ImGui.TableNextColumn();
                ImGui.SliderFloat(text.Label("settings.shadow_alpha", "Shadow Alpha", "AuraTrackerShadowAlpha"), ref settings.PanelShadowAlpha, 0f, 1f);

                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("settings.bar_gloss", "Bar Gloss", "AuraTrackerBarGloss"), ref settings.FancyBarGloss);
                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("settings.bar_inner_border", "Bar Inner Border", "AuraTrackerBarInnerBorder"), ref settings.FancyBarInnerBorder);

                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("settings.es_divider", "ES Divider", "AuraTrackerEsDivider"), ref settings.FancyEsDivider);
                ImGui.TableNextColumn();
                ImGui.SliderFloat(text.Label("settings.es_divider_alpha", "ES Divider Alpha", "AuraTrackerEsDividerAlpha"), ref settings.EsDividerAlpha, 0f, 1f);

                ImGui.TableNextColumn();
                ImGui.SliderFloat(text.Label("settings.bar_corner_radius", "Bar Corner Radius", "AuraTrackerBarCornerRadius"), ref settings.BarCornerRadius, 0f, 12f);
                ImGui.TableNextColumn();
                ImGui.SliderFloat(text.Label("settings.bar_inner_border_alpha", "Bar Inner Border Alpha", "AuraTrackerBarInnerBorderAlpha"), ref settings.BarInnerBorderAlpha, 0f, 1f);

                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("settings.chip_gloss", "Chip Gloss", "AuraTrackerChipGloss"), ref settings.FancyChipGloss);

                ImGui.TableNextColumn();
                ImGui.SliderFloat(text.Label("settings.chip_corner_radius", "Chip Corner Radius", "AuraTrackerChipCornerRadius"), ref settings.ChipCornerRadius, 0f, 12f);
                ImGui.TableNextColumn();
                ImGui.SliderFloat(text.Label("settings.chip_gloss_alpha", "Chip Gloss Alpha", "AuraTrackerChipGlossAlpha"), ref settings.ChipGlossAlpha, 0f, 1f);

                ImGui.EndTable();
            }
        }

        if (ImGui.CollapsingHeader(text.Title("section.list_background", "List Background", "AuraTrackerBg")))
        {
            if (ImGui.BeginTable("at_bg", 2))
            {
                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("settings.show_panel_background", "Show Panel Background", "AuraTrackerShowPanelBackground"), ref settings.ShowPanelBackground);
                ImGui.TableNextColumn();
                ImGui.ColorEdit4(text.Label("settings.panel_background_color", "Panel Background Color", "AuraTrackerPanelBackgroundColor"), ref settings.PanelBg);

                ImGui.TableNextColumn();
                ImGui.ColorEdit4(text.Label("settings.panel_border_color", "Panel Border Color", "AuraTrackerPanelBorderColor"), ref settings.PanelBorder);
                ImGui.TableNextColumn();
                ImGui.DragFloat2(text.Label("settings.panel_padding", "Panel Padding (x,y)", "AuraTrackerPanelPadding"), ref settings.PanelPadding, 0.5f, 0f, 40f);

                ImGui.TableNextColumn();
                ImGui.SliderFloat(text.Label("settings.panel_corner_radius", "Panel Corner Radius", "AuraTrackerPanelCornerRadius"), ref settings.PanelCornerRadius, 0f, 16f);
                ImGui.EndTable();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();

        float txtW = ImGui.CalcTextSize(versionLabel).X;
        float availW = ImGui.GetContentRegionAvail().X;
        float padX = MathF.Max(0f, (availW - txtW) * 0.5f);
        float curX = ImGui.GetCursorPosX();
        ImGui.SetCursorPosX(curX + padX);
        ImGui.TextDisabled(versionLabel);
    }
}
