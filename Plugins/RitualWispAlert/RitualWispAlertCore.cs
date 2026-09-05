// <copyright file="RitualWispAlertCore.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace RitualWispAlert
{
    using System;
    using System.IO;
    using System.Numerics;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using ImGuiNET;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <summary>Draws the effective range around active Ritual wisps.</summary>
    public sealed class RitualWispAlertCore : PCore<RitualWispAlertSettings>
    {
        private const string WispMetadataPath = "Metadata/Monsters/LeagueRitual/RitualWispDaemon";
        private const int CircleSegments = 36;
        private const float GridUnitsPerMeter = 10f;
        private const float WorldUnitsPerGridUnit = 250f / 23f;

        private string SettingsPath => Path.Join(this.DllDirectory, "config", "settings.txt");

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingsPath))
            {
                try
                {
                    this.Settings = JsonConvert.DeserializeObject<RitualWispAlertSettings>(File.ReadAllText(this.SettingsPath))
                        ?? new RitualWispAlertSettings();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RitualWispAlert] Failed to load settings: {ex.Message}");
                    this.Settings = new RitualWispAlertSettings();
                }

                return;
            }

            if (this.TryMigrateRitualHelperSettings())
            {
                this.SaveSettings();
            }
        }

        private bool TryMigrateRitualHelperSettings()
        {
            var pluginsDirectory = Directory.GetParent(this.DllDirectory)?.FullName;
            if (pluginsDirectory == null) return false;

            var legacyPath = Path.Join(pluginsDirectory, "RitualHelper", "config", "settings.txt");
            if (!File.Exists(legacyPath)) return false;

            try
            {
                var legacy = JObject.Parse(File.ReadAllText(legacyPath));
                this.Settings.EnableOverlay = legacy.Value<bool?>("DrawWispCircle") ?? this.Settings.EnableOverlay;
                this.Settings.HideWhenGameUnfocusedOrPaused = legacy.Value<bool?>("HideWispCircleInBackgroundOrPaused") ?? this.Settings.HideWhenGameUnfocusedOrPaused;
                this.Settings.RadiusMeters = legacy.Value<float?>("WispCircleRadiusMeters") ?? this.Settings.RadiusMeters;
                this.Settings.Thickness = legacy.Value<float?>("WispCircleThickness") ?? this.Settings.Thickness;
                this.Settings.InsideColor = legacy["WispCircleColorInside"]?.ToObject<Vector4>() ?? this.Settings.InsideColor;
                this.Settings.OutsideColor = legacy["WispCircleColorOutside"]?.ToObject<Vector4>() ?? this.Settings.OutsideColor;
                this.Settings.OffsetX = legacy.Value<float?>("WispCircleOffsetX") ?? this.Settings.OffsetX;
                this.Settings.OffsetY = legacy.Value<float?>("WispCircleOffsetY") ?? this.Settings.OffsetY;
                this.Settings.OffsetZ = legacy.Value<float?>("WispCircleOffsetZ") ?? this.Settings.OffsetZ;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RitualWispAlert] Failed to migrate RitualHelper settings: {ex.Message}");
                return false;
            }
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.SettingsPath) ?? string.Empty);
                File.WriteAllText(this.SettingsPath, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RitualWispAlert] Failed to save settings: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            ImGui.Checkbox(this.PluginText.Label("settings.enable_overlay", "Draw Ritual wisp range circle", "RitualWispEnableOverlay"), ref this.Settings.EnableOverlay);
            ImGui.Checkbox(this.PluginText.Label("settings.hide_unfocused_or_paused", "Hide while the game is unfocused or paused", "RitualWispHideUnfocused"), ref this.Settings.HideWhenGameUnfocusedOrPaused);
            ImGui.DragFloat(this.PluginText.Label("settings.radius", "Circle radius (metres)", "RitualWispRadius"), ref this.Settings.RadiusMeters, 0.1f, 0.5f, 10f, "%.1f m");
            ImGui.DragFloat(this.PluginText.Label("settings.thickness", "Circle thickness", "RitualWispThickness"), ref this.Settings.Thickness, 0.1f, 0.5f, 10f, "%.1f px");
            ImGui.ColorEdit4(this.PluginText.Label("settings.inside_color", "Color while inside range", "RitualWispInsideColor"), ref this.Settings.InsideColor);
            ImGui.ColorEdit4(this.PluginText.Label("settings.outside_color", "Color while outside range", "RitualWispOutsideColor"), ref this.Settings.OutsideColor);
            ImGui.DragFloat(this.PluginText.Label("settings.offset_x", "Center X offset", "RitualWispOffsetX"), ref this.Settings.OffsetX, 0.5f, -500f, 500f, "%.1f");
            ImGui.DragFloat(this.PluginText.Label("settings.offset_y", "Center Y offset", "RitualWispOffsetY"), ref this.Settings.OffsetY, 0.5f, -500f, 500f, "%.1f");
            ImGui.DragFloat(this.PluginText.Label("settings.offset_z", "Height offset", "RitualWispOffsetZ"), ref this.Settings.OffsetZ, 0.5f, -500f, 500f, "%.1f");
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
            if (!this.Settings.EnableOverlay ||
                Core.States.GameCurrentState is not (GameStateTypes.InGameState or GameStateTypes.EscapeState)) return;

            if (this.Settings.HideWhenGameUnfocusedOrPaused &&
                (!Core.Process.Foreground || Core.States.GameCurrentState != GameStateTypes.InGameState)) return;

            var inGameState = Core.States.InGameStateObject;
            var areaInstance = inGameState.CurrentAreaInstance;
            var player = areaInstance.Player;
            var worldInstance = inGameState.CurrentWorldInstance;
            if (player == null || worldInstance == null) return;

            var drawList = ImGui.GetBackgroundDrawList();
            foreach (var entity in areaInstance.AwakeEntities.Values)
            {
                if (!entity.IsValid ||
                    entity.Path?.Contains(WispMetadataPath, StringComparison.OrdinalIgnoreCase) != true ||
                    !entity.TryGetComponent<Render>(out var render)) continue;

                var radiusGrid = this.Settings.RadiusMeters * GridUnitsPerMeter;
                var circleColor = player.DistanceFrom(entity) <= radiusGrid
                    ? this.Settings.InsideColor
                    : this.Settings.OutsideColor;
                var worldPosition = render.WorldPosition;
                var center = new Vector3(
                    worldPosition.X + this.Settings.OffsetX,
                    worldPosition.Y + this.Settings.OffsetY,
                    worldPosition.Z + this.Settings.OffsetZ);

                DrawCircle(
                    drawList,
                    inGameState,
                    center,
                    render.TerrainHeight + this.Settings.OffsetZ,
                    radiusGrid * WorldUnitsPerGridUnit,
                    ImGuiHelper.Color(circleColor),
                    this.Settings.Thickness);
            }
        }

        private static void DrawCircle(
            ImDrawListPtr drawList,
            InGameState inGameState,
            Vector3 center,
            float terrainHeight,
            float radius,
            uint color,
            float thickness)
        {
            var worldInstance = inGameState.CurrentWorldInstance;
            if (worldInstance == null) return;

            var points = new Vector2[CircleSegments];
            var validPoints = 0;
            for (var index = 0; index < CircleSegments; index++)
            {
                var angle = index * 2f * MathF.PI / CircleSegments;
                var screenPosition = worldInstance.WorldToScreen(
                    new StdTuple3D<float>
                    {
                        X = center.X + (radius * MathF.Cos(angle)),
                        Y = center.Y + (radius * MathF.Sin(angle)),
                        Z = terrainHeight,
                    },
                    terrainHeight);
                if (screenPosition != Vector2.Zero)
                {
                    points[validPoints++] = screenPosition;
                }
            }

            for (var index = 0; index < validPoints; index++)
            {
                var start = points[index];
                var end = points[(index + 1) % validPoints];
                if (Vector2.Distance(start, end) < 500f)
                {
                    drawList.AddLine(start, end, color, thickness);
                }
            }
        }
    }
}
