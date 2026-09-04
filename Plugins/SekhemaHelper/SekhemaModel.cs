using GameHelper.RemoteObjects.UiElement;
using GameOffsets.Natives;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SekhemaHelper
{
    // One room of the Trial floor. Index pair (Layer, Index) is the identity used everywhere.
    public sealed class SekhemaRoom
    {
        public int Layer;
        public int Index;
        public readonly List<int> NextConnections = new(); // room indices in Layer+1
        public bool IsChosen;          // chosen room in its layer (player path)
        public IntPtr Address;         // FloorData room struct (stride 0x38)
        public IntPtr ContentFk;       // widget +0x4D8 (reward/affliction FK) — for RoomClassifier
        public IntPtr WidgetAddr;      // room widget UiElement (content block at +0x4D8)

        public bool HasWidget;
        public Vector2 ScreenPos;      // widget top-left (screen space)
        public Vector2 ScreenSize;

        // Filled by RoomClassifier; null until FK->names is wired (SEKHEMA_WIP §8.12).
        public string RoomType;
        public string Reward;
        // The affliction this room imposes (a "Curse"-type reward names a specific affliction). Read
        // from the FloorData content vector via SanctumPersistentEffects.dat (name @ row+0x28) — pure
        // game data, NOT the player's active-effect state. Drives AfflictionWeight in WeightCalculator.
        public string Affliction;

        public Vector2 ScreenCenter => ScreenPos + ScreenSize * 0.5f;
    }

    public sealed class SekhemaFloor
    {
        public readonly List<List<SekhemaRoom>> Layers = new();
        public int PlayerLayer = -1;
        public int PlayerRoom = -1;
        public bool IsValid;

        // diagnostics (for the debug HUD)
        public IntPtr MapElement;
        public IntPtr FloorObj;
        public IntPtr FloorDataAddr;
        public int WidgetCount;
        public string Status = "";

        public SekhemaRoom Get(int layer, int room) =>
            (layer >= 0 && layer < Layers.Count && room >= 0 && room < Layers[layer].Count)
                ? Layers[layer][room] : null;
    }

    // Reads the runtime FloorData via the chain validated in SEKHEMA_WIP §8.11:
    //   panel(=mapElement) +0x3B8 -> floorObj -> +0x1F8 (or +0x1B0) = FloorData
    public static class SekhemaReader
    {
        // 0.5.5: 0x3B8 -> 0x3A0. mapElement is a UiElement (GameUi.child[84], the PARENT of the map
        // panel), so its own fields ride behind UiElementBase -- which lost 0x18 this patch. Verified by
        // following it: floorObj+0x1B0 gives a FloorData whose layers vector holds 8 layers, whose
        // content vector (FloorData+0x18) holds 14 entries of stride 0x40, whose choices array reads
        // `00 FF FF FF FF FF FF FF` (8 layers, first chosen) and whose counter reads 1 = one choice
        // made. The stale 0x3B8 now reads 0, so the whole plugin degraded to "no FloorData".
        private const int MapElement_FloorObjPtr = 0x3A0; // field [0x74] (0.5.5; was 0x3B8)
        private const int FloorObj_Flag25a = 0x25a;
        private const int FloorData_OffActive = 0x1F8;
        private const int FloorData_OffAlt = 0x1B0;
        private const int FloorData_Layers = 0x00;        // StdVector, stride 0x20
        private const int FloorData_Choices = 0x38;       // byte[layer], 0xFF = none
        private const int FloorData_Counter = 0x40;       // &7 = choices made
        private const int LayerStride = 0x20;
        private const int LayerRooms = 0x00;              // StdVector, stride 0x38
        private const int RoomStride = 0x38;
        private const int RoomConnFirst = 0x00;
        private const int RoomConnLast = 0x08;
        // DEAD as of the §4.4 fix: room classification reads the FloorData content vector instead, and
        // ContentFk is written but never consumed. Left at its 0.5.4 value ON PURPOSE -- it is a
        // UI-widget field, so it certainly drifted, and guessing a new one would only manufacture a
        // plausible-looking pointer. Re-verify before reviving any use of it.
        private const int WidgetContent = 0x4D8;

        private const int UiElement_ParentPtr = 0xB8;

        public static SekhemaFloor Read(UiElementBase panel)
        {
            var floor = new SekhemaFloor();
            if (panel == null || panel.Address == IntPtr.Zero)
            {
                floor.Status = "panel null";
                return floor;
            }

            // Resolve FloorData. panel is expected to be mapElement (its field [0x77] = floorObj);
            // if that doesn't validate, fall back to panel's UI parent (handles the panel==child case).
            if (!TryResolveFloorData(panel.Address, out var mapElement, out var floorObj, out var floorData, out int layerCount))
            {
                var parent = Mem.Read<IntPtr>(panel.Address + UiElement_ParentPtr);
                if (!TryResolveFloorData(parent, out mapElement, out floorObj, out floorData, out layerCount))
                {
                    floor.Status = $"no FloorData (panel 0x{panel.Address.ToInt64():X}, floorObj 0x{floorObj.ToInt64():X})";
                    floor.MapElement = panel.Address;
                    floor.FloorObj = floorObj;
                    return floor;
                }
            }
            floor.MapElement = mapElement;
            floor.FloorObj = floorObj;
            floor.FloorDataAddr = floorData;

            var layersVec = Mem.Read<StdVector>(floorData + FloorData_Layers);

            for (int li = 0; li < layerCount; li++)
            {
                var layerAddr = layersVec.First + (li * LayerStride);
                var roomsVec = Mem.Read<StdVector>(layerAddr + LayerRooms);
                int roomCount = VecCount(roomsVec, RoomStride);

                var rooms = new List<SekhemaRoom>(roomCount);
                for (int ri = 0; ri < roomCount && ri < 64; ri++)
                {
                    var roomAddr = roomsVec.First + (ri * RoomStride);
                    var r = new SekhemaRoom { Layer = li, Index = ri, Address = roomAddr };

                    var cf = Mem.Read<IntPtr>(roomAddr + RoomConnFirst);
                    var cl = Mem.Read<IntPtr>(roomAddr + RoomConnLast);
                    long cnt = cl.ToInt64() - cf.ToInt64();
                    if (cf != IntPtr.Zero && cnt > 0 && cnt <= 16)
                    {
                        var bytes = Mem.ReadBytes(cf, (int)cnt);
                        foreach (var b in bytes)
                            r.NextConnections.Add(b);
                    }
                    rooms.Add(r);
                }
                floor.Layers.Add(rooms);
            }

            // Player path: choices array + counter (low 3 bits = choices made).
            byte counter = Mem.Read<byte>(floorData + FloorData_Counter);
            int choicesMade = counter & 7;
            for (int li = 0; li < floor.Layers.Count; li++)
            {
                byte ch = Mem.Read<byte>(floorData + FloorData_Choices + li);
                if (ch != 0xFF && ch < floor.Layers[li].Count)
                    floor.Layers[li][ch].IsChosen = true;
            }
            if (choicesMade > 0 && choicesMade - 1 < floor.Layers.Count)
            {
                floor.PlayerLayer = choicesMade - 1;
                byte ch = Mem.Read<byte>(floorData + FloorData_Choices + floor.PlayerLayer);
                floor.PlayerRoom = ch == 0xFF ? -1 : ch;
            }

            // Room widgets (screen rect + content FK). The layers-container (D1B0) is a descendant
            // of the panel; its child count == number of layers. Search a few levels for it instead
            // of hard-coding a path (robust whether `panel` is mapElement or its child).
            var d1b0 = FindLayersContainer(panel, floor.Layers.Count);
            if (d1b0 != null)
            {
                for (int li = 0; li < floor.Layers.Count && li < d1b0.TotalChildrens; li++)
                {
                    var layerEl = d1b0[li];
                    if (layerEl == null)
                        continue;
                    for (int ri = 0; ri < floor.Layers[li].Count && ri < layerEl.TotalChildrens; ri++)
                    {
                        var w = layerEl[ri];
                        if (w == null)
                            continue;
                        var room = floor.Layers[li][ri];
                        room.HasWidget = true;
                        room.WidgetAddr = w.Address;
                        room.ScreenPos = w.Position;
                        room.ScreenSize = w.Size;
                        room.ContentFk = Mem.Read<IntPtr>(w.Address + WidgetContent);
                        floor.WidgetCount++;
                    }
                }
            }

            floor.IsValid = floor.Layers.Count > 0;
            floor.Status = $"layers={floor.Layers.Count} widgets={floor.WidgetCount} " +
                           $"d1b0={(d1b0 != null ? "ok" : "MISSING")} player=({floor.PlayerLayer},{floor.PlayerRoom})";
            return floor;
        }

        // floorObj = mapElement[0x77]; FloorData = floorObj + (flag?0x1F8:0x1B0). Valid if the
        // layers vector resolves to a sane count (1..64).
        private static bool TryResolveFloorData(IntPtr mapElement, out IntPtr mapOut, out IntPtr floorObj,
            out IntPtr floorData, out int layerCount)
        {
            mapOut = mapElement;
            floorObj = IntPtr.Zero;
            floorData = IntPtr.Zero;
            layerCount = 0;
            if (mapElement == IntPtr.Zero)
                return false;
            floorObj = Mem.Read<IntPtr>(mapElement + MapElement_FloorObjPtr);
            if (floorObj == IntPtr.Zero)
                return false;
            byte flag = Mem.Read<byte>(floorObj + FloorObj_Flag25a);
            floorData = floorObj + (flag != 0 ? FloorData_OffActive : FloorData_OffAlt);
            var layersVec = Mem.Read<StdVector>(floorData + FloorData_Layers);
            layerCount = VecCount(layersVec, LayerStride);
            if (layerCount > 0 && layerCount <= 64)
                return true;
            // try the alternate base too, in case the flag heuristic is off
            floorData = floorObj + (flag != 0 ? FloorData_OffAlt : FloorData_OffActive);
            layersVec = Mem.Read<StdVector>(floorData + FloorData_Layers);
            layerCount = VecCount(layersVec, LayerStride);
            return layerCount > 0 && layerCount <= 64;
        }

        // BFS the panel subtree (shallow) for the element whose child count == expected layer count
        // and whose first child has children (a layer with rooms).
        private static UiElementBase FindLayersContainer(UiElementBase root, int layerCount)
        {
            if (root == null || layerCount <= 0)
                return null;
            var queue = new Queue<(UiElementBase el, int depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0)
            {
                var (el, depth) = queue.Dequeue();
                if (el == null || depth > 5)
                    continue;
                if (el.TotalChildrens == layerCount)
                {
                    var first = el[0];
                    if (first != null && first.TotalChildrens > 0)
                        return el;
                }
                int n = el.TotalChildrens;
                if (n > 0 && n <= 64)
                    for (int i = 0; i < n; i++)
                        queue.Enqueue((el[i], depth + 1));
            }
            return null;
        }

        private static int VecCount(StdVector v, int stride)
        {
            if (v.First == IntPtr.Zero || v.Last == IntPtr.Zero)
                return 0;
            long bytes = v.Last.ToInt64() - v.First.ToInt64();
            if (bytes <= 0 || (bytes % stride) != 0)
                return 0;
            long c = bytes / stride;
            return (c > 0 && c <= 4096) ? (int)c : 0;
        }
    }
}
