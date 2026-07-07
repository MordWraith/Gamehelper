using GameHelper.Plugin;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Numerics;

namespace Atlas
{
    public sealed class AtlasSettings : IPSettings
    {
        public Vector4 DefaultBackgroundColor = new(0f, 0f, 0f, 0.85f);
        public Vector4 DefaultFontColor = new(1f, 1f, 1f, 1.0f);

        public bool ControllerMode = false;

        // When on, the plugin loads its bundled fonts (DejaVuSans + GNU Unifont fallback) into the
        // overlay so map names in ANY language render without the user configuring a font in GH.
        // Default on so the plugin works out-of-the-box on a vanilla GameHelper.
        public bool UniversalFont = true;

        // Map-node display-name language: resolves names from maps.json "translates".
        //   "auto"  (default) = follow GameHelper's UI language (re-resolves live when it changes).
        //   otherwise a maps.json token ("english", "russian", "korean", …) = explicit override, so a
        //           player can match their PoE2 client even if the GH UI is in another language.
        // Existing installs keep whatever token they had saved (treated as an explicit override).
        public string Language = "auto";

        public string SearchQuery = string.Empty;
        public bool DrawLinesSearchQuery = true;
        public float DrawSearchInRange = 1.0f;

        // Route to all reachable maps flagged 'unique' in maps.json.
        public bool DrawLinesToUniqueMaps = false;
        // Route to reachable maps carrying the 'lineage' / 'arbiter' tag in maps.json.
        public bool PathToLineageMaps = false;
        public bool PathToArbiterMaps = false;

        public bool HideCompletedMaps = true;
        public bool HideNotAccessibleMaps = true;
        // Hide maps that are accessible/runnable right now (state AccessibleNow — the green-bordered
        // nodes). On by default.
        public bool HideAvailableMaps = true;
        public bool HideFailedMaps = true;
        public bool ShowMapBadges = true;
        // Show a small badge with the number of content markers on each node (Essence/Breach/
        // Ritual/Boss…). The exact content TYPE is not persisted by the client for non-rendered
        // nodes (it's rolled from a per-node seed), so only the reliable count is surfaced — it
        // works for every node including off-screen/hidden ones. See docs/re-findings.md §2.7.
        public bool ShowContentCount = true;
        // DEBUG/RE: render the raw per-node content tokens (the StdVector<u32> at element+0x350)
        // above the map name, for visually correlating tokens with known content types across maps.
        // See docs/re-findings.md §2.10. Off by default.
        public bool ShowContentTokens = false;
        // Render per-node content as in-game icons (icons\<basename>.png) instead of / alongside text.
        // Content without a loaded icon still falls back to its text name. See docs/re-findings.md §2.10.3.
        public bool ShowContentIcons = false;
        // Height (px, before UI scaling) of a content icon drawn above a node.
        public float ContentIconSize = 32f;
        // Pixel nudge (before UI scaling) applied to a content icon's position above a node.
        public Vector2 ContentIconOffset = Vector2.Zero;
        // DEBUG/RE: draw the node's child-index (its number in the atlas-panel child list) as a small
        // badge to the LEFT of the map name, so a node called out by number is easy to find on-screen.
        public bool ShowNodeIndex = false;
        public bool ShowBiomeBorder = true;
        public float BiomeBorderThickness = 2.0f;

        // Uncharted Waters leylines: hovering a sea "ship" (the Uncharted Waters logbook button)
        // highlights the map nodes of its 16x16 atlas chunk — the exact set a logbook used there
        // reveals (the hidden maps are already assigned client-side) — as their connection graph,
        // drawn like "Show node connections" but thicker. See obsidian poe2/Atlas.md §"Sea / ships".
        public bool ShowUnchartedLeylines = false;
        public Vector4 UnchartedLeylineColor = new(0.35f, 0.85f, 1f, 0.75f);
        public float UnchartedLeylineThickness = 3.5f;

        // Draw an icon (icons\UnchartedShip.png; fallback marker when absent) at sea ships the
        // game does NOT currently render (deep fog), one per uncharted chunk — so upcoming
        // Uncharted Waters spots are visible before you sail close. Hovering the icon also
        // triggers the leyline highlight when that toggle is on.
        public bool ShowShipsInFog = false;
        public float ShipIconSize = 64f;   // slider range 32..96; out-of-range saves migrate back to 64

        // Per-map rating (0 = normal … 10 = terrible) drawn as a colored number pill to the RIGHT
        // of the map name. Ratings are keyed by the map's canonical ENGLISH display name (the
        // maps.json "name"), so one rating covers every internal id variant and the label itself
        // renders localized. Persisted in config/mapratings.json (seeded from json\mapratings.json
        // on first run), kept out of settings.txt via [JsonIgnore].
        public bool ShowMapRating = true;
        [JsonIgnore]
        public Dictionary<string, int> MapRatings = new();

