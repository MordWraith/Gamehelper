namespace SekhemaHelper
{
    using GameHelper;
    using GameHelper.RemoteObjects.Components;
    using Newtonsoft.Json;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    // Empirical harness to REVERSE the server-side node->physical-room assignment.
    //
    // Established (Ghidra + live, 2026-07-09; see memory project-sekhema-floor-map, anchor
    // SanctumController): the client NEVER resolves a map node (layer,room) to a physical room. The
    // physical floor (SanctumPlugin picks, all 27 types+rewards) and the logical map graph (FloorData:
    // (layer,room) nodes + connections; types only for the ~12 rooms the server has revealed) are
    // unlinked client-side. The link lives on the server.
    //
    // HYPOTHESIS this harness tests: the assignment is a DETERMINISTIC function of the floor seed
    // (AreaHash) + the pick list, so it can be reversed from enough observed floors. We record, per
    // floor:
    //   - picks[]  : every physical room (raw SanctumRooms id + reward id + tile bbox).
    //   - nodes[]  : every FloorData node (layer, room, forward connections) + its revealed id/reward/
    //                affliction where the server has sent it (free node<->pick anchors, ~12/floor).
    //   - obs[]    : (current node) <-> (physical pick the player is standing in), one per room the
    //                player walks, covering the taken path INCLUDING rooms hidden from the content
    //                vector. These are the ground-truth pairs the offline analysis fits.
    // One floor = one JSON line appended to config\sekhema_research\dataset.jsonl.
    internal sealed class ResearchPick
    {
        public int PickIndex;
        public int RecordIndex;
        public string RoomId = string.Empty;    // raw "Ruins_Explore_06"
        public string RewardId = string.Empty;  // raw "Ruins_TreasureChestGold"
        public int TileX, TileY, TileW, TileH;
        public uint Rec90;                       // record +0x90 (flag + "index" @ +0x92) — candidate seed-order key
    }

    internal sealed class ResearchNode
    {
        public int Layer;
        public int Room;
        public List<int> Conn = new();           // forward connections = room indices in Layer+1
        // Revealed identity (empty when the server hasn't revealed this node) — raw ids from the
        // FloorData content vector, so they match ResearchPick.RoomId/RewardId exactly.
        public string RevRoomId = string.Empty;
        public string RevRewardId = string.Empty;
        public string RevAffliction = string.Empty;
    }

    internal sealed class ResearchObs
    {
        public int Layer = -1;                   // current node (FloorData PlayerLayer/PlayerRoom)
        public int Room = -1;
        public int PickIndex = -1;               // physical pick whose bbox contains the player
        public int RecordIndex = -1;
        public float PlayerX, PlayerY;           // player world-grid position at capture
    }

    internal sealed class ResearchFloor
    {
        public string AreaHash = string.Empty;   // floor seed identifier
        public int AreaLevel;
        public string CreatedUtc = string.Empty;
        public int PickCount;
        public List<ResearchPick> Picks = new();
        public List<ResearchNode> Nodes = new();
        public List<ResearchObs> Obs = new();
    }

    internal static class FloorResearch
    {
        private const int TileToGrid = 23;

        private static ResearchFloor current;
        public static string Status { get; private set; } = "no floor";

        // Snapshot the whole floor: physical picks + logical nodes (+ revealed identity). Call with a
        // fresh SekhemaFloor already classified by the core (so revealed types are populated).
        public static string NewFloor(SekhemaFloor floor)
        {
            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            var rf = new ResearchFloor
            {
                AreaHash = area?.AreaHash ?? string.Empty,
                AreaLevel = area?.CurrentAreaLevel ?? 0,
                CreatedUtc = DateTime.UtcNow.ToString("o"),
            };

            // Physical rooms (client terrain).
            var plots = FloorReader.Read();
            rf.PickCount = plots.PickCount;
            foreach (var p in plots.Rooms)
            {
                if (p.IsAirlock)
                    continue;
                rf.Picks.Add(new ResearchPick
                {
                    PickIndex = p.PickIndex,
                    RecordIndex = p.RecordIndex,
                    RoomId = p.RoomId,
                    RewardId = p.RewardId,
                    TileX = p.TileX,
                    TileY = p.TileY,
                    TileW = p.TileW,
                    TileH = p.TileH,
                    Rec90 = p.Rec90,
                });
            }

            // Logical map nodes + revealed identity from the content vector (raw ids).
            var reveals = ReadContentVector(floor.FloorDataAddr);
            for (int l = 0; l < floor.Layers.Count; l++)
            {
                for (int r = 0; r < floor.Layers[l].Count; r++)
                {
                    var room = floor.Layers[l][r];
                    var node = new ResearchNode { Layer = l, Room = r, Conn = new List<int>(room.NextConnections) };
                    if (reveals.TryGetValue((l, r), out var rev))
                    {
                        node.RevRoomId = rev.roomId;
                        node.RevRewardId = rev.rewardId;
                        node.RevAffliction = rev.affliction;
                    }
                    rf.Nodes.Add(node);
                }
            }

            current = rf;
            Status = $"floor {rf.AreaHash} lvl{rf.AreaLevel}: picks={rf.Picks.Count} nodes={rf.Nodes.Count} " +
                     $"revealed={rf.Nodes.Count(n => n.RevRoomId.Length > 0)} obs=0";
            return Status;
        }

        // Record the pairing (current node) <-> (physical room the player stands in) for this frame.
        public static string RecordRoom(SekhemaFloor floor)
        {
            if (current == null)
                return Status = "record: no floor snapshot (press New floor first)";

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (area?.Player == null || !area.Player.TryGetComponent<Render>(out var render))
                return Status = "record: no player render";

            float px = render.GridPosition.X;
            float py = render.GridPosition.Y;
            var obs = new ResearchObs
            {
                Layer = floor?.PlayerLayer ?? -1,
                Room = floor?.PlayerRoom ?? -1,
                PlayerX = px,
                PlayerY = py,
            };

            // Physical pick whose tile bbox (in world-grid units) contains the player.
            foreach (var p in current.Picks)
            {
                float x0 = p.TileX * TileToGrid, x1 = (p.TileX + p.TileW) * TileToGrid;
                float y0 = p.TileY * TileToGrid, y1 = (p.TileY + p.TileH) * TileToGrid;
                if (px >= x0 && px < x1 && py >= y0 && py < y1)
                {
                    obs.PickIndex = p.PickIndex;
                    obs.RecordIndex = p.RecordIndex;
                    break;
                }
            }

            current.Obs.Add(obs);
            Status = $"floor {current.AreaHash}: obs={current.Obs.Count} last=(node {obs.Layer},{obs.Room} -> pick {obs.PickIndex})";
            return Status;
        }

        // Append the current floor record to the dataset and clear it (ready for the next floor).
        public static string SaveFloor(string dir)
        {
            if (current == null)
                return Status = "save: no floor to save";
            try
            {
                Directory.CreateDirectory(dir);
                var line = JsonConvert.SerializeObject(current, Formatting.None);
                File.AppendAllText(Path.Join(dir, "dataset.jsonl"), line + Environment.NewLine);
                var saved = $"saved floor {current.AreaHash} (picks={current.Picks.Count} obs={current.Obs.Count}) -> dataset.jsonl";
                current = null;
                return Status = saved;
            }
            catch (Exception ex)
            {
                return Status = "save failed: " + ex.Message;
            }
        }

        // Raw content-vector read (FloorData+0x18, stride 0x40) -> (layer,room) => raw ids. Mirrors the
        // core's ClassifyRooms/DumpFk but keeps RAW SanctumRooms ids (no display/reward mapping) so the
        // offline analysis can match nodes to picks by exact id.
        private static Dictionary<(int, int), (string roomId, string rewardId, string affliction)> ReadContentVector(IntPtr floorData)
        {
            var map = new Dictionary<(int, int), (string, string, string)>();
            if (floorData == IntPtr.Zero)
                return map;
            var first = Mem.Read<IntPtr>(floorData + 0x18);
            var last = Mem.Read<IntPtr>(floorData + 0x20);
            if (first == IntPtr.Zero || last.ToInt64() <= first.ToInt64())
                return map;
            long count = (last.ToInt64() - first.ToInt64()) / 0x40;
            for (long i = 0; i < count && i < 512; i++)
            {
                var e = first + (int)(i * 0x40);
                int layer = Mem.Read<byte>(e + 0x00);
                int room = Mem.Read<byte>(e + 0x01);
                string roomId = string.Empty, rewardId = string.Empty, affliction = string.Empty;
                for (int k = 0; k < 3; k++)
                {
                    var rowPtr = Mem.Read<IntPtr>(e + 0x08 + k * 0x10);
                    var tablePtr = Mem.Read<IntPtr>(e + 0x10 + k * 0x10);
                    if (rowPtr == IntPtr.Zero || tablePtr == IntPtr.Zero)
                        continue;
                    var tpath = Mem.ReadWideString(Mem.Read<IntPtr>(tablePtr + 0x08), 96);
                    if (string.IsNullOrEmpty(tpath))
                        continue;
                    if (tpath.IndexOf("SanctumPersistentEffects", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var name = Mem.ReadWideString(Mem.Read<IntPtr>(rowPtr + 0x28), 48);
                        if (!string.IsNullOrEmpty(name))
                            affliction = name;
                    }
                    else if (tpath.IndexOf("SanctumRooms", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var id = Mem.ReadWideString(Mem.Read<IntPtr>(rowPtr + 0x00), 64);
                        if (string.IsNullOrEmpty(id))
                            continue;
                        if (id.IndexOf("Treasure", StringComparison.OrdinalIgnoreCase) >= 0)
                            rewardId = id;
                        else
                            roomId = id;
                    }
                }
                if (roomId.Length > 0 || rewardId.Length > 0 || affliction.Length > 0)
                    map[(layer, room)] = (roomId, rewardId, affliction);
            }
            return map;
        }
    }
}
