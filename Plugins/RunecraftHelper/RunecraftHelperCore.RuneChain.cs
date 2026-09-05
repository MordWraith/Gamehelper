namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using ImGuiNET;

    // Rune-chain (proliferation) valuation.
    //
    // WHAT THE GOLD SOCKET MEANS (official 0.5.4 patch notes): "Remnants now randomly choose which Rune
    // SLOT will propagate to further Monsters in Expeditions and Grand Expeditions… Added a highlight to
    // the Rune that will be propagated upon completing a Remnant in the Runeshape Recipes list." Plus the
    // base chain rule: a remnant's modifiers apply only to monsters unearthed by the explosive placed ON
    // it or by a LATER explosive. Buffing those monsters raises THEIR drops (Opulent = "Increases Monster
    // Rarity"; 0.5.3 doubled Runic Modifier magnitudes), so the propagated rune is worth real currency.
    //
    // THE KEY THAT MAKES THIS PREDICTABLE: station+0x40 holds socket POSITIONS, not runes — the frame is a
    // property of the monolith, chosen before the player touches anything. Since a socket position IS the
    // rune's index inside a recipe, for every offered recipe we already know what it would propagate:
    //     propagated(recipe) = recipe.runeIdx[glowSocket]
    // So we can RECOMMEND a recipe rather than merely report one after the fact.
    //
    // VALUE MODEL (the two sources ADD UP — picking a recipe grants both):
    //     total(recipe)  = rewardEx(recipe) + chainEx(recipe)
    //     chainEx(recipe) = baseMonsterEx × downstreamPacks × (effMult(propagated) − 1)
    //     effMult(rune)   = 1 + (lootMult[rune] − 1) × (powerInChain ? powerFactor : 1)
    //     downstreamPacks = recipe.size (its own waves — each extra runeshape adds one)
    //                     + charges still unplaced (every later charge raises ≈ one more pack)
    //
    // Magnitudes are server-side and absent from the .dat, so lootMult is calibrated, not read. Full
    // write-up incl. sources: obsidian poe2/mehanics/expedition-rune-chain.md.
    public sealed partial class RunecraftHelperCore
    {
        // Tier-list defaults (community list: Opulent > Bond > Power > Time > Death > Rebirth). LootMult
        // below 1.0 encodes a NET COST: Oath seeds immortal, loot-less waves and — because the chain waits
        // for the previous pack to die — drags the whole run; Wisdom only grants experience and burns the
        // slot a good rune could have used. Runes absent from the table are worth 1.0 (pure danger, no
        // loot effect) — see RuneEffects / obsidian poe2/expedition-runes for what each one does.
        private static readonly RuneChainEntry[] DefaultRuneChainWeights =
        {
            new RuneChainEntry { Rune = "Opulent", LootMult = 1.35f },
            new RuneChainEntry { Rune = "Bond", LootMult = 1.25f },
            new RuneChainEntry { Rune = "Power", LootMult = 1.30f },
            new RuneChainEntry { Rune = "Time", LootMult = 1.18f },
            new RuneChainEntry { Rune = "Death", LootMult = 1.15f },
            new RuneChainEntry { Rune = "Rebirth", LootMult = 1.10f },
            new RuneChainEntry { Rune = "Wisdom", LootMult = 0.95f, Avoid = true },
            new RuneChainEntry { Rune = "Oath", LootMult = 0.75f, Avoid = true },
            new RuneChainEntry { Rune = "Bait", LootMult = 1.00f, Avoid = true },
        };

        // Add any missing default row; never touches an existing one, so a re-tuned LootMult survives.
        private void EnsureRuneChainDefaults()
        {
            foreach (var d in DefaultRuneChainWeights)
                if (!this.Settings.RuneChainWeights.Exists(
                        e => string.Equals(e.Rune, d.Rune, StringComparison.Ordinal)))
                    this.Settings.RuneChainWeights.Add(
                        new RuneChainEntry { Rune = d.Rune, LootMult = d.LootMult, Avoid = d.Avoid });
        }

        private RuneChainEntry? RuneChainEntryFor(string rune) =>
            string.IsNullOrEmpty(rune) ? null
            : this.Settings.RuneChainWeights.Find(e => string.Equals(e.Rune, rune, StringComparison.Ordinal));

        // Does this monolith actually SHOW a gold frame? The panel builder
        // (Expedition2_PopulateCombinationsPanel, 0.5.4FHF) hands the row widgets the +0x40 index vector
        // ONLY when (station+0x58 − 1) < 2 — recipe-mode 1 or 2 — and when area stat 0x69dd is 0;
        // otherwise it passes an EMPTY vector, so mode 0 (standalone "additional") and mode 3
        // (anchor-less / "unique") render no frame at all even though +0x40 is populated in memory.
        // A non-empty +0x40 therefore does NOT imply a visible propagating rune.
        // The area-stat half of the gate is NOT implemented: it lives on a different stats container
        // (AreaInstance+0x130, not the +0x158/+0x160 map-mod vector we already read) whose layout we
        // haven't mapped — so a zone carrying that stat would still get a chain value here.
        private static bool RuneChainHighlightActive(MonoView v) => v.RecipeMode == 1 || v.RecipeMode == 2;

        // Effective downstream loot multiplier of propagating `rune`, with the Power empowerment applied.
        // Power multiplies the UPLIFT, not the multiplier: a 1.35 rune at powerFactor 1.5 becomes
        // 1 + 0.35×1.5 = 1.525. A rune we have no entry for is neutral (1.0).
        // `empowered` comes from the station itself (+0x5d, the same flag the panel uses to draw the
        // empowered rune art) — the manual setting only forces it on.
        private double RuneChainEffMult(string rune, bool empowered)
        {
            var e = this.RuneChainEntryFor(rune);
            if (e == null) return 1.0;
            bool power = empowered || this.Settings.RuneChainPowerInChain;
            double k = power ? Math.Max(1f, this.Settings.RuneChainPowerFactor) : 1.0;
            return 1.0 + ((e.LootMult - 1.0) * k);
        }

        // Charges not yet placed — every one of them raises ≈ one more pack that a rune propagated now
        // would still reach. Controller-authoritative when readable (see TryReadExpeditionCounts), else the
        // manual total minus entity-counted placements. 0 when we have no usable count at all (campaign
        // monolith, controller unreadable) — the chain value then rests on the recipe's own waves only.
        private int RuneChainChargesLeft()
        {
            int total = this.expCtrlResolved ? this.expTotalCharges : this.Settings.ExpTotalChargesManual;
            int placed = this.expCtrlResolved ? this.expPlacedFromCtrl : this.expPlacedFromEntities;
            if (total <= 0) return 0;
            return Math.Clamp(total - placed, 0, 64);
        }

        // Waves still ahead of each monolith on the planned route, keyed by MonoView.EntityId (the device
        // address). Rebuilt from the router's ordered anchor list once per monolith scan.
        private readonly Dictionary<long, int> runeChainWavesAhead = new();
        private int runeChainWavesTotal;      // sockets over every live monolith — the no-plan fallback

        // Monoliths that a Power rune is expected to be already propagating over, because an EARLIER
        // monolith on the planned route is expected to put Power on its gold socket. The station's own
        // +0x5d flag only turns on once Power has actually been detonated, which is never true while the
        // plan is still being made — so without this every rune behind a Power reads unempowered.
        private readonly HashSet<long> runeChainPowerUpstream = new();

        // Per monolith: the ex of uplift its propagated rune would empower if that rune were Power, i.e.
        // SUM over the monoliths AFTER it of (their expected rune's ex/wave x the waves from them onward).
        // This is Power's whole point and it lives nowhere else: Power's own 1.30 is the small half.
        private readonly Dictionary<long, double> runeChainUpliftAhead = new();

        private const string RuneChainPowerName = "Power";

        // Runes already propagating over each monolith, as a bit per Expedition2Runes index (34 runes fit a
        // ulong, so this costs nothing to build or test). "Runeshape modifiers of the same type no longer
        // stack" — official 0.5.x rule — so a gold socket that would re-propagate a rune already in the
        // chain adds NOTHING, however strong that rune is.
        //
        // This is not just a subtraction: it changes the RECOMMENDATION. Once Opulent is propagating, a
        // downstream monolith offering Opulent-or-Bond should be told to take Bond, even though Opulent
        // carries the higher weight. Without the mask the panel confidently recommends the dead duplicate.
        private readonly Dictionary<long, ulong> runeChainActiveMask = new();

        // Runes already COMMITTED on some monolith (station+0x60 resolves a recipe). Unlike a
        // predicted choice this is a fact, and one that cannot be undone -- so a locked rune is off the
        // table EVERYWHERE, not merely downstream of it. Order does not rescue it: if two monoliths take
        // Opulent, one of the two is wasted whichever detonates first, and the sealed one is the one we
        // cannot change. `runeChainLockedBit` keeps each monolith's own contribution so a sealed monolith
        // is never treated as a duplicate of itself.
        private ulong runeChainLockedMask;
        private readonly Dictionary<long, ulong> runeChainLockedBit = new();

        private static readonly Dictionary<string, int> RuneIndexByName = BuildRuneIndex();

        private static Dictionary<string, int> BuildRuneIndex()
        {
            var map = new Dictionary<string, int>(AllRuneNames.Length, StringComparer.Ordinal);
            for (int i = 0; i < AllRuneNames.Length && i < 64; i++) map[AllRuneNames[i]] = i;
            return map;
        }

        private static ulong RuneChainBit(string rune) =>
            RuneIndexByName.TryGetValue(rune, out var i) ? 1UL << i : 0UL;

        private static int RuneChainIdOf(string rune) =>
            RuneIndexByName.TryGetValue(rune, out var i) ? i : -1;

        // ROUTER-facing: the strongest loot uplift this monolith could propagate, and which rune carries it.
        // Deliberately ORDER-INDEPENDENT -- the router compares candidate tour orders, so it cannot use
        // ChainBestEx / ChainBestRune (those are already priced for the order currently in force, which is
        // exactly the circularity that let a strong rune sit at the tail of the tour and value itself at zero).
        // Duplicate suppression is not applied either: whether a rune is a duplicate depends on what precedes
        // it in the candidate order, so ExpChainReorder dedups by RuneId as it walks each order.
        //
        // A committed recipe (station+0x60) pins the rune -- there is nothing left to choose there. An open
        // monolith reports the best uplift over all its offers, which is optimistic about the reward the player
        // would give up for it; that is the right bias for ORDERING (where the uplift sits), and the recipe
        // choice itself stays the player's, guided by BestCombined as before.
        private void RuneChainRouteUplift(MonoView v, out int runeId, out double uplift)
        {
            runeId = -1;
            uplift = 0.0;
            if (!this.Settings.RuneChainEnabled || v.GlowSockets.Count == 0) return;
            if (!RuneChainHighlightActive(v)) return;   // mode 0/3 render no frame => nothing propagates

            var locked = this.RuneChainLockedRune(v);
            if (!string.IsNullOrEmpty(locked))
            {
                runeId = RuneChainIdOf(locked);
                uplift = Math.Max(0.0, this.RuneChainEffMult(locked, v.RunesEmpowered) - 1.0);
                return;
            }

            foreach (var rec in v.Offered)
            {
                if (rec.runeIdx == null) continue;
                foreach (var g in v.GlowSockets)
                {
                    if (g < 0 || g >= rec.runeIdx.Count) continue;
                    var name = this.RuneNameByIndex(rec.runeIdx[g]);
                    if (name == null) continue;
                    double up = this.RuneChainEffMult(name, v.RunesEmpowered) - 1.0;
                    if (up > uplift) { uplift = up; runeId = RuneChainIdOf(name); }
                }
            }
        }

        // Would propagating `rune` here be a no-op because it is already in the chain?
        private bool RuneChainIsDuplicate(long monoId, string rune)
        {
            if (string.IsNullOrEmpty(rune)) return false;
            ulong bit = RuneChainBit(rune);
            if (bit == 0) return false;

            // Locked elsewhere => dead here, whether or not this monolith is on the planned route.
            this.runeChainLockedBit.TryGetValue(monoId, out var own);
            if ((this.runeChainLockedMask & ~own & bit) != 0) return true;

            // Otherwise only what an EARLIER monolith on the route is expected to propagate counts.
            return this.runeChainActiveMask.TryGetValue(monoId, out var mask) && (mask & bit) != 0;
        }

        // The rune a monolith has already committed to propagating: the gold-socket rune of the recipe at
        // station+0x60. Empty while nothing is committed (+0x60 is null on an untouched monolith, verified
        // live), so there is no risk of striking a rune off the map over a mere browse. Picked with the same
        // "best of the gold sockets" rule used everywhere else, rather than marking every gold socket, so no
        // new assumption about the unverified second socket creeps in.
        private string RuneChainLockedRune(MonoView v)
        {
            if (string.IsNullOrEmpty(v.SelectedRecipeId) || v.GlowSockets.Count == 0)
                return string.Empty;
            if (!RuneChainHighlightActive(v)) return string.Empty;

            var rec = v.Offered.Find(r => string.Equals(r.id, v.SelectedRecipeId, StringComparison.Ordinal))
                      ?? this.monolithRecipes.Find(
                          r => string.Equals(r.id, v.SelectedRecipeId, StringComparison.Ordinal));
            if (rec?.runeIdx == null) return string.Empty;

            string best = string.Empty;
            double bestMult = double.NegativeInfinity;
            foreach (var g in v.GlowSockets)
            {
                if (g < 0 || g >= rec.runeIdx.Count) continue;
                var name = this.RuneNameByIndex(rec.runeIdx[g]);
                if (name == null) continue;
                double m = this.RuneChainEffMult(name, v.RunesEmpowered);
                if (m > bestMult) { bestMult = m; best = name; }
            }

            return best;
        }

        // Effective multiplier of `rune` AT a given monolith: neutral when it is a duplicate there, since a
        // second copy adds no loot. The plain RuneChainEffMult stays the chain-agnostic table lookup.
        private double RuneChainEffMultAt(long monoId, string rune, bool empowered) =>
            this.RuneChainIsDuplicate(monoId, rune)
                ? 1.0
                : this.RuneChainEffMult(rune, empowered || this.runeChainPowerUpstream.Contains(monoId));

        // Packs a rune propagated by THIS recipe would still buff: the monolith's own waves (the chosen
        // combination's length — 5 runeshapes give 5 waves, confirmed in game) plus the waves of every
        // monolith still AHEAD of it in the detonation order.
        //
        // This used to be `size + chargesLeft`, and its comment called that an upper bound. Measured in the
        // offline simulator, it is the opposite — a 2-4x UNDER-estimate — and for two compounding reasons:
        // it credits one wave per remaining CHARGE (most charges only extend the chain and unearth nothing),
        // while a single monolith with 8 sockets is 8 waves. On a 19-monolith plan the real count was 68
        // waves (103 by socket capacity) against a chargesLeft of 15.
        //
        // Counted by the waves each downstream monolith is EXPECTED to spawn (see MonoView.ExpectedWaves):
        // the recipe already locked in if it is sealed, else the one this plugin recommends there, else its
        // socket count. Self-consistent -- the player is following our own recommendation -- and it avoids
        // the systematic inflation of counting every downstream monolith at full socket capacity, which
        // would push every rune-bearing monolith up in the route weighting.
        private double RuneChainDownstreamPacks(int recipeSize, long monoId)
        {
            int own = Math.Max(1, recipeSize);
            if (this.runeChainWavesAhead.TryGetValue(monoId, out var ahead)) return own + ahead;

            // No plan yet, or this monolith is not on it (out of the bubble / below the route gate): fall
            // back to the sockets of every OTHER live monolith. That is a true upper bound, and still far
            // closer than one wave per charge. Nothing is subtracted for monoliths already detonated -- the
            // plan is frozen once the detonator is pressed anyway, so this only ever runs before the dig.
            return own + Math.Max(0, this.runeChainWavesTotal - own);
        }

        // Detonation order comes from the router's ordered anchors (expSpineAnchorIdx into expSpinePts);
        // each anchor cell is matched back to the monolith standing on it, then suffix-summed.
        //
        // Called from the monolith scan, so it reads the PREVIOUS scan's views. That is fine: socket counts
        // and positions are static for an area, so only the first scan after a zone load sees an empty map
        // and takes the fallback above.
        private void RuneChainRebuildWavesAhead()
        {
            this.runeChainWavesAhead.Clear();
            this.runeChainUpliftAhead.Clear();
            this.runeChainPowerUpstream.Clear();
            this.runeChainActiveMask.Clear();
            this.runeChainLockedBit.Clear();
            this.runeChainLockedMask = 0;
            this.runeChainWavesTotal = 0;

            // Pass 0: what is already committed. Covers EVERY monolith, on the planned route or not --
            // a sealed monolith off the route still spends its rune.
            foreach (var v in this.monolithViews)
            {
                if (v.IsForeign) continue;
                this.runeChainWavesTotal += RuneChainWavesOf(v);

                ulong bit = RuneChainBit(this.RuneChainLockedRune(v));
                if (bit == 0) continue;
                this.runeChainLockedBit[v.EntityId] = bit;
                this.runeChainLockedMask |= bit;
            }

            if (this.expSpineAnchorIdx.Count == 0 || this.expSpinePts.Count == 0) return;

            var ordered = new List<MonoView>();
            foreach (int si in this.expSpineAnchorIdx)
            {
                if (si < 0 || si >= this.expSpinePts.Count) continue;
                var p = this.expSpinePts[si];
                MonoView? near = null;
                float bestD = float.MaxValue;
                foreach (var v in this.monolithViews)
                {
                    if (!v.HasPos || v.IsForeign) continue;
                    float d = Vector2.DistanceSquared(v.GridPos, p);
                    if (d < bestD) { bestD = d; near = v; }
                }

                // Anchors are also relics and the Sentinel; only a cell sitting ON a monolith counts.
                if (near == null || bestD > 16f) continue;
                if (!ordered.Contains(near)) ordered.Add(near);
            }

            // Pass 1, FORWARDS: what is already propagating by the time each monolith is detonated --
            // Power (which empowers the rest) and the set of runes (which cannot stack with themselves).
            // Must run before the backward pass, which asks RuneChainIsDuplicate and would otherwise be
            // reading masks that have not been filled yet.
            //
            // Each monolith reads the PREVIOUS scan's expected choice for the ones before it in the tour,
            // so a fresh area converges over a few scans rather than instantly: position 1 is right at once,
            // position 2 on the next scan, and so on. Values only sharpen, and in practice it is
            // near-immediate -- a predicted duplicate only arises when two monoliths offer the same rune.
            bool power = false;
            ulong active = 0;
            foreach (var v in ordered)
            {
                if (power) this.runeChainPowerUpstream.Add(v.EntityId);
                this.runeChainActiveMask[v.EntityId] = active;

                // A sealed monolith propagates what it actually locked in, not what we would advise.
                var propagated = this.RuneChainLockedRune(v);
                if (string.IsNullOrEmpty(propagated)) propagated = v.ChainBestRune;
                if (string.IsNullOrEmpty(propagated)) continue;

                if (string.Equals(propagated, RuneChainPowerName, StringComparison.Ordinal)) power = true;
                active |= RuneChainBit(propagated);
            }

            // Pass 2, BACKWARDS: waves still ahead, and the uplift-ex still ahead that a Power here would
            // empower.
            int acc = 0;
            double upliftAcc = 0;
            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                var v = ordered[i];
                this.runeChainWavesAhead[v.EntityId] = acc;
                this.runeChainUpliftAhead[v.EntityId] = upliftAcc;
                acc += RuneChainWavesOf(v);

                var propagated = this.RuneChainLockedRune(v);
                if (string.IsNullOrEmpty(propagated)) propagated = v.ChainBestRune;

                // The rune expected HERE, valued unempowered: Power's factor is what multiplies it, so
                // folding an empowerment in already would double-count. A duplicate contributes nothing,
                // so Power gets no credit for "empowering" it either.
                if (!string.IsNullOrEmpty(propagated) &&
                    !string.Equals(propagated, RuneChainPowerName, StringComparison.Ordinal) &&
                    !this.RuneChainIsDuplicate(v.EntityId, propagated))
                {
                    upliftAcc += this.RuneChainExPerWave(propagated, false) * acc;
                }
            }
        }

        // Falls back to the socket count until the monolith has been valued once (first scan in an area),
        // which is an upper bound rather than a guess.
        private static int RuneChainWavesOf(MonoView v) =>
            v.ExpectedWaves > 0 ? v.ExpectedWaves : Math.Max(1, v.HoleCount);

        // Ex-equivalent value of the rune `recipe` would drop into the gold socket. Negative for runes
        // whose LootMult is below 1 (Oath/Wisdom) — propagating them costs value, so the recommender
        // pushes those rows down instead of pretending they are neutral.
        private double RuneChainEx(string rune, int recipeSize, bool empowered, long monoId)
        {
            if (string.IsNullOrEmpty(rune)) return 0.0;

            // Already propagating over this monolith => a second copy adds no loot at all.
            if (this.RuneChainIsDuplicate(monoId, rune)) return 0.0;

            // Empowered if this station already says so, OR if an earlier monolith on the plan is expected
            // to propagate Power (the flag cannot know that yet).
            bool emp = empowered || this.runeChainPowerUpstream.Contains(monoId);
            double ex = this.RuneChainExPerWave(rune, emp) * this.RuneChainDownstreamPacks(recipeSize, monoId);

            // Propagating Power ALSO multiplies every rune after it. Without this the strongest single
            // decision on a map reads as a mid-tier rune: measured on a 19-monolith plan, Power's own share
            // was 540 ex against a true worth of 880.
            if (string.Equals(rune, RuneChainPowerName, StringComparison.Ordinal) && !emp &&
                this.runeChainUpliftAhead.TryGetValue(monoId, out var upliftAhead))
            {
                ex += (Math.Max(1f, this.Settings.RuneChainPowerFactor) - 1.0) * upliftAhead;
            }

            return ex;
        }

        // Ex a propagated rune adds per buffed wave. The rune table is stored as a MULTIPLIER (so a rune's
        // worth scales with map tier through baseMonsterEx, which is how the buff actually works), but the
        // multiplier is unobservable in game while "this rune was worth ~10 ex a wave" can be measured -- so
        // this is the number the settings table shows and takes edits in.
        private double RuneChainExPerWave(string rune, bool empowered) =>
            this.Settings.RuneChainBaseMonsterEx * (this.RuneChainEffMult(rune, empowered) - 1.0);

        // The rune a recipe would propagate on the monolith whose panel is open: runeIdx[glowSocket].
        // Several gold sockets are possible in our read of station+0x40 (officially one — the second
        // element is unverified, see the note in obsidian), so take the best-valued of them.
        private string RuneChainPropagatedRune(MonoView v, MonoRecipe rec)
        {
            if (rec.runeIdx == null || v.GlowSockets.Count == 0) return string.Empty;
            string best = string.Empty;
            double bestMult = double.NegativeInfinity;
            foreach (var g in v.GlowSockets)
            {
                if (g < 0 || g >= rec.runeIdx.Count) continue;
                var name = this.RuneNameByIndex(rec.runeIdx[g]);
                if (name == null) continue;
                double m = this.RuneChainEffMultAt(v.EntityId, name, v.RunesEmpowered);
                if (m > bestMult) { bestMult = m; best = name; }
            }

            return best;
        }

        // Panel-facing: for a visible recipe row (matched to the offline catalog by Id), WHICH rune it would
        // propagate and how good that rune is. `mult` is the rune's effective loot multiplier — the row-ranking
        // key for the "best rune" frame, deliberately NOT the row's chainEx: chainEx also scales with the
        // recipe's size, so ranking by it would call a weak rune on a long combination "the best rune".
        // Returns false when the chain feature is off, no monolith panel is open, that monolith renders no
        // gold frame (mode 0/3), or the recipe puts nothing on a gold socket.
        private bool TryGetPropagatedRuneForRecipeId(string recipeId, out string rune, out double mult,
                                                    out bool taken)
        {
            rune = string.Empty;
            mult = 1.0;
            taken = false;
            if (!this.Settings.RuneChainEnabled || string.IsNullOrEmpty(recipeId)) return false;
            var open = this.monolithViews.Find(v => v.PanelOpen);
            if (open == null || open.GlowSockets.Count == 0) return false;
            if (!RuneChainHighlightActive(open)) return false;   // no gold frame here ⇒ nothing propagates
            var rec = this.monolithRecipes.Find(m => string.Equals(m.id, recipeId, StringComparison.Ordinal));
            if (rec == null) return false;

            rune = this.RuneChainPropagatedRune(open, rec);
            if (string.IsNullOrEmpty(rune)) return false;

            // Deduped: a rune already propagating reads as neutral here, so it is tinted plain and never
            // takes the amber "best rune" ring. That is the honest signal -- taking it again adds nothing.
            // Only call it "taken" when being taken already COSTS something, i.e. the rune would have been
            // worth propagating on its own. A neutral rune is duplicated all over a map and flagging it
            // would just be noise on a row that was never a candidate.
            taken = this.RuneChainIsDuplicate(open.EntityId, rune) &&
                    this.RuneChainEffMult(rune, open.RunesEmpowered) > 1.0;
            mult = this.RuneChainEffMultAt(open.EntityId, rune, open.RunesEmpowered);
            return true;
        }

        // Monolith-facing: the offered recipe with the highest JOINT value (reward + chain) and that value.
        // A joint max, not the sum of two separate maxima — the player picks ONE recipe, so the expensive
        // reward and the strong rune usually cannot both be had.
        private void RuneChainResolveBest(MonoView v)
        {
            v.ChainBestEx = 0.0;
            v.ChainBestRune = string.Empty;
            v.ChainBestRecipeId = string.Empty;
            v.BestCombined = v.Best;
            v.ExpectedWaves = this.RuneChainExpectedWaves(v);
            if (!this.Settings.RuneChainEnabled || v.GlowSockets.Count == 0 || v.Offered.Count == 0) return;
            if (!RuneChainHighlightActive(v)) return;   // mode 0/3 render no frame ⇒ no chain value

            double bestJoint = double.NegativeInfinity;
            foreach (var rec in v.Offered)
            {
                var rune = this.RuneChainPropagatedRune(v, rec);
                double chain = string.IsNullOrEmpty(rune)
                    ? 0.0
                    : this.RuneChainEx(rune, rec.size, v.RunesEmpowered, v.EntityId);

                double reward = 0.0;
                if (rec.reward != null && !string.IsNullOrEmpty(rec.reward.name) &&
                    this.priceCache.TryGetExaltedPrice(rec.reward.name, out var unit) && unit > 0)
                    reward = unit * Math.Max(1, rec.rewardCount);

                double joint = reward + chain;
                if (joint > bestJoint)
                {
                    bestJoint = joint;
                    v.ChainBestEx = chain;
                    v.ChainBestRune = rune;
                    v.ChainBestRecipeId = rec.id;
                }
            }

            if (bestJoint > double.NegativeInfinity) v.BestCombined = bestJoint;

            // Now that a recommendation exists, the expected wave count is its length.
            var chosen = v.Offered.Find(r => string.Equals(r.id, v.ChainBestRecipeId, StringComparison.Ordinal));
            if (chosen != null && chosen.size > 0) v.ExpectedWaves = chosen.size;
        }

        // Waves to expect from `v` before any recommendation exists: the locked-in recipe's length on a
        // sealed monolith, otherwise the socket count (it can roll up to that).
        private int RuneChainExpectedWaves(MonoView v)
        {
            if (!string.IsNullOrEmpty(v.SelectedRecipeId))
            {
                var sel = this.monolithRecipes.Find(
                    r => string.Equals(r.id, v.SelectedRecipeId, StringComparison.Ordinal));
                if (sel != null && sel.size > 0) return sel.size;
            }

            return Math.Max(1, v.HoleCount);
        }

        // ── settings UI ──────────────────────────────────────────────────────
        // Red — a rune it costs value to propagate. Vector4 (ImGui.TextColored takes RGBA floats, unlike the
        // packed uint colours the draw lists use).
        private static readonly Vector4 ColorAvoidRuneText = new(1f, 0.42f, 0.42f, 1f);

        private void DrawRuneChainSection()
        {
            // One collapsible block for the whole proliferation feature: the master toggle AND everything it
            // switches on. Collapsed, the 34-row rune weight table stops dominating the planner tab.
            if (!ImGui.CollapsingHeader(this.Loc.Title("runechain.header", "Rune settings", "rh_runechain_header")))
                return;

            ImGui.Spacing();
            ImGui.Indent();
            this.DrawRuneChainBody();
            ImGui.Unindent();
        }

        private void DrawRuneChainBody()
        {
            var s = this.Settings;

            // Four of this section's controls are no longer user-facing: the master toggle and the
            // route-steering toggle are always on, the Power empowerment factor is fixed at 1.5, and the
            // Power override stays off (whether Power is live is READ per monolith from the station, which
            // is the authoritative source -- the override only ever existed to paper over a misread). All
            // four are [JsonIgnore]'d in RunecraftHelperSettings so a config saved while a toggle was off
            // cannot strand the feature off now that there is no control left to switch it back on.
            this.EnsureRuneChainDefaults();

            ImGui.TextDisabled(this.L("runechain.enable_hint",
                "The gold-framed socket marks the rune that propagates to every pack unearthed LATER in\n" +
                "the chain, and buffing those monsters raises their drops. In the panel the GREEN frame\n" +
                "keeps meaning \"most valuable reward\"; an AMBER frame around a rune name marks the\n" +
                "strongest rune this monolith can propagate. Two separate calls, not one merged number."));

            ImGui.InputFloat(this.L("runechain.base_ex", "Loot per monster pack (ex)"), ref s.RuneChainBaseMonsterEx, 0.5f, 2f, "%.2f");
            if (s.RuneChainBaseMonsterEx < 0f) s.RuneChainBaseMonsterEx = 0f;
            ImGui.TextDisabled(this.L("runechain.base_ex_hint",
                "Expected drop value of ONE pack of Runic monsters. The chain value scales linearly with\n" +
                "this, so it is the main calibration knob — measure it, don't trust the default."));

            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextDisabled(this.L("runechain.table_hint",
                "Loot multiplier per propagated rune, and the same weight as the ex it adds to ONE buffed\n" +
                "wave (edit either — they are one number). 1.00× / 0 ex = no loot effect (pure danger); below\n" +
                "1.00 is a net cost (Oath seeds immortal loot-less waves; Wisdom only grants experience).\n" +
                "Magnitudes are server-side, so calibrate the ex column — it is the half you can measure."));
            this.DrawRuneChainTable();
        }

        private void DrawRuneChainTable()
        {
            var rows = this.Settings.RuneChainWeights;
            string? removeKey = null;
            if (ImGui.BeginTable("runechain", 6,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
                    new Vector2(0f, Math.Min(rows.Count + 1, 12) * ImGui.GetFrameHeightWithSpacing())))
            {
                ImGui.TableSetupColumn("Rune", ImGuiTableColumnFlags.WidthFixed, 92f);
                ImGui.TableSetupColumn("Loot ×", ImGuiTableColumnFlags.WidthFixed, 76f);
                ImGui.TableSetupColumn("Ex/wave", ImGuiTableColumnFlags.WidthFixed, 76f);
                ImGui.TableSetupColumn("Avoid", ImGuiTableColumnFlags.WidthFixed, 44f);
                ImGui.TableSetupColumn("Effect", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##rm", ImGuiTableColumnFlags.WidthFixed, 22f);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                foreach (var e in rows)
                {
                    ImGui.TableNextRow();
                    ImGui.PushID(e.Rune);

                    ImGui.TableSetColumnIndex(0);
                    ImGui.AlignTextToFramePadding();
                    if (e.Avoid) ImGui.TextColored(ColorAvoidRuneText, e.Rune);
                    else ImGui.TextUnformatted(e.Rune);

                    ImGui.TableSetColumnIndex(1);
                    ImGui.SetNextItemWidth(70f);
                    float m = e.LootMult;
                    if (ImGui.InputFloat("##m", ref m, 0f, 0f, "%.2f"))
                        e.LootMult = Math.Clamp(m, 0f, 10f);

                    // Same weight, expressed as the ex it adds to one buffed wave -- the measurable form.
                    // Editing it writes the multiplier back, so the two columns are one number.
                    ImGui.TableSetColumnIndex(2);
                    ImGui.SetNextItemWidth(70f);
                    float baseEx = Math.Max(0.01f, this.Settings.RuneChainBaseMonsterEx);
                    float perWave = (e.LootMult - 1f) * baseEx;
                    if (ImGui.InputFloat("##ex", ref perWave, 0f, 0f, "%.1f"))
                        e.LootMult = Math.Clamp(1f + (perWave / baseEx), 0f, 10f);

                    ImGui.TableSetColumnIndex(3);
                    bool av = e.Avoid;
                    if (ImGui.Checkbox("##av", ref av)) e.Avoid = av;

                    ImGui.TableSetColumnIndex(4);
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextDisabled(RuneEffects.TryGetValue(e.Rune, out var eff) ? eff : string.Empty);

                    ImGui.TableSetColumnIndex(5);
                    if (Array.FindIndex(DefaultRuneChainWeights,
                            d => string.Equals(d.Rune, e.Rune, StringComparison.Ordinal)) < 0)
                    {
                        if (ImGui.SmallButton("×")) removeKey = e.Rune;
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove from table");
                    }

                    ImGui.PopID();
                }

                ImGui.EndTable();
            }

            if (removeKey != null)
                rows.RemoveAll(e => string.Equals(e.Rune, removeKey, StringComparison.Ordinal));

            if (ImGui.BeginCombo("Add rune##rc", "+ add…", ImGuiComboFlags.HeightLarge))
            {
                foreach (var name in AllRuneNames)
                {
                    if (rows.Exists(e => string.Equals(e.Rune, name, StringComparison.Ordinal))) continue;
                    var eff = RuneEffects.TryGetValue(name, out var x) ? x : string.Empty;
                    if (ImGui.Selectable($"{name}  —  {eff}"))
                        rows.Add(new RuneChainEntry { Rune = name, LootMult = 1f });
                }

                ImGui.EndCombo();
            }

            if (ImGui.SmallButton(this.L("runechain.reset", "Reset to tier-list defaults")))
            {
                rows.Clear();
                this.EnsureRuneChainDefaults();
            }
        }
    }
}