        // "Show Ritual mods (on hover)" — PREDICTION (green), ONLY BEFORE the first node of the
        // Ritual atlas line is picked: hover an accessible map in line mode to preview the exact
        // Rite-mod chain that start would roll — fully reversed client-side roll (TinyMT32 seeded
        // by (lineId, committedCount, candIdx, modCount), weighted reservoir over
        // RitualAtlasLineMods; both mods of a two-mod node). Once the line has a start (pending
        // or committed) the planner window owns the route display and the green chain is
        // suppressed. The game's own blue committed-node mod text is never drawn (removed
        // 2026-07-07; the old separate ShowRitualMods toggle was folded in on 2026-07-06).
        // See obsidian poe2/Ritual.md.
        public bool ShowRitualPrediction = false;

        // RE/DEBUG: log the deterministic Rite-mod roll ground-truth (line id + committed/pending
        // grids + each line node's rolled mod text) to config/ritual_roll_log.jsonl, deduped. Used
        // to reverse the client-side roll so mods can be predicted before selection. Off by default.
        public bool LogRitualRolls = false;

        // "Head of the king Rewards" planner window, auto-shown while the ritual LINE mode (page mode 6)
        // is active: enumerates every possible chain from EVERY eligible start node (or from the
        // line's committed frontier once drawing started) with each node's predicted Rite mod,
        // filterable by desired rewards; selected chains draw a ray from the player to their start
        // plus a highlighted route with reward labels on the atlas. Needs ShowRitualPrediction's
        // data (pool/candidate table).
        public bool ShowRitualPlanner = true;

        // Persisted reward wish-list for the planner window: '|'-joined short reward labels picked
        // in the filter dropdown, OR-matched (a chain is shown when ANY selected reward is in it).
        public string RitualRewardFilter = string.Empty;

        // Per-reward weights (short label → weight, sparse: only nonzero saved) edited in the
        // table under the planner toggle. Planner routes are sorted by the summed weight of
        // their predicted rewards, highest first; negative pushes a route down. The initializer
        // is the shipped default ranking (roughly: value in Divines); ObjectCreationHandling
        // .Replace makes a saved dict REPLACE the defaults instead of merging, so a user who
        // zeroes a default-weighted reward doesn't get it resurrected on restart.
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Dictionary<string, int> RitualRewardWeights = new()
        {
            ["+25% Tribute"] = 10,
            ["+Favours"] = 25,
            ["+Free Reroll"] = 20,
            ["+Monster Packs"] = 25,
            ["-Reroll Cost"] = 15,
            ["Chaos Orbs x2"] = 50,
            ["Chaos Orbs x4"] = 100,
            ["Chaos Orbs x8"] = 200,
            ["Greater Chaos Orbs"] = 50,
            ["Greater Exalted Orbs"] = 5,
            ["Divine Orb x1"] = 200,
            ["Divine Orbs x2"] = 400,
            ["Divine Orbs x5"] = 1000,
            ["Orbs of Annulment"] = 300,
            ["Perfect Orbs of Transmutation"] = 20,
            ["Perfect Orbs of Augmentation"] = 20,
            ["Perfect Regal Orbs"] = 200,
            ["Perfect Exalted Orbs"] = 1000,
            ["Perfect Chaos Orbs"] = 3000,
            ["Alpha's Howl"] = 100,
            ["Astramentis"] = 100,
            ["Defiance of Destiny"] = 50,
            ["Dream Fragments"] = 100,
            ["Headhunter"] = 25000,
            ["Kalandra's Touch"] = 1500,
            ["Mageblood"] = 10000,
            ["Original Sin"] = 50,
            ["Queen of the Forest"] = 500,
            ["Yoke of Suffering"] = 200,
            ["Very Rare Unique"] = 200,
            ["Omen: Whittling"] = 500,
            ["Omen: the Blessed"] = 500,
            ["Omen: Sanctification"] = 500,
            ["Omen: Sinistral Annulment"] = 500,
            ["Omen: Sinistral Crystallisation"] = 500,
            ["Omen: Sinistral Erasure"] = 500,
            ["Omen: Dextral Annulment"] = 500,
            ["Omen: Dextral Erasure"] = 500,
        };

        // Draw the atlas connection graph: a faint line along every edge between adjacent map nodes
        // (from the panel's edge list at panel+0x5A8), rendered under labels/routes on ChannelGrid.
        public bool ShowAtlasGraph = false;
        public Vector4 AtlasGraphLineColor = new(0.5f, 0.5f, 0.55f, 0.5f);
        public float AtlasGraphThickness = 1.5f;

        public bool RouteLinesThroughNodes = true;
        public float PathLineThickness = 1f;
        // Gap between the directional chevrons drawn along a route, as a multiple of the chevron size
        // (higher = more spread out).
        public float RouteArrowSpacing = 8f;

        public float BaseWidth = 1920f;
        public float BaseHeight = 1080f;
        public Vector2 AnchorNudge = new(0f, 28f);
        public float ScaleMultiplier = 1.0f;

