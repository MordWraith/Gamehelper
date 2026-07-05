namespace SekhemaHelper
{
    using GameHelper;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.Utils;
    using ImGuiNET;
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    // Marks Sanctum room interactables on the large map: hazard Portals and the generic Lever, with their
    // used/activated state (SEKHEMA_WIP §12). Both carry a StateMachine component; the byte at
    // StateMachine+0x10 reads 0 while the object is still "open"/un-activated and 1 once it has been
    // used/closed (confirmed via A/B on 3 portals + the lever, §12.1/§12.2). The byte briefly flickers
    // during the close/pull animation, which is harmless for a visual overlay.
    //
    // Drawing reuses HazardRoute/ChestPriority's large-map (Radar) projection so placement matches the
    // rest of the plugin.
    internal static class RoomObjects
    {
        // EXACT metadata paths (case-sensitive). A substring like "/Hazards/Portal" also catches monster
        // entities that share the prefix, so match the full path verbatim. Only PortalPlatform is marked
        // (PortalRitual is intentionally excluded).
        private const string PortalPlatformPath = "Metadata/Monsters/MarakethSanctumTrial/Hazards/PortalPlatform";
        private const string LeverPath = "Metadata/Terrain/Gallows/Leagues/Sanctum/Objects/SanctumGenericLever";

        // StateMachine+0x10: 0 = active / not yet used, 1 = used / closed / activated (§12).
        // CAVEAT (observed live): this byte is TRANSIENT — the game only holds it at 1 while the object is
        // inside the player's network bubble; walk away and it reverts to 0, so a closed portal would wrongly
        // re-appear as "open". Fix: LATCH it — once an object is seen used (==1) we remember its entity id and
        // keep showing it used for the rest of the area. The latch is cleared on area change (AreaHash).
        private const int StateMachineUsedByteOffset = 0x10;

        private static readonly HashSet<uint> latchedUsed = new();
        private static string latchAreaHash = string.Empty;

        // Calibration copied from HazardRoute/ChestPriority/Radar.
        private const float LargeMapXBias = 0.6f;
        private const float LargeMapYBias = 0.3f;
        private const float LargeMapScaleBaseline = 0.187812f;
        private static readonly double CameraAngle = 38.7 * Math.PI / 180;

        public static void Draw(SekhemaHelperSettings settings)
        {
            if (settings == null || (!settings.ShowPortals && !settings.ShowLevers))
                return;
            try { DrawInner(settings); }
            catch { /* never bubble a draw exception into the host */ }
        }

        private static void DrawInner(SekhemaHelperSettings settings)
        {
            var gameUi = Core.States.InGameStateObject.GameUi;
            var largeMap = gameUi?.LargeMap;
            if (largeMap == null || !largeMap.IsVisible || gameUi.WorldMapPanel.IsVisible)
                return;

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (area?.Player == null || !area.Player.TryGetComponent<Render>(out var playerRender))
                return;
            var player = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);
            float playerHeight = playerRender.TerrainHeight;

            // Reset the used-latch when the area changes (entity ids are per-area).
            if (area.AreaHash != latchAreaHash)
            {
                latchedUsed.Clear();
                latchAreaHash = area.AreaHash;
            }

            // ---- Radar large-map projection (1:1 with ChestPriority) ----
            var baseRes = GameOffsets.Objects.UiElement.UiElementBaseFuncs.BaseResolution;
            double baseDiag = Math.Sqrt(((double)baseRes.X * baseRes.X) + ((double)baseRes.Y * baseRes.Y));
            double diag = baseDiag * largeMap.Size.Y / baseRes.Y;
            if (diag <= 0)
                return;
            float scale = largeMap.Zoom * LargeMapScaleBaseline;
            if (scale <= 0)
                return;
            float mapScale = 240f / scale;
            float cos = (float)(diag * Math.Cos(CameraAngle) / mapScale);
            float sin = (float)(diag * Math.Sin(CameraAngle) / mapScale);
            var center = largeMap.Center + largeMap.Shift + largeMap.DefaultShift;
            center.X += LargeMapXBias;
            center.Y += LargeMapYBias;

            var dl = ImGui.GetForegroundDrawList();
            uint portalColor = ImGuiHelper.Color(settings.PortalColor);
            uint leverColor = ImGuiHelper.Color(settings.LeverColor);
            uint labelBg = ImGuiHelper.Color(new Vector4(0f, 0f, 0f, 0.65f));
            uint labelFg = ImGuiHelper.Color(new Vector4(1f, 1f, 1f, 1f));
            var font = ImGui.GetFont();
            float fontPx = ImGui.GetFontSize();
            float radius = settings.RoomObjectMarkerRadius;

            foreach (var kv in area.AwakeEntities)
            {
                var e = kv.Value;
                if (e == null || string.IsNullOrEmpty(e.Path))
                    continue;

                bool isPortal = settings.ShowPortals && string.Equals(e.Path, PortalPlatformPath, StringComparison.Ordinal);
                bool isLever = settings.ShowLevers && string.Equals(e.Path, LeverPath, StringComparison.Ordinal);
                if (!isPortal && !isLever)
                    continue;
                if (!e.TryGetComponent<Render>(out var r))
                    continue;

                // Used/activated state from StateMachine+0x10. Once seen used, LATCH it so the marker stays
                // removed even after the player walks away and the transient byte reverts to 0 (the bug:
                // a closed portal otherwise re-appeared as active). Used objects are NOT drawn.
                bool used = latchedUsed.Contains(e.Id);
                if (!used && e.TryGetComponent<StateMachine>(out var sm) && sm.Address != IntPtr.Zero &&
                    Mem.Read<byte>(sm.Address + StateMachineUsedByteOffset) != 0)
                {
                    latchedUsed.Add(e.Id);
                    used = true;
                }
                if (used)
                    continue;   // closed portal / pulled lever — remove the marker

                var grid = new Vector2(r.GridPosition.X, r.GridPosition.Y);
                var delta = grid - player;
                float deltaZ = (r.TerrainHeight - playerHeight) / 10.86957f;
                var screen = center + new Vector2((delta.X - delta.Y) * cos, (deltaZ - (delta.X + delta.Y)) * sin);

                dl.AddCircleFilled(screen, radius, isPortal ? portalColor : leverColor, 18);
                dl.AddCircle(screen, radius, labelFg, 18, 1.5f);

                string text = isPortal ? "Portal" : "Lever";
                var ts = ImGui.CalcTextSize(text);
                var at = new Vector2(screen.X - (ts.X * 0.5f), screen.Y - radius - ts.Y - 3f);
                var pad = new Vector2(3f, 1f);
                dl.AddRectFilled(at - pad, at + ts + pad, labelBg, 2f);
                dl.AddText(font, fontPx, at, labelFg, text);
            }
        }
    }
}
