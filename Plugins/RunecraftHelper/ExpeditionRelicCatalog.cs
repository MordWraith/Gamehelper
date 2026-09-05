namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;

    // Full catalog of ExpeditionRelic mods, extracted from the game's Mods.dat (schema 7, 2026-06-10 dump;
    // rows 5249–5309 + faction/special variants). These are the mods a field relic ("remnant") carries on its
    // ObjectMagicProperties; when the explosive chain reaches the relic its mods apply to the encounter.
    //
    // Keys are the LANGUAGE-INDEPENDENT internal mod Ids — profiles/weights are stored by these, so a client
    // language change never breaks a saved profile. Human-readable text is a separate display layer (a future
    // name→description map keyed by these same Ids, looked up per client locale) — see memory
    // project-expedition-relic-mods. The Upside/Downside split is purely for UI grouping; the route algorithm
    // only ever sums the user-assigned weights of whatever mods a relic actually has.
    //
    // Excluded on purpose: ExpeditionRelicUpsideDummyStat / ExpeditionRelicDownsideDummyStat (placeholders),
    // the ExpeditionRelicModifier* value-tuning sub-stats (StatusAilmentThreshold, BeastSkin — neutral, not
    // user-weighted), and the non-relic Tower*/Logbook* rows.
    internal static class ExpeditionRelicCatalog
    {
        // Map-specific "logbook remnants": one or two per logbook type, they behave like a field relic (the
        // blast chain reaches one, its mod applies to the encounter) but they are NOT the generic
        // Metadata/MiscellaneousObjects/Expedition/ExpeditionRelic entity -- they are terrain doodads under the
        // tileset. So the relic detection (that path, or EntityCustomGroup 101) missed every single one of them:
        // live on a Wastes logbook, three "Sulphite Stalagmite" objects (each "20% increased Rarity of Items
        // Dropped by Monsters") were not even in the target cache, let alone weighted by the buff profile.
        //
        // Read out of the game data rather than guessed: ExpeditionRelics.dat has one row per remnant (Name, the
        // per-map count range, and a MiscObject key), MiscObjects.dat resolves that key to the entity metadata
        // path, and the row's ItemTag gates which Mods can roll on it -- for these the pool is exactly ONE mod,
        // so the effect is fixed per type rather than rolled. The counts check out live: Wastes lists Sulphite
        // 3-5 and the map had 3.
        //
        // (Numbering trap: the Sulphite remnant's tag id 1272 is also the row id of an unrelated rarity STAT,
        // which makes a stats-column reading of Mods.dat look convincing. It is a tag -- the other remnants'
        // ids resolve to nonsense as stats.)
        //
        // Matched on the path TAIL so the tileset root ("Metadata/Terrain/Gallows/Leagues/Expedition/") can
        // change without breaking this. Note the trailing "/Objects/<X>" is required: the same folder holds pure
        // decor (e.g. ~148 KrutogSulphitePath segments on that map) which must NOT become targets.
        public static readonly (string PathTail, string Name, string Mod)[] LogbookRemnants =
        {
            ("Logbook_Wastes/Objects/Sulphite", "Sulphite Stalagmite", "ExpeditionRelicUpsideSpecialSulphite"),
            ("Logbook_Wastes/Objects/Totem", "Karui Totem", "ExpeditionRelicUpsideSpecialKaruiTotem"),
            ("Logbook_Peninsula/Objects/GoblinRelic", "Kin Totem", "ExpeditionRelicUpsideSpecialGoblinTotem"),
            ("Logbook_Heath/Objects/HeathHenge", "Runic Henge", "ExpeditionRelicUpsideSpecialRunicHenge"),
            ("Logbook_Prairie/Objects/WispTrap_Wild", "Imprisoned Wild Wisp", "ExpeditionRelicUpsideSpecialAzmeriWisp"),
            ("Logbook_Prairie/Objects/WispTrap_Vivid", "Imprisoned Vivid Wisp", "ExpeditionRelicUpsideSpecialAzmeriWisp"),
            ("Logbook_Prairie/Objects/WispTrap_Primal", "Imprisoned Primal Wisp", "ExpeditionRelicUpsideSpecialAzmeriWisp"),
            ("Logbook_Gully/Objects/DevourerSegment", "Dormant Burrower", "ExpeditionRelicUpsideSpecialDevourerTail"),

            // Listed as remnants too, but their mod pool is empty / placeholder-only in the current dump, so
            // they are detected WITHOUT a mod: they appear as a remnant carrying no weight instead of silently
            // not existing at all. Fill the mod in once one is actually seen in game.
            ("Logbook_Digsite/Objects/Lighthouse_Destructable", "Precursor Leyline", ""),
            ("Logbook_Reef/Objects/ClamChest", "Overgrown Clam", ""),
        };

        /// <summary>Is this entity path one of the per-logbook remnants, and what does its type propagate?</summary>
        public static bool TryMatchLogbookRemnant(string path, out string name, out string mod)
        {
            name = string.Empty;
            mod = string.Empty;
            if (string.IsNullOrEmpty(path)) return false;

            foreach (var (tail, rname, rmod) in LogbookRemnants)
            {
                if (path.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                {
                    name = rname;
                    mod = rmod;
                    return true;
                }
            }

            return false;
        }

        // Beneficial mods (ExpeditionRelicUpside*). Loot-relevant ones first, then density, xp, faction/special.
        public static readonly string[] Upsides =
        {
            // — loot —
            "ExpeditionRelicUpsideItemQuantityChest",
            "ExpeditionRelicUpsideItemQuantityMonster",
            "ExpeditionRelicUpsideItemRarityChest",
            "ExpeditionRelicUpsideItemRarityMonster",
            "ExpeditionRelicUpsideIncreasedArtifactsChest",
            "ExpeditionRelicUpsideIncreasedArtifactsMonster",
            "ExpeditionRelicUpsideExpeditionLogbookQuantityMonster",
            // — density —
            "ExpeditionRelicUpsidePackSize",
            "ExpeditionRelicUpsideMagicMonsterChance",
            "ExpeditionRelicUpsideRareMonsterChance",
            "ExpeditionRelicUpsideElitesDuplicated",
            // — misc —
            "ExpeditionRelicUpsideExperience",
            "ExpeditionRelicUpsideMissingLife",
            // — faction-specific —
            "ExpeditionRelicUpsideItemRarityMonsterEzomyte",
            "ExpeditionRelicUpsideExperienceKarui",
            "ExpeditionRelicUpsideMagicRareMonsterChanceGoblin",
            "ExpeditionRelicUpsideCorruptedDropChanceVaal",
            "ExpeditionRelicUpsidePreventWeaponDrops",
            "ExpeditionRelicUpsidePreventArmourDrops",
            "ExpeditionRelicUpsidePreventJewelleryDrops",
            // — special spawns —
            "ExpeditionRelicUpsideSpecialDevourerTail",
            "ExpeditionRelicUpsideSpecialRunicHenge",
            "ExpeditionRelicUpsideSpecialAzmeriWisp",
            "ExpeditionRelicUpsideSpecialGoblinTotem",
            "ExpeditionRelicUpsideSpecialSulphite",
            "ExpeditionRelicUpsideSpecialKaruiTotem",
        };

        // Penalty / danger mods (ExpeditionRelicDownside*). Grouped: immunities, penetrations, added-damage,
        // crit mechanics, then assorted survivability hazards.
        public static readonly string[] Downsides =
        {
            // — damage-type immunity —
            "ExpeditionRelicDownsideImmunePhysicalDamage",
            "ExpeditionRelicDownsideImmuneFireDamage",
            "ExpeditionRelicDownsideImmuneColdDamage",
            "ExpeditionRelicDownsideImmuneLightningDamage",
            "ExpeditionRelicDownsideImmuneChaosDamage",
            // — penetration —
            "ExpeditionRelicDownsideFirePenetration",
            "ExpeditionRelicDownsideColdPenetration",
            "ExpeditionRelicDownsideLightningPenetration",
            "ExpeditionRelicDownsideChaosPenetration",
            // — added damage —
            "ExpeditionRelicDownsideDamageAsFire",
            "ExpeditionRelicDownsideDamageAsCold",
            "ExpeditionRelicDownsideDamageAsLightning",
            "ExpeditionRelicDownsideDamageAsChaos",
            // — crit —
            "ExpeditionRelicDownsideAlwaysCrit",
            "ExpeditionRelicDownsideCannotBeCrit",
            "ExpeditionRelicDownsideCriticalAgainstFullLife",
            // — defence / mitigation —
            "ExpeditionRelicDownsideAvoidDamage",
            "ExpeditionRelicDownsideHitsCannotBeEvaded",
            "ExpeditionRelicDownsideCannotBeLeechedFrom",
            "ExpeditionRelicDownsideResistancesAndMaxResistances",
            "ExpeditionRelicDownsideArmourBreak",
            "ExpeditionRelicDownsideGrantNoFlaskCharges",
            // — ailments —
            "ExpeditionRelicDownsideElementalAilmentChance",
            "ExpeditionRelicDownsideBleedOnHitBleedDuration",
            "ExpeditionRelicDownsideAllDamagePoisonsPoisonDuration",
            "ExpeditionRelicDownsideElitesRandomCurseOnHit",
            "ExpeditionRelicDownsideImmuneToCurses",
            // — monster buffs —
            "ExpeditionRelicDownsideIncreasedLife",
            "ExpeditionRelicDownsideIncreasedDamage",
            "ExpeditionRelicDownsideIncreasedSpeed",
            "ExpeditionRelicDownsideDamageAttackCastMovementSpeedLowLife",
            "ExpeditionRelicDownsideRegenerateLifeEveryFourSeconds",
        };

        // True for any ExpeditionRelicUpside* mod Id (case-insensitive).
        public static bool IsUpside(string modName) =>
            modName != null && modName.IndexOf("Upside", StringComparison.OrdinalIgnoreCase) >= 0;

        // True for any ExpeditionRelicDownside* mod Id (case-insensitive).
        public static bool IsDownside(string modName) =>
            modName != null && modName.IndexOf("Downside", StringComparison.OrdinalIgnoreCase) >= 0;

        // Strip the "ExpeditionRelic{Upside|Downside|Modifier}" prefix for a shorter display label (dev-stage,
        // pre-i18n). E.g. "ExpeditionRelicUpsideItemQuantityChest" → "ItemQuantityChest".
        public static string ShortName(string modName)
        {
            if (string.IsNullOrEmpty(modName)) return modName;
            foreach (var p in Prefixes)
                if (modName.StartsWith(p, StringComparison.Ordinal)) return modName.Substring(p.Length);
            return modName;
        }

        private static readonly string[] Prefixes =
        {
            "ExpeditionRelicUpside", "ExpeditionRelicDownside", "ExpeditionRelicModifier", "ExpeditionRelic",
        };
    }
}
