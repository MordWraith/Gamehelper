namespace SekhemaHelper
{
    using GameHelper;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using ImGuiNET;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Text;

    // Reads the WHOLE Sekhema floor straight from the client's terrain-generation data — every room's
    // TYPE + REWARD + world position, from the first step, INCLUDING rooms the UI hasn't revealed and
    // rooms hidden by a "map obscured / room type unknown" affliction (the terrain must be built
    // physically, so the client always knows the layout). This is strictly more than the UI-driven
    // SekhemaReader (FloorData), which only sees revealed rooms.
    //
    // Chain (live-verified HF4 2026-07-09; full write-up: obsidian poe2/Sekhema.md §Room placement):
    //   AreaInstance +0xC8  std::vector<terrainPlugin*>   (0.5.5; was 0xD0)
    //       -> element whose +0x08(u16) == 0x20B5 (fold16 hash of "SanctumPlugin") = SanctumPlugin
    //   SanctumPlugin +0x50 std::vector<pick, stride 0x20> = {row, table, row2, table2}
    //       row  -> +0x00 = wchar* id ("Ruins_Explore_06")        => room TYPE token
    //       row2 -> +0x00 = wchar* id ("Ruins_TreasureLegendBoon") => REWARD token
    //   AreaInstance +0x4C0 std::vector<record, stride 0x98>   (0.5.5; was the +0x498 inline holder)
    //       record[0] = airlock, record[1..N] <-> pick[0..N-1] (build order; index @ +0x92)
    //       +0x40 posX  +0x44 posY  +0x48 w  +0x4C h   (TILES; * 23 = world-grid, == Render.GridPosition units)
    //       +0x58/+0x60 door vector (connectivity source; element layout still being RE'd -> Dump()).
    internal sealed class PlotRoom
    {
        public const float TileToGrid = 23f;

        public int RecordIndex;          // 0 = airlock, 1.. = rooms (matches pick index + 1)
        public int PickIndex = -1;       // RecordIndex - 1; -1 for the airlock
        public bool IsAirlock;

        public string RoomId = string.Empty;    // SanctumRooms id, e.g. "Ruins_Explore_06"
        public string TypeToken = string.Empty; // "Explore"/"Boss"/"Arena"/"Lair"/...
        public string RewardId = string.Empty;
        public string RewardToken = string.Empty;

        public int TileX, TileY, TileW, TileH;
        public uint Rec90;               // record +0x90 dword (rotation/flag + "index" @ +0x92) — research
        public IntPtr DoorFirst, DoorLast;
        public int DoorBytes => (int)Math.Max(0, DoorLast.ToInt64() - DoorFirst.ToInt64());

        public Vector2 CenterGrid =>
            new((TileX + (TileW * 0.5f)) * TileToGrid, (TileY + (TileH * 0.5f)) * TileToGrid);

        public string Label =>
            string.IsNullOrEmpty(RewardToken) ? TypeToken : $"{TypeToken}\n{RewardToken}";
    }

    internal sealed class FloorPlots
    {
        public bool IsValid;
        public string Status = string.Empty;
        public IntPtr SanctumPlugin;
        public int PickCount;
        public readonly List<PlotRoom> Rooms = new(); // includes airlock at [0]
    }

    internal static class FloorReader
    {
        // 0.5.5: 0xD0 -> 0xC8 (AreaInstance's early region shifted -8). Verified live: +0xC8 holds
        // {first,last,end} spanning one pointer, and that pointer's +0x08 reads 0x20B5 = SanctumPlugin.
        // The stale 0xD0 read {last,end} as {first,last} -> span 0 -> "no SanctumPlugin".
        private const int Area_TerrainPluginsVec = 0xC8;   // std::vector<plugin*>

        // 0.5.5: the placement records are now a PLAIN vector at AreaInstance+0x4C0 -- the old inline
        // holder at +0x498 (vec at holder+0x10/+0x18) is gone. Verified by content, not by shape alone:
        // the vector holds 25 elements of stride 0x98 with capacity 28, and 25 == 24 picks + 1 airlock;
        // record[0] reads pos (0,0) size (24,36) and record[1] pos (25,0) size (60,36) -- adjacent tile
        // rects sharing the y=0 row. The record layout itself did NOT move (pos +0x40, size +0x48).
        //
        // NOTE: upstream GameOffsets declares AreaInstance.Environments at this very offset. It cannot
        // be both, and the content here (elements of 0x98 bytes starting with pointers) is not the
        // `int Key` vector Environments is declared as -- see the drift note.
        private const int Area_PlacementVec = 0x4C0;
        private const int Vec_First = 0x00;
        private const int Vec_Last = 0x08;

        private const int Plugin_TypeId = 0x08;            // u16
        private const ushort SanctumPluginTypeId = 0x20B5; // fold16("SanctumPlugin")
        private const int Plugin_PicksVec = 0x50;          // std::vector<pick 0x20>

        private const int PickStride = 0x20;
        private const int Pick_Row = 0x00;
        private const int Pick_Row2 = 0x10;

        private const int RecordStride = 0x98;
        private const int Rec_PosX = 0x40, Rec_PosY = 0x44, Rec_W = 0x48, Rec_H = 0x4C;
        private const int Rec_DoorFirst = 0x58, Rec_DoorLast = 0x60;
        private const int Rec_Index90 = 0x90;   // dword: bit7 map-stat flag + "index" @ +0x92 (research)

        // Map projection (1:1 with RoomObjects/ChestPriority/Radar).
        private const float LargeMapXBias = 0.6f;
        private const float LargeMapYBias = 0.3f;
        private const float LargeMapScaleBaseline = 0.187812f;
        private static readonly double CameraAngle = 38.7 * Math.PI / 180;

        public static FloorPlots Read()
        {
            var fp = new FloorPlots();
            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (area == null || area.Address == IntPtr.Zero)
            {
                fp.Status = "no area";
                return fp;
            }
            var areaAddr = area.Address;

            // 1) locate SanctumPlugin in the terrain-plugin vector.
            var plugin = IntPtr.Zero;
            var pv = Mem.Read<StdVector>(areaAddr + Area_TerrainPluginsVec);
            if (pv.First != IntPtr.Zero && pv.Last.ToInt64() > pv.First.ToInt64())
            {
                long n = (pv.Last.ToInt64() - pv.First.ToInt64()) / 8;
                for (long i = 0; i < n && i < 32; i++)
                {
                    var cand = Mem.Read<IntPtr>(pv.First + (int)(i * 8));
                    if (cand != IntPtr.Zero && Mem.Read<ushort>(cand + Plugin_TypeId) == SanctumPluginTypeId)
                    {
                        plugin = cand;
                        break;
                    }
                }
            }
            if (plugin == IntPtr.Zero)
            {
                fp.Status = "no SanctumPlugin (not in a Sanctum floor?)";
                return fp;
            }
            fp.SanctumPlugin = plugin;

            // 2) picks -> per-room identity (type + reward).
            var picks = Mem.Read<StdVector>(plugin + Plugin_PicksVec);
            var ids = new List<(string room, string reward)>();
            if (picks.First != IntPtr.Zero && picks.Last.ToInt64() > picks.First.ToInt64())
            {
                fp.PickCount = (int)((picks.Last.ToInt64() - picks.First.ToInt64()) / PickStride);
                for (int i = 0; i < fp.PickCount && i < 64; i++)
                {
                    var e = picks.First + (i * PickStride);
                    ids.Add((ReadRoomId(Mem.Read<IntPtr>(e + Pick_Row)),
                             ReadRoomId(Mem.Read<IntPtr>(e + Pick_Row2))));
                }
            }

            // 3) records -> positions + door vectors. record[k] <-> pick[k-1].
            var recFirst = Mem.Read<IntPtr>(areaAddr + Area_PlacementVec + Vec_First);
            var recLast = Mem.Read<IntPtr>(areaAddr + Area_PlacementVec + Vec_Last);
            if (recFirst == IntPtr.Zero || recLast.ToInt64() <= recFirst.ToInt64())
            {
                fp.Status = $"plugin ok, no records (picks={fp.PickCount})";
                fp.IsValid = ids.Count > 0;
                return fp;
            }
            int recCount = (int)((recLast.ToInt64() - recFirst.ToInt64()) / RecordStride);
            for (int i = 0; i < recCount && i < 128; i++)
            {
                var rec = recFirst + (i * RecordStride);
                var pr = new PlotRoom
                {
                    RecordIndex = i,
                    IsAirlock = i == 0,
                    PickIndex = i - 1,
                    TileX = Mem.Read<int>(rec + Rec_PosX),
                    TileY = Mem.Read<int>(rec + Rec_PosY),
                    TileW = Mem.Read<int>(rec + Rec_W),
                    TileH = Mem.Read<int>(rec + Rec_H),
                    Rec90 = Mem.Read<uint>(rec + Rec_Index90),
                    DoorFirst = Mem.Read<IntPtr>(rec + Rec_DoorFirst),
                    DoorLast = Mem.Read<IntPtr>(rec + Rec_DoorLast),
                };
                if (!pr.IsAirlock && pr.PickIndex >= 0 && pr.PickIndex < ids.Count)
                {
                    pr.RoomId = ids[pr.PickIndex].room;
                    pr.TypeToken = TokenOf(pr.RoomId);
                    pr.RewardId = ids[pr.PickIndex].reward;
                    pr.RewardToken = TokenOf(pr.RewardId);
                }
                else if (pr.IsAirlock)
                {
                    pr.TypeToken = "Airlock";
                }
                fp.Rooms.Add(pr);
            }

            fp.IsValid = fp.Rooms.Count > 0;
            fp.Status = $"plugin=0x{plugin.ToInt64():X} picks={fp.PickCount} records={fp.Rooms.Count}";
            return fp;
        }

        // A SanctumRooms row stores its id at +0x00 as a pointer to a null-terminated wchar[] (a raw
        // C string, NOT an MSVC std::wstring — so ReadStdWString returns empty here; deref then read).
        private static string ReadRoomId(IntPtr row)
        {
            if (row == IntPtr.Zero)
                return string.Empty;
            var strPtr = Mem.Read<IntPtr>(row);
            return strPtr == IntPtr.Zero ? string.Empty : Mem.ReadWideString(strPtr, 64);
        }

        // "Ruins_Explore_06" -> "Explore"; "Ruins_Boss_01" -> "Boss";
        // "Ruins_TreasureLegendBoon" -> "TreasureLegendBoon". Drops the leading area prefix and a
        // trailing pure-number segment; joins the rest.
        internal static string TokenOf(string id)
        {
            if (string.IsNullOrEmpty(id))
                return string.Empty;
            var parts = id.Split('_');
            if (parts.Length < 2)
                return id;
            int start = 1; // skip area prefix (Ruins / Caverns / ...)
            int end = parts.Length;
            if (end - 1 > start && parts[end - 1].Length > 0 && parts[end - 1].All(char.IsDigit))
                end--;
            return string.Join("_", parts, start, end - start);
        }

        // --- Debug overlay: draw every room (incl. hidden) at its world position on the large map ---
        public static void Draw(SekhemaHelperSettings settings)
        {
            if (settings == null || !settings.DebugEnable || !settings.DebugDrawFloorPlots)
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

            var floor = Read();
            if (!floor.IsValid)
                return;

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

            Vector2 Project(Vector2 grid)
            {
                var d = grid - player;
                return center + new Vector2((d.X - d.Y) * cos, -(d.X + d.Y) * sin);
            }

            var dl = ImGui.GetForegroundDrawList();
            uint roomColor = ImGuiHelper.Color(new Vector4(0.35f, 0.75f, 1f, 0.95f));
            uint airlockColor = ImGuiHelper.Color(new Vector4(1f, 0.85f, 0.2f, 0.95f));
            uint bossColor = ImGuiHelper.Color(new Vector4(1f, 0.35f, 0.35f, 0.95f));
            uint labelBg = ImGuiHelper.Color(new Vector4(0f, 0f, 0f, 0.7f));
            uint labelFg = ImGuiHelper.Color(new Vector4(1f, 1f, 1f, 1f));
            var font = ImGui.GetFont();
            float fontPx = ImGui.GetFontSize();

            foreach (var r in floor.Rooms)
            {
                var at = Project(r.CenterGrid);
                uint col = r.IsAirlock ? airlockColor
                    : r.TypeToken.Equals("Boss", StringComparison.OrdinalIgnoreCase) ? bossColor
                    : roomColor;
                dl.AddCircleFilled(at, 7f, col, 20);
                dl.AddCircle(at, 7f, labelFg, 20, 1.5f);

                string text = string.IsNullOrEmpty(r.Label) ? $"#{r.RecordIndex}" : r.Label;
                var ts = ImGui.CalcTextSize(text);
                var tp = new Vector2(at.X - (ts.X * 0.5f), at.Y - 10f - ts.Y);
                var pad = new Vector2(3f, 1f);
                dl.AddRectFilled(tp - pad, tp + ts + pad, labelBg, 2f);
                dl.AddText(font, fontPx, tp, labelFg, text);
            }
        }

        // Dumps the whole floor + raw door-vector bytes to config\sekhema_floor_dump.txt so the door
        // element layout (connectivity edges) can be reverse-engineered offline against the map screenshot.
        public static void Dump(string path)
        {
            var sb = new StringBuilder();
            var floor = Read();
            sb.AppendLine($"# Sekhema floor dump  status={floor.Status}");
            sb.AppendLine($"# plugin=0x{floor.SanctumPlugin.ToInt64():X} picks={floor.PickCount} records={floor.Rooms.Count}");
            sb.AppendLine();
            foreach (var r in floor.Rooms)
            {
                sb.AppendLine($"[{r.RecordIndex}] pick={r.PickIndex} {(r.IsAirlock ? "AIRLOCK" : "")}");
                sb.AppendLine($"    id='{r.RoomId}' type='{r.TypeToken}'  reward='{r.RewardId}' rtoken='{r.RewardToken}'");
                sb.AppendLine($"    tile pos=({r.TileX},{r.TileY}) size=({r.TileW},{r.TileH})  centerGrid=({r.CenterGrid.X:F0},{r.CenterGrid.Y:F0})");
                sb.AppendLine($"    doors first=0x{r.DoorFirst.ToInt64():X} last=0x{r.DoorLast.ToInt64():X} bytes={r.DoorBytes}");
                if (r.DoorFirst != IntPtr.Zero && r.DoorBytes > 0)
                {
                    int n = Math.Min(r.DoorBytes, 256);
                    var bytes = Mem.ReadBytes(r.DoorFirst, n);
                    sb.AppendLine("    door-bytes: " + BitConverter.ToString(bytes).Replace("-", " "));
                }
                sb.AppendLine();
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, sb.ToString());
            }
            catch { /* ignore */ }
        }
    }
}