        // Persisted in its own config/mapgroups.json (loaded/saved by the plugin), not settings.txt.
        // Seeded with the default set of map styles, used when no mapgroups.json exists yet.
        [JsonIgnore]
        public List<MapGroupSettings> MapGroups = BuildDefaultMapGroups();
        public string GroupNameInput = string.Empty;

        public Dictionary<string, ContentOverride> ContentOverrides = [];
        public Dictionary<byte, ContentOverride> BiomeOverrides = [];

        // Map Content route groups: user-defined sets of content types. For each content type a route
        // line is drawn from the accessible frontier to the nearest node carrying it (color/thickness/
        // hop-limit per entry). Persisted in settings.txt. See docs/re-findings.md §2.10.5.
        public List<ContentGroupSettings> ContentGroups = [];
        public string ContentGroupNameInput = string.Empty;

        // Default map styles, applied on a fresh install (no config/mapgroups.json). Once that file
        // exists the plugin loads it instead, so user edits/removals persist.
        private static List<MapGroupSettings> BuildDefaultMapGroups() => new()
        {
            new MapGroupSettings("Expedition unique", new(1f, 1f, 1f, 0.85f), new(0f, 0.03187251f, 1f, 1f))
                { Maps = { "Moor of Fallen Skies" } },
            new MapGroupSettings("Expedition bosses", new(1f, 1f, 1f, 0.85f), new(0.06374502f, 0f, 1f, 1f))
                { Maps = { "Sprawling Jungle", "Secluded Temple", "Obscure Island", "Mournful Cliffside" } },
            new MapGroupSettings("Unique maps low tier", new(0f, 0f, 0f, 0.85f), new(0.9760956f, 0.45393923f, 0.019444108f, 1f))
                { Maps = { "The Fractured Lake", "The Ezomyte Megaliths", "Merchant's Campsite", "Jado's Campsite",
                           "Moment of Zen", "The Voyage", "The Silent Cave", "Vaults of Kamasa", "The Viridian Wildwood" } },
            new MapGroupSettings("Unique maps top tier", new(0.80876493f, 0.34799448f, 0f, 0.85f), new(0.9163346f, 0.9163346f, 0.9163346f, 1f))
                { Maps = { "Castaway", "Untainted Paradise" } },
            new MapGroupSettings("Citadels", new(0.3851442f, 0.38499388f, 0.3944223f, 0.85f), new(1f, 0.9561753f, 0f, 1f))
                { Maps = { "The Copper Citadel", "The Iron Citadel", "The Stone Citadel" } },
            new MapGroupSettings("Halls", new(0.38431373f, 0.38431373f, 0.39607844f, 0.8509804f), new(1f, 0.95686275f, 0f, 1f))
                { Maps = { "The Matriarch Halls", "The Patriarch Halls" } },
            new MapGroupSettings("Anomaly maps", new(0.056364175f, 0.21115535f, 0.07979868f, 0.85f), new(1f, 1f, 1f, 1f))
                { Maps = { "The Jade Isles", "Sealed Vault", "Sacred Reservoir", "Derelict Mansion" } },
        };
    }

    public class MapGroupSettings(string name, Vector4 backgroundColor, Vector4 fontColor)
    {
        public string Name = name;
        public Vector4 BackgroundColor = backgroundColor;
        public Vector4 FontColor = fontColor;
        public List<string> Maps = [];
        public string MapNameInput = string.Empty;
    }

    // A named group of content-route entries. DrawPaths is the group master switch: when off, no
    // entry in the group draws a route, but each entry keeps its own independent DrawPath flag.
    public class ContentGroupSettings
    {
        public string Name { get; set; } = string.Empty;
        public bool DrawPaths { get; set; } = true;
        public List<ContentRouteEntry> Contents { get; set; } = [];
        // Built-in group: can't be deleted and its content list is fixed (the preset). Per-entry
        // colour/hops/draw toggle and the group master toggle stay editable.
        public bool Locked { get; set; } = false;
        // Group-level line thickness, shown under "Draw paths". Used (for all entries) by the built-in
        // group instead of per-entry thickness; user groups keep their per-entry thickness.
        public float LineThickness { get; set; } = 1f;
    }

    // One content type inside a group: routes to the nearest node carrying ContentName (canonical
    // English name, the mapcontent.json key). MaxHops 0 = unlimited; >0 suppresses longer routes.
    public class ContentRouteEntry
    {
        public string ContentName { get; set; } = string.Empty;
        public Vector4 LineColor { get; set; } = new(1f, 0.85f, 0.2f, 1f);
        public float LineThickness { get; set; } = 1f;
        public bool DrawPath { get; set; } = true;
        public int MaxHops { get; set; } = 0;
        // Optional map-based matcher used by the built-in group: "tag:<tag>" or "type:<type>" matches
        // by maps.json classification instead of by node content. Null/empty = match by ContentName.
        public string Match { get; set; } = null;
    }

    public class ContentOverride
    {
        public Vector4? BackgroundColor { get; set; }
        public Vector4? BorderColor { get; set; }
        public Vector4? FontColor { get; set; }
        public bool? Show { get; set; }
        public string Abbrev { get; set; }
    }
}