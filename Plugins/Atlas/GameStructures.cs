using GameOffsets.Natives;
using GameOffsets.Objects.UiElement;
using System;
using System.Runtime.InteropServices;

namespace Atlas
{
    /// <summary>
    ///     Struct UiElement — thin wrapper over UiElementBaseOffset with helpers to walk
    ///     children and reinterpret a child as an AtlasNode.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct UiElement
    {
        [FieldOffset(0x000)] public UiElementBaseOffset UiElementBase;

        private static readonly Func<uint, bool> IsVisibleBit = UiElementBaseFuncs.IsVisibleChecker;
        private const int MaxChildren = 10000;

        private static int CountFromSnapshot(in StdVector vector)
        {
            if (vector.First == IntPtr.Zero || vector.Last == IntPtr.Zero)
                return 0;

            long bytes = vector.Last.ToInt64() - vector.First.ToInt64();
            if (bytes <= 0)
                return 0;

            int stride = IntPtr.Size;
            if ((bytes % stride) != 0)
                return 0;

            long count = bytes / stride;
            if (count <= 0 || count > MaxChildren)
                return 0;

            return (int)count;
        }

        public readonly int Length
        {
            get
            {
                var vector = UiElementBase.ChildrensPtr;
                return CountFromSnapshot(vector);
            }
        }

        public readonly bool IsVisible => IsVisibleBit(UiElementBase.Flags);

        public readonly UiElement GetChild(int index)
        {
            var address = GetChildAddress(index);
            return address == IntPtr.Zero ? default : Atlas.Read<UiElement>(address);
        }

        public readonly IntPtr GetChildAddress(int index)
        {
            var vector = UiElementBase.ChildrensPtr;
            int count = CountFromSnapshot(in vector);
            if ((uint)index >= (uint)count)
                return IntPtr.Zero;

            int stride = IntPtr.Size;
            var slot = IntPtr.Add(vector.First, index * stride);

            return Atlas.Read<IntPtr>(slot);
        }

        public readonly AtlasNode GetAtlasNode(int index)
        {
            var address = GetChildAddress(index);
            return address == IntPtr.Zero ? default : AtlasNode.Load(address);
        }

        public readonly uint Flags => UiElementBase.Flags;
    }

    /// <summary>
    ///     One node on the PoE2 endgame atlas (verified live for 0.5.x — 2026-06).
    ///
    ///     The atlas-node UiElement no longer carries map data inline (the 0.4.x layout where
    ///     NodeName / Flags / BiomeId lived at +0x270 / +0x290 / +0x293 is gone). Instead all
    ///     per-node map data is reached via a side allocation:
    ///
    ///         A = *(node + 0x10)         (Children StdVector .First — heap allocation larger
    ///                                     than the declared 1-element vec; game uses the tail
    ///                                     as per-node metadata storage)
    ///         B = *(A + 0x20)            (per-node data block, ~0x300 bytes)
    ///         C = *(B + 0x290)           (EndgameMaps.dat row wrapper; 0.5.5, was 0x2A0 -- which
    ///                                     now lands on the node's OTHER dat row, an atlas passive,
    ///                                     yielding a real-looking id from the wrong table)
    ///
    ///     Resolved fields:
    ///         MapId         = *(C + 0x00) → wstring header → buffer @ +0x00 (UTF-16, null-term)
    ///         Flavor text   = *(C + 0x20) → UTF-16 buffer
    ///         Completion    = *(C + 0x10) as int64 (≥ 2 → completed at least once)
    ///         BiomeId       = byte at B + 0x2BE  (0.5.5; was 0x2CE)
    ///                         (0=Water, 1=Mountain, 2=Grass, 3=Forest, 4=Swamp, 5=Desert,
    ///                          6=Ezomyte City, 7=Faridun City, 8=Vaal City, 9=Breach City,
    ///                          10=Ocean, 11=Island, 12=Oriath City — matches biome.json)
    ///         LockStatus    = int32 at B + 0x2DC (-1 = locked / inaccessible; ≥ 0 = unlocked)
    ///         Grid coords   = node + 0x310 (x), node + 0x314 (y)  (0.5.5; was 0x320)
    ///
    ///     This struct is not a memory layout — it's a snapshot read once per node per frame.
    /// </summary>
    public struct AtlasNode
    {
        public IntPtr Address;
        public UiElementBaseOffset UiElementBase;
        public string MapId;
        public byte BiomeId;
        public AtlasNodeState State;
        public StdTuple2D<int> GridPosition;

        public readonly bool IsAccessible => State == AtlasNodeState.AccessibleNow || State == AtlasNodeState.CompletedBase;
        public readonly bool IsNotAccessible => !IsAccessible;
        public readonly bool IsCompleted => State == AtlasNodeState.CompletedBase;
        public static bool IsFailedAttempt => false;

