// <copyright file="AtlasMapNodeContent.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.RemoteObjects.States.InGameStateObjects
{
    using System;
    using System.Collections.Generic;

    /// <summary>Describes one known Atlas badge value.</summary>
    public sealed class AtlasMapNodeBadge
    {
        internal AtlasMapNodeBadge(uint id, string name, string description, string? icon)
        {
            this.Id = id;
            this.Name = name;
            this.Description = description;
            this.Icon = icon;
        }

        public uint Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string? Icon { get; }

        public static IReadOnlyList<AtlasMapNodeBadge> Known { get; } = new AtlasMapNodeBadge[]
        {
            new(0x0064, "Powerful Map Boss", "Area contains a Powerful Map Boss", "AtlasIconContentMapBoss"),
            new(0x008E, "Sekhema's Student", "Map Boss drops a Djinn Barya", "SorceressSandDjinnCorpseBeetles"),
            new(0x007D, "Power of Faith", "Contains 3 additional Shrines", "Shrines"),
            new(0x008F, "Azmeri Champion", "Map Boss is Possessed", "BossNotableAzmeriSpirit"),
            new(0x0088, "Breach Hive", "Contains a Breach Hive", "BreachNotable4"),
            new(0x008C, "Monstrous Treasure", "Contains many extra Strongboxes with monsters waiting in ambush", "AtlasIconContentStrongBox"),
            new(0x0094, "Swarming Spirits", "Contains 5 additional Azmeri Spirits", "EnduranceFrenzyPowerChargeNode"),
            new(0x0091, "Glimmering Mutation", "Currency found is replaced with rarer varieties", "CurrencyNode"),
            new(0x0070, "Essence Trove", "All Rare monsters are Essence monsters", "AtlasIconContentEssence"),
            new(0x03E8, "Corruption", "This map is Corrupted", "AtlasIconContentCorruption"),
            new(0x6157, "Grand Mirror", "Contains a reflection of the Map Boss", "AtlasIconContentGigaMirror"),
            new(0x009A, "Mountain Influence", "Also counts as a Mountain Area", "MountainBiome"),
            new(0x009B, "Grass Influence", "Also counts as a Grass Area", "GrassBiome"),
            new(0x009C, "Forest Influence", "Also counts as a Forest Area", "ForestBiome"),
            new(0x009D, "Swamp Influence", "Also counts as a Swamp Area", "SwampBiome"),
            new(0x009E, "Desert Influence", "Also counts as a Desert Area", "DesertBiome"),
            new(0x0097, "Energized Ley Lines", "Doubles Effect of Tablets used on Area", "CaptivatedInterestKeystone"),
            new(0x0095, "Power Struggle", "Contains 3 additional Map Bosses throughout the area", "BossNotableSpawnBeyondMonsters"),
            new(0x0073, "Arcane Hordes", "All Monsters are at least Magic", "ItemQuantityandRarity"),
            new(0x0096, "Corrupted Mirage", "Area has 2 additional random Waystone Modifiers", "CorruptedDefences"),
            new(0x008B, "Affluent Armies", "50% increased Rarity of items found in area", "BossMapDrops"),
            new(0x0085, "Scattered Stones", "Contains 3 additional Summoning Circles", "StoneCirclesNode"),
            new(0x0084, "Twinned Terrors", "Summoning Circles always summon an additional Boss", "StoneCircles"),
            new(0x0077, "Indomitable Essence", "Essences transfer to a random Unique Monster on death", "EssenceNotable2"),
            new(0x007F, "Zealous Reverence", "Elemental Shrines do not appear in area", "BossNotableSpawnAdditionalShrine"),
            new(0x0075, "Nature Shrines", "Shrines release an Azmeri Spirit when activated", "HybridShrineAzmeriSpirit"),
            new(0x0065, "Breach", "Area contains an Otherworldly Breach", "AtlasIconContentBreach"),
            new(0x0066, "Expedition", "Area contains a Kalguuran Expedition", "AtlasIconContentExpedition"),
            new(0x0067, "Delirium", "Area contains a Delirium Mirror", "AtlasIconContentDelirium"),
            new(0x0068, "Ritual", "Area contains Ritual Altars", "AtlasIconContentRitual"),
            new(0x0069, "Irradiated", "Area has +1 to Monster Level", "AtlasIconContentIrradiated"),
            new(0x006A, "Overrun by the Abyssal", "Area contains many extra Abysses", "AtlasIconContentAbyssOverrun"),
            new(0x006B, "Vaal Beacons", "Area contains Vaal Beacons", "AtlasIconContentIncursion"),
            new(0x006C, "Abyss", "Area contains Abysses", "AtlasIconContentAbyss"),
            new(0x006D, "Notable Location", "Area contains an important objective", "AtlasMasteryBiome"),
            new(0x006E, "[DNT] Breach City - Not Shown to Players", "DNT No visual identity = not shown", null),
            new(0x006F, "Great Beast", "Slay the Great Beast to earn Hilda's Favour", "CompanionsNotable1"),
            new(0x0071, "Monstrous Treasure", "Contains many extra Strongboxes with monsters waiting in ambush", "AtlasIconContentStrongBox"),
            new(0x0072, "Spirit Guide", "Contains an Azmeri Spirit that will be released when Possessed Monsters are slain", "AtlasIconContentAzmeriSpirit"),
            new(0x0074, "Hunting Grounds", "Contains 2 additional Rogue Exiles and 5 additional Rare Beasts", "Hunter"),
            new(0x0076, "Crystalised Twinning", "Contains 3 additional Essence Packs Essence Packs have an additional Rare Monster", "EssenceNotable1"),
            new(0x0078, "Azmeri Energisation", "Contains 2 additional Azmeri Spirits Azmeri Spirits have 1000% increased maximum Empowerment", "MoreWildWisps"),
            new(0x0079, "Spirit Migration", "An Azmeri Spirit moves to a nearby map on completion, eventually ascending to a Sacred Spirit", "VividPrimalWildWisps"),
            new(0x007A, "Sacred Spirit", "The Azmeri Spirit has ascended to a Sacred Spirit", "MoreSacredWisps"),
            new(0x007B, "Ancient Trove", "Contains a Unique Strongbox", "StrongboxNotable2"),
            new(0x007C, "Twice-Locked Boxes", "Contains 3 additional Strongboxes Strongboxes are openable twice", "StrongboxNotable1"),
            new(0x007E, "Large Congregation", "Contains 3 additional Shrines Shrines have 2 additional packs of Worshippers", "ShrinesNode"),
            new(0x0080, "Persistent Devotion", "Shrine Buffs are reapplied when entering the Map Boss Arena", "GreedShrinenoteble"),
            new(0x0081, "Rites of the Rogues", "Contains 2 additional Shrines Shrines are Worshipped by a Rogue Exile", "Anarchy5"),
            new(0x0082, "Surprising Alliances", "Contains 2 additional Rogue Exiles Rogue Exiles appear in Pairs", "AnarchyNode1"),
            new(0x0083, "Azmeri Bloodline", "Contains an additional Rogue Exile and 2 additional Azmeri Spirits Rogue Exiles are Possessed when a Possessed Monster is killed in Area", "Anarchy4"),
            new(0x0086, "Map Area Modified", "World Area has been manipulated and cannot be manipulated again", "Mapnode"),
            new(0x0087, "Fleeing Exile", "", "AnarchyNotable2"),
            new(0x0089, "Simulacrum", "Contains a manifestation of Delirium", "DeliriumNotable7"),
            new(0x008A, "Chaotic Cacophony", "Contains an extra of each type of content", "ElderShaperNotable1"),
            new(0x008D, "Trialmaster's Trainee", "Map Boss drops an Inscribed Ultimatum", "VaalNotable1"),
            new(0x0090, "Gigantic Uprising", "Monsters are Gigantic, have 50% reduced pack size and drop 50% increased items", "MinionsandManaNotable"),
            new(0x0092, "Stolen Power", "Contains an additional Summoning Circle Summoning Circle Bosses have increased difficulty and reward per power of enemy slain", "ScorchTheEarth"),
            new(0x0093, "Headhunters", "When Players Kill a Rare Monster they will gain 1 of its Modifiers for 20 seconds", "skullcracking"),
            new(0x0098, "Exceptional Find", "1000% increased Exceptional Items found in Area Monster may drop anyExceptional Items", "ExceptionalItemsBodyArmour"),
            new(0x0099, "Water Influence", "Also counts as a Water Area", "WaterBiome"),
            new(0x009F, "Immured Fury", "Doryani has spotted The Immured Fury in this Area", "AtlasIconContentSanctificationBoss"),
            new(0x00A0, "Mirage of Riches", "Equipment dropped by monsters is replaced with other items", "Currency2"),
            new(0x00A1, "Wisdom's Teachings", "Monsters grant 100% increased Experience", "BossNotableGrantMoreExperience"),
            new(0x00A2, "Tight Pockets", "Gold dropped by monsters is replaced with other items", "BossNotableDropMoreItems"),
            new(0x00A3, "Fragment of Immortality", "Players have unlimited Revivals in area Monsters have 100% increased Effectiveness", "IncreaseMinionLifeNode"),
            new(0x00A4, "Prosperous Populous", "100% increased Rarity of items found in area", "ItemQuantity"),
            new(0x00A5, "Echoes of Power", "5 Rare Monsters are Duplicated", "GenericMinionNotable"),
            new(0x00A6, "Grand Expedition", "Area contains a Grand Expedition", "ExpeditionNode1"),
            new(0x00A7, "Abyssal Depths", "Area contains an Abyssal Depths", "AtlasIconContentAbyssalDepths"),
            new(0x00A8, "Abyssal Fissure", "Area contains Abysses along an Abyssal Fissure", "AtlasIconContentAbyss"),
            new(0x00A9, "Viridian Wildwood", "Area contains a Viridian Wildwood", "AtlasIconContentAzmeriSpirit"),
            // Do not map 0x03E9: the full UI value 0x000203E9 is a shared special-border badge
            // marker, observed on both Grand Expeditions and Simulacrums. Grand Expedition is
            // identified by its persistent EndgameMapContent id 0x00A6 instead.
            // ATLAS_CONTENT_PORT_INSERT
        };
    }

    /// <summary>Describes one known Atlas effect-token value.</summary>
    public sealed class AtlasMapNodeEffect
    {
        internal AtlasMapNodeEffect(uint id, string description, string? icon = null)
        {
            this.Id = id;
            this.Description = description;
            this.Icon = icon;
        }

        public uint Id { get; }
        public string Description { get; }
        public string? Icon { get; }

        public static IReadOnlyList<AtlasMapNodeEffect> Known { get; } = new AtlasMapNodeEffect[]
        {
            // Generated from the PoE 0.5.5 Stats rows used by EndgameMapContent. The token id is
            // Stats row + 1, so these ids must be refreshed whenever rows are inserted by a patch.
            new(0x04D8, "Affluent Armies", "BossMapDrops"),
            new(0x0550, "Wisdom's Teachings", "BossNotableGrantMoreExperience"),
            new(0x0890, "Fleeing Exile", "AnarchyNotable2"),
            new(0x0962, "Monstrous Treasure", "AtlasIconContentStrongBox"),
            new(0x0963, "Power of Faith", "Shrines"),
            new(0x0A8C, "Spirit Migration", "VividPrimalWildWisps"),
            new(0x127C, "Behemoth's Bounty"),
            new(0x1283, "Corrupted Mirage", "CorruptedDefences"),
            new(0x12BF, "Fragment of Immortality", "IncreaseMinionLifeNode"),
            new(0x153C, "Ancient Trove", "StrongboxNotable2"),
            new(0x157F, "Headhunters", "skullcracking"),
            new(0x1867, "Abyss", "AtlasIconContentAbyss"),
            new(0x1C43, "Vaal Beacons", "AtlasIconContentIncursion"),
            new(0x2096, "Echoes of Power", "GenericMinionNotable"),
            new(0x2D80, "Delirium", "AtlasIconContentDelirium"),
            new(0x3174, "Twice-Locked Boxes", "StrongboxNotable1"),
            new(0x3210, "Arcane Hordes", "ItemQuantityandRarity"),
            new(0x3252, "Surprising Alliances", "AnarchyNode1"),
            new(0x336E, "Ritual", "AtlasIconContentRitual"),
            new(0x3411, "Expedition", "AtlasIconContentExpedition"),
            new(0x3899, "Azmeri Champion", "BossNotableAzmeriSpirit"),
            new(0x3A5F, "Notable Location", "AtlasMasteryBiome"),
            new(0x3DCC, "Crystalised Twinning", "EssenceNotable1"),
            new(0x4C5A, "Powerful Map Boss", "AtlasIconContentMapBoss"),
            new(0x4E8A, "Energized Ley Lines", "CaptivatedInterestKeystone"),
            new(0x5479, "Mirage of Riches", "Currency2"),
            new(0x55C3, "Immured Fury", "AtlasIconContentSanctificationBoss"),
            new(0x592E, "Overrun by the Abyssal", "AtlasIconContentAbyssOverrun"),
            new(0x5DFE, "Breach", "AtlasIconContentBreach"),
            new(0x5DFF, "Irradiated", "AtlasIconContentIrradiated"),
            new(0x5E2D, "Scattered Stones", "StoneCirclesNode"),
            new(0x60C6, "Breach Hive", "BreachNotable4"),
            new(0x6114, "Azmeri Energisation", "MoreWildWisps"),
            new(0x613D, "Tight Pockets", "BossNotableDropMoreItems"),
            new(0x615C, "Grand Mirror", "AtlasIconContentGigaMirror"),
            new(0x61CC, "Twinned Terrors", "StoneCircles"),
            new(0x6208, "Rites of the Rogues", "Anarchy5"),
            new(0x622B, "Stolen Power", "ScorchTheEarth"),
            new(0x6231, "Large Congregation", "ShrinesNode"),
            new(0x6249, "Nature Shrines", "HybridShrineAzmeriSpirit"),
            new(0x6303, "Hunting Grounds", "Hunter"),
            new(0x634F, "Power Struggle", "BossNotableSpawnBeyondMonsters"),
            new(0x6352, "Indomitable Essence", "EssenceNotable2"),
            new(0x6357, "Azmeri Bloodline", "Anarchy4"),
            new(0x6363, "Exceptional Find", "ExceptionalItemsBodyArmour"),
            new(0x64E5, "Essence Trove", "AtlasIconContentEssence"),
            new(0x64E6, "Spirit Guide", "AtlasIconContentAzmeriSpirit"),
            new(0x6506, "Water Influence", "WaterBiome"),
            new(0x6507, "Mountain Influence", "MountainBiome"),
            new(0x6508, "Grass Influence", "GrassBiome"),
            new(0x6509, "Forest Influence", "ForestBiome"),
            new(0x650A, "Swamp Influence", "SwampBiome"),
            new(0x650B, "Desert Influence", "DesertBiome"),
            new(0x653F, "Persistent Devotion", "GreedShrinenoteble"),
            new(0x65F9, "{0} Atlas Point"),
            new(0x6764, "Trialmaster's Trainee", "VaalNotable1"),
            new(0x6765, "Sekhema's Student", "SorceressSandDjinnCorpseBeetles"),
            new(0x6766, "Gigantic Uprising", "MinionsandManaNotable"),
            new(0x6767, "Glimmering Mutation", "CurrencyNode"),
            new(0x6873, "Delirium", "AtlasIconContentDelirium"),
            new(0x6874, "Abyss", "AtlasIconContentAbyss"),
            new(0x6875, "Ritual", "AtlasIconContentRitual"),
            new(0x6876, "Vaal Beacons", "AtlasIconContentIncursion"),
            new(0x6877, "Breach", "AtlasIconContentBreach"),
            new(0x6A75, "Viridian Wildwood", "AtlasIconContentAzmeriSpirit"),
            new(0x6A86, "Abyssal Fissure", "AtlasIconContentAbyss"),

            // Separately observed token types that are not EndgameMapContent Stats[0] rows.
            new(0x6290, "Simulacrum", "DeliriumNotable7"),
            new(0x685A, "{0}% Delirious", "AtlasIconContentDelirium"),
            new(0x685C, "{0}% Delirious", "AtlasIconContentDelirium"),
        };
    }
}
