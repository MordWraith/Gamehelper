namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;
    using GameHelper.Plugin;

    // How the per-row price text is tinted to signal reward value at a glance.
    //   Off       — single neutral colour.
    //   Relative  — green/yellow/red vs. the median of the rows currently on screen.
    //   Absolute  — green/yellow/red vs. fixed Exalted thresholds.
    public enum RewardColorMode
    {
        Off = 0,
        Relative = 1,
        Absolute = 2,
    }

    // A named set of weights keyed by a LANGUAGE-INDEPENDENT internal id (reward MinimapIcon name, or
    // ExpeditionRelic mod id). Profiles let the user keep several presets (e.g. one buff profile per build /
    // per farming goal) and switch between them from the planner window. Storing by internal id means a client
    // language change never invalidates a saved profile.
    public sealed class WeightProfile
    {
        public string Name = "Default";
        public Dictionary<string, float> Weights = new(StringComparer.Ordinal);
    }

    // One watched glow-rune row. Rune = the language-independent Expedition2Runes Id (e.g. "Opulent").
    // Show gates whether a match is labelled on the map; Weight decides which of several matched runes on
    // one monolith is shown (highest wins; ties show all). The default rows can only be toggled off, not
    // removed (see RunecraftHelperCore glow-rune helpers).
    public sealed class GlowRuneEntry
    {
        public string Rune = string.Empty;
        public float Weight = 100f;
        public bool Show = true;
    }

    public sealed class RunecraftHelperSettings : IPSettings
    {
        // poe.ninja PoE2 league slug as it appears in the API "league" parameter (spaces become '+').
        // Update each league launch. Default is the current league as of 2026-06-06.
        public string League = "Runes of Aldur";

        // How long cached prices stay valid before we re-fetch (minutes). Range enforced in the
        // UI slider (5–60).
        public int CacheTtlMinutes = 60;

        // Last successful sync timestamp (UTC). Zero means "never fetched yet".
        public DateTime LastSyncUtc = DateTime.MinValue;

        // Colour-coding of the overlay price text (see RewardColorMode).
        public RewardColorMode ColorMode = RewardColorMode.Relative;

        // Horizontal nudge (screen px) applied to the price text — lets the user slide it left/right
        // to clear long reward names or sit it wherever reads best. Negative = left, positive = right.
        public float OverlayXOffset = 0f;

        // When the Runeshape Combinations panel is open at a SEALED (rerolled) monolith, draw a gold
        // border around the row of the locked-in recipe (the one the monolith will produce) so it's
        // obvious which of the listed combinations is fixed. On by default.
        public bool HighlightLockedRecipeInPanel = true;

        // Show glow-rune scouting: when a watched rune sits on a glowing socket of a monolith, label the
        // monolith on the large map (above its price) with that rune's name. GlowRunes is the watch table
        // (rune → weight + show); the default rows (Time/Death/Bond/Power/Opulent) are seeded on first use
        // and can be toggled off but not removed. Off by default.
        public bool ShowGlowRunes = false;
        public List<GlowRuneEntry> GlowRunes = new();

        // Show the per-monolith debug window: pick a nearby monolith and dump everything the offer
        // rule uses (anchor/p/N, sockets-vs-station N, area level, addresses, and the full offered
        // recipe list). Used to report game-vs-plugin recipe mismatches. Off by default.
        public bool ShowWindow = false;

        // ── Monolith reward window (Runeshape Encounter) ─────────────────────
        // Show a window listing, per nearby monolith, the candidate recipes (filtered by the
        // monolith's anchor rune + hole position) and their poe.ninja Exalted prices. The anchor is
        // read off the persistent Expedition2Encounter device, so it works out of the network bubble.
        public bool ShowMonolithRewards = false;

        // Hide candidate rewards whose unit Exalted price is below this (0 = show all, incl. unpriced).
        public float MonolithRewardsMinExalted = 0f;

        // Monolith Rewards header highlight by absolute value. If a monolith's best reward value (ex)
        // reaches this threshold its header is tinted green; from 0.6× the threshold up to it, yellow;
        // below 0.6× it is not tinted. 0 disables the threshold highlight (header falls back to ColorMode).
        public float MonolithHighlightThreshold = 0f;

        // Draw each monolith's best reward value (ex) on the in-game large-map overlay, at the monolith's
        // map position (the same place Radar shows the socket count). Tinted by MonolithHighlightThreshold.
        public bool DrawMonolithValueOnMap = false;

        // Hide the on-map value labels while the in-game Runeshape Combinations panel is open (the same
        // panel the recipe overlay reads). Avoids cluttering the map with summary prices while the player
        // is reading the panel + its per-recipe overlay. On by default.
        public bool HideMapValueWhenPanelOpen = true;

        // Large-map projection tuning (mirrors Radar's calibration so the label lines up with the monolith).
        // Defaults match Radar's defaults; nudge these if your Radar large-map zoom/offsets are non-default.
        public float MapValueScaleMultiplier = 1f;
        public float MapValueXOffset = 0f;
        public float MapValueYOffset = 0f;

        // ── Expedition planner (WIP, built brick-by-brick) ───────────────────
        // Brick 1: read-only debug window listing the detonator, explosive counts (from the in-game
        // controller), placed charges, and candidate targets. No map drawing / planning yet.
        public bool ShowExpeditionDebug = false;

        // Draw, above each placed charge in the world, its grid distance to the detonator (straight-line /
        // walkable A* path). Used to verify whether the ~108.4-grid max placement distance is straight or
        // path-based by placing a charge behind an obstacle.
        public bool ShowExpeditionGridValue = false;

        // Draw, on the large map, the footprint of every path blocker (terrain objects with a
        // TriggerableBlockage component — "DevourerSegment", "RootBlocker", … — name/icon vary by tileset).
        // A blocked one punches a hole in the RAW walkable grid; we flood-fill that hole and paint it so you
        // can confirm visually which passages only open AFTER a blast destroys the blocker. Visualization only
        // for now — the route planner does not yet exploit blast-opened paths.
        public bool ShowExpeditionGates = false;

        // Flood-fill tuning for the blocker footprint (the hole a blocked blocker punches in the walkable grid).
        // The fill starts at the blocker cell and 4-connects over non-walkable cells, bounded by these:
        //   • MaxRadius — how far (cells) from the blocker the fill may spread.
        //   • MaxCells  — hard cap; if exceeded (the hole merged into a big chasm/wall) it falls back to a disk.
        //   • DiskRadius — radius of that disk fallback.
        // Tune live with "Show path blockers (gates)" on until the red footprint matches the blocker in-game.
        public int ExpGateFloodMaxRadius = 36;
        public int ExpGateFloodMaxCells = 1200;
        public int ExpGateDiskRadius = 7;

        // Paint a WEIGHT HEATMAP on the large map (Tab): every weighted target (monolith priced in ex, reward
        // markers by profile weight, beneficial relics by net buff weight) splats a radial blob of its weight onto
        // a coarse grid; cells are tinted by accumulated weight (transparent → green → yellow → red). Shows the
        // "hot" clusters the new monolith-first route planner reasons about, and where spare charges could grab
        // en-route weight. Visualization + a companion inventory dump (expedition_inventory.txt). Off by default.
        public bool ShowExpeditionHeatmap = false;

        // Second heatmap layer: only NON-MONOLITH weighted targets (reward markers / relics), normalized among
        // themselves so small marker weights show up instead of being drowned out by the huge monolith prices in
        // the combined heatmap. Distinct cool palette (blue→magenta). Independent toggle; can overlay the main one.
        public bool ShowExpeditionHeatmapMarkers = false;

        // Heatmap influence radius (grid units): each target's weight falls off as a Gaussian with this sigma, so
        // larger = smoother/broader hot zones. ~70 ≈ two blast radii. Tune live with the heatmap on.
        public int ExpHeatmapRadius = 70;

        // Draw the ROUTER's strict-spine polyline (Algorithm 1) on the large map (Tab) as a thin cyan line — the
        // path the player walks (detonator → anchors), independent of where the Placer drops charges. Lets you see
        // the route and the charge placements separately while the two-algorithm planner is built. Off by default.
        public bool ShowExpeditionSpine = false;

        // When on, the route planner records a full decision trace on every Run — every candidate placement it
        // considered (coverage + pursuit), their scores, why ones were rejected, which gates it opened, and the
        // final pick per charge — to <DllDir>/expedition_planner_log.txt. For debugging WHY the route chose a path
        // (e.g. up vs left) without reading screenshots. Off by default (the trace adds work + a file write).
        public bool ExpLogPlanner = false;

        // ── Expedition route planner ─────────────────────────────────────────
        // Master toggle. When on, a planner window auto-appears while the in-game ExplosiveCounter HUD
        // (GameUi→[97][9][17][1]) is visible — i.e. while you're placing explosives. All the knobs below
        // are edited from that window, not from this settings page.
        public bool ShowExpeditionPlanner = false;

        // Map "+% to explosive placement distance" modifier (0–100). Effective placement = 108 × (1 + %/100).
        // Read off the map yourself (no clean memory source) — see EXPEDITION_WIP.md.
        public int ExpPlacementDistancePct = 0;

        // Map "+% to explosion radius" modifier (0–100). Effective blast radius = 35 × (1 + %/100).
        public int ExpBlastRadiusPct = 0;

        // Fallback total explosive count used ONLY when the in-game controller can't be read (the HUD widget's
        // +0x378 controller pointer is null — a fragile UI path that can drift). The player sets this to match
        // the in-game counter; planning then proceeds using entity-counted placed charges for progress. 15 =
        // a Grand expedition. Persists (the total is map-type, not a per-map modifier).
        public int ExpTotalChargesManual = 15;

        // Route only monoliths whose best reward value (ex) is at or above this (0 = all priced monoliths).
        public float ExpMonolithMinEx = 0f;

        // Per reward-marker weight OVERRIDES (ex). Key = MinimapIcon.IconName from MinimapIcons.dat (the
        // "RewardChest*" family, e.g. "RewardChestCurrency"); value = the ex weight the planner gives that
        // reward type. This dict holds only the user's overrides and PERSISTS across maps. Reward types not
        // listed here fall back to RunecraftHelperCore's code defaults (a few notable ones) and otherwise to
        // weight 1. A weight of 0 means "ignore this reward type". Edit / add / reset from the planner window.
        public Dictionary<string, float> ExpRewardWeights = new(StringComparer.Ordinal);

        // ── Relic-buff weighting profiles ────────────────────────────────────
        // Each relic ("remnant") carries Upside/Downside mods that apply to the encounter when the blast chain
        // reaches it. A buff profile assigns an ex-equivalent weight (magnitude) to each mod; the route value of a
        // relic = Σ(its Upside weights) − Σ(its Downside weights). A relic becomes a route target only when that
        // net is > 0 (net ≤ 0 → ignored — never sought, never avoided). Weights are positive magnitudes; the
        // Upside/Downside sign is implied by the mod id (see ExpeditionRelicCatalog). Multiple named profiles let
        // the user keep a preset per build; the active one drives planning.
        public List<WeightProfile> ExpBuffProfiles = new();
        public string ExpActiveBuffProfile = string.Empty;

        // ── Reward / target weighting profiles ───────────────────────────────
        // Per reward-marker ex weight (the value the planner gives each RewardChest* marker type). Replaces the
        // flat ExpRewardWeights dict with named profiles (same idea as buff profiles); ExpRewardWeights is kept
        // ONLY as the migration source — on first use its entries seed a "Default" target profile. Monoliths are
        // valued by their real reward price, not this profile; this weights the markers.
        public List<WeightProfile> ExpTargetProfiles = new();
        public string ExpActiveTargetProfile = string.Empty;

        // Marker-coverage gate for SPARE charges (normal maps only). We cannot tell a good reward marker
        // from a trash/mob marker from client memory (server-authoritative — see project-expedition-marker-types),
        // so markers never DRIVE the route. After every valued monolith is captured, leftover charges are spent
        // only where one blast covers at least this many markers — preventing "marker weight 1 → 5 charges burned
        // chasing single trash markers". Higher = stingier (only dense clusters earn a spare charge). Range 1..3.
        public int ExpMinMarkersPerSpareCharge = 2;

        // Reward-flag value by HEIGHT tier. ExpeditionMarker poles come in fixed per-type heights (the entity's
        // Render.WorldPosition.Z is type-fixed, not terrain-snapped — live-verified in NORMAL expedition): tiny
        // throwaway flags dominate at a baseline Z, taller poles = more valuable (white < magic < gold < the tall
        // 2-triangle Logbook flag). The planner classifies each marker by its height ABOVE the live tiny baseline
        // (relative, so Normal↔Grand absolute-Z shifts don't matter) and weights it with these. Tiny = 0 (excluded
        // so the swarm of throwaway flags stops creating junk routing weight). Tune per taste.
        public int ExpMarkerWeightWhite = 10;
        public int ExpMarkerWeightMagic = 30;
        public int ExpMarkerWeightGold = 60;
        public int ExpMarkerWeightLogbook = 100;
    }
}
