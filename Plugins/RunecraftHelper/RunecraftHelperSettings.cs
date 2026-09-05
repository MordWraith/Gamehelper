namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;
    using GameHelper.Plugin;
    using Newtonsoft.Json;

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

    // One rune's PROLIFERATION value. The gold-framed socket of a monolith marks the rune that
    // propagates to every pack of monsters unearthed AFTER that monolith (official 0.5.4 patch notes:
    // "Remnants now randomly choose which Rune slot will propagate to further Monsters"), and buffing
    // those monsters raises THEIR drops (Opulent = "Increases Monster Rarity"). So a rune carries an
    // ex-equivalent value on top of the recipe's own reward — the two ADD UP.
    //
    // LootMult = multiplier on the loot of every downstream pack. 1.0 = no effect (most runes are pure
    // danger). BELOW 1.0 encodes a net cost: Oath seeds immortal, loot-less waves and, because the chain
    // waits for kills, it slows the whole run. Rune = the language-independent Expedition2Runes Id.
    //
    // Magnitudes are SERVER-SIDE — they are not in the .dat and cannot be read from the client. These are
    // calibratable defaults ordered by the community tier list (Opulent > Bond > Power > Time > Death >
    // Rebirth); measure and re-tune. See obsidian poe2/mehanics/expedition-rune-chain.md.
    public sealed class RuneChainEntry
    {
        public string Rune = string.Empty;
        public float LootMult = 1f;
        public bool Avoid = false;    // never worth propagating (Oath / Wisdom / Bait) — flagged in the UI
    }

    public sealed class RunecraftHelperSettings : IPSettings
    {
        // poe.ninja PoE2 league name, stored VERBATIM as the API spells it — i.e. the `name` field of
        // economyLeagues ("HC Runes of Aldur"), never the web slug ("runesofaldurhc"), which the API
        // answers with an empty body. URL-encoding (spaces → '+') happens in PriceCache. Picked from
        // the settings combo; shared default with LootTracker as of 2026-06.
        public string League = "Runes of Aldur";

        // True once the user has consciously picked a league in the combo (or typed a custom one).
        // While false the plugin is allowed to move itself once onto poe.ninja's current indexed
        // league — but only if the saved league has disappeared from economyLeagues.
        public bool LeaguePinned = false;

        // Show a free-text league field instead of the combo. Escape hatch for league-launch day,
        // when the new league isn't in index-state yet.
        public bool UseCustomLeague = false;

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
        // obvious which of the listed combinations is fixed. Always on: it left the settings UI, so it is
        // a get-only property (and JsonIgnore'd) to guarantee a stale saved "false" cannot strand it off
        // with no way left to switch it back on.
        [JsonIgnore]
        public bool HighlightLockedRecipeInPanel => true;

        // Show glow-rune scouting: label a monolith on the large map (above its price) with the rune(s) it
        // would propagate from its gold socket(s), so you can see from the map which monoliths are worth
        // routing the chain through. "Worth showing" is decided by the rune-chain weight table below
        // (LootMult above 1 = gains loot) — there is deliberately no second rune list to keep in sync.
        // Several runes are joined with " | ". Off by default.
        public bool ShowGlowRunes = false;

        // ── Rune chain (proliferation) valuation ─────────────────────────────
        // Master toggle. When on, every offered recipe is scored as
        //     total = rewardEx(recipe) + chainEx(rune it would put in the gold socket)
        // instead of by its reward price alone, and the combined best row is framed in the panel. The
        // gold socket is a POSITION (station+0x40), known before the player picks anything, so for each
        // offered recipe we already know which rune it would propagate: runes[glowSocket].
        //
        // ALWAYS ON: the toggle left the settings UI, so it is [JsonIgnore]'d as well -- otherwise a
        // config saved while it was off would strand the feature off with no control left to switch it
        // back on. Kept as a field rather than a get-only property because Sim/ assigns it to run the
        // with-chain / without-chain comparison.
        [JsonIgnore]
        public bool RuneChainEnabled = true;

        // Expected loot of ONE pack of Runic monsters, in Exalted. The whole chain value scales linearly
        // with this, so it is the main calibration knob: chainEx = baseMonsterEx × downstreamPacks ×
        // (effMult − 1).
        //
        // MEASURED 2026-09-02 (T15+ Grand, recipe rewards excluded — those are unaffected by runes):
        //   • 8-wave (8/8) encounter → ~180-250 ex of monster loot ⇒ ~22-31 ex per wave;
        //   • ~6-wave encounter      → ~740-790 ex, but 440 of that was a single Divine; without that
        //                              spike ~300-350 ex ⇒ ~50-58 ex per wave.
        // 30 sits in that range, deliberately at the low end: n=1 per condition and the two samples
        // disagree ~2x. The previous 2.0 default was a pure guess and wrong by an order of magnitude,
        // which made the chain value look like rounding error next to reward prices. It is not.
        public float RuneChainBaseMonsterEx = 30f;

        // A propagated Power rune empowers the OTHER runes in the chain (official 0.5.4 fix: "Runes were
        // not being empowered by Power Runes that were propagated from previously unearthed Remnants").
        // This is READ per monolith from station+0x5d — the very flag the game uses to draw the empowered
        // rune art (Ghidra Expedition2_SetRowRunesEmpowered). The setting below is only a manual OVERRIDE
        // that forces the empowerment on everywhere, for when you know Power is live but the byte reads 0.
        // Both left the settings UI and are [JsonIgnore]'d so a stale config cannot change them: the
        // override stays OFF (the per-monolith flag read from the station is the real source), and the
        // factor is fixed at 1.5.
        [JsonIgnore]
        public bool RuneChainPowerInChain = false;
        [JsonIgnore]
        public float RuneChainPowerFactor = 1.5f;

        // Per-rune proliferation value (see RuneChainEntry). Seeded on first use with the tier-list
        // defaults; runes absent from the table are worth 1.0 (no loot effect). Edit / add / remove from
        // the settings table.
        public List<RuneChainEntry> RuneChainWeights = new();

        // Route planner: add each monolith's best achievable chain value to its route weight, so the
        // planner prefers monoliths that can seed a strong chain and not only expensive rewards. This is
        // a POSITION-INDEPENDENT upper bound (the real value depends on how many packs are raised after
        // that monolith, which is only known once the order is fixed) — order-aware routing is a separate
        // step.
        //
        // ALWAYS ON, and [JsonIgnore]'d for the same reason as RuneChainEnabled above: the toggle is gone
        // from the UI, and it used to default to off, so any config saved before this change would keep
        // it off forever. Sim/ still assigns it to compare the router with and without chain steering.
        [JsonIgnore]
        public bool RuneChainAffectsRoute = true;

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
        // is reading the panel + its per-recipe overlay. Always on (no longer a settings-UI choice).
        [JsonIgnore]
        public bool HideMapValueWhenPanelOpen => true;

        // Large-map projection tuning (mirrors Radar's calibration so the label lines up with the monolith).
        // These three sliders left the settings UI: the Radar defaults they mirror are the values that
        // actually line up, so they are fixed rather than tunable.
        [JsonIgnore]
        public float MapValueScaleMultiplier => 1f;

        [JsonIgnore]
        public float MapValueXOffset => 0f;

        [JsonIgnore]
        public float MapValueYOffset => 0f;

        // Text size of BOTH large-map labels (the monolith's price and its propagating rune names), as a
        // multiple of the ambient ImGui font size. 1.5 was the old hard-coded value and read far too big;
        // 1.0 (= the ambient UI font) is what we ship, so this is fixed rather than a slider.
        [JsonIgnore]
        public float MapLabelFontScale => 1.0f;

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
