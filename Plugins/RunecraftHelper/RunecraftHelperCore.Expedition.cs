namespace RunecraftHelper
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Numerics;
    using System.Threading;
    using System.Threading.Tasks;
    using GameHelper;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameOffsets.Natives;
    using GameOffsets.Objects.UiElement;
    using ImGuiNET;
    using Newtonsoft.Json;

    // Expedition explosive-chain planner — built brick-by-brick (see memory project-expedition-planner).
    //
    // BRICK 1 (+walkable, +chain): data read + debug window + a "grid value" world overlay. No route
    // planning yet. Reads the detonator (chain start), explosive counts from the in-game controller
    // (total @ +0x2b0, placed vector @ +0x220), placed charge entities, and candidate targets.
    //
    // "Show grid value" draws, above each PLACED charge, three CHAIN-STEP distances from its previous
    // node (the previous-placed charge, or the detonator for the first): S = straight line, P = walkable
    // A* path (smoothed), C = raw A* cost (grid steps, no smoothing). Charges are chained by entity Id
    // (which increases in placement order). This tests how the game measures its ~108-grid max placement
    // distance: behind an obstacle the straight number drops while the path/cost numbers reveal the real
    // walked length.
    //
    // A* + walkability reuse SekhemaHelper/Radar's proven approach (WalkablePathfinder + LineWalker, copied
    // since plugins can't reference each other). RE-verified: ExplosiveCounter HUD widget = GameUi→[97][9]
    // [17][1] (GameUi = UiRoot[1]); widget+0x378 = controller; controller+0x2b0 (byte) = TOTAL explosives;
    // controller+0x220 = std::vector<placed charge>.
    public sealed partial class RunecraftHelperCore
    {
        private static readonly int[] ExpWidgetPath = { 97, 9, 17, 1 };
        private const int ExpControllerOffset = 0x378;
        private const int ExpCtrlTotalOffset = 0x2b0;       // byte: total explosives
        private const int ExpCtrlPlacedVecOffset = 0x220;   // std::vector<placed charge> {begin,end}

        // Stable controller anchor (RE 2026-06-28, PoE2 0.5.4HF3 — Ghidra ExpeditionExplosiveController_ctor,
        // vtable 0x…3311e60). The controller is a ServerData field, NOT fundamentally a UI object:
        // AreaInstance.PlayerInfo.ServerDataPtr (AreaInstance+0x598, +0x00) -> +0x2618 = controller. This source
        // is UI-independent AND range-independent, so it survives walking away from the detonator and any UI
        // child-index / state drift that broke the old GameUi->[97][9][17][1]->+0x378 path.
        private const int AreaPlayerInfoOffset = 0x598;     // AreaInstance.PlayerInfo (ServerDataPtr @ +0x00)
        private const int ServerDataExpCtrlOffset = 0x2618; // ServerData -> ExpeditionExplosiveController
        private const int ServerDataScanStart = 0x2580;     // drift-recovery scan window around +0x2618
        private const int ServerDataScanEnd = 0x26b0;

        // Map/zone modifiers: the AreaInstance exposes its active mods as a std::vector<{ i32 StatsKey; i32 Value }>
        // at +0x158 (begin) / +0x160 (end) — locale-free, Value = signed integer percent (RE 2026-07-03, obsidian
        // poe2/MapMods, live-verified 0.5.4BHF3). The planner reads the Expedition placement-range / explosive-radius
        // mods straight from here so they're applied automatically (no manual entry).
        //
        // NB: these StatsKeys are EMPIRICALLY confirmed against live zones — the numeric ids do NOT line up with the
        // names in the dumped Stats.dat for 0.5.4b (the id space is misaligned/scrambled: e.g. the radius mod reads
        // on key 13471 even though Stats.dat calls that row "explosives"). Confirm any new one by reading the vector
        // in a zone that has the mod and matching the displayed %, not by the .dat name.
        private const int AreaMapModsVecOffset = 0x158;
        private const int StatMapExpeditionExplosiveRadiusPct = 13471;   // "Increased Expedition Explosive Radius" (confirmed: 36% zone)
        private const int StatMapExpeditionPlacementRangePct = 13685;    // "Increased Expedition Explosive Placement Range" (confirmed: 32% zone)

        private const string ExpDetonatorPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionDetonator";
        private const string ExpExplosivePath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosive";
        private const string ExpMarkerPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionMarker";
        private const string ExpRelicPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionRelic";
        private const string ExpMonolithPath = "Expedition2Encounter";
        // Kalguur "Sentinel" drone object (current LeagueExpeditionNew mechanic): a buff that multiplies the
        // Expedition Logbook drop chance from tall "double-flag" (logbook-tier) markers. See obsidian poe2/Expedition.
        private const string ExpSentinelPath = "Sentinel/SentinelRandomEncounterObject";

        // ExpeditionMarker reward icons share metadata; the reward TYPE is the MinimapIcon.IconName (RE
        // 2026-06-26), one of the MinimapIcons.dat "RewardChest*" family. Friendly labels for the planner UI;
        // anything not listed falls back to the icon name minus the "RewardChest" prefix.
        private static readonly Dictionary<string, string> ExpRewardLabels = new(StringComparer.Ordinal)
        {
            ["RewardChestCurrency"] = "Currency",
            ["RewardChestCurrencyRare"] = "Currency (rare)",
            ["RewardChestGeneric"] = "Generic",
            ["RewardChestUnique"] = "Uniques",
            ["RewardChestGems"] = "Gems",
            ["RewardChestMaps"] = "Maps / Waystones",
            ["RewardChestTrinkets"] = "Trinkets",
            ["RewardChestArmour"] = "Armour",
            ["RewardChestWeapons"] = "Weapons",
            ["RewardChestRunes"] = "Runes",
            ["RewardChestBreach"] = "Breach",
            ["RewardChestRitual"] = "Ritual",
            ["RewardChestExpedition"] = "Expedition",
        };

        private static string ExpRewardLabel(string icon) =>
            ExpRewardLabels.TryGetValue(icon, out var l) ? l
            : icon.StartsWith("RewardChest", StringComparison.Ordinal) ? icon.Substring("RewardChest".Length)
            : icon;

        // Default reward weights (ex) for notable types; everything else defaults to ExpDefaultRewardWeight.
        // These are CODE defaults — the user's saved ExpRewardWeights overrides them per type when present.
        private const float ExpDefaultRewardWeight = 1f;
        private static readonly Dictionary<string, float> ExpDefaultRewardWeights = new(StringComparer.Ordinal)
        {
            ["RewardChestCurrencyRare"] = 40f,
            ["RewardChestArmour"] = 10f,
            ["RewardChestWeapons"] = 10f,
            ["RewardChestCurrency"] = 25f,
        };

        // The active reward/target profile. Guarantees a non-empty list, migrating the legacy flat ExpRewardWeights
        // dict into a "Default" profile the first time (one-off). Always returns a real profile. Render-thread only
        // (mutates settings) — all callers (UI + BuildRouteInputs snapshot) run there.
        private WeightProfile ExpActiveTargetProfile()
        {
            var s = this.Settings;
            if (s.ExpTargetProfiles.Count == 0)
            {
                var p = new WeightProfile { Name = "Default" };
                foreach (var kv in s.ExpRewardWeights) p.Weights[kv.Key] = kv.Value;   // migrate legacy weights once
                s.ExpTargetProfiles.Add(p);
                s.ExpActiveTargetProfile = p.Name;
            }

            return s.ExpTargetProfiles.Find(p => string.Equals(p.Name, s.ExpActiveTargetProfile, StringComparison.Ordinal))
                   ?? s.ExpTargetProfiles[0];
        }

        // Effective routing weight for a reward icon: active-profile override (incl. 0 = ignore) wins; else the
        // code default for that type; else the catch-all ExpDefaultRewardWeight (1).
        private float ExpEffectiveRewardWeight(string icon) =>
            this.ExpActiveTargetProfile().Weights.TryGetValue(icon, out var w) ? w
            : ExpDefaultRewardWeights.TryGetValue(icon, out var d) ? d
            : ExpDefaultRewardWeight;

        // See project-expedition-marker-types
        private static float ExpMarkerPoleOffset(in ExpCachedTarget t) => t.World.Z - t.GroundZ;

        // Returns NaN when there are no markers.
        private float ExpMarkerBaselineZ()
        {
            var counts = new Dictionary<int, int>();
            int bestK = 0, bestC = 0; bool any = false;
            foreach (var t in this.expTargetCache.Values)
            {
                if (t.Kind != ExpKind.Marker) continue;
                any = true;
                int k = (int)Math.Round(ExpMarkerPoleOffset(t));
                counts.TryGetValue(k, out int c); c++; counts[k] = c;
                if (c > bestC) { bestC = c; bestK = k; }
            }

            return any ? bestK : float.NaN;
        }

        // see project-expedition-marker-types
        private double ExpMarkerTierWeight(float poleOffset, float baseline, out string tier)
        {
            var s = this.Settings;
            if (float.IsNaN(baseline)) { tier = "white"; return s.ExpMarkerWeightWhite; }
            float delta = baseline - poleOffset;
            if (delta >= 45f) { tier = "logbook"; return s.ExpMarkerWeightLogbook; }
            if (delta >= 24f) { tier = "gold";    return s.ExpMarkerWeightGold; }
            if (delta >= 19f) { tier = "magic";   return s.ExpMarkerWeightMagic; }
            if (delta >= 4f)  { tier = "white";   return s.ExpMarkerWeightWhite; }
            tier = "tiny"; return 0;
        }

        // The currently selected buff profile (or the first if the saved active name is stale, or null if none).
        private WeightProfile? ExpActiveBuffProfile()
        {
            var list = this.Settings.ExpBuffProfiles;
            if (list == null || list.Count == 0) return null;
            return list.Find(p => string.Equals(p.Name, this.Settings.ExpActiveBuffProfile, StringComparison.Ordinal))
                   ?? list[0];
        }

        // Net routing value of a relic from its ';'-joined mod ids: Σ(Upside weights) − Σ(Downside weights) using
        // the active buff profile (unweighted / neutral mods contribute 0). 0 when no profile or no weighted mod.
        private double ExpRelicNetWeight(string modsJoined)
        {
            if (string.IsNullOrEmpty(modsJoined)) return 0;
            var profile = this.ExpActiveBuffProfile();
            if (profile == null || profile.Weights.Count == 0) return 0;

            double net = 0;
            foreach (var mod in modsJoined.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!profile.Weights.TryGetValue(mod, out var wv) || wv == 0f) continue;
                if (ExpeditionRelicCatalog.IsUpside(mod)) net += wv;
                else if (ExpeditionRelicCatalog.IsDownside(mod)) net -= wv;
            }

            return net;
        }

        // Texture handle for a reward icon (icons/<IconName>.png next to the DLL), or IntPtr.Zero if absent.
        // Result is cached (incl. the miss) so a missing file is probed only once per session.
        private IntPtr GetRewardIconTexture(string icon)
        {
            if (this.expRewardIconCache.TryGetValue(icon, out var cached)) return cached;

            var tex = IntPtr.Zero;
            try
            {
                var path = Path.Join(this.DllDirectory, "icons", icon + ".png");
                if (File.Exists(path))
                {
                    Core.Overlay.AddOrGetImagePointer(path, false, out var handle, out _, out _);
                    tex = handle;
                }
            }
            catch
            {
                tex = IntPtr.Zero;
            }

            this.expRewardIconCache[icon] = tex;
            return tex;
        }

        // Base (no-mod) values, live-measured (see EXPEDITION_WIP.md). Effective = base × (1 + mod%/100).
        // They DIFFER by expedition type (live-measured 2026-06-28): a normal map (base 5 charges) has a smaller
        // placement reach AND blast than a Grand / Logbook one (base ~20). Earlier 108/35 were measured on Grand
        // (15-explosive) maps; the normal 90/28 came from maxing all 5 hops / edge-touching a marker on a 5-charge
        // map. Picked by ExpIsGrand(total) so each strategy uses its own physics.
        // EXACT bases from Ghidra (BHF3): placement in ExpeditionExplosive_BuildPlacementPath (0x141ec19a0) =
        // 0x6c/0x5a; blast radius in ExpeditionExplosive_ComputeBlastRadius (0x14178d1b0) = 0x25/0x1e. The
        // Grand vs Normal branch is the same area-type test in both. Effective = base × (1 + mod%/100); the
        // engine also adds +8×(nearby-uncovered) to the radius, so this floor is a safe (conservative) coverage
        // value. (Radius was 35/28 from live measurement — 2 grid low vs the code floor 37/30.)
        private const float ExpBasePlacementDistanceGrand = 108f;   // grid (0x6c)
        private const float ExpBasePlacementDistanceNormal = 90f;   // grid (0x5a)
        private const float ExpBaseBlastRadiusGrand = 37f;          // grid (0x25)
        private const float ExpBaseBlastRadiusNormal = 30f;         // grid (0x1e)

        // The active base for the CURRENT map, chosen by the charge total (Grand vs normal). The Grand verdict is
        // only trusted from a CONFIRMED total — the controller (ServerData+0x2618) or the in-game HUD counter.
        // Before the first charge is placed the controller isn't allocated yet, so ExpEffectiveTotal() falls back
        // to the manual default (15 ⇒ would falsely read Grand and show 108/35 on a normal map for a few frames).
        // When unconfirmed we default to NORMAL physics: it's the safe direction (90<108 reach, 28<35 radius never
        // suggests an illegal/over-reaching point), and it flips to Grand the moment the controller/HUD resolves.
        private bool ExpCurrentIsGrand() =>
            (this.expCtrlResolved || this.expHudResolved) && ExpIsGrand(this.ExpEffectiveTotal());

        // Confirmed-normal = an expedition IS resolved AND it is not Grand. Distinct from !ExpCurrentIsGrand()
        // (which is also true when nothing is resolved yet). Used to hide the Grand-only route-planner controls
        // (Reward/Buff weight profiles, Min-markers gate) only once we KNOW the current map is a normal
        // Expedition — so those controls stay available out of a map (unresolved) for setup.
        private bool ExpCurrentIsNormal() =>
            (this.expCtrlResolved || this.expHudResolved) && !ExpIsGrand(this.ExpEffectiveTotal());

        private float ExpBasePlacementDistance() =>
            this.ExpCurrentIsGrand() ? ExpBasePlacementDistanceGrand : ExpBasePlacementDistanceNormal;

        private float ExpBaseBlastRadius() =>
            this.ExpCurrentIsGrand() ? ExpBaseBlastRadiusGrand : ExpBaseBlastRadiusNormal;

        // Small SAFETY margin (grid) applied ONLY to stepping-stone hops (charges placed on empty ground at
        // the very limit): our smoothed A* path can slightly underestimate the game's true path, so a step
        // at exactly effDist can land just out of range. A few grid of slack fixes the "чуть-чуть не хватает"
        // without the big cumulative reach loss a percentage would cause. Direct placements onto real targets
        // use the FULL effDist (a target at 105 IS reachable — don't drop it). 1 grid → steps at ~107, which
        // placed fine in-game while 108 was just short.
        private const float ExpStepMarginGrid = 1f;

        private enum ExpKind { Detonator, Charge, Chest, Marker, Remnant, Monolith, Sentinel }

        private readonly struct ExpItem
        {
            public ExpItem(ExpKind kind, Vector2 pos, StdTuple3D<float> world, string info, double value,
                int chainIndex, float straight, float path, float cost)
            {
                this.Kind = kind;
                this.Pos = pos;
                this.World = world;
                this.Info = info;
                this.Value = value;
                this.ChainIndex = chainIndex;
                this.Straight = straight;
                this.Path = path;
                this.Cost = cost;
            }

            public ExpKind Kind { get; }
            public Vector2 Pos { get; }                 // grid position
            public StdTuple3D<float> World { get; }     // world position (for the in-world label)
            public string Info { get; }
            public double Value { get; }
            public int ChainIndex { get; }              // 1-based chain order for charges, else -1
            public float Straight { get; }              // chain-step straight-line (charges) / to-detonator (others); -1 n/a
            public float Path { get; }                  // chain-step walkable A* path (smoothed); -1 n/a
            public float Cost { get; }                  // chain-step raw A* cost (grid steps); -1 n/a
        }

        private readonly List<ExpItem> expItems = new();
        private DateTime expNextScanUtc = DateTime.MinValue;
        private bool expHasDetonator;
        private bool expDetonatorActivated;  // detonator StateMachine "activated" != 0 ⇒ dig started, plan is locked in
        private Vector2 expDetonatorPos;
        private int expTotalCharges;        // controller +0x2b0
        private int expPlacedFromCtrl;      // controller placed-vector count
        private int expPlacedFromEntities;  // distinct ExpeditionExplosive entity ids seen this area (accumulated)
        private readonly HashSet<uint> expPlacedIds = new();  // every placed-charge id seen — accumulates, so the
                                                              // count is right even for charges placed far apart
        private bool expCtrlResolved;
        private string expCtrlSource = "none";  // which path resolved the controller (debug/diagnostics)
        private IntPtr expCtrlVtable;           // captured once for the drift-recovery scan to match against
        private bool expHudResolved;        // HUD remaining-counter read OK this scan (controller fallback)
        private int expHudTotal;            // HUD remaining + placed-from-entities = total charges (auto, monotonic)
        private int expHudRemaining;        // last HUD remaining-counter value read (range-independent)
        private int expPlacedMax;           // monotonic placed count this area — never regresses as you walk away
        private string expScanStatus = "idle";

        // Walkable context captured each scan so the route compute can run A* outside the scan loop.
        private byte[]? expWalkData;
        private int expBpr;
        private HashSet<(int, int)>? expDoors;

        // Path-blocker ("gate") footprints — TriggerableBlockage terrain objects that punch holes in the RAW
        // walkable grid until a blast destroys them. Rebuilt each scan from `blockers`; the flood-filled
        // footprint is memoized per entity id while blocked (the hole is stable until it opens).
        private readonly struct ExpGate
        {
            public ExpGate(Vector2 grid, float worldZ, bool blocked, List<(int, int)>? footprint)
            {
                this.Grid = grid;
                this.WorldZ = worldZ;
                this.Blocked = blocked;
                this.Footprint = footprint;
            }

            public Vector2 Grid { get; }                 // blocker render cell
            public float WorldZ { get; }                 // world Z (height) for projection
            public bool Blocked { get; }                 // TriggerableBlockage.IsBlocked
            public List<(int, int)>? Footprint { get; }  // RAW non-walkable cells of the hole (null when open)
        }

        private readonly List<ExpGate> expGates = new();
        private readonly Dictionary<uint, List<(int, int)>> expGateFootprints = new();  // id → memoized hole
        private string expGateFloodSig = string.Empty;  // flood-fill tuning signature; memo clears when it changes
        private string expRelicSig = string.Empty;       // relic-mods dump signature; file rewritten only on change
        private string expProfileInput = string.Empty;   // text buffer for the new/rename-profile popup
        private string expInventorySig = string.Empty;   // inventory-dump signature; file rewritten only on change
        private string expMarkerHeightSig = string.Empty;

        // Weight-heatmap accumulation layer, cached (rebuilt only when its target set / tuning changes — see Sig).
        // Stored NORMALIZED 0..1; the draw projects it to the live map each frame (cheap). Two layers: ALL weighted
        // targets, and NON-MONOLITH only (markers/relics) — the latter normalizes among themselves so small marker
        // weights aren't drowned out by the huge monolith prices in the combined view.
        private sealed class ExpHeatLayer
        {
            public double[]? Grid;
            public int Nx;
            public int Ny;
            public float MinX;
            public float MinY;
            public float Step;
            public string Sig = string.Empty;
        }

        private readonly ExpHeatLayer expHeatAll = new();
        private readonly ExpHeatLayer expHeatMarkers = new();

        // Detonator world position (sticky; the route always starts here).
        private StdTuple3D<float> expDetonatorWorld;

        private readonly struct ExpRoutePoint
        {
            public ExpRoutePoint(Vector2 grid, StdTuple3D<float> world, double marginal, int captured,
                                 Vector2 targetGrid, float targetWorldZ, string dbg, bool sentinel = false)
            {
                this.Grid = grid;
                this.World = world;
                this.Marginal = marginal;
                this.Captured = captured;
                this.TargetGrid = targetGrid;
                this.TargetWorldZ = targetWorldZ;
                this.Dbg = dbg;
                this.Sentinel = sentinel;
            }

            public Vector2 Grid { get; }
            public StdTuple3D<float> World { get; }
            public double Marginal { get; }   // ex weight this charge newly captures
            public int Captured { get; }      // target count this charge newly captures
            public Vector2 TargetGrid { get; }     // centre of the primary target this charge collects (for the blast-circle viz)
            public float TargetWorldZ { get; }     // its world Z (height) for ground-circle projection
            public string Dbg { get; }             // why the recommendation point landed where it did (debug)
            public bool Sentinel { get; }          // this charge captures the Kalguur Sentinel buff → keep it early in the chain
        }

        // Greedy route result, recomputed only when the planner fingerprint changes (A* is too heavy/frame).
        private readonly List<ExpRoutePoint> expRoute = new();

        // Router (Algorithm 1) output for the debug overlay: the strict-spine walkable polyline + per-cell world Z.
        // Drawn as a thin line on the large map when "Show route spine (Router)" is on, so the route the Placer lays
        // charges along is visible independent of the charge placements themselves.
        private readonly List<Vector2> expSpinePts = new();
        private readonly List<float> expSpineZs = new();

        private double expRouteWeight;
        private int expRouteCovered;
        private int expRouteTargets;
        private string expRouteFingerprint = string.Empty;

        // ── Background route compute ─────────────────────────────────────────
        // ComputeExpeditionRoute's A* is too heavy for the render thread (the UI froze on "Run"). The Run
        // button now snapshots all inputs on the main thread and runs the planner on a Task; while it runs the
        // button reads "Cooking…". The finished plan is published into expPendingResult and swapped into the
        // live route at the top of the next planner frame (main thread), so expRoute / the diag fields are only
        // ever touched by the render thread.
        private volatile bool expComputing;
        private readonly object expResultLock = new();
        private ExpRouteResult? expPendingResult;
        private string expPendingFingerprint = string.Empty;

        // Last-plan instrumentation (surfaced in the planner window), set on apply from the finished result.
        private double expLastComputeMs;
        private double expLastAStarMs;    // summed A* wall-time across threads
        private long expLastAStarCalls;   // A* searches actually run (memo misses)
        private long expLastAStarHits;    // queries served from the per-compute memo
        private string expLastPhase = string.Empty;   // per-phase ms breakdown of the last plan

        // Per-compute A* memo, reached by the static path helpers without threading a parameter through ~30 call
        // sites. ExpReach/ExpFullPath/ExpStepToward each used to run a fresh full-grid A*; within ONE plan the same
        // (door-set, start, end) query recurs dozens of times (diagnostics, greedy, pursuit, refine, smooth,
        // gate-lookahead) and the worst ones flood the whole reachable component before returning -1. Door sets are
        // immutable after creation (gate-opening COPIES — see ExpOpenGatesHitBy), so each configuration is keyed by
        // reference identity; (start,end) complete the key. [ThreadStatic] is safe + correct because one compute
        // runs synchronously on a single Task thread (set in ExpComputeRoute, cleared in its finally).
        [ThreadStatic]
        private static ExpPathCache? expCache;

        private sealed class ExpPathCache
        {
            // All collections are concurrent: the greedy step evaluates its candidates in parallel (Parallel.For),
            // so the path helpers hit this cache from several threads at once. ConcurrentDictionary reads/writes are
            // lock-free; a rare double-compute of the same key just stores the same value twice (harmless).
            private readonly ConcurrentDictionary<object, int> doorIds = new(ReferenceEqualityComparer.Instance);
            private int nextDoorId;
            public readonly ConcurrentDictionary<(int, int, int, int, int), float> Length = new();
            public readonly ConcurrentDictionary<(int, int, int, int, int), List<Vector2>?> Path = new();

            // Separate from Length: ExpReach runs a DISTANCE-BOUNDED A* (maxCost) and returns the reach value
            // (smoothed length if ≤ effDist, else -1). That bounded result can't share Length's keys (a far target
            // is -1 here but has a real length there), and effDist is constant per compute, so keying by (door,a,b)
            // is sound.
            public readonly ConcurrentDictionary<(int, int, int, int, int), float> ReachLen = new();

            // Per-door-set connected-component label grid (lazy + cached). null = not built / grid too big to label.
            private readonly ConcurrentDictionary<int, int[]?> components = new();
            private long calls;
            private long hits;
            private long aStarTicks;   // summed wall-time INSIDE WalkablePathfinder across all threads (Stopwatch ticks)

            public long Calls => Interlocked.Read(ref this.calls);
            public long Hits => Interlocked.Read(ref this.hits);
            public long AStarTicks => Interlocked.Read(ref this.aStarTicks);
            public void Miss() => Interlocked.Increment(ref this.calls);
            public void Hit() => Interlocked.Increment(ref this.hits);
            public void AddAStarTicks(long t) => Interlocked.Add(ref this.aStarTicks, t);

            // Hard cap on cells we'll label (16M ⇒ ~64MB transient int[]); above it we skip labeling and fall back
            // to plain A*. Expedition grids are far smaller in practice.
            private const long MaxLabelCells = 16_000_000;

            // Stable per-compute id for a door-set object (reference identity; null ⇒ 0). Distinct objects with
            // identical content just miss the cache (correct, only slightly less efficient) — never a wrong hit.
            public int DoorId(HashSet<(int, int)>? doors)
            {
                if (doors == null) return 0;
                return this.doorIds.GetOrAdd(doors, _ => Interlocked.Increment(ref this.nextDoorId));
            }

            // 8-connected component labels for a door-set: labels[y*width + x] = component id (≥0) for a walkable
            // cell, -2 for a blocked cell. 8-connectivity WITHOUT the corner-cut rule is a SUPERSET of A*'s stricter
            // moves, so two walkable cells in DIFFERENT components are provably A*-unreachable (safe instant -1);
            // cells in the SAME component may still be A*-blocked by a corner, so we only short-circuit on -1, never
            // assert reachability. Built once per door-set (the base set dominates), cached. Returns null if the
            // grid is absent / too big — caller then just runs A*.
            public int[]? Components(byte[] data, int bpr, HashSet<(int, int)>? doors, int doorId)
            {
                if (this.components.TryGetValue(doorId, out var cached)) return cached;

                int w = bpr * 2, h = bpr > 0 ? data.Length / bpr : 0;
                int[]? labels = null;
                if (w > 0 && h > 0 && (long)w * h <= MaxLabelCells)
                {
                    labels = new int[w * h];
                    for (int i = 0; i < labels.Length; i++) labels[i] = -1;
                    var stack = new Stack<int>();
                    int next = 0;
                    for (int sy = 0; sy < h; sy++)
                    {
                        for (int sx = 0; sx < w; sx++)
                        {
                            int si = (sy * w) + sx;
                            if (labels[si] != -1) continue;
                            if (!LineWalker.IsWalkable(data, bpr, sx, sy, doors)) { labels[si] = -2; continue; }

                            int id = next++;
                            labels[si] = id;
                            stack.Push(si);
                            while (stack.Count > 0)
                            {
                                int ci = stack.Pop();
                                int cx = ci % w, cy = ci / w;
                                for (int dy = -1; dy <= 1; dy++)
                                {
                                    for (int dx = -1; dx <= 1; dx++)
                                    {
                                        if (dx == 0 && dy == 0) continue;
                                        int nx = cx + dx, ny = cy + dy;
                                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                        int ni = (ny * w) + nx;
                                        if (labels[ni] != -1) continue;
                                        if (!LineWalker.IsWalkable(data, bpr, nx, ny, doors)) { labels[ni] = -2; continue; }
                                        labels[ni] = id;
                                        stack.Push(ni);
                                    }
                                }
                            }
                        }
                    }
                }

                this.components[doorId] = labels;
                return labels;
            }
        }

        // Immutable snapshot of everything the route planner needs, captured on the render thread at "Run" so the
        // background task never touches live game state (walk grid / target cache mutate during scans).
        private sealed class ExpRouteInputs
        {
            public byte[]? WalkData;
            public int Bpr;
            public HashSet<(int, int)>? Doors;
            public bool HasDetonator;
            public Vector2 DetonatorPos;
            public StdTuple3D<float> DetonatorWorld;
            public int Budget;
            public float EffDist;
            public float EffRadius;
            public float StepDist;
            public List<Vector2> TPos = new();
            public List<StdTuple3D<float>> TWorld = new();
            public List<double> TW = new();

            // Per-target role (parallel to TPos): true = PRIMARY (monolith, ex-weighted, may drive a long pursuit
            // bridge), false = SECONDARY (reward marker — uniform coverage weight; never drives the route because we
            // can't tell good markers from trash from client memory, see project-expedition-marker-types).
            public List<bool> TPrimary = new();

            // Per-target flag (parallel to TPos): true = the Kalguur Sentinel buff object. Detonating it spawns a
            // mob-buffing drone whose uptime = more empowered Logbook drops, so the spine PINS it FIRST in the anchor
            // tour (detonate it as early as possible). See project-expedition-sentinel-buff.
            public List<bool> TSentinel = new();

            // Marker-coverage gating (normal maps only; see RunecraftHelperSettings.ExpMinMarkersPerSpareCharge).
            // When MarkerCoverageMode is on, a charge that captures NO primary is only committed if it covers at
            // least MinMarkers secondary markers in one blast — so spare charges go to dense marker clusters, not
            // single trash markers. Grand keeps this OFF (MarkerCoverageMode=false ⇒ identical to the old behaviour).
            public bool MarkerCoverageMode;
            public int MinMarkers = 2;

            // Blocked path-blockers ("gates") snapshotted for the planner. A blocked blocker is a TRUE WALL in
            // `Doors` (the door-override freebie is stripped); opening one (a charge whose blast reaches its
            // centre) re-adds its Footprint to a per-chain working copy of the doors, letting the chain path
            // through. Empty when no blocked gates this scan ⇒ the planner behaves exactly as before.
            public List<ExpGateInput> Gates = new();

            // Decision trace (non-null only when "Log planner decisions" is on). The compute appends every
            // candidate it weighed and why it picked what it did; written to a file on the main thread afterwards.
            public List<string>? Log;
        }

        // One blocked path-blocker, snapshotted for the route planner (immutable on the background thread).
        private sealed class ExpGateInput
        {
            public Vector2 Center;                       // blocker render cell (where the opening blast must reach)
            public float WorldZ;                         // height for projection / world circle
            public HashSet<(int, int)> Footprint = new(); // RAW non-walkable cells the blocker punches (becomes walkable when opened)
        }

        // What the background planner produces; applied wholesale to the live fields on the main thread.
        private sealed class ExpRouteResult
        {
            public List<ExpRoutePoint> Route = new();
            public double Weight;
            public int Covered;
            public int Targets;
            public bool HaveStart;
            public float NearestStraight = -1f;
            public float NearestPath = -1f;

            // Decision trace carried back from the compute (when logging is on); written to a file on apply.
            public List<string>? Log;

            // Timing / A* instrumentation for this plan (always set, independent of logging).
            public double ComputeMs;
            public long AStarCalls;
            public long AStarHits;
            public double AStarMs;   // summed wall-time inside A* across threads (≈ComputeMs ⇒ serial; «ComputeMs ⇒ parallel overlap)
            public string Phase = string.Empty;   // per-phase ms breakdown (greedy / tourOrder / relay / smooth)

            // ── Router output (Algorithm 1) ──────────────────────────────────────────────────────────────────
            // The strict-spine WALKABLE polyline the Placer (Algorithm 2) lays charges along: detonator → ordered
            // anchors, every A* cell concatenated. SpinePts[k] is a grid cell, SpineZs[k] its (interpolated) world Z
            // for iso projection; SpineAnchorIdx holds the index in SpinePts where each ordered anchor sits (the
            // segment junctions). Empty on the old greedy/grand planners. Visualization + Placer input only.
            public List<Vector2> SpinePts = new();
            public List<float> SpineZs = new();
            public List<int> SpineAnchorIdx = new();
        }

        // Persistent target cache (keyed by entity Id). Targets, the detonator and the last charge are
        // ACCUMULATED across scans and survive leaving the awake bubble, so walking the map doesn't shrink
        // the set or trigger a re-plan. Cleared only on area change. This makes movement essentially free:
        // the route is recomputed only when a knob / the target data / the charge budget changes.
        private readonly struct ExpCachedTarget
        {
            public ExpCachedTarget(Vector2 pos, StdTuple3D<float> world, ExpKind kind, string info, double value, float groundZ = 0f)
            {
                this.Pos = pos;
                this.World = world;
                this.Kind = kind;
                this.Info = info;
                this.Value = value;
                this.GroundZ = groundZ;
            }

            public Vector2 Pos { get; }
            public StdTuple3D<float> World { get; }
            public ExpKind Kind { get; }
            public string Info { get; }
            public double Value { get; }

            // See ExpMarkerTierWeight.
            public float GroundZ { get; }
        }

        private readonly Dictionary<long, ExpCachedTarget> expTargetCache = new();
        private string expCachedAreaHash = string.Empty;

        // Lazily-loaded reward-marker icons (IntPtr.Zero = file missing / failed; cached so we don't probe
        // disk every frame). Sliced from the game's minimap sprite sheet into <DllDirectory>/icons/<Id>.png
        // by extract_minimap_icons.py. Key = MinimapIcon.IconName (same key as ExpRewardWeights).
        private readonly Dictionary<string, IntPtr> expRewardIconCache = new(StringComparer.Ordinal);

        // Called from DrawUI when either expedition toggle is on (and we're in-game with a process handle).
        private void ExpeditionTick()
        {
            var now = DateTime.UtcNow;
            if (now >= this.expNextScanUtc)
            {
                this.ScanExpedition();
                this.expNextScanUtc = now.AddMilliseconds(500);
            }

            if (this.Settings.ShowExpeditionDebug) this.DrawExpeditionDebugWindow();
            if (this.Settings.ShowExpeditionGridValue) this.DrawExpeditionGridValues();
            if (this.Settings.ShowExpeditionPlanner) this.DrawExpeditionPlannerWindow();
            if (this.Settings.ShowExpeditionGates) this.DrawExpeditionGatesLargeMap();
            if (this.Settings.ShowExpeditionHeatmap || this.Settings.ShowExpeditionHeatmapMarkers) this.DrawExpeditionHeatmapLargeMap();
        }

        private void ScanExpedition()
        {
            this.expItems.Clear();
            this.expPlacedFromEntities = 0;
            this.expScanStatus = "scanning";

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (area == null) { this.expScanStatus = "no area"; return; }

            // Auto-detect this map's Expedition mods (placement distance / explosive radius %) from the area's
            // mod vector, every scan — replaces the old manual sliders. Per-map data; self-heals if it reads a
            // frame late on entry.
            this.ApplyExpeditionMapMods(area.Address);

            // Drop the persistent cache (targets + sticky detonator/charge anchor) only when the area
            // changes; within an area it accumulates so player movement never shrinks the routed set.
            if (area.AreaHash != this.expCachedAreaHash)
            {
                this.expCachedAreaHash = area.AreaHash;
                this.expTargetCache.Clear();
                this.expHasDetonator = false;
                this.expDetonatorActivated = false;
                this.expRoute.Clear();
                this.expSpinePts.Clear();
                this.expSpineZs.Clear();
                this.expRouteFingerprint = string.Empty;
                this.expPlacedMax = 0;
                this.expHudTotal = 0;
                this.expHudRemaining = 0;
                this.expPlacedIds.Clear();
                this.expGateFootprints.Clear();

                // Drop any in-flight / pending background plan from the previous area (a late result would be
                // stale; the fingerprint would flag it anyway, but clear so the button doesn't stick on "Cooking").
                lock (this.expResultLock) { this.expPendingResult = null; }
                this.expComputing = false;

                // Map modifiers are per-map and auto-detected from the AreaInstance each scan
                // (ApplyExpeditionMapMods), so nothing to carry over or reset here.
            }

            // Monolith ex-values reused from RunecraftHelper's own (patch-current) scan.
            this.EnsureExpeditionMonoliths();
            var monoByAddr = new Dictionary<long, double>();
            var foreignMonos = new HashSet<long>();
            foreach (var mv in this.monolithViews)
            {
                // Standalone non-Expedition monolith (activated==1): collected by hand, NOT by the explosive
                // chain — drop it from the route value map entirely so it can never become an anchor.
                if (mv.IsForeign) { foreignMonos.Add(mv.EntityId); continue; }
                monoByAddr[mv.EntityId] = mv.Best;
            }

            // Pass 1: collect non-charge items + the detonator; charges go to a separate list (chained by Id).
            var others = new List<(ExpKind Kind, Vector2 Pos, StdTuple3D<float> World, string Info, double Value)>();
            var charges = new List<(Vector2 Pos, StdTuple3D<float> World, uint Id)>();
            // Path blockers (gates): AwakeEntities carrying a TriggerableBlockage component. The component is the
            // universal, name/icon-independent key, BUT many tilesets also carry TriggerableBlockage on terrain that
            // is NOT an explosive-chain gate (e.g. RootBlocker), and including those punches phantom holes that make
            // the planner see a maze that the blast chain doesn't actually open. So we restrict to DevourerSegment —
            // the verified expedition path-blocker for the tilesets we plan on. (If a future tileset uses a different
            // gate name, add it here.) Terrain objects ⇒ always in AwakeEntities (available outside the bubble).
            var blockers = new List<(int X, int Y, float Z, bool Blocked, uint Id)>();
            // Relics (a.k.a. "remnants"): the field devices whose Upside/Downside mods get applied to the encounter
            // when the blast chain reaches them. Captured here (debug only) so we can dump each relic's +/- mods.
            var relics = new List<(Vector2 Pos, ObjectMagicProperties Omp)>();
            foreach (var kv in area.AwakeEntities)
            {
                var e = kv.Value;
                if (e == null || !e.IsValid) continue;
                if (!e.TryGetComponent<Render>(out var render)) continue;
                var pos = new Vector2(render.GridPosition.X, render.GridPosition.Y);
                var world = render.WorldPosition;
                float groundZ = render.TerrainHeight;
                var path = e.Path ?? string.Empty;

                if (path.IndexOf("DevourerSegment", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    e.TryGetComponent<TriggerableBlockage>(out var tb))
                {
                    blockers.Add(((int)Math.Round(pos.X), (int)Math.Round(pos.Y), world.Z, tb.IsBlocked, e.Id));
                    // fall through: a blocker won't match any of the target classifications below.
                }

                if (path.Equals(ExpDetonatorPath, StringComparison.OrdinalIgnoreCase))
                {
                    this.expHasDetonator = true;
                    this.expDetonatorPos = pos;
                    this.expDetonatorWorld = world;
                    // Once the player presses the detonator the dig begins and the plan can't change. The
                    // detonator's StateMachine "activated" state flips 0→1 on the press (live-verified 0.5.4HF3:
                    // [activated,light_colour,3rd] = [0,0,0] idle → [0,1,0] with a charge placed → [1,2,1] after
                    // detonation — so ONLY "activated" is the dig-started flag; "light_colour" toggles on mere
                    // placement). We read it RAW by name (StateMachineNamedStateValue), NOT via GameHelper's
                    // StateMachine.States: GH refreshes that component on a slow tier for static terrain objects,
                    // so its cache stayed stale. Sticky for the area (never un-set; "activated" holds for the dig).
                    if (!this.expDetonatorActivated &&
                        e.TryGetComponent<StateMachine>(out var detSm) &&
                        this.StateMachineNamedStateValue(detSm.Address, "activated") != 0)
                    {
                        this.expDetonatorActivated = true;
                    }

                    others.Add((ExpKind.Detonator, pos, world, "detonator", 0));
                    continue;
                }

                if (path.Equals(ExpExplosivePath, StringComparison.OrdinalIgnoreCase))
                {
                    // Accumulate the id (placed charges don't move/despawn mid-dig) so the count survives walking
                    // away from earlier charges AND grows when you place a new one far from the rest.
                    this.expPlacedIds.Add(e.Id);
                    charges.Add((pos, world, e.Id));
                    continue;
                }

                if (path.IndexOf(ExpMonolithPath, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Standalone non-Expedition monolith (activated==1): skip it as a route target AND purge any
                    // stale cached value, so the keep-the-last-known-value logic below can't resurrect it.
                    if (foreignMonos.Contains(e.Address.ToInt64()))
                    {
                        this.expTargetCache.Remove((long)e.Id);
                        continue;
                    }

                    monoByAddr.TryGetValue(e.Address.ToInt64(), out var best);
                    others.Add((ExpKind.Monolith, pos, world, "monolith", best));
                    // Cache it; keep the last KNOWN ex value when this scan reads 0 (out of bubble).
                    double keep = best;
                    if (best <= 0 && this.expTargetCache.TryGetValue((long)e.Id, out var oldMono)) keep = oldMono.Value;
                    this.expTargetCache[(long)e.Id] = new ExpCachedTarget(pos, world, ExpKind.Monolith, "monolith", keep);
                    continue;
                }

                if (e.EntitySubtype == EntitySubtypes.ExpeditionChest ||
                    path.StartsWith("Metadata/Chests/LeaguesExpedition", StringComparison.OrdinalIgnoreCase))
                {
                    others.Add((ExpKind.Chest, pos, world, ChestInfo(path), 0));
                    continue;
                }

                if (path.IndexOf(ExpSentinelPath, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Kalguur Sentinel buff object. Detected by presence only (the empowerment magnitude is
                    // server-side); the route treats it as a high-priority anchor when logbook flags exist (Normal).
                    others.Add((ExpKind.Sentinel, pos, world, "sentinel", 0));
                    this.expTargetCache[(long)e.Id] = new ExpCachedTarget(pos, world, ExpKind.Sentinel, "sentinel", 0);
                    continue;
                }

                if (e.EntityCustomGroup == 100 || path.IndexOf(ExpMarkerPath, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string icon = e.TryGetComponent<MinimapIcon>(out var mi) && !string.IsNullOrEmpty(mi.IconName)
                        ? mi.IconName
                        : "marker";
                    others.Add((ExpKind.Marker, pos, world, icon, 0));
                    this.expTargetCache[(long)e.Id] = new ExpCachedTarget(pos, world, ExpKind.Marker, icon, 0, groundZ);
                    continue;
                }

                if (e.EntityCustomGroup == 101 || path.IndexOf(ExpRelicPath, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Cache the relic's mod ids (joined) so the planner can value it from the active buff profile,
                    // out of the network bubble. ModNames can read empty before the component first resolves — keep
                    // the last non-empty set we cached for this relic (mirrors the monolith-value keep below).
                    string mods = string.Empty;
                    if (e.TryGetComponent<ObjectMagicProperties>(out var relicOmp))
                    {
                        mods = string.Join(';', relicOmp.ModNames);
                        if (this.Settings.ShowExpeditionDebug) relics.Add((pos, relicOmp));
                    }

                    if (string.IsNullOrEmpty(mods) &&
                        this.expTargetCache.TryGetValue((long)e.Id, out var oldRelic) && oldRelic.Kind == ExpKind.Remnant)
                        mods = oldRelic.Info;

                    others.Add((ExpKind.Remnant, pos, world, mods, 0));
                    this.expTargetCache[(long)e.Id] = new ExpCachedTarget(pos, world, ExpKind.Remnant, mods, 0);
                    continue;
                }
            }

            // Placed count = distinct charge ids accumulated this area (NOT just those awake this scan).
            this.expPlacedFromEntities = this.expPlacedIds.Count;

            // Walkable context (snapshot once per scan) for the A* distances.
            var data = area.GridWalkableData;
            int bpr = area.TerrainMetadata.BytesPerRow;
            bool canWalk = data != null && data.Length > 0 && bpr > 0;
            var doors = canWalk ? LineWalker.BuildDoorOverrideMap(area) : null;
            this.expWalkData = canWalk ? data : null;
            this.expBpr = bpr;
            this.expDoors = doors;

            // Gate footprints: flood-fill the RAW (door-override-free) walkable hole each blocked blocker punches.
            // Memoized per id while blocked — the hole is stable until the blocker is destroyed; an open blocker
            // has no hole (footprint null) and is dropped from the memo so re-blocking re-floods. The memo is
            // invalidated when the flood-fill tuning sliders change, so live-tuning re-floods immediately.
            int fMaxCells = Math.Max(50, this.Settings.ExpGateFloodMaxCells);
            int fMaxRadius = Math.Max(3, this.Settings.ExpGateFloodMaxRadius);
            int fDiskRadius = Math.Max(1, this.Settings.ExpGateDiskRadius);
            string floodSig = $"{fMaxCells}|{fMaxRadius}|{fDiskRadius}";
            if (floodSig != this.expGateFloodSig)
            {
                this.expGateFootprints.Clear();
                this.expGateFloodSig = floodSig;
            }

            this.expGates.Clear();
            if (canWalk && blockers.Count > 0)
            {
                foreach (var b in blockers)
                {
                    List<(int, int)>? fp = null;
                    if (b.Blocked)
                    {
                        if (!this.expGateFootprints.TryGetValue(b.Id, out fp))
                        {
                            fp = FloodBlockerFootprint(data!, bpr, b.X, b.Y, fMaxCells, fMaxRadius, fDiskRadius);
                            this.expGateFootprints[b.Id] = fp;
                        }
                    }
                    else
                    {
                        this.expGateFootprints.Remove(b.Id);
                    }

                    this.expGates.Add(new ExpGate(new Vector2(b.X, b.Y), b.Z, b.Blocked, fp));
                }
            }

            // Non-charge items: straight-line distance to the detonator (context only; not part of the chain).
            foreach (var o in others)
            {
                float s = this.expHasDetonator ? Vector2.Distance(this.expDetonatorPos, o.Pos) : -1f;
                this.expItems.Add(new ExpItem(o.Kind, o.Pos, o.World, o.Info, o.Value, -1, s, -1f, -1f));
            }

            // Charges chained by entity Id (Id increases in placement order). Each charge's distances are
            // measured from its PREVIOUS node: the previous-placed charge, or the detonator for the first.
            charges.Sort((a, b) => a.Id.CompareTo(b.Id));
            Vector2 prev = this.expDetonatorPos;
            for (int i = 0; i < charges.Count; i++)
            {
                var c = charges[i];
                float straight = -1f, path = -1f, cost = -1f;
                bool haveFrom = this.expHasDetonator || i > 0;   // need a valid previous node
                if (haveFrom)
                {
                    straight = Vector2.Distance(prev, c.Pos);
                    if (canWalk)
                    {
                        path = ExpPathLength(data, bpr, doors, prev, c.Pos);
                        cost = ExpPathCost(data, bpr, doors, prev, c.Pos);
                    }
                }

                this.expItems.Add(new ExpItem(ExpKind.Charge, c.Pos, c.World, $"charge (id {c.Id})", 0, i + 1, straight, path, cost));
                prev = c.Pos;
            }

            this.expCtrlResolved = this.TryReadExpeditionCounts(out this.expTotalCharges, out this.expPlacedFromCtrl);

            // Controller pointer can read null (fragile UI path). Fall back to the HUD counter (remaining) +
            // entity-counted placed charges for an automatic total — no manual input needed.
            int hudRemaining = 0;
            this.expHudResolved = !this.expCtrlResolved && this.TryReadExpeditionHudRemaining(out hudRemaining);
            if (this.expHudResolved)
            {
                this.expHudRemaining = hudRemaining;
                // Total charges for the map is FIXED; (remaining + placed-this-scan) converges to it. Take the
                // running MAX so a late-opened planner or an out-of-range placed entity can never shrink it.
                this.expHudTotal = Math.Max(this.expHudTotal, hudRemaining + this.expPlacedFromEntities);
            }

            // Placed-count source priority: controller vector → HUD (total − remaining) → counted awake entities.
            int rawPlaced =
                this.expCtrlResolved ? this.expPlacedFromCtrl :
                this.expHudResolved ? Math.Max(0, this.expHudTotal - hudRemaining) :
                this.expPlacedFromEntities;

            // The stable controller is AUTHORITATIVE and range-independent — trust its placed count DIRECTLY so a
            // rollback / cancel (placed legitimately DECREASES) is reflected at once. Only the flaky fallbacks
            // (HUD / awake-entity count, which drop as charges leave the awake bubble while you walk) need the
            // per-area monotonic clamp to stop the "next charge" index from regressing spuriously. Using Math.Max
            // on the controller too was the bug: cancelling all charges left expPlacedMax latched high → the plan
            // showed "Route complete" forever (survived GH restart because the controller re-reported the latched
            // value during the un-place, then we clamped it).
            this.expPlacedMax = this.expCtrlResolved ? rawPlaced : Math.Max(this.expPlacedMax, rawPlaced);

            this.expScanStatus = canWalk ? $"{this.expItems.Count} items" : $"{this.expItems.Count} items (no walkable grid)";
        }

        // Auto-detect this map's Expedition modifiers from the AreaInstance map-mods vector and feed them into the
        // planner's placement-distance / blast-radius %s (replaces the old manual sliders). Vector = std::vector<
        // { i32 StatsKey; i32 Value }> at areaAddr+0x158 (begin) / +0x160 (end); Value is a signed integer percent.
        // Absent stat ⇒ 0 (no mod). Read failure ⇒ values left untouched (no flicker); a genuinely empty vector ⇒ 0.
        // Keys are empirically confirmed (see constants) — the dumped Stats.dat names are misaligned for this build.
        private void ApplyExpeditionMapMods(IntPtr areaAddr)
        {
            if (areaAddr == IntPtr.Zero || this.processHandle == IntPtr.Zero) return;

            var head = new byte[16];
            if (!ReadProcessMemory(this.processHandle, areaAddr + AreaMapModsVecOffset, head, (uint)head.Length, out _))
                return;

            long begin = BitConverter.ToInt64(head, 0);
            long end = BitConverter.ToInt64(head, 8);
            if (begin == 0 && end == 0) { this.Settings.ExpPlacementDistancePct = 0; this.Settings.ExpBlastRadiusPct = 0; return; }

            ulong b = (ulong)begin;
            if (b < 0x10000 || b > 0x7FFFFFFFFFFFul) return;
            long span = end - begin;
            if (span <= 0 || (span % 8) != 0 || span > 0x8000) return;   // 0x8000 ⇒ 4096-entry sanity cap

            var buf = new byte[span];
            if (!ReadProcessMemory(this.processHandle, (IntPtr)begin, buf, (uint)buf.Length, out _)) return;

            int distancePct = 0, radiusPct = 0;
            for (int off = 0; off + 8 <= buf.Length; off += 8)
            {
                int statId = BitConverter.ToInt32(buf, off);
                int value = BitConverter.ToInt32(buf, off + 4);
                switch (statId)
                {
                    case StatMapExpeditionPlacementRangePct: distancePct += value; break;
                    case StatMapExpeditionExplosiveRadiusPct: radiusPct += value; break;
                }
            }

            this.Settings.ExpPlacementDistancePct = distancePct;
            this.Settings.ExpBlastRadiusPct = radiusPct;
        }

        // Debug dump: list every relic's mods split into Upside (+) / Downside (−) by the self-documenting mod-name
        // prefix (ExpeditionRelicUpside* / *Downside*), read straight from ObjectMagicProperties.ModNames (works out
        // of the network bubble — relics are AwakeEntities). Written to <DllDir>/expedition_relics.txt, and ONLY when
        // the set changes (per-map), so it never thrashes the disk on a per-scan basis. Sorted by grid for a stable
        // diff. Internal mod Ids only (human-readable text would need a name→description map — see memory).
        private void DumpExpeditionRelics(List<(Vector2 Pos, ObjectMagicProperties Omp)> relics)
        {
            relics.Sort((a, b) => a.Pos.X != b.Pos.X ? a.Pos.X.CompareTo(b.Pos.X) : a.Pos.Y.CompareTo(b.Pos.Y));

            var sb = new System.Text.StringBuilder();
            var sig = new System.Text.StringBuilder();
            sb.AppendLine($"=== EXPEDITION RELICS === {relics.Count} relics  (+ = Upside / - = Downside)");
            int idx = 1;
            foreach (var (pos, omp) in relics)
            {
                var up = new List<string>();
                var down = new List<string>();
                var other = new List<string>();
                foreach (var name in omp.ModNames)
                {
                    if (name.IndexOf("Upside", StringComparison.OrdinalIgnoreCase) >= 0) up.Add(name);
                    else if (name.IndexOf("Downside", StringComparison.OrdinalIgnoreCase) >= 0) down.Add(name);
                    else other.Add(name);
                }

                up.Sort(); down.Sort(); other.Sort();
                sb.AppendLine($"RELIC #{idx} @ ({pos.X:F0},{pos.Y:F0})  +{up.Count} / -{down.Count}");
                foreach (var n in up) sb.AppendLine($"    + {n}");
                foreach (var n in down) sb.AppendLine($"    - {n}");
                foreach (var n in other) sb.AppendLine($"    ? {n}");
                sig.Append(pos.X).Append(',').Append(pos.Y).Append('|');
                foreach (var n in up) sig.Append('+').Append(n);
                foreach (var n in down) sig.Append('-').Append(n);
                foreach (var n in other) sig.Append('?').Append(n);
                sig.Append(';');
                idx++;
            }

            string s = sig.ToString();
            if (s == this.expRelicSig) return;
            this.expRelicSig = s;
            try { File.WriteAllText(Path.Join(this.DllDirectory, "expedition_relics.txt"), sb.ToString()); } catch { }
        }

        // Walkable A* path length (grid units, smoothed geometric) between two grid points; -1 if no path.
        private static float ExpPathLength(byte[]? data, int bytesPerRow, HashSet<(int, int)>? doors, Vector2 a, Vector2 b)
        {
            if (data == null) return -1f;

            var cache = expCache;
            (int, int, int, int, int) key = default;
            if (cache != null)
            {
                int did = cache.DoorId(doors);
                int ax = (int)Math.Round(a.X), ay = (int)Math.Round(a.Y);
                int bx = (int)Math.Round(b.X), by = (int)Math.Round(b.Y);
                key = (did, ax, ay, bx, by);
                if (cache.Length.TryGetValue(key, out var hit)) { cache.Hit(); return hit; }

                // Same-component pre-check: when both endpoints are walkable but lie in different connected
                // components, A* can only flood its whole component before returning -1 — answer instantly instead.
                // (If either endpoint is itself blocked, FindPath would SNAP it to nearby walkable terrain, so we
                // can't pre-judge — fall through to A*.)
                var labels = cache.Components(data, bytesPerRow, doors, did);
                if (labels != null)
                {
                    int w = bytesPerRow * 2, h = data.Length / bytesPerRow;
                    if ((uint)ax < (uint)w && (uint)ay < (uint)h && (uint)bx < (uint)w && (uint)by < (uint)h)
                    {
                        int la = labels[(ay * w) + ax], lb = labels[(by * w) + bx];
                        if (la >= 0 && lb >= 0 && la != lb) { cache.Length[key] = -1f; return -1f; }
                    }
                }
            }

            long t0 = Stopwatch.GetTimestamp();
            var route = WalkablePathfinder.FindPath(data, bytesPerRow, a, b, doors);
            float len = 0f;
            if (route == null || route.Count < 2)
            {
                len = -1f;
            }
            else
            {
                for (int i = 1; i < route.Count; i++) len += Vector2.Distance(route[i - 1], route[i]);
            }

            if (cache != null) { cache.Miss(); cache.AddAStarTicks(Stopwatch.GetTimestamp() - t0); cache.Length[key] = len; }
            return len;
        }

        // Raw A* path cost (grid steps, no smoothing) between two grid points; -1 if no path.
        private static float ExpPathCost(byte[]? data, int bytesPerRow, HashSet<(int, int)>? doors, Vector2 a, Vector2 b)
        {
            if (data == null) return -1f;
            return WalkablePathfinder.FindPathCost(data, bytesPerRow, a, b, doors);
        }

        // Flood-fill the connected RAW-non-walkable region a blocker punches into the walkable grid. Seeds at the
        // blocker's render cell (or the nearest blocked cell in a small window if the centre reads walkable), then
        // 4-connects over blocked cells, bounded to a window around the blocker. If it spills past the cell cap
        // (the hole merged into a permanent wall rather than an isolated barrier) it falls back to a small disk so
        // SOMETHING is drawn rather than half a wall. Always returns a non-null list (empty = nothing blocked here).
        private static List<(int, int)> FloodBlockerFootprint(byte[] data, int bpr, int cx, int cy,
                                                              int maxCells, int maxRadius, int diskRadius)
        {
            // Find a blocked seed near the centre (the render cell can sit on a walkable edge of the object).
            int sx = -1, sy = -1;
            for (int r = 0; r <= 4 && sx < 0; r++)
            {
                for (int dy = -r; dy <= r && sx < 0; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int x = cx + dx, y = cy + dy;
                        if (x < 0 || y < 0) continue;
                        if (!LineWalker.IsWalkable(data, bpr, x, y, null)) { sx = x; sy = y; break; }
                    }
                }
            }

            if (sx < 0) return new List<(int, int)>();   // already open — no hole

            var seen = new HashSet<(int, int)> { (sx, sy) };
            var stack = new Stack<(int, int)>();
            stack.Push((sx, sy));
            var cells = new List<(int, int)>();
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                if (Math.Abs(x - cx) > maxRadius || Math.Abs(y - cy) > maxRadius) continue;
                cells.Add((x, y));
                if (cells.Count > maxCells) return DiskFootprint(cx, cy, diskRadius);   // spilled into a wall → disk fallback

                void Try(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || seen.Contains((nx, ny))) return;
                    if (LineWalker.IsWalkable(data, bpr, nx, ny, null)) return;
                    seen.Add((nx, ny));
                    stack.Push((nx, ny));
                }

                Try(x + 1, y);
                Try(x - 1, y);
                Try(x, y + 1);
                Try(x, y - 1);
            }

            return cells;
        }

        private static List<(int, int)> DiskFootprint(int cx, int cy, int r)
        {
            var l = new List<(int, int)>();
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if ((dx * dx) + (dy * dy) <= r * r && cx + dx >= 0 && cy + dy >= 0)
                        l.Add((cx + dx, cy + dy));
                }
            }

            return l;
        }

        private static string ChestInfo(string path) =>
            path.Contains("LeagueFaction", StringComparison.OrdinalIgnoreCase) ? "logbook chest" : "chest";

        // Reuse the monolith partial's scan + timer so when that window is also open the two share one scan.
        private void EnsureExpeditionMonoliths()
        {
            if (!this.LoadMonolithData()) return;
            var now = DateTime.UtcNow;
            if (now >= this.nextMonolithScanUtc)
            {
                this.monolithViews = this.EnumerateMonoliths();
                this.nextMonolithScanUtc = now.AddMilliseconds(750);
            }
        }

        // GameUi → [97][9][17][1] → ExplosiveCounter HUD widget. Present (and IsVisible) only while the
        // player is placing explosives at the detonator.
        private bool TryGetExpeditionWidget(out IntPtr widget)
        {
            widget = IntPtr.Zero;
            var node = Core.States.InGameStateObject.GameUi.Address;
            if (node == IntPtr.Zero) return false;
            foreach (var idx in ExpWidgetPath)
            {
                node = this.GetChild(node, idx);
                if (node == IntPtr.Zero) return false;
            }

            widget = node;
            return true;
        }

        // Resolve the ExpeditionExplosiveController. Source priority, each candidate validated by
        // ExpControllerLooksValid before it's trusted:
        //   (1) STABLE — AreaInstance -> ServerData (+0x598) -> +0x2618. UI-independent and range-independent;
        //       this is the real home of the controller and the only source that stays correct as you walk the
        //       map. The old fragile UI path drifted / read null, which is what destabilised the planner.
        //   (2) UI widget +0x378 — only present while actively placing; kept purely as a fallback.
        //   (3) Drift recovery — once we've seen a valid controller we know its vtable, so we can scan a small
        //       ServerData window to re-find it if +0x2618 ever shifts on a patch (vtable-matched, so it can't
        //       grab the wrong object).
        private bool TryGetExpeditionController(out IntPtr controller)
        {
            controller = IntPtr.Zero;

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            IntPtr serverData = IntPtr.Zero;
            if (area != null && area.Address != IntPtr.Zero)
                serverData = this.ReadPtr(area.Address + AreaPlayerInfoOffset);

            // (1) Stable ServerData field.
            if (serverData != IntPtr.Zero)
            {
                var c = this.ReadPtr(serverData + ServerDataExpCtrlOffset);
                if (this.ExpControllerLooksValid(c))
                {
                    this.CaptureControllerVtable(c);
                    this.expCtrlSource = "serverData+0x2618";
                    controller = c;
                    return true;
                }
            }

            // (2) HUD widget fallback.
            if (this.TryGetExpeditionWidget(out var widget))
            {
                var c = this.ReadPtr(widget + ExpControllerOffset);
                if (this.ExpControllerLooksValid(c))
                {
                    this.CaptureControllerVtable(c);
                    this.expCtrlSource = "widget+0x378";
                    controller = c;
                    return true;
                }
            }

            // (3) Drift-recovery scan (only trusts an exact vtable match).
            if (serverData != IntPtr.Zero && this.expCtrlVtable != IntPtr.Zero)
            {
                for (int off = ServerDataScanStart; off <= ServerDataScanEnd; off += 8)
                {
                    var c = this.ReadPtr(serverData + off);
                    if (c == IntPtr.Zero || this.ReadPtr(c) != this.expCtrlVtable) continue;
                    if (!this.ExpControllerLooksValid(c)) continue;
                    this.expCtrlSource = $"serverData+0x{off:x} (scan)";
                    controller = c;
                    return true;
                }
            }

            this.expCtrlSource = "none";
            return false;
        }

        // Structural validation (no build-specific vtable hardcode): a small total explosive count at +0x2b0
        // and a well-formed placed-charge std::vector at +0x220 whose element count never exceeds the total.
        // These invariants reject the unrelated objects that occupy the same ServerData slot outside expedition.
        private bool ExpControllerLooksValid(IntPtr c)
        {
            if (c == IntPtr.Zero) return false;
            if (!this.TryReadI32(c + ExpCtrlTotalOffset, out var raw)) return false;
            int total = raw & 0xFF;
            if (total < 1 || total > 64) return false;
            if (!this.TryReadPlacedCount(c, out var placed)) return false;
            return placed >= 0 && placed <= total;
        }

        // Read the placed-charge count from the controller's std::vector at +0x220, TOLERATING an empty
        // (null) vector. Pre-placement the vector is genuinely {begin=0,end=0} — TryReadStdVector rejects
        // that null begin, which used to make ExpControllerLooksValid fail and the controller "disappear"
        // until the first charge was placed (symptom: "Controller + HUD unreadable", manual fallback shown).
        // Empty ⇒ 0 placed; a non-empty vector must be 8-aligned with last ≥ first.
        private bool TryReadPlacedCount(IntPtr c, out int placed)
        {
            placed = 0;
            var buf = new byte[16];
            if (!ReadProcessMemory(this.processHandle, c + ExpCtrlPlacedVecOffset, buf, (uint)buf.Length, out _))
                return false;
            long first = BitConverter.ToInt64(buf, 0);
            long last = BitConverter.ToInt64(buf, 8);
            if (first == 0 && last == 0) return true;   // empty vector ⇒ 0 placed
            ulong f = (ulong)first;
            if (f < 0x10000 || f > 0x7FFFFFFFFFFFul) return false;
            long span = last - first;
            if (span < 0 || (span % 8) != 0) return false;
            placed = (int)(span / 8);
            return true;
        }

        // Remember the controller's vtable the first time a validated controller resolves, so the drift-recovery
        // scan can match it exactly. Constant per game build, so capturing once per session is enough.
        private void CaptureControllerVtable(IntPtr c)
        {
            if (this.expCtrlVtable != IntPtr.Zero) return;
            var vt = this.ReadPtr(c);
            if (vt != IntPtr.Zero) this.expCtrlVtable = vt;
        }

        private bool TryReadExpeditionCounts(out int total, out int placed)
        {
            total = 0;
            placed = 0;
            if (!this.TryGetExpeditionController(out var c)) return false;
            if (this.TryReadI32(c + ExpCtrlTotalOffset, out var raw)) total = raw & 0xFF;
            this.TryReadPlacedCount(c, out placed);
            return true;
        }

        // Read the explosive-counter HUD's displayed number = explosives REMAINING (max − placed). RE path
        // (project-expedition-planner): widget GameUi→[97][9][17][1] → [0]→[0]→[0] → leaf; number is a
        // std::wstring at +0x4C0 (dup at +0x390 = NameWStringOffset). Used as the controller fallback when its
        // pointer reads null: total = remaining + placed-from-entities. Language-independent (just digits).
        private bool TryReadExpeditionHudRemaining(out int remaining)
        {
            remaining = 0;
            if (!this.TryGetExpeditionWidget(out var leaf)) return false;
            for (int i = 0; i < 3; i++)
            {
                leaf = this.GetChild(leaf, 0);
                if (leaf == IntPtr.Zero) return false;
            }

            // Counter glyphs live at +0x4C0; fall back to the +0x390 duplicate if that's empty.
            string txt = this.ReadStdWString(leaf + 0x4C0);
            if (string.IsNullOrEmpty(txt)) txt = this.ReadStdWString(leaf + NameWStringOffset);
            if (string.IsNullOrEmpty(txt)) return false;

            int val = 0;
            bool any = false;
            foreach (char ch in txt)
            {
                if (ch >= '0' && ch <= '9') { val = (val * 10) + (ch - '0'); any = true; }
                else if (any) break;
            }

            if (!any || val > 999) return false;
            remaining = val;
            return true;
        }

        // Draw above each placed charge its chain-step distances from the previous node: S / P / C.
        private void DrawExpeditionGridValues()
        {
            var world = Core.States.InGameStateObject.CurrentWorldInstance;
            if (world == null) return;

            var draw = ImGui.GetBackgroundDrawList();
            foreach (var it in this.expItems)
            {
                if (it.Kind != ExpKind.Charge || it.Straight < 0f) continue;

                var screen = world.WorldToScreen(it.World);
                string text = it.Path >= 0f
                    ? $"S{it.Straight:F0} P{it.Path:F0} C{it.Cost:F0}"
                    : $"S{it.Straight:F0}";
                var ts = ImGui.CalcTextSize(text);
                var at = new Vector2(screen.X - (ts.X * 0.5f), screen.Y - ts.Y - 10f);
                draw.AddRectFilled(at - new Vector2(3f, 1f), at + ts + new Vector2(3f, 1f), 0xCC000000);
                draw.AddText(at + new Vector2(1f, 1f), 0xFF000000, text);   // shadow
                draw.AddText(at, 0xFF00D7FFu, text);                       // gold
            }
        }

        private void DrawExpeditionDebugWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(500, 470), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("Expedition Debug###RunecraftExpeditionDebug"))
            {
                ImGui.End();
                return;
            }

            ImGui.Text($"Scan: {this.expScanStatus}");
            ImGui.Separator();

            if (this.expHasDetonator)
                ImGui.Text($"Detonator: ({this.expDetonatorPos.X:F0}, {this.expDetonatorPos.Y:F0})");
            else
                ImGui.TextDisabled("Detonator: not found");

            if (this.expCtrlResolved)
            {
                int remaining = this.expTotalCharges - this.expPlacedFromCtrl;
                ImGui.Text($"Charges: total {this.expTotalCharges}  ·  placed {this.expPlacedFromCtrl} (ctrl) / " +
                           $"{this.expPlacedFromEntities} (entities)  ·  remaining {remaining}");
                ImGui.TextDisabled($"controller source: {this.expCtrlSource}");
            }
            else
            {
                ImGui.TextDisabled($"Charges: controller not resolved  ·  placed {this.expPlacedFromEntities} (entities)");
            }

            int chests = 0, markers = 0, remnants = 0, monos = 0;
            foreach (var it in this.expItems)
            {
                switch (it.Kind)
                {
                    case ExpKind.Chest: chests++; break;
                    case ExpKind.Marker: markers++; break;
                    case ExpKind.Remnant: remnants++; break;
                    case ExpKind.Monolith: monos++; break;
                }
            }

            ImGui.Separator();
            ImGui.Text($"Targets — chests {chests} · markers {markers} · remnants {remnants} · monoliths {monos}");
            ImGui.TextDisabled("charges: S/P/C = straight / A* path / A* raw-cost, from the PREVIOUS chain node");
            ImGui.Spacing();

            if (ImGui.BeginTable("expitems", 6,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("kind", ImGuiTableColumnFlags.WidthFixed, 64f);
                ImGui.TableSetupColumn("grid", ImGuiTableColumnFlags.WidthFixed, 88f);
                ImGui.TableSetupColumn("S", ImGuiTableColumnFlags.WidthFixed, 42f);
                ImGui.TableSetupColumn("P", ImGuiTableColumnFlags.WidthFixed, 42f);
                ImGui.TableSetupColumn("C", ImGuiTableColumnFlags.WidthFixed, 42f);
                ImGui.TableSetupColumn("info", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                foreach (var it in this.expItems)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0); ImGui.Text(it.Kind == ExpKind.Charge ? $"chg#{it.ChainIndex}" : it.Kind.ToString());
                    ImGui.TableSetColumnIndex(1); ImGui.Text($"{it.Pos.X:F0}, {it.Pos.Y:F0}");
                    ImGui.TableSetColumnIndex(2); ImGui.Text(it.Straight >= 0f ? it.Straight.ToString("F0") : "-");
                    ImGui.TableSetColumnIndex(3); ImGui.Text(it.Path >= 0f ? it.Path.ToString("F0") : "-");
                    ImGui.TableSetColumnIndex(4); ImGui.Text(it.Cost >= 0f ? it.Cost.ToString("F0") : "-");
                    ImGui.TableSetColumnIndex(5);
                    ImGui.Text(it.Value > 0 ? $"{it.Info}  ({it.Value:F0} ex)" : it.Info);
                }

                ImGui.EndTable();
            }

            ImGui.End();
        }

        private const float ExpWorldPerGrid = 250f / 23f;

        private static float ExpDistSq(Vector2 a, Vector2 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return (dx * dx) + (dy * dy);
        }

        // Total uncaptured ex weight whose target centre lies within the blast (r2 = effRadius²) of p.
        private static double ExpCoverGain(List<Vector2> tPos, List<double> tW, bool[] captured, Vector2 p, float r2, out int count)
        {
            double g = 0; count = 0;
            for (int u = 0; u < tPos.Count; u++)
                if (!captured[u] && ExpDistSq(p, tPos[u]) <= r2) { g += tW[u]; count++; }
            return g;
        }

        // Marker-coverage gate: is committing a coverage charge at `p` worthwhile? Always yes if it grabs a PRIMARY
        // (a valued monolith). Otherwise (a marker-only placement) only if it covers ≥ MinMarkers secondary markers
        // in one blast — so spare charges land on dense clusters, never single trash markers. Off ⇒ always allowed
        // (Grand / no primary list), preserving the old "any positive gain is worth a charge" behaviour.
        private static bool ExpPlacementWorthwhile(ExpRouteInputs inp, bool[] captured, Vector2 p, float r2)
        {
            if (!inp.MarkerCoverageMode) return true;
            var tPos = inp.TPos; var prim = inp.TPrimary;
            int markers = 0;
            for (int u = 0; u < tPos.Count; u++)
            {
                if (captured[u] || ExpDistSq(p, tPos[u]) > r2) continue;
                if (u < prim.Count && prim[u]) return true;   // captures a primary ⇒ always worth it
                markers++;
            }

            return markers >= inp.MinMarkers;
        }

        // Centroid of the uncaptured targets currently within blast range of `around`. Used to recentre a
        // charge into the MIDDLE of the cluster it grabs, so the suggest sits among its targets instead of
        // on a blast-edge tangent.
        private static Vector2 ExpCentroidOfCovered(List<Vector2> tPos, bool[] captured, Vector2 around, float r2)
        {
            float sx = 0f, sy = 0f; int m = 0;
            for (int u = 0; u < tPos.Count; u++)
                if (!captured[u] && ExpDistSq(around, tPos[u]) <= r2) { sx += tPos[u].X; sy += tPos[u].Y; m++; }
            return m > 0 ? new Vector2(sx / m, sy / m) : around;
        }

        // Slide a coverage charge off the bare cluster marker to the reachable point that catches the MOST
        // uncaptured weight in one blast — so an adjacent high-value flag just outside the cluster's own radius
        // (the GOLD-next-to-#5 case: 36 grid off, missed when the charge sat on the cluster) joins the same blast.
        // Candidates: the cluster point itself, the WEIGHTED centroid of uncaptured targets within 2·effRadius
        // (pulls toward the heaviest neighbours), and the midpoint toward each such neighbour (pairs the cluster
        // with one strong outlier). Keeps only points reachable from `node` in one hop; never lowers coverage
        // (seeded with the cluster point's own gain), so it's a strict free upgrade. Returns the chosen point + its
        // reach. `node`→c0 is already known reachable by the caller, so a valid answer always exists.
        private static Vector2 ExpMaxCoverPoint(ExpRouteInputs inp, bool[] captured, Vector2 node, Vector2 c0,
                                                float r2, float effDist, out float reach)
        {
            var data = inp.WalkData; int bpr = inp.Bpr; var doors = inp.Doors;
            var tPos = inp.TPos; var tW = inp.TW; int n = tPos.Count;
            float wide = 4f * r2;   // (2·effRadius)² — gather neighbours one extra radius out

            Vector2 best = c0;
            double bestGain = ExpCoverGain(tPos, tW, captured, c0, r2, out _);
            reach = ExpReach(data, bpr, doors, node, c0, effDist);

            // Gather uncaptured targets near the cluster, then build candidates: the weighted centroid,
            // the cluster→neighbour midpoints, AND the midpoint of every NEIGHBOUR PAIR. The pair midpoint
            // is the covering-circle centre for two outliers that each sit just outside the other's radius
            // (the gold-next-to-#5 case: gold(920,1368) and the far logbook(967,1392) are 53 grid apart, so
            // their midpoint (943.5,1380) sits within one effRadius of BOTH — and the 3rd flag (938,1392)
            // falls in the same blast). cluster→neighbour midpoints alone can't reach that point because it
            // is centred between two flags, NEITHER of which is the cluster marker the charge sat on.
            double sw = 0; float cx = 0f, cy = 0f;
            var near = new List<int>();
            for (int u = 0; u < n; u++)
            {
                if (captured[u] || ExpDistSq(c0, tPos[u]) > wide) continue;
                near.Add(u);
                sw += tW[u]; cx += (float)(tPos[u].X * tW[u]); cy += (float)(tPos[u].Y * tW[u]);
            }

            var cands = new List<Vector2>();
            for (int i = 0; i < near.Count; i++)
            {
                cands.Add(Vector2.Lerp(c0, tPos[near[i]], 0.5f));                   // cluster ↔ each neighbour
                for (int j = i + 1; j < near.Count; j++)
                    cands.Add(Vector2.Lerp(tPos[near[i]], tPos[near[j]], 0.5f));    // neighbour ↔ neighbour
            }
            if (sw > 0) cands.Add(new Vector2(cx / (float)sw, cy / (float)sw));

            foreach (var p in cands)
            {
                double g = ExpCoverGain(tPos, tW, captured, p, r2, out _);
                if (g <= bestGain) continue;                       // never reduce coverage
                float rr = ExpReach(data, bpr, doors, node, p, effDist);
                if (rr < 0f) continue;                             // must still be one hop from the chain
                best = p; bestGain = g; reach = rr;
            }

            return best;
        }

        // Walkable A* path distance a→b if ≤ maxDist (and a path exists), else -1. Falls back to straight
        // line when no walkable grid is available this scan. STATIC + walk-data params so the background route
        // compute can call it off the render thread against a snapshot (the instance fields may change mid-run).
        //
        // The A* is DISTANCE-BOUNDED at maxDist × 1.5: a path whose smoothed length ≤ maxDist has raw grid cost
        // ≤ √2 × maxDist (diagonal-staircase worst case) < the bound, so it is always found — i.e. the answer is
        // identical to an unbounded search. But a target that's far / behind a wall is rejected after a small local
        // expansion (often the very first node, when its straight-line h already exceeds the bound) instead of
        // flooding the whole reachable component. This is the dominant query type, so the bound is the main speedup.
        private static float ExpReach(byte[]? data, int bpr, HashSet<(int, int)>? doors, Vector2 a, Vector2 b, float maxDist)
        {
            if (data == null)
            {
                float s = Vector2.Distance(a, b);
                return s <= maxDist ? s : -1f;
            }

            var cache = expCache;
            (int, int, int, int, int) key = default;
            if (cache != null)
            {
                key = (cache.DoorId(doors), (int)Math.Round(a.X), (int)Math.Round(a.Y),
                       (int)Math.Round(b.X), (int)Math.Round(b.Y));
                if (cache.ReachLen.TryGetValue(key, out var hit)) { cache.Hit(); return hit; }
            }

            long t0 = Stopwatch.GetTimestamp();
            var route = WalkablePathfinder.FindPath(data, bpr, a, b, doors, maxCost: maxDist * 1.5f);
            float result;
            if (route == null || route.Count < 2)
            {
                result = -1f;
            }
            else
            {
                float len = 0f;
                for (int i = 1; i < route.Count; i++) len += Vector2.Distance(route[i - 1], route[i]);
                result = len <= maxDist ? len : -1f;
            }

            if (cache != null) { cache.Miss(); cache.AddAStarTicks(Stopwatch.GetTimestamp() - t0); cache.ReachLen[key] = result; }
            return result;
        }

        // Full walkable A* path length a→b (no cap); -1 if no path. Falls back to straight line when there's no
        // walkable grid. Used by the route planner to score pursuit of far targets (prize-per-path) and tour cost.
        private static float ExpFullPath(byte[]? data, int bpr, HashSet<(int, int)>? doors, Vector2 a, Vector2 b)
        {
            if (data != null) return ExpPathLength(data, bpr, doors, a, b);
            return Vector2.Distance(a, b);
        }

        // A door-override set with EVERY gate footprint opened (its blocked cells made walkable). Used to test
        // what the route could reach if all blockers were blasted. Returns the input set unchanged when no gates.
        private static HashSet<(int, int)>? ExpDoorsAllGatesOpen(HashSet<(int, int)>? doors, List<ExpGateInput> gates)
        {
            if (gates == null || gates.Count == 0) return doors;
            var d = doors != null ? new HashSet<(int, int)>(doors) : new HashSet<(int, int)>();
            foreach (var g in gates)
                foreach (var c in g.Footprint)
                    d.Add(c);
            return d;
        }

        // Append one line to the planner decision trace (no-op when logging is off).
        private static void ExpLog(ExpRouteInputs inp, string msg) => inp.Log?.Add(msg);

        private static StdTuple3D<float> ExpGridToWorld(Vector2 g, float z) =>
            new StdTuple3D<float> { X = g.X * ExpWorldPerGrid, Y = g.Y * ExpWorldPerGrid, Z = z };

        private static bool ExpIsWalkable(byte[]? data, int bpr, HashSet<(int, int)>? doors, Vector2 grid)
        {
            if (data == null) return true;   // no grid this scan → don't block placement
            int x = (int)(grid.X + 0.5f);
            int y = (int)(grid.Y + 0.5f);
            if (x < 0 || y < 0) return false;
            return LineWalker.IsWalkable(data, bpr, x, y, doors);
        }

        // Farthest WALKABLE point ≤ maxDist (path length) from `from` along the route toward `toward`. Used to
        // drop a stepping-stone / forward charge; advances only through walkable path vertices and validates
        // the final partial step, so it never returns a point on non-walkable terrain. False if it can't move.
        private static bool ExpStepToward(byte[]? data, int bpr, HashSet<(int, int)>? doors, Vector2 from, Vector2 toward, float maxDist, out Vector2 step)
            => ExpStepToward(data, bpr, doors, doors, from, toward, maxDist, out step);

        // Two-door variant: ROUTE the path with `pathDoors` (optimistic — may treat closed gates as open so we can
        // head toward a prize behind a blocker) but only STEP onto cells walkable per `walkDoors` (the real grid),
        // stopping at the last really-walkable vertex. That lands the charge right at the blocker — whose blast
        // then opens the gate — so the next hop genuinely continues through it. With pathDoors == walkDoors this is
        // identical to the plain version (no gate optimism).
        private static bool ExpStepToward(byte[]? data, int bpr, HashSet<(int, int)>? pathDoors, HashSet<(int, int)>? walkDoors, Vector2 from, Vector2 toward, float maxDist, out Vector2 step)
        {
            step = from;
            if (data == null)
            {
                float dist = Vector2.Distance(from, toward);
                if (dist <= 1f) return false;
                step = Vector2.Lerp(from, toward, Math.Min(1f, maxDist / dist));
                return Vector2.Distance(from, step) > 1f;
            }

            var cache = expCache;
            List<Vector2>? route;
            if (cache != null)
            {
                var key = (cache.DoorId(pathDoors), (int)Math.Round(from.X), (int)Math.Round(from.Y),
                           (int)Math.Round(toward.X), (int)Math.Round(toward.Y));
                if (cache.Path.TryGetValue(key, out route)) { cache.Hit(); }
                else { long t0 = Stopwatch.GetTimestamp(); route = WalkablePathfinder.FindPath(data, bpr, from, toward, pathDoors); cache.Miss(); cache.AddAStarTicks(Stopwatch.GetTimestamp() - t0); cache.Path[key] = route; }
            }
            else
            {
                route = WalkablePathfinder.FindPath(data, bpr, from, toward, pathDoors);
            }

            if (route == null || route.Count < 2) return false;

            // Walk the optimistic polyline RASTERISED (≈1 cell/sample). Checking only the sparse A* waypoints would
            // skip over a closed gate's footprint when the optimistic path runs dead-straight through it; rasterising
            // catches it. We advance while each sampled cell is walkable on the REAL grid (walkDoors). When a sample
            // is NOT real-walkable we must tell two cases apart:
            //   • walkable under pathDoors but not walkDoors → it's a still-CLOSED GATE on the optimistic shortcut →
            //     STOP here, so the charge lands at the blocker and its blast opens the gate.
            //   • blocked under BOTH door sets → it's just a straight-line corner-cut between two A* waypoints (the
            //     real grid path bends around it) → SKIP and keep scanning; stopping here would be a false dead-end.
            bool gateAware = !ReferenceEquals(pathDoors, walkDoors);
            float acc = 0f;
            Vector2 best = from;        // last confirmed real-walkable point (start is walkable)
            Vector2 prev = route[0];
            for (int i = 1; i < route.Count; i++)
            {
                Vector2 a = route[i - 1], b = route[i];
                float seg = Vector2.Distance(a, b);
                int sub = Math.Max(1, (int)Math.Ceiling(seg));
                for (int s = 1; s <= sub; s++)
                {
                    Vector2 p = Vector2.Lerp(a, b, (float)s / sub);
                    float d = Vector2.Distance(prev, p);
                    if (acc + d > maxDist)
                    {
                        var cand = Vector2.Lerp(prev, p, d <= 1e-3f ? 0f : (maxDist - acc) / d);
                        if (ExpIsWalkable(data, bpr, walkDoors, cand)) best = cand;
                        step = best;
                        return Vector2.Distance(from, step) > 1f;
                    }

                    if (!ExpIsWalkable(data, bpr, walkDoors, p))
                    {
                        if (gateAware && ExpIsWalkable(data, bpr, pathDoors, p)) { step = best; return Vector2.Distance(from, step) > 1f; }
                        acc += d; prev = p;   // corner-cut between waypoints → advance past it, don't mark it walkable
                        continue;
                    }

                    acc += d; prev = p; best = p;
                }
            }

            step = best;
            return Vector2.Distance(from, step) > 1f;
        }

        // A cheap fingerprint of everything the route depends on (modifier sliders, reward weights, the
        // detonator anchor, the discovered targets, the charge budget). The planner compares it against the
        // fingerprint captured at the last "Run" to tell the user the plan is stale — the heavy A* recompute
        // itself runs ONLY when Run is pressed (editing weights/sliders must stay lag-free).
        private string BuildRouteFingerprint()
        {
            var s = this.Settings;
            var anchor = this.expDetonatorPos;

            // Aggregate over the persistent cache (NOT the awake snapshot) so the fingerprint is stable as
            // the player walks; it changes only when new targets are discovered or a monolith value resolves.
            int monoN = 0;
            double monoSum = 0;
            var markerCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var relicSet = new List<string>();
            foreach (var t in this.expTargetCache.Values)
            {
                if (t.Kind == ExpKind.Monolith) { monoN++; monoSum += t.Value; }
                else if (t.Kind == ExpKind.Marker && !string.IsNullOrEmpty(t.Info))
                {
                    markerCounts.TryGetValue(t.Info, out var cnt);
                    markerCounts[t.Info] = cnt + 1;
                }
                else if (t.Kind == ExpKind.Remnant && !string.IsNullOrEmpty(t.Info))
                {
                    relicSet.Add(t.Info);
                }
            }

            // Serialise the discovered marker composition + every configured weight so an edit to any reward
            // weight (or a newly seen reward type) re-plans, while plain movement does not.
            var mb = new System.Text.StringBuilder();
            foreach (var kv in markerCounts) mb.Append(kv.Key).Append(':').Append(kv.Value).Append(',');
            var tp = this.ExpActiveTargetProfile();
            var wb = new System.Text.StringBuilder();
            wb.Append(tp.Name).Append(':');
            foreach (var kv in new SortedDictionary<string, float>(tp.Weights, StringComparer.Ordinal))
                wb.Append(kv.Key).Append('=').Append(kv.Value.ToString("F2")).Append(',');

            // Relics: the discovered mod-sets (so a newly resolved relic re-plans) + the active buff profile's name
            // and weights (so editing a relic weight or switching profile re-plans). Sorted for stability.
            relicSet.Sort(StringComparer.Ordinal);
            var rb = string.Join("|", relicSet);
            var bp = this.ExpActiveBuffProfile();
            var bb = new System.Text.StringBuilder();
            bb.Append(bp?.Name ?? "-").Append(':');
            if (bp != null)
                foreach (var kv in new SortedDictionary<string, float>(bp.Weights, StringComparer.Ordinal))
                    bb.Append(kv.Key).Append('=').Append(kv.Value.ToString("F2")).Append(',');

            return $"{s.ExpPlacementDistancePct}|{s.ExpBlastRadiusPct}|{s.ExpMonolithMinEx}|" +
                   $"{wb}|{mb}|{monoN}|{monoSum:F0}|{anchor.X:F0},{anchor.Y:F0}|" +
                   $"{this.ExpEffectiveTotal()}|{this.expCtrlResolved}|{this.expHasDetonator}|" +
                   $"{s.ExpMinMarkersPerSpareCharge}|{bb}|{rb}";
        }

        // Total charges to plan for: controller count if resolved → else the HUD-counter total (remaining +
        // placed) → else the manual fallback. (The controller pointer can read null when its UI path drifts.)
        private int ExpEffectiveTotal() =>
            this.expCtrlResolved ? this.expTotalCharges :
            this.expHudResolved ? this.expHudTotal :
            this.Settings.ExpTotalChargesManual;

        // Index of the next charge to place = how many are already down. Uses the per-area monotonic placed
        // count (see ScanExpedition): controller vector / HUD-derived / awake-entity count, clamped so it never
        // drops when earlier placements fall out of the awake list as you advance.
        private int ExpNextIndex() => this.expPlacedMax;

        // Snapshot every input the route planner needs, on the render thread, so the background Task never
        // touches live game state (the walk grid and target cache are mutated by scans on the main thread).
        private ExpRouteInputs BuildRouteInputs()
        {
            var s = this.Settings;
            float effDist = this.ExpBasePlacementDistance() * (1f + (s.ExpPlacementDistancePct / 100f));
            float effRadius = this.ExpBaseBlastRadius() * (1f + (s.ExpBlastRadiusPct / 100f));
            var inp = new ExpRouteInputs
            {
                WalkData = this.expWalkData,
                Bpr = this.expBpr,
                Doors = this.expDoors,
                HasDetonator = this.expHasDetonator,
                DetonatorPos = this.expDetonatorPos,
                DetonatorWorld = this.expDetonatorWorld,
                Budget = this.expHasDetonator ? this.ExpEffectiveTotal() : 0,
                EffDist = effDist,
                EffRadius = effRadius,
                StepDist = Math.Max(1f, effDist - ExpStepMarginGrid),
                // Normal maps drive the route off monoliths and only spend SPARE charges on dense marker clusters;
                // Grand stays on the old "every weighted target is a route driver" behaviour (frozen).
                MarkerCoverageMode = !ExpIsGrand(this.expHasDetonator ? this.ExpEffectiveTotal() : 0),
                // Normal Expedition: always treat as 1 (any covered marker earns the spare charge) — the
                // density knob is a Grand-only control and hidden in the UI on normal maps. Grand uses the slider.
                MinMarkers = ExpIsGrand(this.expHasDetonator ? this.ExpEffectiveTotal() : 0)
                    ? Math.Max(1, s.ExpMinMarkersPerSpareCharge)
                    : 1,
                Log = s.ExpLogPlanner ? new List<string>() : null,
            };

            // Gate-aware doors: a BLOCKED blocker is a true wall for the planner. LineWalker.BuildDoorOverrideMap
            // force-marks 5×5 around EVERY TriggerableBlockage as walkable (a generic-door convenience) — wrong for
            // expedition, where the blocker walls a passage off until blasted. Strip that freebie (and the flood
            // footprint) so A* sees the real hole; opening a gate later re-adds its footprint to a working copy.
            if (this.expGates.Count > 0 && this.expDoors != null)
            {
                var routeDoors = new HashSet<(int, int)>(this.expDoors);
                foreach (var g in this.expGates)
                {
                    if (!g.Blocked) continue;
                    int cx = (int)g.Grid.X, cy = (int)g.Grid.Y;
                    for (int dx = -2; dx <= 2; dx++)
                        for (int dy = -2; dy <= 2; dy++)
                            routeDoors.Remove((cx + dx, cy + dy));
                    if (g.Footprint != null) foreach (var c in g.Footprint) routeDoors.Remove(c);
                }

                inp.Doors = routeDoors;
            }

            // Snapshot blocked gates (deep-copy the footprint sets — they're mutated by scans on the main thread).
            foreach (var g in this.expGates)
            {
                if (!g.Blocked || g.Footprint == null || g.Footprint.Count == 0) continue;
                inp.Gates.Add(new ExpGateInput
                {
                    Center = g.Grid,
                    WorldZ = g.WorldZ,
                    Footprint = new HashSet<(int, int)>(g.Footprint),
                });
            }

            float markerBaseline = this.ExpMarkerBaselineZ();

            // Kalguur Sentinel buff (Normal expedition only): it multiplies the Logbook drop from tall "double-flag"
            // (logbook-tier) markers, so grabbing it is worth more than any single double flag — but ONLY when such a
            // flag exists to empower (nothing to multiply otherwise). Grand keeps the old behaviour (MarkerCoverageMode
            // is off on Grand). Decide once here; the per-target loop below promotes the Sentinel accordingly.
            bool sentinelWorthwhile = inp.MarkerCoverageMode;
            if (sentinelWorthwhile)
            {
                bool hasLogbookFlag = false;
                foreach (var t in this.expTargetCache.Values)
                {
                    if (t.Kind != ExpKind.Marker) continue;
                    this.ExpMarkerTierWeight(ExpMarkerPoleOffset(t), markerBaseline, out string tier);
                    if (tier == "logbook") { hasLogbookFlag = true; break; }
                }
                sentinelWorthwhile = hasLogbookFlag;
            }

            foreach (var t in this.expTargetCache.Values)
            {
                double w = 0;
                bool primary = false;
                if (t.Kind == ExpKind.Monolith)
                {
                    if (t.Value > 0 && t.Value >= s.ExpMonolithMinEx) { w = t.Value; primary = true; }
                }
                else if (t.Kind == ExpKind.Marker)
                {
                    // NORMAL: reward flags are valued by pole HEIGHT (logbook/gold/magic/white tiers) — the whole
                    // height-tier + Sentinel logic only makes sense here. GRAND: those same flags are INERT (their
                    // drop is completely different / useless), so height weights must NOT leak in and send the route
                    // chasing them. In Grand, value markers by their reward-icon profile instead (reward-chest icons
                    // get their profile weight; inert flags fall to the catch-all default and stay negligible).
                    w = inp.MarkerCoverageMode
                        ? this.ExpMarkerTierWeight(ExpMarkerPoleOffset(t), markerBaseline, out _)
                        : this.ExpEffectiveRewardWeight(t.Info);
                }
                else if (t.Kind == ExpKind.Sentinel)
                {
                    // Ranked strictly above a logbook flag so, under budget pressure, the flag is dropped before the
                    // buff (the buff empowers ALL the double flags at once). Primary route driver, Normal only.
                    if (sentinelWorthwhile) { w = s.ExpMarkerWeightLogbook * 1.5 + 1; primary = true; }
                }
                else if (t.Kind == ExpKind.Remnant)
                {
                    // Relic value = Σ(+ weights) − Σ(− weights) from the active buff profile. Net > 0 → a beneficial
                    // relic worth routing the blast through (a primary driver, like a monolith); ≤ 0 → ignored.
                    double net = this.ExpRelicNetWeight(t.Info);
                    if (net > 0) { w = net; primary = true; }
                }

                if (w <= 0) continue;
                inp.TPos.Add(t.Pos);
                inp.TWorld.Add(t.World);
                inp.TW.Add(w);
                inp.TPrimary.Add(primary);
                inp.TSentinel.Add(t.Kind == ExpKind.Sentinel);
            }

            // FALLBACK route drivers: a Normal expedition often has NO monolith passing the price filter and no
            // beneficial relic → zero primary anchors → the spine planner builds nothing and the valuable flags are
            // never collected (the reported "no monoliths ⇒ no route" bug). When nothing primary exists, promote
            // every surviving marker (all non-tiny — tiny flags were dropped above at w≤0) to a primary anchor so a
            // route IS built over the flags. Highest-weight first so the tour favours logbook/gold over white.
            bool anyPrimary = false;
            for (int i = 0; i < inp.TPrimary.Count; i++) if (inp.TPrimary[i]) { anyPrimary = true; break; }
            if (!anyPrimary && inp.TPrimary.Count > 0)
            {
                int promoted = 0;
                for (int i = 0; i < inp.TPrimary.Count; i++) { inp.TPrimary[i] = true; promoted++; }
                ExpLog(inp, $"[fallback] no primary anchors — promoted {promoted} reward flag(s) to route drivers");
            }

            return inp;
        }

        // Kick the heavy A* planner onto a Task (the UI froze when it ran inline on "Run"). The result is
        // published into expPendingResult and applied on the next planner frame (ApplyPendingRouteResult).
        private void LaunchRouteCompute(string fingerprint)
        {
            if (this.expComputing) return;
            var inp = this.BuildRouteInputs();
            this.expComputing = true;
            Task.Run(() =>
            {
                ExpRouteResult res;
                try { res = ExpComputeRoute(inp); }
                catch { res = new ExpRouteResult(); }   // a bg-thread throw must never take the plugin down
                lock (this.expResultLock)
                {
                    this.expPendingResult = res;
                    this.expPendingFingerprint = fingerprint;
                }
            });
        }

        // Main-thread: if the background planner finished, swap its plan into the live route + diag fields.
        private void ApplyPendingRouteResult()
        {
            ExpRouteResult res;
            string fp;
            lock (this.expResultLock)
            {
                if (this.expPendingResult == null) return;
                res = this.expPendingResult;
                fp = this.expPendingFingerprint;
                this.expPendingResult = null;
            }

            this.expRoute.Clear();
            this.expRoute.AddRange(res.Route);
            this.expSpinePts.Clear();
            this.expSpinePts.AddRange(res.SpinePts);
            this.expSpineZs.Clear();
            this.expSpineZs.AddRange(res.SpineZs);
            this.expRouteWeight = res.Weight;
            this.expRouteCovered = res.Covered;
            this.expRouteTargets = res.Targets;
            this.expRouteFingerprint = fp;
            this.expLastComputeMs = res.ComputeMs;
            this.expLastAStarMs = res.AStarMs;
            this.expLastAStarCalls = res.AStarCalls;
            this.expLastAStarHits = res.AStarHits;
            this.expLastPhase = res.Phase;
            this.expComputing = false;

            if (res.Log != null)
            {
                try
                {
                    var path = Path.Join(this.DllDirectory, "expedition_planner_log.txt");
                    File.WriteAllLines(path, res.Log);
                    this.expLogPath = path;
                    this.expLogLines = res.Log.Count;
                }
                catch { /* logging must never break the planner */ }
            }
        }

        private string expLogPath = string.Empty;
        private int expLogLines;

        // Charge-budget threshold that separates a Grand / Logbook Expedition (base 20 charges) from a normal-map
        // Expedition (base 5). A normal map can drift up a little with +explosives mods, so the split sits at a
        // safe midpoint. The two share EVERYTHING here (controller detection, target scan, geometry/route helpers,
        // drawing) and differ ONLY in the route ORCHESTRATION, which lives per-type so each can be tuned without
        // risking the other (user directive 2026-06-28). See RunecraftHelperCore.ExpeditionNormal.cs /
        // RunecraftHelperCore.ExpeditionGrand.cs.
        private const int ExpGrandChargeThreshold = 10;

        private static bool ExpIsGrand(int totalCharges) => totalCharges >= ExpGrandChargeThreshold;

        // Route planner: the monolith SPINE + PLACER pipeline (ComputeRouteSpine) — the only strategy.
        private static ExpRouteResult ExpComputeRoute(ExpRouteInputs inp)
        {
            var cache = new ExpPathCache();
            expCache = cache;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var res = ComputeRouteSpine(inp);
                sw.Stop();
                res.ComputeMs = sw.Elapsed.TotalMilliseconds;
                res.AStarCalls = cache.Calls;
                res.AStarHits = cache.Hits;
                res.AStarMs = cache.AStarTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                inp.Log?.Add($"=== TIMING === {res.ComputeMs:F0} ms · A* {cache.Calls} run = {res.AStarMs:F0} ms summed " +
                             $"+ {cache.Hits} cached ({cache.Calls + cache.Hits} queries)");
                inp.Log?.Add("=== PHASES === " + res.Phase);
                return res;
            }
            finally
            {
                expCache = null;   // never leak the per-compute memo onto a pooled thread
            }
        }

        // Spine polyline resolution (grid). FindPath returns SPARSE waypoints (a 1365-grid route can be ~13 points),
        // and consecutive waypoints can be >effDist apart — which would stop the Placer's forward sweep dead. So we
        // RASTERISE each waypoint pair to ~this spacing, giving a dense line the Placer can step along at any point.
        // 4 grid is far finer than the blast radius (~35), so edge-placement granularity is plenty.
        private const float ExpSpineStep = 4f;

        // ── Algorithm 1: the ROUTER (strict-spine polyline) ──────────────────────────────────────────────────
        // Builds the line the player walks — detonator → anchors in the given (already 2-opt'd) order — as ONE
        // continuous DENSE walkable polyline. Each detonator/anchor→anchor A* path is concatenated and its sparse
        // waypoints rasterised to ~ExpSpineStep spacing (see above). It decides ONLY where to go, never where charges
        // land (that's the Placer, Algorithm 2). World Z per point is linearly interpolated between the segment's
        // endpoint heights so the iso projection sits the line on the ground; SpineAnchorIdx records where each
        // ordered anchor falls in the point list. On a no-grid scan it degrades to straight det→anchor hops.
        private static void ExpBuildSpinePolyline(ExpRouteInputs inp, List<Vector2> ordered,
            out List<Vector2> pts, out List<float> zs, out List<int> anchorIdx)
        {
            pts = new List<Vector2>();
            zs = new List<float>();
            anchorIdx = new List<int>();
            var data = inp.WalkData; int bpr = inp.Bpr; var doors = inp.Doors;

            pts.Add(inp.DetonatorPos);
            zs.Add(inp.DetonatorWorld.Z);

            Vector2 from = inp.DetonatorPos;
            float fromZ = inp.DetonatorWorld.Z;
            foreach (var to in ordered)
            {
                float toZ = fromZ;
                for (int i = 0; i < inp.TPos.Count; i++)
                    if (inp.TPos[i] == to) { toZ = inp.TWorld[i].Z; break; }

                var seg = data != null ? WalkablePathfinder.FindPath(data, bpr, from, to, doors) : null;
                var wp = (seg != null && seg.Count >= 2) ? seg : new List<Vector2> { from, to };

                // Total segment length up front, so Z can interpolate by arc fraction across the whole hop.
                float total = 0f;
                for (int i = 1; i < wp.Count; i++) total += Vector2.Distance(wp[i - 1], wp[i]);

                float acc = 0f;
                for (int i = 1; i < wp.Count; i++)
                {
                    Vector2 a = wp[i - 1], b = wp[i];
                    float segLen = Vector2.Distance(a, b);
                    int sub = Math.Max(1, (int)Math.Ceiling(segLen / ExpSpineStep));   // rasterise this waypoint pair
                    for (int s = 1; s <= sub; s++)
                    {
                        float f = (float)s / sub;
                        acc += segLen / sub;
                        float t = total > 0f ? acc / total : 1f;
                        pts.Add(Vector2.Lerp(a, b, f));
                        zs.Add(fromZ + ((toZ - fromZ) * t));
                    }
                }

                anchorIdx.Add(pts.Count - 1);
                from = to;
                fromZ = toZ;
            }
        }

        // Capture every uncaptured target within `pos`'s blast, append the route point at an EXPLICIT world Z (the
        // spine cell's interpolated height — more accurate than deriving Z from a captured target, and correct even
        // for a bridge that captures nothing). Returns the ex newly collected. Placer's analogue of ExpCommit.
        private static double ExpCommitAt(ExpRouteInputs inp, bool[] captured, List<ExpRoutePoint> route,
            Vector2 pos, float placeZ, float r2, bool isBridge, float reach, string note, bool sentinel = false)
        {
            var tPos = inp.TPos; var tW = inp.TW;
            double cgain = 0; int cap = 0;
            for (int u = 0; u < tPos.Count; u++)
            {
                if (captured[u] || ExpDistSq(pos, tPos[u]) > r2) continue;
                captured[u] = true; cap++; cgain += tW[u];
            }

            var placeWorld = ExpGridToWorld(pos, placeZ);
            string kind = isBridge ? "bridge" : "cover";
            route.Add(new ExpRoutePoint(pos, placeWorld, cgain, cap, pos, placeZ,
                $"{kind} {cap} tgt · {cgain:F0} ex · reach {reach:F0}/{inp.EffDist:F0}{note}", sentinel));
            return cgain;
        }

        // ── Algorithm 2: the PLACER (forward sweep along the Router polyline) ─────────────────────────────────
        // Lays charges along the spine polyline so that every anchor (in tour order) is captured, with two human
        // habits baked in:
        //   • EDGE-PLACEMENT — a coverage charge is placed at the FURTHEST-FORWARD polyline cell that still has the
        //     anchor inside the blast, i.e. the anchor sits at the forward edge of the radius, not dead-centre.
        //   • FORWARD-COMPACTION — because that cell may be PAST the anchor (toward the next one), the chain head
        //     advances further per charge, so the next charge starts closer to the next anchor and may even grab it
        //     by radius for free (the "#5→#6" case the smoothing pass couldn't do).
        // Reach between two points is the REAL straight A* distance (ExpReach), NOT the polyline arc length: the
        // spine can double back on itself at a spur/turnaround anchor (out to the monolith, then back the way it
        // came toward the next one), where the arc "there-and-back" wildly overestimates the true reach — that bug
        // pinned coverage charges dead-centre on spur monoliths. Real reach is bounded + memoised, and only probed
        // on the handful of cells that actually cover an anchor, so the cost stays small. When an anchor can't be
        // reached in one hop, a BRIDGE charge advances as far toward it as one hop allows and the loop retries.
        // Spare charges (after all anchors) are left for Phase B. Mutates `captured`; returns the ex collected.
        private static List<ExpRoutePoint> ExpPlaceAlongSpine(ExpRouteInputs inp, List<Vector2> pts, List<float> zs,
            List<int> anchorIdx, List<Vector2> ordered, float r2, bool[] captured, out double weight)
        {
            weight = 0;
            var route = new List<ExpRoutePoint>();
            int M = pts.Count;
            if (M == 0 || ordered.Count == 0) return route;
            var data = inp.WalkData; int bpr = inp.Bpr; var doors = inp.Doors;
            float effDist = inp.EffDist, effRadius = inp.EffRadius;

            // Map each ordered anchor to its target index (for capture marking + weight + logging).
            var anchorTgt = new int[ordered.Count];
            for (int k = 0; k < ordered.Count; k++)
            {
                anchorTgt[k] = -1;
                for (int u = 0; u < inp.TPos.Count; u++)
                    if (inp.TPos[u] == ordered[k]) { anchorTgt[k] = u; break; }
            }

            ExpLog(inp, $"--- PLACER (forward sweep) effDist={effDist:F0} effRadius={effRadius:F0} polyCells={M} ---");

            // How far past an anchor's polyline index a covering cell can still sit (the forward "departure" band).
            // One blast radius of cells either side, plus slack — bounds the coverage scan to cells truly near the
            // anchor so a coincidental far match (a later cell that happens to fall within radius) can't be chosen.
            int band = (int)Math.Ceiling((2f * effRadius / ExpSpineStep)) + 4;

            Vector2 node = inp.DetonatorPos;
            int nodeIdx = 0;     // polyline index of the current chain head (charge or detonator)
            int ai = 0;          // next anchor to cover (index into ordered/anchorIdx)
            int safety = 0;
            while (ai < ordered.Count && route.Count < inp.Budget)
            {
                if (++safety > M + ordered.Count + 8) break;   // belt-and-braces against a stuck loop

                int at = anchorTgt[ai];
                if (at >= 0 && captured[at]) { ai++; continue; }    // already grabbed by a prior blast

                int anchorPosIdx = Math.Min(anchorIdx[ai], M - 1);
                // Cover-test against the anchor's EXACT target position (the polyline endpoint is ~1 cell off after
                // rasterisation); ExpCommitAt re-checks against the same tPos, so a charge chosen here truly captures.
                Vector2 anchorPos = at >= 0 ? inp.TPos[at] : pts[anchorPosIdx];

                // Coverage: among the band cells that cover the anchor AND are reachable from node, take the one
                // whose blast catches the MOST total uncaptured weight — i.e. a monolith with an adjacent reward
                // marker in range gets BOTH in one charge, instead of edge-placing past the reward and paying a
                // separate spare charge for it (the "#7 monolith + #8 reward = 2 charges" waste). Ties break to the
                // furthest-forward cell (higher j), so with no nearby reward this is exactly the old edge-placement
                // + forward-compaction (every anchor-only cell scores the same anchor weight → max j wins). Coverage
                // is cheap; we only spend an A* reach check when a cell would actually improve the best score, so
                // the extra cost over "first-reachable" is small.
                int bestJ = -1; float bestReach = 0f; double bestScore = double.NegativeInfinity;
                Vector2 bestP = default; float bestPZ = 0f;
                int hi = Math.Min(M - 1, anchorPosIdx + band);
                for (int j = hi; j > nodeIdx; j--)
                {
                    if (ExpDistSq(pts[j], anchorPos) > r2) continue;       // must keep the anchor in the blast
                    double cov = ExpCoverGain(inp.TPos, inp.TW, captured, pts[j], r2, out _);
                    double score = cov + (j * 1e-6);                       // +j: forward progress breaks coverage ties
                    if (score <= bestScore) continue;                      // can't beat best → skip the costly reach
                    float rr = ExpReach(data, bpr, doors, node, pts[j], effDist);
                    if (rr < 0f) continue;
                    bestScore = score; bestJ = j; bestReach = rr; bestP = pts[j]; bestPZ = zs[j];
                }

                // OFF-POLYLINE merge: a reward sitting off the spine axis falls on no band cell, but ONE point can
                // still cover the monolith AND that reward when they're within 2·effRadius of each other. For each
                // such uncaptured reward, try the point on the anchor's blast edge toward it (as close to the reward
                // as it can get while keeping the monolith in range); if it's walkable, reachable, and grabs more
                // total weight than the best polyline cell, place THERE. That turns "detour out to the reward + a
                // bridge back" (the #4/#5 near-duplicate) and "monolith + adjacent reward in two charges" (#7/#8)
                // into a single charge. No forward-progress bonus here — only a strict coverage win overrides.
                {
                    int nT = inp.TPos.Count;
                    float twoR = 2f * effRadius;
                    for (int u = 0; u < nT; u++)
                    {
                        if (captured[u] || inp.TPrimary[u]) continue;          // reward markers only, not other anchors
                        float d = Vector2.Distance(anchorPos, inp.TPos[u]);
                        if (d < 1e-3f || d > twoR) continue;                   // too far to share one blast
                        Vector2 dir = (inp.TPos[u] - anchorPos) / d;
                        Vector2 p = anchorPos + (dir * Math.Min(d, effRadius * 0.999f));      // ≤ effRadius from anchor
                        if (ExpDistSq(p, anchorPos) > r2 || ExpDistSq(p, inp.TPos[u]) > r2) continue;  // cover both
                        if (!ExpIsWalkable(data, bpr, doors, p)) continue;
                        double cov = ExpCoverGain(inp.TPos, inp.TW, captured, p, r2, out _);
                        if (cov <= bestScore) continue;                        // only a real coverage gain wins
                        float rr = ExpReach(data, bpr, doors, node, p, effDist);
                        if (rr < 0f) continue;
                        bestScore = cov; bestReach = rr; bestP = p; bestJ = anchorPosIdx;   // resume scan at the anchor
                        bestPZ = at >= 0 ? inp.TWorld[at].Z : zs[Math.Min(anchorPosIdx, M - 1)];
                    }
                }

                if (bestJ >= 0)
                {
                    double anchorW = at >= 0 ? inp.TW[at] : 0;
                    bool isSentinel = at >= 0 && at < inp.TSentinel.Count && inp.TSentinel[at];
                    weight += ExpCommitAt(inp, captured, route, bestP, bestPZ, r2, false, bestReach,
                        isSentinel ? $" · SENTINEL buff {anchorW:F0} ex @edge" : $" · anchor {anchorW:F0} ex @edge", isSentinel);
                    node = bestP; nodeIdx = bestJ;
                    int adv = 0;
                    while (ai < ordered.Count && (anchorTgt[ai] < 0 || captured[anchorTgt[ai]])) { ai++; adv++; }
                    if (adv > 1) ExpLog(inp, $"    (one blast covered {adv} anchors)");
                }
                else
                {
                    // Bridge: the FURTHEST reachable cell toward the anchor (scan from the anchor back to node).
                    int bridgeJ = -1; float br = 0f;
                    for (int j = anchorPosIdx; j > nodeIdx; j--)
                    {
                        float rr = ExpReach(data, bpr, doors, node, pts[j], effDist);
                        if (rr >= 0f) { bridgeJ = j; br = rr; break; }
                    }

                    if (bridgeJ < 0)
                    {
                        ExpLog(inp, $"  MISSED anchor {(at >= 0 ? inp.TW[at] : 0):F0} ex ({anchorPos.X:F0},{anchorPos.Y:F0}) — can't advance");
                        ai++;
                        continue;
                    }

                    // En-route marker pickup: a bridge is pure traversal, so within a SMALL band behind its
                    // furthest-reachable cell, prefer a cell whose blast catches more uncaptured targets — free
                    // pickup with negligible progress loss (the band is ≈ one radius, so the anchor is barely
                    // further next iteration). Strict ">" with the furthest cell pre-seeded keeps max progress on
                    // ties, so empty bridges stay maximally forward.
                    int pickBand = (int)Math.Ceiling(effRadius / ExpSpineStep);
                    int chosenJ = bridgeJ;
                    int chosenCnt = ExpCountUncovered(inp.TPos, captured, pts[bridgeJ], r2);
                    for (int j = bridgeJ - 1; j >= Math.Max(nodeIdx + 1, bridgeJ - pickBand); j--)
                    {
                        int cnt = ExpCountUncovered(inp.TPos, captured, pts[j], r2);
                        if (cnt > chosenCnt) { chosenCnt = cnt; chosenJ = j; }
                    }

                    float chosenReach = chosenJ == bridgeJ ? br : ExpReach(data, bpr, doors, node, pts[chosenJ], effDist);
                    weight += ExpCommitAt(inp, captured, route, pts[chosenJ], zs[chosenJ], r2, true, chosenReach,
                        chosenCnt > 0 ? " → toward anchor (+grabs en-route)" : " → toward anchor");
                    node = pts[chosenJ]; nodeIdx = chosenJ;
                }
            }

            return route;
        }

        private static int ExpCountUncovered(List<Vector2> tPos, bool[] captured, Vector2 pos, float r2)
        {
            int cnt = 0;
            for (int u = 0; u < tPos.Count; u++)
                if (!captured[u] && ExpDistSq(pos, tPos[u]) <= r2) cnt++;
            return cnt;
        }

        // ── Algorithm 3: the SPARE OPTIMISER (budget-aware secondary-target planner) ──────────────────────────
        // After the Placer has covered every anchor, the leftover charges are spent here. Unlike a one-hop grab,
        // this REASONS ABOUT THE BUDGET: for each dense secondary cluster (the marker heatmap's hot spots) it
        // estimates how many charges it costs to WALK there from the chain end — ceil(pathLen / effDist), counting
        // the bridges spent traversing — and only considers clusters it can actually afford with the spares left.
        // It then picks the best by value-per-charge (cluster ex ÷ charges-to-reach-and-harvest) and walks to it,
        // laying bridge charges (which grab any marker they pass), then a harvest charge on the cluster. Repeats
        // from the new end until no affordable cluster remains. So when the best secondary targets sit at the END
        // of the route (this map), it spends spares travelling to them instead of dropping one next door and
        // stalling; clusters too far for the remaining budget are skipped rather than half-chased. Returns the ex
        // collected; mutates `captured` + `route`. (Deviating the SPINE itself toward a mid-route cluster — vs.
        // walking from the end — is a further step; this walks from the end, which also reaches mid clusters when
        // they win on value-per-charge.)
        private static double ExpSpareOptimize(ExpRouteInputs inp, List<ExpRoutePoint> route, bool[] captured, float r2)
        {
            int spare0 = inp.Budget - route.Count;
            if (spare0 <= 0) { ExpLog(inp, "--- SPARE optimise (Algorithm 3): no spare charges ---"); return 0; }

            var data = inp.WalkData; int bpr = inp.Bpr; var doors = inp.Doors;
            var tPos = inp.TPos; var tW = inp.TW; var tWorld = inp.TWorld;
            float effDist = inp.EffDist, stepDist = inp.StepDist, effRadius = inp.EffRadius;
            int n = tPos.Count;
            int minCluster = Math.Max(1, inp.MinMarkers);
            double extra = 0;

            ExpLog(inp, $"--- SPARE optimise (Algorithm 3): {spare0} spare, gate ≥{minCluster}, " +
                        $"budget-aware, branch from nearest charge ---");

            int guard = 0;
            while (inp.Budget - route.Count > 0 && guard++ < spare0 + 8)
            {
                int remaining = inp.Budget - route.Count;

                // Keep the Sentinel buff EARLY in the chain: spare clusters may only splice in AT or AFTER the
                // sentinel charge, never before it (so the buff still detonates before the loot it empowers).
                // No sentinel charge (Grand, or none present) ⇒ minEdge 0 ⇒ unconstrained (old behaviour).
                int minEdge = 0;
                for (int i = 0; i < route.Count; i++) if (route[i].Sentinel) minEdge = i;

                // Pick the best affordable secondary cluster by value-per-charge (ex ÷ charges-to-reach-AND-rejoin),
                // splicing it onto the route EDGE that minimises total added charges (out-bridges + reconnect-bridges
                // back to the next spine charge — see ExpBestDetourEdge). Costing the reconnect means an outlier
                // cluster off the side of the route is charged for the walk back, so it loses to clusters that sit
                // ON the path; and it's attached where it flows (det→cluster→anchor), not as a dead-end spur off the
                // nearest node. Ties (equal-weight lone markers each one charge) break to the nearer cluster so the
                // spares sweep compactly instead of zig-zagging.
                Vector2 bestC = default; double bestScore = 0, bestGain = 0; int bestCnt = 0, bestHops = 0; float bestZ = 0, bestPath = float.MaxValue; int bestStart = -1;
                for (int c = 0; c < n; c++)
                {
                    if (captured[c]) continue;
                    Vector2 cand = tPos[c];

                    int cnt = 0; double gain = 0;
                    for (int u = 0; u < n; u++)
                        if (!captured[u] && ExpDistSq(cand, tPos[u]) <= r2) { cnt++; gain += tW[u]; }
                    if (cnt < minCluster || gain <= 0) continue;

                    int si = ExpBestDetourEdge(inp, route, cand, effDist, minEdge, out float outPath, out _, out int hops);
                    if (si < 0) continue;                                          // unreachable / can't rejoin from any edge
                    if (hops > remaining) continue;                               // can't afford the detour + reconnect

                    double score = gain / hops;                                   // value per charge spent
                    if (score > bestScore + 1e-9 || (score > bestScore - 1e-9 && outPath < bestPath))   // higher value, or equal value & nearer
                    { bestScore = score; bestC = cand; bestGain = gain; bestCnt = cnt; bestHops = hops; bestZ = tWorld[c].Z; bestPath = outPath; bestStart = si; }
                }

                if (bestScore <= 0 || bestStart < 0)
                {
                    ExpLog(inp, $"  no affordable cluster ≥{minCluster} within {remaining} charge(s) — STOP, {remaining} spare left unused");
                    break;
                }

                Vector2 node = route[bestStart].Grid;
                float nodeZ = route[bestStart].World.Z;
                bool terminal = bestStart >= route.Count - 1;
                ExpLog(inp, $"  → cluster ({bestC.X:F0},{bestC.Y:F0}) ×{bestCnt} {bestGain:F0} ex, ~{bestHops} charge(s) " +
                            $"from #{bestStart + 1}{(terminal ? " (terminal spur)" : $" → rejoin #{bestStart + 2}")} (path {bestPath:F0}, {bestScore:F0} ex/charge)");

                // Branch a sub-chain from charge #bestStart toward the cluster; the new charges are INSERTED right
                // after it (not appended), so the player drops them while passing that spot and continues the spine —
                // no backtracking from the far end. Bridge until within one hop, then harvest on the cluster. Snapshot
                // capture+extra first: if the detour can't complete OR reconnect within budget we roll it all back
                // rather than splice a broken chain the blast can't propagate through.
                bool[] capSnapshot = (bool[])captured.Clone();
                double extraBefore = extra;
                var branch = new List<ExpRoutePoint>();
                bool aborted = false, harvested = false;
                while (inp.Budget - (route.Count + branch.Count) > 0)
                {
                    float reach = ExpReach(data, bpr, doors, node, bestC, effDist);
                    if (reach >= 0f)
                    {
                        // Don't sit dead-on the cluster marker — slide to the reachable point that covers the MOST
                        // uncaptured weight, so a nearby high-value flag just outside the cluster's own radius (e.g.
                        // an adjacent GOLD flag 36 grid off) gets caught in the SAME blast. (#5 missing the gold next
                        // to it was exactly this: 110 ex on-marker vs 170 ex shifted to cover gold+logbook+white.)
                        Vector2 hp = ExpMaxCoverPoint(inp, captured, node, bestC, r2, effDist, out float hpReach);
                        extra += ExpCommitAt(inp, captured, branch, hp, bestZ, r2, false, hpReach, " · SPARE harvest");
                        node = hp; harvested = true;
                        break;
                    }

                    if (!ExpStepToward(data, bpr, doors, node, bestC, stepDist, out var step)) { aborted = true; break; }

                    // Marker-aware spare bridge: a traversal charge should grab a reward by radius rather than land on
                    // empty ground — but only when the detour is FREE, i.e. it doesn't add an extra charge to reach
                    // the cluster. Among rewards reachable from node this hop, take the highest-coverage one for which
                    // (this bridge + the hops still needed to the cluster) is no more than going straight would cost.
                    float nodeToCluster = ExpFullPath(data, bpr, doors, node, bestC);
                    int straightHops = Math.Max(1, (int)Math.Ceiling(nodeToCluster / effDist));
                    Vector2 place = step;
                    double placeCov = ExpCoverGain(tPos, tW, captured, step, r2, out _);
                    float r = ExpReach(data, bpr, doors, node, step, effDist);
                    for (int u = 0; u < n; u++)
                    {
                        if (captured[u]) continue;
                        // The marker CENTRE is often out of one hop, but a point within blast radius of it (the near
                        // EDGE, pulled toward node) can be reachable — so cover it by radius, don't require landing on
                        // it. Slide from the marker toward node; take the first reachable point that still covers it.
                        float du = Vector2.Distance(node, tPos[u]);
                        if (du - effRadius > effDist) continue;          // even the near edge is beyond one hop
                        float aMax = du > 1f ? Math.Min(1f, effRadius / du) : 1f;   // stay within radius of the marker
                        for (float a = 0f; a <= aMax + 1e-3f; a += 0.1f)
                        {
                            Vector2 cand = Vector2.Lerp(tPos[u], node, a);
                            if (!ExpIsWalkable(data, bpr, doors, cand)) continue;
                            float rr = ExpReach(data, bpr, doors, node, cand, effDist);
                            if (rr < 0f) continue;                       // not yet reachable — slide a bit nearer node
                            double cov = ExpCoverGain(tPos, tW, captured, cand, r2, out _);
                            if (cov > placeCov)
                            {
                                float pathUC = ExpFullPath(data, bpr, doors, cand, bestC);
                                if (pathUC >= 0f && 1 + (int)Math.Ceiling(pathUC / effDist) <= straightHops)
                                { place = cand; placeCov = cov; r = rr; }   // free detour that grabs more → take it
                            }
                            break;   // first reachable cover-point for this marker (closest = best coverage)
                        }
                    }

                    if (r < 0f) { aborted = true; break; }
                    bool grabbed = placeCov > 0;
                    float placeZ = nodeZ;
                    for (int u = 0; u < n; u++) if (!captured[u] && ExpDistSq(place, tPos[u]) <= 4f) { placeZ = tWorld[u].Z; break; }
                    extra += ExpCommitAt(inp, captured, branch, place, placeZ, r2, true, r,
                        grabbed ? " · SPARE bridge→cluster (+grabs)" : " · SPARE bridge→cluster");
                    node = place;
                }

                if (!harvested) aborted = true;   // ran out of budget before reaching the cluster — nothing usable

                // RECONNECT: explosives form ONE continuous linear rope from the detonator — each charge must be
                // within effDist of the PREVIOUS one in placement order (you can't start a side-branch off an older
                // charge). So a mid-route detour MUST bridge back to the next existing charge, or the rope is broken
                // and the blast dies at the harvest. Lay bridges from the cluster toward route[bestStart+1] until
                // it's within one hop. A terminal detour (spliced after the last charge) is an out-and-back tail and
                // needs no reconnect.
                if (!aborted && !terminal)
                {
                    Vector2 reconTarget = route[bestStart + 1].Grid;
                    while (ExpReach(data, bpr, doors, node, reconTarget, effDist) < 0f &&
                           inp.Budget - (route.Count + branch.Count) > 0)
                    {
                        if (!ExpStepToward(data, bpr, doors, node, reconTarget, stepDist, out var rstep)) { aborted = true; break; }
                        float rr = ExpReach(data, bpr, doors, node, rstep, effDist);
                        if (rr < 0f) { aborted = true; break; }
                        float rZ = nodeZ;
                        for (int u = 0; u < n; u++) if (!captured[u] && ExpDistSq(rstep, tPos[u]) <= 4f) { rZ = tWorld[u].Z; break; }
                        extra += ExpCommitAt(inp, captured, branch, rstep, rZ, r2, true, rr, " · SPARE reconnect");
                        node = rstep;
                    }
                    if (!aborted && ExpReach(data, bpr, doors, node, reconTarget, effDist) < 0f) aborted = true;   // budget ran out, still broken
                }

                if (aborted)
                {
                    Array.Copy(capSnapshot, captured, captured.Length);   // roll back — never splice a broken chain
                    extra = extraBefore;
                    ExpLog(inp, "  detour can't complete/reconnect within budget — skip, STOP");
                    break;
                }

                if (branch.Count > 0) route.InsertRange(bestStart + 1, branch);   // slot the detour in walking order
            }

            return extra;
        }

        // Pick where to SPLICE a spare detour to a secondary cluster so the chain stays connected. A detour
        // inserted after charge i must (a) reach the cluster from route[i] and (b) RECONNECT from the cluster to
        // route[i+1] (the next charge) — otherwise the cluster is a dead-end spur and the blast can't propagate
        // past it (the (1254,334) far-right outlier symptom: branch off the nearest node, then a 236-grid gap to
        // the next spine charge). So among the K charges nearest the cluster (straight-line, cheap), we choose the
        // insert-after index that MINIMISES total added charges = out-bridges + reconnect-bridges. The last charge
        // is a terminal spur (no reconnect). This naturally attaches an out-and-back cluster on the edge it sits
        // on — det→cluster→anchor — instead of hanging it off whichever node is merely closest.
        // Returns the insert-after index, the out/reconnect walkable path lengths, and the total hop estimate
        // (-1 / out=-1 if the cluster is unreachable-and-rejoinable from every candidate edge).
        private static int ExpBestDetourEdge(ExpRouteInputs inp, List<ExpRoutePoint> route, Vector2 cand,
                                             float effDist, int minEdge, out float outPath, out float reconnectPath, out int totalHops)
        {
            var data = inp.WalkData; int bpr = inp.Bpr; var doors = inp.Doors;
            outPath = -1f; reconnectPath = 0f; totalHops = 0;
            if (route.Count == 0) return -1;

            const int K = 5;
            int[] near = new int[K]; float[] nd = new float[K];
            for (int i = 0; i < K; i++) { near[i] = -1; nd[i] = float.MaxValue; }
            for (int i = minEdge; i < route.Count; i++)   // minEdge keeps the Sentinel buff early: never splice before it
            {
                float d = Vector2.Distance(route[i].Grid, cand);
                for (int k = 0; k < K; k++)
                    if (d < nd[k]) { for (int m = K - 1; m > k; m--) { nd[m] = nd[m - 1]; near[m] = near[m - 1]; } nd[k] = d; near[k] = i; break; }
            }

            int bestIdx = -1, bestTotal = int.MaxValue;
            float bestOut = -1f, bestRecon = 0f;
            for (int k = 0; k < K; k++)
            {
                int i = near[k];
                if (i < 0) continue;
                float op = ExpFullPath(data, bpr, doors, route[i].Grid, cand);
                if (op < 0f) continue;                                       // cluster unreachable from this charge
                int outHops = Math.Max(1, (int)Math.Ceiling(op / effDist));

                float rp = 0f; int reconHops = 0;
                bool terminal = i == route.Count - 1;
                if (!terminal)
                {
                    rp = ExpFullPath(data, bpr, doors, cand, route[i + 1].Grid);
                    if (rp < 0f) continue;                                   // can't rejoin the chain — reject this edge
                    // Reconnect lays bridges from the cluster back to route[i+1]; the harvest already sits at the
                    // cluster and route[i+1] already exists, so the closing hop is free — count only the bridges
                    // actually laid (ceil − 1), matching ExpSpareOptimize's reconnect loop.
                    reconHops = Math.Max(0, (int)Math.Ceiling(rp / effDist) - 1);
                }

                int total = outHops + reconHops;
                if (total < bestTotal) { bestTotal = total; bestIdx = i; bestOut = op; bestRecon = rp; }
            }

            if (bestIdx < 0) return -1;
            outPath = bestOut; reconnectPath = bestRecon; totalHops = Math.Max(1, bestTotal);
            return bestIdx;
        }

        // ── NEW monolith-first "spine" planner (beam branch, Brick 1: Phase A) ───────────────────────────────
        // Instead of the old value-greedy chain (which chased the single biggest prize across the map, wasted
        // charges on 0-ex bridges, skipped cheap-but-near monoliths, and force-spent leftover charges), this builds
        // the route how a human does: take ALL valuable anchors (monoliths + beneficial relics), order them into a
        // short tour FROM the detonator (the "spine"), lay charges along it (bridging gaps, grabbing rewards on the
        // way), and STOP — leftover charges are left unplaced (Phase B / Brick 2 will spend them on en-route
        // markers). The spine ORDER is what makes a near-start cheap monolith (e.g. the 143) get collected in its
        // geographic turn rather than first. Logs the spine + coverage so every decision is explicit (no guessing).
        private static ExpRouteResult ComputeRouteSpine(ExpRouteInputs inp)
        {
            var res = new ExpRouteResult { HaveStart = inp.HasDetonator, Targets = inp.TPos.Count };
            int n = inp.TPos.Count;
            if (inp.Budget <= 0 || !inp.HasDetonator || n == 0) { res.Log = inp.Log; return res; }

            float effDist = inp.EffDist, effRadius = inp.EffRadius;
            float r2 = effRadius * effRadius;
            Vector2 det = inp.DetonatorPos;
            var data = inp.WalkData; int bpr = inp.Bpr; var doors = inp.Doors;

            // Anchors = PRIMARY targets (monoliths / beneficial relics). Reward markers are NOT anchors — they're
            // en-route pickups handled in Phase B (Brick 2); here they're only collected for free when a spine
            // charge's blast happens to cover them.
            var anchors = new List<int>();
            for (int i = 0; i < n; i++) if (inp.TPrimary[i]) anchors.Add(i);

            ExpLog(inp, $"=== SPINE PLAN (new) === budget={inp.Budget} effDist={effDist:F0} effRadius={effRadius:F0} " +
                        $"anchors={anchors.Count} markers={n - anchors.Count} det=({det.X:F0},{det.Y:F0})");

            if (anchors.Count == 0)
            {
                ExpLog(inp, "  no valuable anchors (monolith/relic) — nothing to route");
                res.Log = inp.Log;
                return res;
            }

            var anchorPos = new List<Vector2>(anchors.Count);
            foreach (var i in anchors) anchorPos.Add(inp.TPos[i]);

            // Phase A2: order the anchors into the spine (NN + 2-opt over walkable distances, from the detonator).
            var ordered = ExpTourOrder(inp, anchorPos);

            // Pin the Kalguur Sentinel buff FIRST (a monolith-level anchor, forced to the head of the tour): detonating
            // it as early as possible maximises the mob-buffing drone's uptime ⇒ more empowered Logbook drops. The rest
            // keep their toured order behind it. See project-expedition-sentinel-buff.
            for (int i = anchors.Count - 1; i >= 0; i--)
            {
                if (!inp.TSentinel[anchors[i]]) continue;
                var sp = inp.TPos[anchors[i]];
                if (ordered.Count > 0 && ordered[0] == sp) continue;   // already first
                ordered.Remove(sp);
                ordered.Insert(0, sp);
                ExpLog(inp, $"  [sentinel] pinned buff anchor ({sp.X:F0},{sp.Y:F0}) FIRST — detonate ASAP for drone uptime");
            }

            // Algorithm 1 (Router): turn that order into ONE continuous walkable polyline (the line the player walks).
            // Stored on the result for the Placer (Algorithm 2) + the debug overlay. Decides route only, not charges.
            ExpBuildSpinePolyline(inp, ordered, out var spinePts, out var spineZs, out var spineAnchorIdx);
            res.SpinePts = spinePts;
            res.SpineZs = spineZs;
            res.SpineAnchorIdx = spineAnchorIdx;
            float spinePolyLen = 0f;
            for (int i = 1; i < spinePts.Count; i++) spinePolyLen += Vector2.Distance(spinePts[i - 1], spinePts[i]);
            ExpLog(inp, $"--- ROUTER polyline === {spinePts.Count} cells, {ordered.Count} segments, total≈{spinePolyLen:F0} grid ---");

            // Log the spine in order: per-hop walkable distance + a rough per-hop charge estimate (ceil(path/effDist)).
            ExpLog(inp, "--- SPINE order (det → anchors) ---");
            Vector2 prev = det;
            float spineLen = 0f;
            int spineChargesEst = 0;
            for (int k = 0; k < ordered.Count; k++)
            {
                var a = ordered[k];
                double wgt = 0;
                foreach (var i in anchors)
                    if (inp.TPos[i] == a) { wgt = inp.TW[i]; break; }
                float hop = ExpFullPath(data, bpr, doors, prev, a);
                if (hop < 0f) hop = Vector2.Distance(prev, a);
                int hopCharges = Math.Max(1, (int)Math.Ceiling(hop / effDist));
                spineLen += hop;
                spineChargesEst += hopCharges;
                ExpLog(inp, $"  #{k + 1} {wgt,7:F0} ex  ({a.X:F0},{a.Y:F0})  hop={hop:F0} (~{hopCharges} charge{(hopCharges == 1 ? string.Empty : "s")})  cumCharges≈{spineChargesEst}");
                prev = a;
            }

            ExpLog(inp, $"  spine total path≈{spineLen:F0}, est charges≈{spineChargesEst} / budget {inp.Budget}" +
                        (spineChargesEst > inp.Budget ? "  ⚠ over budget — far/cheap anchors should be dropped (pruning = next brick)" : string.Empty));

            // Phase A3: lay charges along the spine with the PLACER (Algorithm 2) — a forward sweep over the Router
            // polyline that edge-places coverage charges (anchor at the forward blast EDGE, not dead-centre) and
            // compacts the chain forward so the next charge starts further along and may grab the next anchor by radius.
            var captured = new bool[n];
            var route = ExpPlaceAlongSpine(inp, spinePts, spineZs, spineAnchorIdx, ordered, r2, captured, out double weight);

            int coveredAnchors = 0;
            foreach (var i in anchors) if (captured[i]) coveredAnchors++;
            ExpLog(inp, $"--- LAID (spine) === {route.Count} charges, anchors covered {coveredAnchors}/{anchors.Count} ---");
            foreach (var i in anchors)
                if (!captured[i])
                    ExpLog(inp, $"  MISSED anchor {inp.TW[i]:F0} ex ({inp.TPos[i].X:F0},{inp.TPos[i].Y:F0}) — out of budget or unreachable");

            // Phase B: spend leftover charges on en-route reward-marker CLUSTERS (≥ MinMarkers under one blast),
            // never lone markers, stopping the moment nothing's worth it. (The Placer already edge-places + compacts
            // as it lays, so no post-smoothing pass is needed — re-smoothing would only undo its placement.)
            weight += ExpSpareOptimize(inp, route, captured, r2);

            // Recount coverage by scanning the FINAL placements (smoothing/slack may have shifted what's covered).
            int covered = 0;
            {
                var capF = new bool[n];
                foreach (var rp in route)
                    for (int u = 0; u < n; u++)
                        if (!capF[u] && ExpDistSq(rp.Grid, inp.TPos[u]) <= r2) { capF[u] = true; covered++; }
            }

            res.Route = route;
            res.Weight = weight;
            res.Covered = covered;

            int spare = inp.Budget - route.Count;
            res.Phase = $"spine: anchors {coveredAnchors}/{anchors.Count}, {route.Count} charges, {spare} spare";
            ExpLog(inp, $"=== FINAL (spine) === {route.Count} charges, weight={weight:F0}, covered={covered}/{n}, " +
                        $"spare={spare} left unplaced");
            for (int i = 0; i < route.Count; i++)
                ExpLog(inp, $"  #{i + 1} ({route[i].Grid.X:F0},{route[i].Grid.Y:F0}) {route[i].Marginal:F0} ex · {route[i].Captured} tgt · {route[i].Dbg}");

            // Secondary-target census: every reward marker (non-anchor) with its position, weight and capture state,
            // plus its straight-line distance to the detonator and to the chain end — so a "missed useful target" can
            // be located in the log (which charge could have grabbed it, and whether reaching it was a free detour).
            if (inp.Log != null)
            {
                Vector2 end = route.Count > 0 ? route[route.Count - 1].Grid : det;
                int missed = 0;
                for (int u = 0; u < n; u++) if (!inp.TPrimary[u] && !captured[u]) missed++;
                ExpLog(inp, $"--- SECONDARY targets ({n - anchors.Count} markers, {missed} uncaptured) — pos · weight · state · dDet · dEnd ---");
                for (int u = 0; u < n; u++)
                {
                    if (inp.TPrimary[u]) continue;
                    float dDet = Vector2.Distance(det, inp.TPos[u]);
                    float dEnd = Vector2.Distance(end, inp.TPos[u]);
                    ExpLog(inp, $"  ({inp.TPos[u].X:F0},{inp.TPos[u].Y:F0}) {inp.TW[u]:F0} ex · {(captured[u] ? "GRABBED" : "missed ")} · dDet {dDet:F0} · dEnd {dEnd:F0}");
                }
            }

            res.Log = inp.Log;
            return res;
        }

        // Order the coverage stops into a short open tour from the detonator: nearest-neighbour seed + 2-opt on
        // a precomputed walkable-distance matrix (unreachable pairs penalised). This is what turns the greedy's
        // value-ordered zig-zag into a spatial loop, so far fewer bridge charges are needed to connect clusters.
        private static List<Vector2> ExpTourOrder(ExpRouteInputs inp, List<Vector2> stops)
        {
            var data = inp.WalkData; int bpr = inp.Bpr; var doors = inp.Doors;
            int m = stops.Count;
            if (m <= 2) return new List<Vector2>(stops);

            float Dist(Vector2 a, Vector2 b)
            {
                float d = ExpFullPath(data, bpr, doors, a, b);
                return d < 0f ? Vector2.Distance(a, b) * 4f : d;   // no path → discourage but keep finite
            }

            // All-pairs walkable distances — m² independent ExpFullPath calls, each a potentially cross-map (so
            // EXPENSIVE, uncapped) A*. This is the dominant serial cost on blocker-free maps (the tour relay runs
            // since no gate ever opens), and the calls are heavy enough that thread-pool overhead is negligible, so
            // fill the matrix in PARALLEL. Race-free: outer row i writes only ddet[i] and dmat[i,j]/dmat[j,i] for
            // j>i, and each off-diagonal cell is owned by exactly one row.
            var dmat = new float[m, m];
            var ddet = new float[m];
            Parallel.For(0, m, i =>
            {
                ddet[i] = Dist(inp.DetonatorPos, stops[i]);
                for (int j = i + 1; j < m; j++) { float d = Dist(stops[i], stops[j]); dmat[i, j] = d; dmat[j, i] = d; }
            });

            var used = new bool[m];
            var order = new List<int>(m);
            int curIdx = -1;
            for (int s = 0; s < m; s++)
            {
                int best = -1; float bd = float.MaxValue;
                for (int i = 0; i < m; i++)
                {
                    if (used[i]) continue;
                    float d = curIdx < 0 ? ddet[i] : dmat[curIdx, i];
                    if (d < bd) { bd = d; best = i; }
                }

                if (best < 0) break;
                used[best] = true; order.Add(best); curIdx = best;
            }

            bool improved = true; int guard = 0;
            while (improved && guard++ < 60)
            {
                improved = false;
                for (int i = 0; i < order.Count - 1; i++)
                {
                    for (int k = i + 1; k < order.Count; k++)
                    {
                        float ab = i == 0 ? ddet[order[i]] : dmat[order[i - 1], order[i]];
                        float ac = i == 0 ? ddet[order[k]] : dmat[order[i - 1], order[k]];
                        bool hasD = k + 1 < order.Count;
                        float cd = hasD ? dmat[order[k], order[k + 1]] : 0f;
                        float bd = hasD ? dmat[order[i], order[k + 1]] : 0f;
                        if (ac + bd + 1e-3f < ab + cd)
                        {
                            order.Reverse(i, k - i + 1);
                            improved = true;
                        }
                    }
                }
            }

            var result = new List<Vector2>(m);
            foreach (int idx in order) result.Add(stops[idx]);
            return result;
        }

        // FULL route on the in-game LARGE map (Tab): white dot = anchor, gold line = order, blue numbered
        // dots = placement points. Uses the same Radar large-map projection as the monolith map labels
        // (replicated in DrawMonolithMapLabels). Hidden unless the large map is open.
        private void DrawExpeditionRouteLargeMap()
        {
            if (this.expRoute.Count == 0) return;

            var gameUi = Core.States.InGameStateObject.GameUi;
            var largeMap = gameUi.LargeMap;
            if (largeMap == null || !largeMap.IsVisible || gameUi.WorldMapPanel.IsVisible) return;

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (area?.Player == null || !area.Player.TryGetComponent<Render>(out var playerRender)) return;
            var trackingPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);
            float trackingHeight = playerRender.TerrainHeight;

            var baseRes = UiElementBaseFuncs.BaseResolution;
            double baseDiag = Math.Sqrt(((double)baseRes.X * baseRes.X) + ((double)baseRes.Y * baseRes.Y));
            double diag = baseDiag * largeMap.Size.Y / baseRes.Y;
            if (diag <= 0) return;
            float scale = this.Settings.MapValueScaleMultiplier * largeMap.Zoom * 0.187812f;
            if (scale <= 0) return;
            float mapScale = 240f / scale;
            float cos = (float)(diag * Math.Cos(MapCameraAngle) / mapScale);
            float sin = (float)(diag * Math.Sin(MapCameraAngle) / mapScale);
            var center = largeMap.Center + largeMap.Shift + largeMap.DefaultShift;
            center.X += 0.6f + this.Settings.MapValueXOffset;
            center.Y += 0.3f + this.Settings.MapValueYOffset;

            Vector2 Project(Vector2 grid, float worldZ)
            {
                var d = grid - trackingPos;
                float dz = (worldZ - trackingHeight) / 10.86957f;
                return center + new Vector2((d.X - d.Y) * cos, (dz - (d.X + d.Y)) * sin);
            }

            var dl = ImGui.GetForegroundDrawList();
            int nextIndex = this.ExpNextIndex();
            var prev = Project(this.expDetonatorPos, this.expDetonatorWorld.Z);
            dl.AddCircleFilled(prev, 4f, 0xFFFFFFFFu);

            // Router spine polyline (Algorithm 1): the walkable line the player follows, drawn UNDER the charges so
            // route and placement read as two separate things. Thin cyan; only when the debug toggle is on.
            if (this.Settings.ShowExpeditionSpine && this.expSpinePts.Count >= 2)
            {
                var sp = Project(this.expSpinePts[0], this.expSpineZs.Count > 0 ? this.expSpineZs[0] : this.expDetonatorWorld.Z);
                for (int i = 1; i < this.expSpinePts.Count; i++)
                {
                    float z = i < this.expSpineZs.Count ? this.expSpineZs[i] : this.expDetonatorWorld.Z;
                    var cur = Project(this.expSpinePts[i], z);
                    dl.AddLine(sp, cur, 0xCCFFCC00u, 2f);   // cyan (ABGR)
                    sp = cur;
                }
            }

            // Faint blast circle per planned charge (around the placement point), so overlapping coverage of the
            // same target by two charges is directly visible on the map.
            float effRadiusMap = this.ExpBaseBlastRadius() * (1f + (this.Settings.ExpBlastRadiusPct / 100f));
            for (int i = 0; i < this.expRoute.Count; i++)
            {
                var c = this.expRoute[i].Grid;
                float cz = this.expRoute[i].World.Z;
                Vector2 ringPrev = default;
                for (int k = 0; k <= 24; k++)
                {
                    double a = 2.0 * Math.PI * k / 24;
                    var rp = Project(new Vector2(c.X + (effRadiusMap * (float)Math.Cos(a)),
                                                 c.Y + (effRadiusMap * (float)Math.Sin(a))), cz);
                    if (k > 0) dl.AddLine(ringPrev, rp, 0x3300D7FFu, 1f);   // faint yellow
                    ringPrev = rp;
                }
            }

            for (int i = 0; i < this.expRoute.Count; i++)
            {
                var p = this.expRoute[i];
                var sc = Project(p.Grid, p.World.Z);
                bool placed = i < nextIndex;
                bool current = i == nextIndex;
                uint lineCol = placed ? 0x55888888u : 0xFF00D7FFu;
                uint dotCol = placed ? 0xFF888888u : current ? 0xFF00FF00u : 0xFF3030FFu;
                dl.AddLine(prev, sc, lineCol, 2f);
                dl.AddCircleFilled(sc, current ? 8f : 6f, dotCol);
                string num = (i + 1).ToString();
                var ts = ImGui.CalcTextSize(num);
                dl.AddText(sc - (ts * 0.5f) + new Vector2(1f, 1f), 0xFF000000u, num);
                dl.AddText(sc - (ts * 0.5f), 0xFFFFFFFFu, num);
                prev = sc;
            }
        }

        // Paint each path-blocker's footprint on the large map (Tab) so you can confirm visually which passages
        // are walled off until a blast destroys the blocker. Red translucent cells = the hole a blocked blocker
        // punches in the walkable grid; a small red ring marks the blocker centre, green if already open. Shares
        // DrawExpeditionRouteLargeMap's projection. Visualization only — the route planner ignores these for now.
        private void DrawExpeditionGatesLargeMap()
        {
            if (this.expGates.Count == 0) return;

            var gameUi = Core.States.InGameStateObject.GameUi;
            var largeMap = gameUi.LargeMap;
            if (largeMap == null || !largeMap.IsVisible || gameUi.WorldMapPanel.IsVisible) return;

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (area?.Player == null || !area.Player.TryGetComponent<Render>(out var playerRender)) return;
            var trackingPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);
            float trackingHeight = playerRender.TerrainHeight;

            var baseRes = UiElementBaseFuncs.BaseResolution;
            double baseDiag = Math.Sqrt(((double)baseRes.X * baseRes.X) + ((double)baseRes.Y * baseRes.Y));
            double diag = baseDiag * largeMap.Size.Y / baseRes.Y;
            if (diag <= 0) return;
            float scale = this.Settings.MapValueScaleMultiplier * largeMap.Zoom * 0.187812f;
            if (scale <= 0) return;
            float mapScale = 240f / scale;
            float cos = (float)(diag * Math.Cos(MapCameraAngle) / mapScale);
            float sin = (float)(diag * Math.Sin(MapCameraAngle) / mapScale);
            var center = largeMap.Center + largeMap.Shift + largeMap.DefaultShift;
            center.X += 0.6f + this.Settings.MapValueXOffset;
            center.Y += 0.3f + this.Settings.MapValueYOffset;

            // Per-cell terrain height (same source the DebugHelper walkable paint uses) — a blocker is a terrain
            // object whose entity WorldPosition.Z is unreliable/flat, so projecting the whole footprint at one
            // entity-Z shifted it off the actual ground. Height per cell makes the footprint sit on the terrain.
            var heights = area.GridHeightData;
            float HeightAt(int x, int y)
            {
                if (heights != null && y >= 0 && y < heights.Length && heights[y] != null &&
                    x >= 0 && x < heights[y].Length)
                    return heights[y][x];
                return trackingHeight;
            }

            Vector2 Project(int gx, int gy)
            {
                float dz = (HeightAt(gx, gy) - trackingHeight) / 10.86957f;
                float dx = gx - trackingPos.X, dy = gy - trackingPos.Y;
                return center + new Vector2((dx - dy) * cos, (dz - (dx + dy)) * sin);
            }

            var dl = ImGui.GetForegroundDrawList();
            float hs = Math.Max(1f, 0.5f * (float)Math.Sqrt((cos * cos) + (sin * sin)));  // cell half-size in px

            foreach (var g in this.expGates)
            {
                if (g.Blocked && g.Footprint != null)
                {
                    foreach (var (gx, gy) in g.Footprint)
                    {
                        var s = Project(gx, gy);
                        dl.AddRectFilled(s - new Vector2(hs, hs), s + new Vector2(hs, hs), 0x553030FFu);  // red ~33% (ABGR)
                    }
                }

                var cs = Project((int)g.Grid.X, (int)g.Grid.Y);
                uint ring = g.Blocked ? 0xFF3030FFu : 0xFF30FF30u;  // red blocked / green open
                dl.AddCircle(cs, 5f, 0xFF000000u, 16, 3f);
                dl.AddCircle(cs, 5f, ring, 16, 2f);
            }
        }

        // Live weighted-target inventory — the inputs the route planner reasons about, built fresh from the
        // persistent target cache with the SAME weighting as BuildRouteInputs: monolith = best reward price (ex)
        // when ≥ the min-ex filter (PRIMARY); reward marker = active-profile icon weight (secondary, coverage-only);
        // beneficial relic = net buff weight (PRIMARY). Shared by the heatmap, the inventory dump, and (going
        // forward) the monolith-first planner, so all three see one consistent valuation.
        private List<(Vector2 Pos, float Z, double Weight, bool Primary, ExpKind Kind, string Info)> ExpCollectWeightedTargets()
        {
            var list = new List<(Vector2, float, double, bool, ExpKind, string)>();
            var s = this.Settings;
            float markerBaseline = this.ExpMarkerBaselineZ();
            foreach (var t in this.expTargetCache.Values)
            {
                double w = 0;
                bool primary = false;
                if (t.Kind == ExpKind.Monolith)
                {
                    if (t.Value > 0 && t.Value >= s.ExpMonolithMinEx) { w = t.Value; primary = true; }
                }
                else if (t.Kind == ExpKind.Marker)
                {
                    w = this.ExpMarkerTierWeight(ExpMarkerPoleOffset(t), markerBaseline, out _);   // value by height tier (tiny → 0)
                }
                else if (t.Kind == ExpKind.Remnant)
                {
                    double net = this.ExpRelicNetWeight(t.Info);
                    if (net > 0) { w = net; primary = true; }
                }

                if (w <= 0) continue;
                list.Add((t.Pos, t.World.Z, w, primary, t.Kind, t.Info));
            }

            return list;
        }

        // Map a normalized weight 0..1 to a translucent heat colour. ABGR packed for ImGui (0xAABBGGRR); hotter
        // cells are more opaque. WARM (false) = monolith/all layer: green → yellow → red. COOL (true) = non-monolith
        // marker layer: blue → magenta, so the two layers stay visually distinct when overlaid.
        private static uint ExpHeatColor(double t, bool cool)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            byte a = (byte)(40 + (150 * t));
            if (cool)
            {
                byte r = (byte)(255 * t);   // blue(0) → magenta(1)
                return ((uint)a << 24) | (255u << 16) | r;   // B = 255, G = 0
            }

            byte rr, gg;
            if (t < 0.5) { rr = (byte)(255 * (t / 0.5)); gg = 255; }
            else { rr = 255; gg = (byte)(255 * (1.0 - ((t - 0.5) / 0.5))); }
            return ((uint)a << 24) | ((uint)gg << 8) | rr;   // B = 0
        }

        // Rebuild ONE heatmap layer ONLY when its target set / tuning changed (cheap O(cells×targets) Gaussian
        // splat; stored normalized 0..1 in the layer). Keyed by a signature so the per-frame draw just projects the
        // cached grid. Each layer normalizes among ITS OWN targets, so the marker layer isn't drowned by monoliths.
        private bool ExpRebuildHeatLayer(ExpHeatLayer layer, List<(Vector2 Pos, float Z, double Weight, bool Primary, ExpKind Kind, string Info)> targets, float sigma)
        {
            double sig = targets.Count + sigma;
            foreach (var t in targets) sig += t.Weight + t.Pos.X + t.Pos.Y;
            string sigStr = sig.ToString("F1");
            if (sigStr == layer.Sig && layer.Grid != null) return layer.Nx > 0;
            layer.Sig = sigStr;

            if (targets.Count == 0) { layer.Grid = null; layer.Nx = 0; return false; }

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var t in targets)
            {
                if (t.Pos.X < minX) minX = t.Pos.X;
                if (t.Pos.X > maxX) maxX = t.Pos.X;
                if (t.Pos.Y < minY) minY = t.Pos.Y;
                if (t.Pos.Y > maxY) maxY = t.Pos.Y;
            }

            float margin = sigma * 1.5f;
            minX -= margin; minY -= margin; maxX += margin; maxY += margin;

            const int MaxDim = 64;
            float spanX = Math.Max(1f, maxX - minX), spanY = Math.Max(1f, maxY - minY);
            float step = Math.Max(12f, Math.Max(spanX, spanY) / MaxDim);
            int nx = Math.Min(MaxDim, (int)(spanX / step) + 1);
            int ny = Math.Min(MaxDim, (int)(spanY / step) + 1);
            if (nx <= 0 || ny <= 0) { layer.Grid = null; layer.Nx = 0; return false; }

            var grid = new double[nx * ny];
            double inv2s2 = 1.0 / (2.0 * sigma * sigma);
            double maxVal = 0;
            for (int iy = 0; iy < ny; iy++)
            {
                float cy = minY + ((iy + 0.5f) * step);
                for (int ix = 0; ix < nx; ix++)
                {
                    float cx = minX + ((ix + 0.5f) * step);
                    double acc = 0;
                    foreach (var t in targets)
                    {
                        float ddx = cx - t.Pos.X, ddy = cy - t.Pos.Y;
                        acc += t.Weight * Math.Exp(-(((double)ddx * ddx) + ((double)ddy * ddy)) * inv2s2);
                    }

                    grid[(iy * nx) + ix] = acc;
                    if (acc > maxVal) maxVal = acc;
                }
            }

            if (maxVal <= 0) { layer.Grid = null; layer.Nx = 0; return false; }
            for (int i = 0; i < grid.Length; i++) grid[i] /= maxVal;   // normalize 0..1

            layer.Grid = grid;
            layer.Nx = nx;
            layer.Ny = ny;
            layer.MinX = minX;
            layer.MinY = minY;
            layer.Step = step;
            return true;
        }

        // Project + paint one cached heat layer onto the iso ground (per-cell terrain height). `cool` picks the
        // marker palette so an overlaid marker layer reads distinctly from the monolith/all layer.
        private static void ExpDrawHeatLayer(ExpHeatLayer layer, Func<float, float, Vector2> project, bool cool)
        {
            if (layer.Grid == null || layer.Nx <= 0) return;
            var dl = ImGui.GetForegroundDrawList();
            int nx = layer.Nx, ny = layer.Ny;
            float step = layer.Step, half = 0.5f * step;
            for (int iy = 0; iy < ny; iy++)
            {
                float cy = layer.MinY + ((iy + 0.5f) * step);
                for (int ix = 0; ix < nx; ix++)
                {
                    double norm = layer.Grid[(iy * nx) + ix];
                    if (norm < 0.06) continue;   // skip near-cold cells (cleaner + cheaper)
                    float cx = layer.MinX + ((ix + 0.5f) * step);
                    var p0 = project(cx - half, cy - half);
                    var p1 = project(cx + half, cy - half);
                    var p2 = project(cx + half, cy + half);
                    var p3 = project(cx - half, cy + half);
                    dl.AddQuadFilled(p0, p1, p2, p3, ExpHeatColor(norm, cool));
                }
            }
        }

        // Paint the weight heatmap(s) on the large map (Tab). Two independent layers (toggles): the ALL layer
        // (green→red, monolith-dominated) and the NON-MONOLITH marker layer (blue→magenta, normalized among
        // markers/relics so small weights show). Projection is computed once and shared. Visualization only.
        private void DrawExpeditionHeatmapLargeMap()
        {
            var all = this.ExpCollectWeightedTargets();
            this.DumpExpeditionInventory(all);

            bool showAll = this.Settings.ShowExpeditionHeatmap;
            bool showMarkers = this.Settings.ShowExpeditionHeatmapMarkers;
            if (!showAll && !showMarkers) return;

            float sigma = Math.Max(8f, this.Settings.ExpHeatmapRadius);
            bool haveAll = showAll && this.ExpRebuildHeatLayer(this.expHeatAll, all, sigma);
            bool haveMarkers = false;
            if (showMarkers)
            {
                var nonMono = all.FindAll(t => t.Kind != ExpKind.Monolith);
                haveMarkers = this.ExpRebuildHeatLayer(this.expHeatMarkers, nonMono, sigma);
            }

            if (!haveAll && !haveMarkers) return;

            var gameUi = Core.States.InGameStateObject.GameUi;
            var largeMap = gameUi.LargeMap;
            if (largeMap == null || !largeMap.IsVisible || gameUi.WorldMapPanel.IsVisible) return;

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (area?.Player == null || !area.Player.TryGetComponent<Render>(out var playerRender)) return;
            var trackingPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);
            float trackingHeight = playerRender.TerrainHeight;

            var baseRes = UiElementBaseFuncs.BaseResolution;
            double baseDiag = Math.Sqrt(((double)baseRes.X * baseRes.X) + ((double)baseRes.Y * baseRes.Y));
            double diag = baseDiag * largeMap.Size.Y / baseRes.Y;
            if (diag <= 0) return;
            float scale = this.Settings.MapValueScaleMultiplier * largeMap.Zoom * 0.187812f;
            if (scale <= 0) return;
            float mapScale = 240f / scale;
            float cos = (float)(diag * Math.Cos(MapCameraAngle) / mapScale);
            float sin = (float)(diag * Math.Sin(MapCameraAngle) / mapScale);
            var center = largeMap.Center + largeMap.Shift + largeMap.DefaultShift;
            center.X += 0.6f + this.Settings.MapValueXOffset;
            center.Y += 0.3f + this.Settings.MapValueYOffset;

            var heights = area.GridHeightData;
            float HeightAt(int x, int y)
            {
                if (heights != null && y >= 0 && y < heights.Length && heights[y] != null &&
                    x >= 0 && x < heights[y].Length)
                    return heights[y][x];
                return trackingHeight;
            }

            Vector2 Project(float gx, float gy)
            {
                float dz = (HeightAt((int)gx, (int)gy) - trackingHeight) / 10.86957f;
                float dx = gx - trackingPos.X, dy = gy - trackingPos.Y;
                return center + new Vector2((dx - dy) * cos, (dz - (dx + dy)) * sin);
            }

            if (haveAll) ExpDrawHeatLayer(this.expHeatAll, Project, false);
            if (haveMarkers) ExpDrawHeatLayer(this.expHeatMarkers, Project, true);
        }

        // Dump the weighted-target inventory (the future monolith-first planner's inputs) to expedition_inventory.txt
        // — monoliths sorted by ex desc, reward markers grouped by icon (count + total weight), relics by net buff —
        // each with grid pos + straight distance to the detonator. Rewritten only when the set changes (signature).
        private void DumpExpeditionInventory(List<(Vector2 Pos, float Z, double Weight, bool Primary, ExpKind Kind, string Info)> targets)
        {
            var det = this.expDetonatorPos;
            var monos = new List<(Vector2 Pos, double W)>();
            var relics = new List<(Vector2 Pos, double W, string Info)>();
            var markerByIcon = new Dictionary<string, (int Count, double Total)>(StringComparer.Ordinal);
            foreach (var t in targets)
            {
                if (t.Kind == ExpKind.Monolith) monos.Add((t.Pos, t.Weight));
                else if (t.Kind == ExpKind.Remnant) relics.Add((t.Pos, t.Weight, t.Info));
                else if (t.Kind == ExpKind.Marker)
                {
                    markerByIcon.TryGetValue(t.Info, out var cur);
                    markerByIcon[t.Info] = (cur.Count + 1, cur.Total + t.Weight);
                }
            }

            monos.Sort((a, b) => b.W.CompareTo(a.W));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== EXPEDITION INVENTORY === detonator=({det.X:F0},{det.Y:F0}) weightedTargets={targets.Count}");
            sb.AppendLine($"-- monoliths ({monos.Count}) sorted by ex --");
            foreach (var m in monos)
                sb.AppendLine($"  {m.W,7:F0} ex  ({m.Pos.X:F0},{m.Pos.Y:F0})  straight={Vector2.Distance(det, m.Pos):F0}");
            sb.AppendLine($"-- relics ({relics.Count}) net buff weight --");
            foreach (var r in relics)
                sb.AppendLine($"  {r.W,7:F0}     ({r.Pos.X:F0},{r.Pos.Y:F0})  straight={Vector2.Distance(det, r.Pos):F0}");
            sb.AppendLine($"-- reward markers grouped by icon ({markerByIcon.Count} types) --");
            foreach (var kv in markerByIcon)
                sb.AppendLine($"  {kv.Value.Total,7:F0} w  ×{kv.Value.Count,-3} {ExpShortIcon(kv.Key)}");

            string s = sb.ToString();
            if (s == this.expInventorySig) return;
            this.expInventorySig = s;
            try { File.WriteAllText(Path.Join(this.DllDirectory, "expedition_inventory.txt"), s); } catch { }
        }

        // Trim the long "Art/2DArt/Minimap/.../RewardChestCurrency" icon path/name to its leaf for the dump.
        private static string ExpShortIcon(string icon)
        {
            if (string.IsNullOrEmpty(icon)) return "(none)";
            int i = icon.LastIndexOfAny(new[] { '/', '\\' });
            return i >= 0 && i + 1 < icon.Length ? icon.Substring(i + 1) : icon;
        }

        // In the 3D world, mark ONLY the NEXT charge placement (route[0]) with a small ring — "place here
        // now". Cheap (one projection), and avoids cluttering the world with the whole chain / blast areas.
        private void DrawExpeditionNextPointWorld()
        {
            if (this.expRoute.Count == 0) return;
            var world = Core.States.InGameStateObject.CurrentWorldInstance;
            if (world == null) return;

            int nextIndex = this.ExpNextIndex();
            if (nextIndex < 0 || nextIndex >= this.expRoute.Count) return;
            var rp = this.expRoute[nextIndex];
            var dl = ImGui.GetBackgroundDrawList();

            // Yellow ground circle = blast radius around the TARGET this charge collects, so the red
            // recommendation point is visibly the tangent on that circle. Projected as a ring of points on
            // the ground plane (iso projection ⇒ it reads as an ellipse, matching the game's own circles).
            //
            // Z DEPTH: each ring point takes the TERRAIN height (GridHeightData) at its own grid cell, not the
            // single stored target-Z. rp.TargetWorldZ comes off the entity's WorldPosition.Z, which points at the
            // HEALTHBAR (top of the entity, see Render.WorldPosition) — so a monolith on a raised totem projected
            // the whole ring high in the air, and because WorldToScreen is a true camera projection, the elevated
            // (camera-nearer) ring rendered LARGER than the on-ground blast actually is. Sampling ground height per
            // point drops the ring onto the terrain and lets it follow slopes, so its size reads correctly.
            float effRadius = this.ExpBaseBlastRadius() * (1f + (this.Settings.ExpBlastRadiusPct / 100f));
            // VISUAL-ONLY shrink: the ground-plane camera projection reads a touch larger than the game's in-game
            // coverage circle, so draw the ring at 0.95× the true blast radius to match by eye. Routing/coverage keep
            // the true effRadius (a charge still grabs exactly what the planner counted) — this only affects the ring.
            float drawRadius = effRadius * 0.95f;
            var heights = Core.States.InGameStateObject.CurrentAreaInstance?.GridHeightData;
            float GroundZ(float gx, float gy)
            {
                int xi = (int)(gx + 0.5f), yi = (int)(gy + 0.5f);
                if (heights != null && yi >= 0 && yi < heights.Length && heights[yi] != null &&
                    xi >= 0 && xi < heights[yi].Length)
                    return heights[yi][xi];
                return rp.TargetWorldZ;   // no terrain grid this frame → fall back to the stored Z
            }

            const int Seg = 36;
            Vector2 prevPt = default;
            for (int i = 0; i <= Seg; i++)
            {
                double a = 2.0 * Math.PI * i / Seg;
                float gx = rp.TargetGrid.X + (drawRadius * (float)Math.Cos(a));
                float gy = rp.TargetGrid.Y + (drawRadius * (float)Math.Sin(a));
                var ring = world.WorldToScreen(new StdTuple3D<float>
                {
                    X = gx * ExpWorldPerGrid,
                    Y = gy * ExpWorldPerGrid,
                    Z = GroundZ(gx, gy),
                });
                if (i == 0) { prevPt = ring; continue; }
                dl.AddLine(prevPt, ring, 0xFF00D7FFu, 2f);   // yellow (ABGR)
                prevPt = ring;
            }

            // Place the recommendation marker on the ground too (same terrain Z), so the red dot sits ON the ring
            // rather than floating up at the healthbar height.
            var sc = world.WorldToScreen(ExpGridToWorld(rp.Grid, GroundZ(rp.Grid.X, rp.Grid.Y)));
            dl.AddCircle(sc, 16f, 0xFF000000u, 24, 5f);   // dark outline for contrast
            dl.AddCircle(sc, 16f, 0xFF3030FFu, 24, 3f);   // red placement ring
            dl.AddCircleFilled(sc, 4f, 0xFFFFFFFFu);      // centre dot
        }

        // Settings UI: reward/target weighting profiles (the marker ex-value table). Moved here from the planner
        // window (which now just picks the active profile). Profile picker + the reward table writing to it.
        private void DrawExpeditionTargetProfileSettings()
        {
            var s = this.Settings;
            ImGui.TextDisabled(this.L("prof.reward_hint",
                "Weight each reward marker (0 = ignore, blank types default to 1). Monoliths are\n" +
                "valued by their real reward price — this profile only weights the markers."));

            this.ExpActiveTargetProfile();   // ensure migration / non-empty before the picker
            var profile = this.ExpProfilePicker("target", s.ExpTargetProfiles, ref s.ExpActiveTargetProfile, "Default");
            this.DrawRewardWeightTable(profile);
        }

        // The reward marker ex-weight table, editing the given profile. Rows = markers discovered on this map ∪ the
        // profile's own entries ∪ the notable code defaults, so the baseline is always visible and any type stays
        // configurable even off-map. "×N" shows how many of that marker are on the current map.
        private void DrawRewardWeightTable(WeightProfile profile)
        {
            var markerN = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in this.expTargetCache.Values)
                if (t.Kind == ExpKind.Marker && !string.IsNullOrEmpty(t.Info))
                {
                    markerN.TryGetValue(t.Info, out var c);
                    markerN[t.Info] = c + 1;
                }

            var rewardRows = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var k in markerN.Keys) rewardRows.Add(k);
            foreach (var k in profile.Weights.Keys) rewardRows.Add(k);
            foreach (var k in ExpDefaultRewardWeights.Keys) rewardRows.Add(k);

            float iconSz = ImGui.GetTextLineHeight() + 4f;
            if (rewardRows.Count == 0)
            {
                ImGui.TextDisabled(this.L("prof.no_markers", "(no reward markers discovered yet)"));
            }
            else if (ImGui.BeginTable("exprewards", 3,
                         ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
                         new Vector2(0f, Math.Min(rewardRows.Count + 1, 9) * ImGui.GetFrameHeightWithSpacing())))
            {
                ImGui.TableSetupColumn("Reward", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 120f);
                ImGui.TableSetupColumn("##rm", ImGuiTableColumnFlags.WidthFixed, 22f);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                string? removeKey = null;
                foreach (var icon in rewardRows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    var tex = this.GetRewardIconTexture(icon);
                    if (tex != IntPtr.Zero)
                    {
                        ImGui.Image(tex, new Vector2(iconSz, iconSz));
                        ImGui.SameLine();
                    }

                    ImGui.AlignTextToFramePadding();
                    ImGui.Text(ExpRewardLabel(icon));
                    int n0 = markerN.TryGetValue(icon, out var cc) ? cc : 0;
                    if (n0 > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.45f, 1f), $"×{n0}");
                    }

                    ImGui.TableSetColumnIndex(1);
                    float w0 = this.ExpEffectiveRewardWeight(icon);
                    ImGui.SetNextItemWidth(112f);
                    if (ImGui.InputFloat($"##w_{icon}", ref w0, 1f, 10f, "%.0f"))
                    {
                        if (w0 < 0f) w0 = 0f;
                        profile.Weights[icon] = w0;
                    }

                    // Remove button — only for rows the profile actually overrides (user-added or user-edited).
                    // Code-default and freshly-discovered rows can't be dropped from the list, only re-weighted.
                    ImGui.TableSetColumnIndex(2);
                    if (profile.Weights.ContainsKey(icon))
                    {
                        if (ImGui.SmallButton($"×##rm_{icon}")) removeKey = icon;
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove from table");
                    }
                }

                ImGui.EndTable();
                if (removeKey != null) profile.Weights.Remove(removeKey);
            }

            if (ImGui.BeginCombo("Add reward type", "+ add…", ImGuiComboFlags.HeightLarge))
            {
                foreach (var kv in ExpRewardLabels)
                {
                    if (rewardRows.Contains(kv.Key)) continue;
                    var tex = this.GetRewardIconTexture(kv.Key);
                    if (tex != IntPtr.Zero) { ImGui.Image(tex, new Vector2(iconSz, iconSz)); ImGui.SameLine(); }
                    if (ImGui.Selectable(kv.Value)) profile.Weights[kv.Key] = this.ExpEffectiveRewardWeight(kv.Key);
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(this.Loc.Label("prof.reset_values", "reset values", "rh_reset_vals"))) profile.Weights.Clear();
            ImGui.SameLine();
            if (ImGui.SmallButton(this.Loc.Label("prof.reload_icons", "reload icons", "rh_reload_icons"))) this.expRewardIconCache.Clear();
        }

        // Combo-only profile selector for the planner window (management — new/rename/delete — lives in settings).
        // Ensures the list is non-empty and `activeName` points at a real profile.
        private void ExpProfileCombo(string id, List<WeightProfile> profiles, ref string activeName, string label, string defaultName)
        {
            if (profiles.Count == 0) profiles.Add(new WeightProfile { Name = defaultName });
            string active = activeName;
            int sel = profiles.FindIndex(p => string.Equals(p.Name, active, StringComparison.Ordinal));
            if (sel < 0) { sel = 0; activeName = profiles[0].Name; }

            ImGui.SetNextItemWidth(200f);
            if (ImGui.BeginCombo($"{label}##{id}", profiles[sel].Name))
            {
                for (int i = 0; i < profiles.Count; i++)
                    if (ImGui.Selectable(profiles[i].Name, i == sel)) activeName = profiles[i].Name;
                ImGui.EndCombo();
            }
        }

        // English display names for relic buff mods (mod Id → name), loaded once from relic_mods_en.json bundled
        // beside the DLL. Keyed by the language-independent mod Id — a pure display layer over the catalog, so a
        // client-language change never touches saved weights. Falls back to the dev ShortName when a key is
        // missing or the file is absent.
        private Dictionary<string, string> relicModNames = new(StringComparer.Ordinal);
        private bool relicModNamesLoadTried;

        private void LoadRelicModNamesIfNeeded()
        {
            if (this.relicModNamesLoadTried) return;
            this.relicModNamesLoadTried = true;
            try
            {
                var path = Path.Join(this.DllDirectory, "relic_mods_en.json");
                if (!File.Exists(path)) return;
                var d = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
                if (d != null) this.relicModNames = new Dictionary<string, string>(d, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RunecraftHelper] relic_mods_en.json load failed: {ex.Message}");
            }
        }

        // Display label for a relic mod: English name from relic_mods_en.json, else the dev ShortName.
        private string RelicModLabel(string mod) =>
            this.relicModNames.TryGetValue(mod, out var name) && !string.IsNullOrEmpty(name)
                ? name
                : ExpeditionRelicCatalog.ShortName(mod);

        // Settings UI: relic-buff weighting profiles. Drawn from DrawSettings under "Show route planner". Lets the
        // user manage named profiles and weight each relic mod (a plain routing weight); the Upside/Downside sign
        // is implied by the catalog, so both columns take POSITIVE numbers (− column = avoidance penalty). The
        // planner reads the ACTIVE profile to value relics (brick 3).
        private void DrawExpeditionBuffProfileSettings()
        {
            var s = this.Settings;
            this.LoadRelicModNamesIfNeeded();
            ImGui.TextDisabled(this.L("prof.buff_hint",
                "Weight each relic mod (a plain weight, not ex). A relic becomes a route target when its\n" +
                "Σ(+ weights) − Σ(− weights) is > 0; otherwise it's ignored. Both columns take positive\n" +
                "numbers — the − column is an avoidance penalty. Stored by internal mod id (any language)."));

            var profile = this.ExpProfilePicker("buff", s.ExpBuffProfiles, ref s.ExpActiveBuffProfile, "Default");
            var w = profile.Weights;

            int rows = Math.Max(ExpeditionRelicCatalog.Upsides.Length, ExpeditionRelicCatalog.Downsides.Length);
            if (ImGui.BeginTable("expbuffs", 2,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
                    new Vector2(0f, Math.Min(rows + 1, 8) * ImGui.GetFrameHeightWithSpacing())))
            {
                ImGui.TableSetupColumn(this.L("prof.buff_upside", "Upside  (+)"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn(this.L("prof.buff_downside", "Downside  (−)"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                for (int i = 0; i < rows; i++)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    if (i < ExpeditionRelicCatalog.Upsides.Length)
                        DrawBuffWeightCell(w, ExpeditionRelicCatalog.Upsides[i]);
                    ImGui.TableSetColumnIndex(1);
                    if (i < ExpeditionRelicCatalog.Downsides.Length)
                        DrawBuffWeightCell(w, ExpeditionRelicCatalog.Downsides[i]);
                }

                ImGui.EndTable();
            }

            if (ImGui.SmallButton(this.Loc.Label("prof.clear_weights", "clear weights", "buff"))) w.Clear();
        }

        // One relic-mod cell: a narrow weight input + the (localized) mod label. 0 removes the entry (keeps the
        // dict sparse). Negative input is clamped to 0 — the Upside/Downside sign comes from the column, not value.
        private void DrawBuffWeightCell(Dictionary<string, float> w, string mod)
        {
            w.TryGetValue(mod, out float v);
            ImGui.SetNextItemWidth(64f);
            if (ImGui.InputFloat($"##bw_{mod}", ref v, 0f, 0f, "%.0f"))
            {
                if (v < 0f) v = 0f;
                if (v == 0f) w.Remove(mod); else w[mod] = v;
            }

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(this.RelicModLabel(mod));
        }

        // Named-profile picker (combo + new/duplicate/delete + rename). Generic so brick 4 can reuse it for the
        // reward/target profiles. Guarantees a non-empty list and keeps `activeName` pointing at a real profile;
        // returns the active one. `id` disambiguates the ImGui widget ids when several pickers share a window.
        private WeightProfile ExpProfilePicker(string id, List<WeightProfile> profiles, ref string activeName, string defaultName)
        {
            if (profiles.Count == 0) profiles.Add(new WeightProfile { Name = defaultName });
            string active = activeName;   // can't capture a ref param in the FindIndex lambda
            int sel = profiles.FindIndex(p => string.Equals(p.Name, active, StringComparison.Ordinal));
            if (sel < 0) { sel = 0; activeName = profiles[0].Name; }

            ImGui.SetNextItemWidth(180f);
            if (ImGui.BeginCombo($"profile##{id}", profiles[sel].Name))
            {
                for (int i = 0; i < profiles.Count; i++)
                    if (ImGui.Selectable(profiles[i].Name, i == sel)) { sel = i; activeName = profiles[i].Name; }
                ImGui.EndCombo();
            }

            // new / rename open a name-entry popup (seeded with "" / the current name); dup + del act at once.
            ImGui.SameLine();
            if (ImGui.SmallButton($"{this.L("prof.new", "new")}##{id}")) { this.expProfileInput = string.Empty; ImGui.OpenPopup($"newprofile##{id}"); }
            ImGui.SameLine();
            if (ImGui.SmallButton($"{this.L("prof.rename", "rename")}##{id}")) { this.expProfileInput = profiles[sel].Name; ImGui.OpenPopup($"renameprofile##{id}"); }
            ImGui.SameLine();
            if (ImGui.SmallButton($"{this.L("prof.dup", "dup")}##{id}"))
            {
                var copy = new WeightProfile
                {
                    Name = profiles[sel].Name + " copy",
                    Weights = new Dictionary<string, float>(profiles[sel].Weights, StringComparer.Ordinal),
                };
                profiles.Add(copy); sel = profiles.Count - 1; activeName = copy.Name;
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(profiles.Count <= 1);
            if (ImGui.SmallButton($"{this.L("prof.del", "del")}##{id}"))
            {
                profiles.RemoveAt(sel);
                sel = Math.Clamp(sel, 0, profiles.Count - 1);
                activeName = profiles[sel].Name;
            }

            ImGui.EndDisabled();

            if (ExpProfileNamePopup($"newprofile##{id}", this.L("prof.popup_new", "New profile name"), ref this.expProfileInput, out var created)
                && !ProfileNameExists(profiles, created))
            {
                profiles.Add(new WeightProfile { Name = created });
                sel = profiles.Count - 1; activeName = created;
            }

            if (ExpProfileNamePopup($"renameprofile##{id}", this.L("prof.popup_rename", "Rename profile"), ref this.expProfileInput, out var renamed)
                && (string.Equals(renamed, profiles[sel].Name, StringComparison.Ordinal) || !ProfileNameExists(profiles, renamed)))
            {
                profiles[sel].Name = renamed; activeName = renamed;
            }

            return profiles[sel];
        }

        private static bool ProfileNameExists(List<WeightProfile> profiles, string name) =>
            profiles.Exists(p => string.Equals(p.Name, name, StringComparison.Ordinal));

        // A small name-entry popup: text box + Save/Cancel, Enter = save. Returns true (with the trimmed name in
        // `result`) only on the frame the user confirms a non-blank name. Auto-focuses the field when it opens.
        private static bool ExpProfileNamePopup(string popupId, string label, ref string buf, out string result)
        {
            result = string.Empty;
            bool confirmed = false;
            if (ImGui.BeginPopup(popupId))
            {
                ImGui.TextUnformatted(label + ":");
                if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(220f);
                bool enter = ImGui.InputText($"##{popupId}_in", ref buf, 48, ImGuiInputTextFlags.EnterReturnsTrue);
                bool save = ImGui.Button($"Save##{popupId}") || enter;
                ImGui.SameLine();
                bool cancel = ImGui.Button($"Cancel##{popupId}");
                if (save && !string.IsNullOrWhiteSpace(buf)) { result = buf.Trim(); confirmed = true; ImGui.CloseCurrentPopup(); }
                else if (cancel) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }

            return confirmed;
        }

        // Route-planner window. Auto-shows while there's an ExpeditionDetonator on the map (i.e. you're in an
        // expedition), holding the map-modifier sliders, the monolith value threshold, and the reward-marker
        // toggles. Gated on the DETONATOR ENTITY (robust, sticky per area) rather than the in-game ExplosiveCounter
        // HUD widget (GameUi→[97][9][17][1]): that UI index path DRIFTS between map loads (see memory) and
        // intermittently failed to resolve, hiding the planner even while the HUD was clearly on screen.
        private void DrawExpeditionPlannerWindow()
        {
            if (!this.expHasDetonator)
                return;

            // Once the detonator is pressed the dig is underway and the plan is frozen — hide the planner;
            // there's nothing left to decide. (Sticky per area; resets on the next expedition.)
            if (this.expDetonatorActivated)
                return;

            // Swap in a background plan if the planner Task finished since last frame (main-thread only).
            this.ApplyPendingRouteResult();

            // Draw the cached route every frame (cheap). The heavy A* recompute runs on a background Task ONLY
            // when the Run button is pressed (below) — so dragging sliders / typing weights never triggers a
            // re-plan and stays lag-free. Full chain on the large map (Tab); next placement marker in the world.
            this.DrawExpeditionRouteLargeMap();
            this.DrawExpeditionNextPointWorld();

            ImGui.SetNextWindowSize(new Vector2(340f, 0f), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin(this.Loc.Title("exp.planner_title", "Expedition Planner", "RunecraftExpeditionPlanner")))
            {
                ImGui.End();
                return;
            }

            var s = this.Settings;

            // The plan is "stale" when anything it depends on changed since the last Run (or after an area
            // change, which clears the stored fingerprint). The Run button is the ONLY thing that recomputes.
            string routeFp = this.BuildRouteFingerprint();
            bool routeStale = routeFp != this.expRouteFingerprint;

            if (this.expCtrlResolved)
            {
                int left = this.expTotalCharges - this.expPlacedFromCtrl;
                ImGui.Text(this.LF("exp.charges_ctrl", "Charges: {0} left / {1} total", left, this.expTotalCharges) +
                           (ExpIsGrand(this.expTotalCharges) ? this.L("exp.grand_suffix", "  (Grand)") : string.Empty));
            }
            else if (this.expHudResolved)
            {
                int left = Math.Max(0, this.expHudTotal - this.expPlacedFromEntities);
                ImGui.Text(this.LF("exp.charges_hud", "Charges: {0} left / {1} total (HUD) · {2} placed", left, this.expHudTotal, this.expPlacedFromEntities));
            }
            else
            {
                int left = Math.Max(0, s.ExpTotalChargesManual - this.expPlacedFromEntities);
                ImGui.Text(this.LF("exp.charges_manual", "Charges: {0} left / {1} (manual) · {2} placed", left, s.ExpTotalChargesManual, this.expPlacedFromEntities));
            }

            // Run button, right-aligned on the header line. While the background planner runs it reads
            // "Cooking..." (disabled); otherwise green "Run*" when the plan is stale, plain "Run" when current.
            ImGui.SameLine();
            const float runW = 72f;
            const float runPadX = 10f;   // extra gap so the button doesn't sit flush against the window edge
            ImGui.SetCursorPosX(ImGui.GetWindowWidth() - runW - ImGui.GetStyle().WindowPadding.X - runPadX);
            if (this.expComputing)
            {
                ImGui.BeginDisabled();
                ImGui.Button(this.L("exp.cooking", "Cooking..."), new Vector2(runW, 0f));
                ImGui.EndDisabled();
            }
            else
            {
                if (routeStale)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.55f, 0.20f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.26f, 0.70f, 0.26f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.16f, 0.45f, 0.16f, 1f));
                }

                if (ImGui.Button(routeStale ? this.L("exp.run_stale", "Run*") : this.L("exp.run", "Run"), new Vector2(runW, 0f)))
                {
                    this.LaunchRouteCompute(routeFp);
                }

                if (routeStale) ImGui.PopStyleColor(3);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(routeStale
                        ? this.L("exp.run_tip_stale", "Settings changed — click to (re)build the route.")
                        : this.L("exp.run_tip_ok", "Route is up to date."));
            }

            // Neither the controller nor the HUD counter could be read this scan — let the player set the total so
            // planning still works; progress uses the entity-counted placed charges.
            if (!this.expCtrlResolved && !this.expHudResolved)
            {
                ImGui.SetNextItemWidth(120f);
                if (ImGui.InputInt(this.L("exp.total_manual", "Total charges (manual)"), ref s.ExpTotalChargesManual) && s.ExpTotalChargesManual < 1)
                    s.ExpTotalChargesManual = 1;
                ImGui.TextDisabled(this.L("exp.manual_hint", "Controller + HUD unreadable — set total to match the in-game counter, then Run."));
            }

            if (this.expLastComputeMs > 0)
            {
                ImGui.TextDisabled(this.LF("exp.last_plan", "last plan: {0:F0} ms · A* {1} run = {2:F0} ms · {3} cached",
                    this.expLastComputeMs, this.expLastAStarCalls, this.expLastAStarMs, this.expLastAStarHits));
                if (this.expLastPhase.Length > 0) ImGui.TextDisabled(this.expLastPhase);
            }

            ImGui.SeparatorText(this.L("exp.map_mods", "Map modifiers (auto)"));
            ImGui.Text(this.LF("exp.map_mods_values", "Placement dist +%: {0}    Blast radius +%: {1}", s.ExpPlacementDistancePct, s.ExpBlastRadiusPct));
            ImGui.TextDisabled(this.L("exp.map_mods_hint", "auto-detected from the map's Expedition modifiers"));
            float effDist = this.ExpBasePlacementDistance() * (1f + (s.ExpPlacementDistancePct / 100f));
            float effRadius = this.ExpBaseBlastRadius() * (1f + (s.ExpBlastRadiusPct / 100f));
            ImGui.TextDisabled(this.LF("exp.eff_values", "→ distance {0:F0} grid · radius {1:F0} grid · {2} base  (auto each map)",
                effDist, effRadius, this.ExpCurrentIsGrand() ? this.L("exp.base_grand", "Grand") : this.L("exp.base_normal", "normal")));

            ImGui.SeparatorText(this.L("exp.targets_to_route", "Targets to route"));

            ImGui.InputFloat(this.L("exp.monolith_min", "Monolith min (ex)"), ref s.ExpMonolithMinEx, 1f, 10f, "%.0f");
            if (s.ExpMonolithMinEx < 0f) s.ExpMonolithMinEx = 0f;
            ImGui.TextDisabled(this.L("exp.monolith_min_hint", "Monoliths with best reward ≥ this are routed."));

            // Weight profiles (Reward + Buff) exist only on Grand expeditions — a normal Expedition has no
            // reward/buff tables to weight, so the controls are hidden once we know the current map is normal.
            if (!this.ExpCurrentIsNormal())
            {
                ImGui.Spacing();
                ImGui.TextDisabled(this.L("exp.weight_profiles_hint", "Weight profiles (create / edit the tables in Settings → Show route planner):"));
                this.ExpProfileCombo("plantarget", s.ExpTargetProfiles, ref s.ExpActiveTargetProfile, this.L("exp.reward_profile_combo", "Reward profile"), "Default");
                this.ExpProfileCombo("planbuff", s.ExpBuffProfiles, ref s.ExpActiveBuffProfile, this.L("exp.buff_profile_combo", "Buff profile"), "Default");

                // Spare-charge marker gate: leftover charges after the monoliths only land where one blast covers
                // ≥ N markers — so they hit dense clusters, never single (possibly-trash) markers. We can't tier
                // markers from memory (server-authoritative), so density is the only honest signal. On a normal
                // Expedition this is forced to 1 in ExpComputeRoute (see MinMarkers there), so the knob is hidden.
                ImGui.Spacing();
                ImGui.SetNextItemWidth(120f);
                ImGui.SliderInt(this.L("exp.min_markers", "Min markers / spare charge"), ref s.ExpMinMarkersPerSpareCharge, 1, 3);
                if (s.ExpMinMarkersPerSpareCharge < 1) s.ExpMinMarkersPerSpareCharge = 1;
                if (s.ExpMinMarkersPerSpareCharge > 3) s.ExpMinMarkersPerSpareCharge = 3;
                ImGui.TextDisabled(this.L("exp.min_markers_hint", "After monoliths, a spare charge is used only if it covers this many\nmarkers at once (normal maps; Grand routes every weighted target)."));
            }

            // Expedition targets (reward-flag value by height tier). Normal-expedition only — flags/height
            // tiers don't apply on Grand (which routes off Reward/Buff weight profiles), so hidden there.
            if (!this.ExpCurrentIsGrand())
            {
                ImGui.Spacing();
                ImGui.SeparatorText(this.L("exp.expedition_targets", "Expedition targets"));
                ImGui.SetNextItemWidth(120f);
                ImGui.SliderInt(this.L("exp.marker_white", "White remnant chests"),   ref s.ExpMarkerWeightWhite,   0, 500);
                ImGui.SetNextItemWidth(120f);
                ImGui.SliderInt(this.L("exp.marker_magic", "Magic remnant chests"),   ref s.ExpMarkerWeightMagic,   0, 500);
                ImGui.SetNextItemWidth(120f);
                ImGui.SliderInt(this.L("exp.marker_gold", "Gold remnant chests"),    ref s.ExpMarkerWeightGold,    0, 500);
                ImGui.SetNextItemWidth(120f);
                ImGui.SliderInt(this.L("exp.marker_logbook", "Logbook flag (tall 2▲)"), ref s.ExpMarkerWeightLogbook, 0, 1000);
            }

            ImGui.End();
        }
    }
}
