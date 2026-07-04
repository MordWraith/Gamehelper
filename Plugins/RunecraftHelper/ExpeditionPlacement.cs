namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    // Pure, side-effect-free placement solver for a SINGLE expedition charge.
    //
    // Decision it answers: given where the chain comes FROM (entry = previous charge / detonator), where it
    // should head TO (exit = next charge, null for the last), the MAIN reward this charge exists to collect,
    // any BONUS rewards that happen to sit nearby, and the blast RADIUS — where exactly do we drop the charge
    // so that it (1) captures the main reward, (2) grabs as much bonus weight as it can, and (3) among equally
    // good spots, sits as close as possible to the exit (so the next hop is short)?
    //
    // It is deliberately decoupled from the game: no memory reads, no rendering, no walk-grid. Feasibility
    // (placement-distance / walkability / exit-reachability) is injected as a predicate so the solver stays a
    // pure function of geometry — call Solve with `_ => true` in unit tests, or with an A* wrapper in the plugin.
    //
    // Objective is LEXICOGRAPHIC (matches the rest of the planner, see project-sekhema-pathfinder-connectivity):
    //   1. maximise captured weight (main + bonuses inside the blast),
    //   2. then minimise distance to the exit (closest tangent to the way out),
    //   3. then minimise distance from the entry (shortest hop in).
    // With no bonuses this degenerates to exactly "the point on the blast circle around the main reward nearest
    // the exit" — the tangent intuition — and generalises it when bonuses pull the optimum inward.
    internal static class ExpeditionPlacement
    {
        internal readonly struct Target
        {
            internal Target(Vector2 pos, double weight)
            {
                this.Pos = pos;
                this.Weight = weight;
            }

            internal Vector2 Pos { get; }
            internal double Weight { get; }
        }

        internal readonly struct Request
        {
            internal Vector2 Entry { get; init; }          // previous node (detonator or previous charge)
            internal Vector2? Exit { get; init; }          // next node; null when this is the last charge
            internal Vector2 Main { get; init; }           // primary reward — MUST end up inside the blast
            internal double MainWeight { get; init; }      // its ex weight
            internal IReadOnlyList<Target>? Bonus { get; init; } // optional nearby extras grabbed for free
            internal float Radius { get; init; }           // effective blast radius (grid)
            internal Func<Vector2, bool>? IsFeasible { get; init; } // placeable? null ⇒ always feasible
            internal int AngularSamples { get; init; }     // disk-boundary sampling density; <=0 ⇒ default 16
        }

        internal readonly struct Result
        {
            internal bool Found { get; init; }
            internal Vector2 Point { get; init; }
            internal double Weight { get; init; }          // total captured at Point (main + bonuses)
            internal int Captured { get; init; }           // captured target count (incl. main)
            internal string Dbg { get; init; }
        }

        private const double WeightEps = 1e-6;
        private const float DistEps = 1e-3f;

        internal static Result Solve(in Request req)
        {
            float r = req.Radius;
            if (r <= 0f) return default;
            float r2 = r * r;
            var bonus = req.Bonus ?? Array.Empty<Target>();
            var feasible = req.IsFeasible ?? (_ => true);

            // Spatial tiebreak target: head for the exit if there is one, else fall back toward the entry so the
            // last charge shortens its own hop instead of drifting away.
            Vector2 toward = req.Exit ?? req.Entry;

            // Hoist the fields the local function needs (an `in` parameter can't be captured by a closure).
            Vector2 main = req.Main;
            Vector2 entry = req.Entry;
            double mainWeight = req.MainWeight;

            Vector2 bestP = default;
            double bestW = double.NegativeInfinity;
            float bestTowardD = float.MaxValue, bestEntryD = float.MaxValue;
            int bestCap = 0;
            bool found = false;

            void Consider(Vector2 cand)
            {
                // Must capture the main reward, must be placeable.
                if (Dist2(cand, main) > r2) return;
                if (!feasible(cand)) return;

                double w = mainWeight;
                int cap = 1;
                for (int i = 0; i < bonus.Count; i++)
                {
                    if (Dist2(cand, bonus[i].Pos) <= r2) { w += bonus[i].Weight; cap++; }
                }

                float towardD = Vector2.Distance(cand, toward);
                float entryD = Vector2.Distance(cand, entry);
                if (!Better(w, towardD, entryD, bestW, bestTowardD, bestEntryD)) return;

                bestW = w; bestTowardD = towardD; bestEntryD = entryD; bestP = cand; bestCap = cap; found = true;
            }

            // (a) The main reward itself (captures main + whatever already overlaps it).
            Consider(req.Main);

            // (b) The pure-tangent candidate: on the blast circle around main, in the direction of the exit. 0.98r
            //     keeps the main just inside the blast against float slop. This is the "closest to the way out" pick.
            {
                var dir = toward - req.Main;
                float len = dir.Length();
                if (len > 1e-4f) Consider(req.Main + (dir / len * (r * 0.98f)));
            }

            // (c) Two-disk intersections with each nearby bonus: the point that pulls a bonus into the blast while
            //     keeping main in it, biased toward the exit. Sampled along the main→bonus segment.
            for (int i = 0; i < bonus.Count; i++)
            {
                if (Dist2(req.Main, bonus[i].Pos) > (2f * r) * (2f * r)) continue; // can't cover both ⇒ skip
                for (float t = 0.1f; t <= 0.9f + 1e-3f; t += 0.2f)
                    Consider(Vector2.Lerp(req.Main, bonus[i].Pos, t));
            }

            // (d) Centroid of main + bonuses that could share one blast (cluster-centre coverage).
            {
                float sx = req.Main.X, sy = req.Main.Y; int m = 1;
                for (int i = 0; i < bonus.Count; i++)
                    if (Dist2(req.Main, bonus[i].Pos) <= (2f * r) * (2f * r)) { sx += bonus[i].Pos.X; sy += bonus[i].Pos.Y; m++; }
                if (m > 1) Consider(new Vector2(sx / m, sy / m));
            }

            // (e) Catch-all: sample the disk around main (a few radii × angles) so a feasibility hole near the
            //     smart candidates can't leave us with nothing placeable.
            int ang = req.AngularSamples > 0 ? req.AngularSamples : 16;
            foreach (float rho in stackalloc[] { 0.95f, 0.6f, 0.3f })
            {
                for (int k = 0; k < ang; k++)
                {
                    double a = 2.0 * Math.PI * k / ang;
                    Consider(req.Main + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * (r * rho));
                }
            }

            if (!found) return default;
            return new Result
            {
                Found = true,
                Point = bestP,
                Weight = bestW,
                Captured = bestCap,
                Dbg = $"cover {bestCap} · {bestW:F0} ex · {bestTowardD:F0} to exit",
            };
        }

        private static float Dist2(Vector2 a, Vector2 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return (dx * dx) + (dy * dy);
        }

        // Lexicographic: more weight wins; on a tie, nearer the exit; on a further tie, nearer the entry.
        private static bool Better(double w, float towardD, float entryD, double bw, float bTowardD, float bEntryD)
        {
            if (w > bw + WeightEps) return true;
            if (w < bw - WeightEps) return false;
            if (towardD + DistEps < bTowardD) return true;
            if (towardD - DistEps > bTowardD) return false;
            return entryD + DistEps < bEntryD;
        }
    }
}
