using System.Collections.Generic;
using StashUtility.Models;

namespace StashUtility.Data
{
    public static class ModDatabase
    {
        public static readonly List<WaystoneMod> AllWaystoneMods = new()
        {
             new WaystoneMod("MapMonsterDamageAsFire", "Monsters deal % of Damage as Extra Fire") { MonsterEffectiveness = 16, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterDamageAsCold", "Monsters deal % of Damage as Extra Cold") { ItemRarity = 14, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterDamageAsLightning", "Monsters deal % of Damage as Extra Lightning") { PackSize = 8, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterDamageIncrease", "% increased Monster Damage") { MonsterRarity = 25, WaystoneDropChance = 20 },
             new WaystoneMod("MapMonsterSpeedIncrease", "Monsters have % increased Attack, Cast and Movement Speed") { PackSize = 9, WaystoneDropChance = 25 },
             new WaystoneMod("MapMonsterCritIncrease", "Monsters have % increased Critical Hit Chance / Monsters have +% Critical Damage Bonus") { PackSize = 9, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterLifeIncrease", "% more Monster Life") { MonsterRarity = 23, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterElementalResistances", "+% Monster Elemental Resistances") { ItemRarity = 14, WaystoneDropChance = 10 },
             new WaystoneMod("MapMonsterArmoured", "Monsters are Armoured") { MonsterRarity = 18, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterEvasive", "Monsters are Evasive") { PackSize = 6, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterEnergyShield", "Monsters gain % of maximum Life as Extra maximum Energy Shield") { ItemRarity = 13, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterPoisoning", "Monsters have % chance to Poison on Hit") { PackSize = 7, WaystoneDropChance = 10 },
             new WaystoneMod("MapMonsterBleeding", "Monsters have % chance to inflict Bleeding on Hit") { MonsterEffectiveness = 13, WaystoneDropChance = 10 },
             new WaystoneMod("MapMonsterStunAilmentThreshold", "Monsters have % increased Ailment Threshold / Monsters have % increased Stun Threshold") { MonsterEffectiveness = 13, WaystoneDropChance = 10 },
             new WaystoneMod("MapMonsterArmourBreak", "Monsters Break Armour equal to % of Physical Damage dealt") { MonsterRarity = 19, WaystoneDropChance = 10 },
             new WaystoneMod("MapMonsterAccuracy", "Monsters have % increased Accuracy Rating") { PackSize = 7, WaystoneDropChance = 10 },
             new WaystoneMod("MapMonsterDamageAsChaos", "Monsters deal % of Damage as Extra Chaos") { ItemRarity = 15, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterStunBuildup", "Monsters have % increased Stun Buildup") { MonsterEffectiveness = 13, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterElementAilmentChance", "Monster have % increased Elemental Ailment Application") { ItemRarity = 11, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterAdditionalProjectiles", "Monsters fire # additional Projectiles") { PackSize = 9, WaystoneDropChance = 25 },
             new WaystoneMod("MapMonsterIncreasedAreaOfEffect", "Monsters have % increased Area of Effect") { WaystoneDropChance = 20 },
             new WaystoneMod("MapPlayerEnfeeble", "Players are periodically Cursed with Enfeeble") { MonsterEffectiveness = 16, WaystoneDropChance = 20 },
             new WaystoneMod("MapPlayerTemporalChains", "Players are periodically Cursed with Temporal Chains") { PackSize = 8, WaystoneDropChance = 20 },
             new WaystoneMod("MapPlayerElementalWeakness", "Players are periodically Cursed with Elemental Weakness") { ItemRarity = 13, WaystoneDropChance = 20 },
             new WaystoneMod("MapSpreadBurningGround", "Area has patches of Ignited Ground") { MonsterEffectiveness = 15, WaystoneDropChance = 15 },
             new WaystoneMod("MapSpreadChilledGround", "Area has patches of Chilled Ground") { ItemRarity = 12, WaystoneDropChance = 15 },
             new WaystoneMod("MapSpreadShockedGround", "Area has patches of Shocked Ground") { PackSize = 7, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonstersElementalPenetration", "Monster Damage Penetrates % Elemental Resistances") { ItemRarity = 16, WaystoneDropChance = 20 },
             new WaystoneMod("MapPlayerMaximumResists", "-% maximum Player Resistances") { PackSize = 10, WaystoneDropChance = 25 },
             new WaystoneMod("MapPlayerFlaskChargeGain", "Players gain % reduced Flask Charges") { PackSize = 7, WaystoneDropChance = 15 },
             new WaystoneMod("MapPlayerRecoveryRate", "Players have % less Recovery Rate of Life and Energy Shield") { ItemRarity = 15, WaystoneDropChance = 20 },
             new WaystoneMod("MapPlayerCooldownRecovery", "Players have % less Cooldown Recovery Rate") { ItemRarity = 12, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonstersBaseSelfCriticalMultiplier", "Monsters take % reduced Extra Damage from Critical Hits") { MonsterRarity = 18, WaystoneDropChance = 10 },
             new WaystoneMod("MapMonstersCurseEffectOnSelf", "% less effect of Curses on Monsters") { ItemRarity = 10, WaystoneDropChance = 10 },
        };

        public static readonly List<TabletMod> AllTabletMods = new()
        {
            new TabletMod("TowerDroppedItemRarityIncrease", "% increased Rarity of Items found in Map", "prefix") { MinRoll = 8, MaxRoll = 12 },
            new TabletMod("TowerAdditionalStoneCircle", "Map contains an additional Summoning Circle", "prefix") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerAdditionalExile", "% increased Quantity of Waystones found in Map Map is inhabited by an additional Rogue Exile", "prefix") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerAdditionalAzmeriWisp", "Map contains additional Azmeri Spirits", "suffix") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerMonsterEffectiveness", "Monsters have % increased Effectiveness", "prefix") { MinRoll = 10, MaxRoll = 15 },
            new TabletMod("TowerRareChestCount", "Map contains additional Rare Chests", "prefix") { MinRoll = 2, MaxRoll = 3 },
            new TabletMod("TowerExperienceGainIncrease", "% increased Experience gain in Map", "prefix") { MinRoll = 12, MaxRoll = 18 },
            new TabletMod("TowerDroppedGoldIncrease", "% increased Gold found in Map", "prefix") { MinRoll = 25, MaxRoll = 35 },
            new TabletMod("TowerMonsterRarityIncrease", "Map has % increased Monster Rarity", "prefix") { MinRoll = 15, MaxRoll = 20 },
            new TabletMod("TowerRarePackIncrease", "Map has % increased number of Rare Monsters", "prefix") { MinRoll = 25, MaxRoll = 35 },
            new TabletMod("TowerMagicPackIncrease", "Map has % increased Magic Monsters", "prefix") { MinRoll = 30, MaxRoll = 40 },
            new TabletMod("TowerPackSizeIncrease", "% increased Pack Size in Map", "prefix") { MinRoll = 5, MaxRoll = 7 },
            new TabletMod("TowerDeliriumAdditionalShardsChance", "Delirium Fog in Map spawns % increased MirrorShards", "suffix") { MinRoll = 12, MaxRoll = 26 },
            new TabletMod("TowerMapBossExperience", "Map Bosses grant % increased Experience", "suffix") { MinRoll = 40, MaxRoll = 80 },
            new TabletMod("TowerMapBossWaystoneChance", "% increased Quantity of Waystones dropped by Map Bosses", "suffix") { MinRoll = 18, MaxRoll = 30 },
            new TabletMod("TowerMapBossAdditionalSpirit", "% increased Quantity of Waystones found in Map Map contains an additional Azmeri Spirit", "prefix") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerMapBossAdditionalEssence", "Map contains an additional Essence % increased Quantity of Waystones found in Map", "prefix") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerAdditionalEssence", "Map contains additional Essences", "suffix") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerMapBossAdditionalShrine", "Map contains additional Shrines", "suffix") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerAdditionalShrine", "% reduced Pack Size in Map % increased Quantity of Waystones found in Map Map contains an additional Shrine", "suffix") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerMapBossAdditionalStrongbox", "Map contains additional Strongboxes", "suffix") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerAdditionalStrongbox", "% reduced Pack Size in Map % increased Quantity of Waystones found in Map Map contains an additional Strongbox", "suffix") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerRitualOmenChance", "Ritual Favours in Map have % increased chance to be Omens", "suffix") { MinRoll = 35, MaxRoll = 70 },
            new TabletMod("TowerMapBossRarity", "% increased Rarity of Items dropped by Map Bosses", "suffix") { MinRoll = 35, MaxRoll = 60 },
            new TabletMod("TowerRitualMagicMonsters", "Revived Monsters from Ritual Altars in Map have % increased chance to be Rare", "suffix") { MinRoll = 25, MaxRoll = 40 },
            new TabletMod("TowerRitualRareMonsters", "Revived Monsters from Ritual Altars in Map have % increased chance to be Magic", "suffix") { MinRoll = 35, MaxRoll = 70 },
            new TabletMod("TowerRitualChanceForNoCost", "Favours Rerolled at Ritual Altars in Map have % chance to cost no Tribute", "suffix") { MinRoll = 3, MaxRoll = 6 },
            new TabletMod("TowerRitualAdditionalReroll", "Ritual Altars in Map allow rerolling Favours additional times", "suffix") { MinRoll = 1, MaxRoll = 3 },
            new TabletMod("TowerRitualDeferSpeed", "Favours Deferred at Ritual Altars in Map reappear % sooner", "suffix") { MinRoll = 25, MaxRoll = 40 },
            new TabletMod("TowerRitualDeferCostIncrease", "Deferring Favours at Ritual Altars in Map costs % reduced Tribute", "suffix") { MinRoll = 20, MaxRoll = 30 },
            new TabletMod("TowerRitualRerollCostIncrease", "Rerolling Favours at Ritual Altars in Map costs % reduced Tribute", "suffix") { MinRoll = 20, MaxRoll = 30 },
            new TabletMod("TowerRitualTributeIncrease", "Monsters Sacrificed at Ritual Altars in Map grant % increased Tribute", "suffix") { MinRoll = 18, MaxRoll = 30 },
            new TabletMod("TowerIncursionRareChestChance", "% increased chance Vaal Beacon Chests are Rare in Map", "suffix") { MinRoll = 30, MaxRoll = 60 },
            new TabletMod("TowerAbyss4AdditionalChance", "Map has % chance to contain four additional Abysses", "suffix") { MinRoll = 20, MaxRoll = 40 },
            new TabletMod("TowerIncursionBossChance", "% chance to add a Vaal Beacon Unique Monster to the Map", "suffix") { MinRoll = 10, MaxRoll = 25 },
            new TabletMod("TowerIncursionTokenChance", "% chance to gain an additional Crystal from Vaal Beacons in Map", "suffix") { MinRoll = 5, MaxRoll = 10 },
            new TabletMod("TowerIncursionSecondaryEncounters", "% increased chance Vaal Beacons summon additional Monsters in Map", "suffix") { MinRoll = 25, MaxRoll = 50 },
            new TabletMod("TowerIncursionExtraPacksChance", "% chance for an extra packs of Monsters around Vaal Beacons in Map", "suffix") { MinRoll = 30, MaxRoll = 60 },
            new TabletMod("TowerIncursionExtraPacks", "1 extra packs of Monsters around Vaal Beacons in Map", "suffix") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerIncursionPackSize", "% increased Pack Size for Monsters around Vaal Beacons in Map", "suffix") { MinRoll = 10, MaxRoll = 30 },
            new TabletMod("TowerAbyssExtraTickets", "% increased chance for Desecrated Currency from Abysses in Map", "suffix") { MinRoll = 20, MaxRoll = 30 },
            new TabletMod("TowerAbyssExtraModifiers", "% increased chance for Abyssal monsters in Map to have Abyssal Modifiers", "suffix") { MinRoll = 20, MaxRoll = 30 },
            new TabletMod("TowerMapBossQuantity", "% increased Quantity of Items dropped by Map Bosses", "suffix") { MinRoll = 13, MaxRoll = 20 },
            new TabletMod("TowerAbyssIncreasedRewards", "Abyss Pits in Map are twice as likely to have Rewards", "suffix"),
            new TabletMod("TowerAbyssAdditionalChance", "Map contains an additional Abyss", "suffix") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerAbyssDepthsChance", "Abysses in Map have % increased chance to lead to an Abyssal Depths", "suffix") { MinRoll = 10, MaxRoll = 20 },
            new TabletMod("TowerAbyssEffectivenessPerChasm", "Abyssal Monsters have % increased Effectiveness for each closed Pit, up to 100%", "suffix") { MinRoll = 8, MaxRoll = 12 },
            new TabletMod("TowerAbyssEnhancedMonstersPerChasm", "Abyssal Monsters in Map have increased Difficulty and Reward for each closed Pit", "suffix"),
            new TabletMod("TowerAbyssRareMonsterIncrease", "additional Rare Monsters are spawned from Abysses in Map", "suffix") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerAbyssMonsterIncrease", "Abysses in Map spawn % increased Monsters", "suffix") { MinRoll = 20, MaxRoll = 30 },
            new TabletMod("TowerAdditionalExileChance", "Map has % increased chance to contain Rogue Exiles", "suffix") { MinRoll = 70, MaxRoll = 100 },
            new TabletMod("TowerBreachAdditionalRares", "Unstable Breaches in Map spawn additional Rare Monsters when Stabilised", "suffix") { MinRoll = 1, MaxRoll = 3 },
            new TabletMod("TowerBreachBossChance", "Unstable Breaches in Map have % increased chance to contain Vruun, Marshal of Xesht", "suffix") { MinRoll = 20, MaxRoll = 50 },
            new TabletMod("TowerBreachWombgiftLevelChance", "Wombgifts have % chance to drop one Level higher in Map", "suffix") { MinRoll = 10, MaxRoll = 30 },
            new TabletMod("TowerBreachWombgiftQuantity", "% increased Quantity of Wombgifts found in Map", "suffix") { MinRoll = 30, MaxRoll = 60 },
            new TabletMod("TowerBreachHivebloodQuantity", "% increased Quantity of Hiveblood found in Map", "suffix") { MinRoll = 30, MaxRoll = 60 },
            new TabletMod("TowerMapAdditionalUniqueMonsterModifier", "Unique Monsters have 1 additional Rare Modifiers", "suffix") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerMapAdditionalModifier", "Map has additional random Modifiers", "suffix") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerStoneCircleChance", "Map has % increased chance to contain a Summoning Circle", "suffix") { MinRoll = 70, MaxRoll = 100 },
            new TabletMod("TowerBreachRareMonsterPotency", "% increased Effectiveness of Rare Breach Monsters in Map", "suffix") { MinRoll = 5, MaxRoll = 20 },
            new TabletMod("TowerAdditionalSpiritChance", "Map has % increased chance to contain Azmeri Spirits", "suffix") { MinRoll = 70, MaxRoll = 100 },
            new TabletMod("TowerAdditionalEssenceChance", "Map has % increased chance to contain Essences", "suffix") { MinRoll = 70, MaxRoll = 100 },
            new TabletMod("TowerAdditionalStrongboxChance", "Map has % increased chance to contain Strongboxes", "suffix") { MinRoll = 70, MaxRoll = 100 },
            new TabletMod("TowerAdditionalShrineChance", "Map has % increased chance to contain Shrines", "suffix") { MinRoll = 70, MaxRoll = 100 },
            new TabletMod("TowerRareAdditionalModChance", "Rare Monsters in Map have a % Surpassing chance to have an additional Modifier", "suffix") { MinRoll = 50, MaxRoll = 80 },
            new TabletMod("TowerMapDroppedMapsIncrease", "% increased Quantity of Waystones found in Map", "suffix") { MinRoll = 30, MaxRoll = 40 },
            new TabletMod("TowerExpeditionRelicModEffect", "% increased Effect of Expedition Remnants in Map", "suffix") { MinRoll = 12, MaxRoll = 18 },
            new TabletMod("TowerDeliriumRareMonsterPause", "Slaying Rare Monsters in Map pauses the Delirium Mirror Timer for seconds", "suffix") { MinRoll = 3, MaxRoll = 5 },
            new TabletMod("TowerDeliriumDoodadsIncrease", "Delirium Fog in Map spawns % increased Fracturing Mirrors", "suffix") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerDeliriumPackSizeIncrease", "Delirium Monsters in Map have % increased Pack Size", "suffix") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerDeliriumDifficultyIncrease", "Delirium Fog in Map applies % increased Deliriousness to Players", "suffix") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerDeliriumFogPersistence", "Delirium Fog in Map dissipates % slower", "suffix") { MinRoll = 20, MaxRoll = 30 },
            new TabletMod("TowerDeliriumFogDissipationDelayNew", "Delirium Fog in Map lasts additional seconds before dissipating", "suffix") { MinRoll = 6, MaxRoll = 12 },
            new TabletMod("TowerDeliriumMonsterSplinterIncrease", "% increased Stack size of Simulacrum Splinters found in Map", "suffix") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerExpeditionRunicMonsters", "Map contains % increased number of Runic Monster Markers", "suffix") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerDeliriumBossChance", "Delirium Encounters in Map are % more likely to spawn Unique Bosses", "suffix") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerExpeditionRareMonsters", "% increased number of Rare Expedition Monsters in Map", "suffix") { MinRoll = 25, MaxRoll = 40 },
            new TabletMod("TowerExpeditionLogbookIncrease", "% increased Quantity of Expedition Logbooks dropped by Runic Monsters in Map", "suffix") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerExpeditionExplosionRadius", "% increased Expedition Explosive Radius in Map", "suffix") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerExpeditionRelicIncrease", "Expeditions in Map have +Remnants", "suffix") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerExpeditionExplosionPlacement", "% increased Expedition Explosive Placement Range in Map", "suffix") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerExpeditionArtifactIncrease", "% increased quantity of Expedition Artifacts dropped by Monsters in Map", "suffix") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerBreachMonsterQuantity", "Breaches in Map have % increased Pack Size", "suffix") { MinRoll = 5, MaxRoll = 15 },
        };

        public static readonly List<JewelMod> AllJewelMods = LoadJewelMods();

        private static List<JewelMod> LoadJewelMods()
        {
            try
            {
                var asmDir = System.IO.Path.GetDirectoryName(typeof(ModDatabase).Assembly.Location) ?? "";
                string[] candidatePaths = new[]
                {
                    System.IO.Path.Combine(asmDir, "Data", "jewel_mod_ranges.json"),
                    System.IO.Path.Combine(asmDir, "jewel_mod_ranges.json"),
                    System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Plugins", "StashUtility", "Data", "jewel_mod_ranges.json"),
                    System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Plugins", "StashUtility", "jewel_mod_ranges.json"),
                    @"c:\Users\Zhu Xian\source\repos\GameHelper2\Plugins\StashUtility\Data\jewel_mod_ranges.json"
                };

                foreach (var p in candidatePaths)
                {
                    if (System.IO.File.Exists(p))
                    {
                        string json = System.IO.File.ReadAllText(p);
                        var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<JewelMod>>(json);
                        if (list != null && list.Count > 0) return list;
                    }
                }
            }
            catch
            {
                // Fallback empty list if file reading fails
            }
            return new List<JewelMod>();
        }
    }
}
