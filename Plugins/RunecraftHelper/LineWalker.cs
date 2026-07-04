#nullable enable
namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;

    // Grid-space line walkability checker. Copied verbatim from SekhemaHelper/Radar (plugins can't
    // reference each other's assemblies) so the Expedition planner follows walkable terrain the same way.
    // Bresenham sampling over the nibble-encoded walkability grid.
    public static class LineWalker
    {
        // 4-bit nibble per cell in the packed walkability byte array. 0 = blocked, 1-5 = walkable.
        public static bool IsWalkable(
            byte[] walkableData,
            int bytesPerRow,
            int x,
            int y,
            HashSet<(int, int)>? doorOverrides = null)
        {
            if (doorOverrides != null && doorOverrides.Contains((x, y)))
            {
                return true;
            }

            var byteIndex = (y * bytesPerRow) + (x / 2);

            if ((uint)byteIndex >= (uint)walkableData.Length)
            {
                return false;
            }

            var shift = ((x & 1) == 0) ? 0 : 4;
            var value = (walkableData[byteIndex] >> shift) & 0xF;
            return value != 0;
        }

        public struct LineResult
        {
            public bool IsClear;
            public int BlockedCells;
            public int TotalCells;
        }

        public static LineResult CheckLine(
            byte[] walkableData,
            int bytesPerRow,
            Vector2 start,
            Vector2 end,
            HashSet<(int, int)>? doorOverrides = null)
        {
            return CheckLine(
                walkableData,
                bytesPerRow,
                (int)Math.Round(start.X),
                (int)Math.Round(start.Y),
                (int)Math.Round(end.X),
                (int)Math.Round(end.Y),
                doorOverrides);
        }

        public static LineResult CheckLine(
            byte[] walkableData,
            int bytesPerRow,
            int x0,
            int y0,
            int x1,
            int y1,
            HashSet<(int, int)>? doorOverrides = null)
        {
            var result = new LineResult { IsClear = true };

            var dx = Math.Abs(x1 - x0);
            var dy = Math.Abs(y1 - y0);
            var sx = x0 < x1 ? 1 : -1;
            var sy = y0 < y1 ? 1 : -1;
            var err = dx - dy;

            var x = x0;
            var y = y0;

            while (true)
            {
                result.TotalCells++;

                if (!IsWalkable(walkableData, bytesPerRow, x, y, doorOverrides))
                {
                    result.IsClear = false;
                    result.BlockedCells++;
                }

                if (x == x1 && y == y1)
                {
                    break;
                }

                var e2 = err * 2;
                if (e2 > -dy)
                {
                    err -= dy;
                    x += sx;
                }

                if (e2 < dx)
                {
                    err += dx;
                    y += sy;
                }
            }

            return result;
        }

        // Doors punch through wall-type terrain cells; mark a 5x5 area around each door entity as
        // forced-walkable so A* can route through opened doorways (same as Radar/SekhemaHelper).
        public static HashSet<(int, int)>? BuildDoorOverrideMap(AreaInstance areaInstance)
        {
            HashSet<(int, int)>? overrides = null;
            const int doorRadius = 2; // 5x5 area

            void MarkArea(int gx, int gy)
            {
                overrides ??= new HashSet<(int, int)>();
                for (var dx = -doorRadius; dx <= doorRadius; dx++)
                {
                    for (var dy = -doorRadius; dy <= doorRadius; dy++)
                    {
                        overrides.Add((gx + dx, gy + dy));
                    }
                }
            }

            foreach (var kv in areaInstance.AwakeEntities)
            {
                var entity = kv.Value;

                if (entity.TryGetComponent<TriggerableBlockage>(out var _))
                {
                    if (entity.TryGetComponent<Render>(out var render))
                    {
                        MarkArea(
                            (int)Math.Round(render.GridPosition.X),
                            (int)Math.Round(render.GridPosition.Y));
                    }

                    continue;
                }

                var path = entity.Path;
                if (!string.IsNullOrEmpty(path) &&
                    path.Contains("Door", StringComparison.OrdinalIgnoreCase))
                {
                    if (entity.TryGetComponent<Render>(out var render))
                    {
                        MarkArea(
                            (int)Math.Round(render.GridPosition.X),
                            (int)Math.Round(render.GridPosition.Y));
                    }
                }
            }

            return overrides;
        }
    }
}
