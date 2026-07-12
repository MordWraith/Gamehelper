namespace StashUtility
{
    using GameHelper.Plugin;
    using System.Collections.Generic;
    using System.Numerics;

    public sealed class StashUtilitySettings : IPSettings
    {
        // General
        public bool EnableWaystoneManager = true;
        public bool EnableTabletManager = true;
        public bool EnableTabletUI = true;
        public bool DisableBadTabletHighlight = false;
        public bool ShowOverlayInBackground = false;

        // UI path for the waystone stash tab
        public string PathString = "2,0,0,0,1,1,45,0,1";

        // Filter Criteria (Basic highlight thresholds)
        public int MinTier = 1;
        public bool FilterMaxRevives = false;
        public int MaxRevivesAvailable = 0;
        
        public bool FilterMinItemRarity = false;
        public int MinItemRarity = 0;
        public bool FilterMaxItemRarity = false;
        public int MaxItemRarity = 200;
        
        public bool FilterMinPackSize = false;
        public int MinPackSize = 0;
        public bool FilterMaxPackSize = false;
        public int MaxPackSize = 100;
        
        public bool FilterMinMonsterRarity = false;
        public int MinMonsterRarity = 0;
        public bool FilterMaxMonsterRarity = false;
        public int MaxMonsterRarity = 100;
        
        public bool FilterMinMonsterEffectiveness = false;
        public int MinMonsterEffectiveness = 0;
        public bool FilterMaxMonsterEffectiveness = false;
        public int MaxMonsterEffectiveness = 100;
        
        public bool FilterMinWaystoneDropChance = false;
        public int MinWaystoneDropChance = 0;
        public bool FilterMaxWaystoneDropChance = false;
        public int MaxWaystoneDropChance = 300;

        public bool FilterMinExplicitMods = false;
        public int MinExplicitMods = 0;
        public bool FilterMaxExplicitMods = false;
        public int MaxExplicitMods = 10;

        // GREAT Conditions (Waystones)
        public bool FilterGreatRarity = false;
        public int MinGreatRarity = 30;
        public bool FilterGreatPackSize = false;
        public int MinGreatPackSize = 20;
        public bool FilterGreatMonstRarity = false;
        public int MinGreatMonstRarity = 20;
        public bool FilterGreatEffect = false;
        public int MinGreatEffect = 15;
        public bool FilterGreatDropChance = false;
        public int MinGreatDropChance = 120;
        public bool FilterGreatExplicitMods = false;
        public int MinGreatExplicitMods = 0;
        public bool FilterGreatMaxExplicitMods = false;
        public int MaxGreatExplicitMods = 10;
        public int GreatIndicatorPosition = 0; // 0: Top-Left, 1: Top-Right, 2: Bottom-Left, 3: Bottom-Right
        public float GreatIndicatorSize = 20f;

        // GREAT Conditions (Tablets)
        public bool FilterTabletGreat = false;
        public int MinTabletGoodMods = 2;
        public int MinTabletGoodModsToHighlight = 1;

        // Colors (RGBA)
        public Vector4 GoodColor = new(0.0f, 0.85f, 1.0f, 1.0f);       // Waystone Good (Vibrant Cyan)
        public Vector4 BadColor = new(1.0f, 0.25f, 0.0f, 1.0f);        // Waystone Bad (Red-Orange)
        public Vector4 TabletGoodColor = new(0.85f, 0.2f, 1.0f, 1.0f); // Tablet Good (Purple/Magenta)
        public Vector4 TabletBadColor = new(0.75f, 0.0f, 0.15f, 1.0f);  // Tablet Bad (Deep Red)
        public Vector4 NeutralColor = new(1f, 1f, 0f, 0.7f);   // Yellow
        public Vector4 NormalRarityColor = new(0.8f, 0.8f, 0.8f, 1f);
        public Vector4 MagicRarityColor = new(0.4f, 0.6f, 1f, 1f);
        public Vector4 RareRarityColor = new(1f, 0.85f, 0f, 1f);
        public Vector4 ColorGreat = new(10f / 255f, 212f / 255f, 7f / 255f, 1.0f);
        public Vector4 TabletColorGreat = new(255f / 255f, 200f / 255f, 0f / 255f, 1.0f); // Gold for GREAT Tablet arrow

        // Visuals
        public float BorderThickness = 3f;
        public float BorderMargin = 4f;
        public float RarityIndicatorSize = 10f;
        public bool ShowRarityBorder = true;
        public bool ShowModBorder = true;
        public bool ShowTabletModBorder = true;
        public bool HideNormalWaystones = false;

        public int FrameStyleBad = 0;   // 0: Solid, 1: Dashed, 2: Dotted
        public int FrameStyleGood = 0;  // 0: Solid, 1: Dashed, 2: Dotted

        public int ScanStartOffset = 0x20;
        public int ScanEndOffset = 0x600;

        // Mod matching config
        public bool RedTakesPriority = true;
        public bool RequireAllGoodMods = false; // false = ANY good mod matches
        public bool FilterBadModsOnlyOnHighlighted = false;

        // Mods lists (populated / loaded from JSON)
        public List<string> GoodModPatterns = new();
        public List<string> BadModPatterns = new();

        public List<string> TabletGoodModPatterns = new();
        public List<string> TabletBadModPatterns = new();
        public List<string> TabletGodModPatterns = new();
        public Dictionary<string, float> TabletModRequiredMinRolls = new();
        public int MinGoodModsToIgnoreBad = 3;

        // Debug Probe Mode
        public bool EnableDebugProbe = false;
        public bool EnableMerchantPurchasePanel = false;
    }
}
