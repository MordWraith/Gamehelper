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