        /// <summary>
        ///     The map identifier — currently the internal MapId (e.g. "MapBurialBog"). The
        ///     player-facing display name with spaces lives in a localization layer not reachable
        ///     from the atlas-node UI tree; joining with maps.json by MapId yields it.
        /// </summary>
        public readonly string MapName => MapId ?? string.Empty;

        // Per-node data block (B) field offsets, verified live for PoE2 0.5.x (FOUND.md).
        // Status byte at B+0x2BF (right after BiomeId), bits found via --status-scan:
        // 0.5.5: 0x2CF -> 0x2BF. The stale slot sits inside the `*(u64*)(node+0x32E) = -1` the widget
        // ctor writes, so it read 0xFF -- every node "completed and accessible".
        private const int StatusByteOffset = 0x2BF;
        private const int GridOffset = 0x310;         // node-relative (0.5.5; was 0x320)
        private const int BiomeIdOffset = 0x2BE;      // B-relative (0.5.5; was 0x2CE)
        private const int MapRowPtrOffset = 0x290;    // B-relative (0.5.5; was 0x2A0)
        private const byte AccessibleBit = 0x01;      // bit 0 = unlocked / accessible (also set on completed)
        private const byte CompletedBit = 0x02;       // bit 1 = completed at least once

        public static AtlasNode Load(IntPtr nodeAddr)
        {
            var node = new AtlasNode { Address = nodeAddr };
            if (nodeAddr == IntPtr.Zero)
                return node;

            node.UiElementBase = Atlas.Read<UiElementBaseOffset>(nodeAddr);

            // Grid coords (per-node, stable offset).
            // TODO(review 2026-06): reviewer says grid coords already live in the UiElement base
            // struct, so the ad-hoc grid read is unnecessary — replace with a named field on
            // UiElementBaseOffset once the exact offset is confirmed in Ghidra.
            node.GridPosition = Atlas.Read<StdTuple2D<int>>(IntPtr.Add(nodeAddr, GridOffset));

            // Walk the chain into the per-node data block (B).
            var a = Atlas.Read<IntPtr>(IntPtr.Add(nodeAddr, 0x10));
            if (a == IntPtr.Zero)
                return node;

            var b = Atlas.Read<IntPtr>(IntPtr.Add(a, 0x20));
            if (b == IntPtr.Zero)
                return node;

            node.BiomeId = Atlas.Read<byte>(IntPtr.Add(b, BiomeIdOffset));

            // Both node states come from the status byte at B+0x2BF, each bit found via Research's
            // --status-scan (bit-scan over labelled nodes for the bit that perfectly separates the
            // groups — avoids the per-sample overfit that sank earlier guesses):
            //   bit 0x02 (completed)  : 11 known-completed vs 27 not — sole separating bit.
            //   bit 0x01 (accessible) : 2 accessible vs 14 not — sole separating bit; set on
            //                           accessible AND completed nodes.
            // Accessible-bit replaces the old "lock (B+0x2DC) == -1" test, which missed Breach-
            // region nodes (gated in-game yet lock != -1, so they wrongly showed as accessible).
            // TODO: a "failed" state exists (accessible map the player failed) — sample it to see
            // whether another bit of this byte encodes it.
            byte status = Atlas.Read<byte>(IntPtr.Add(b, StatusByteOffset));
            bool completed = (status & CompletedBit) != 0;
            bool accessible = (status & AccessibleBit) != 0;

            var c = Atlas.Read<IntPtr>(IntPtr.Add(b, MapRowPtrOffset));
            if (c != IntPtr.Zero)
            {
                // wrapper +0x00 → wstring header; header +0x00 → null-terminated UTF-16 buffer
                var hdr = Atlas.Read<IntPtr>(c);
                if (hdr != IntPtr.Zero)
                {
                    var buf = Atlas.Read<IntPtr>(hdr);
                    if (buf != IntPtr.Zero)
                        node.MapId = Atlas.ReadWideString(buf, 64);
                }
            }

            // Completed takes priority (a finished map reads as completed even though its
            // accessible bit is also set); then accessible → AccessibleNow; else not accessible.
            node.State = completed
                ? AtlasNodeState.CompletedBase
                : accessible
                    ? AtlasNodeState.AccessibleNow
                    : AtlasNodeState.None;

            return node;
        }
    }

    /// <summary>
    ///     Enum AtlasNodeState — encodes the three observable states a node can have on the
    ///     endgame atlas overlay.
    /// </summary>
    [Flags]
    public enum AtlasNodeState : ushort
    {
        /// <summary>Not unlocked yet (path not cleared, behind a quest / gate).</summary>
        None                = 0x0000,

        /// <summary>Unlocked but not completed.</summary>
        AccessibleNow       = 0x0001,

        /// <summary>Completed at least once.</summary>
        CompletedBase       = 0x0002,

        /// <summary>Attempted and failed — map can no longer be re-run (impassable for routing).</summary>
        Failed              = 0x0004,
    }
}
