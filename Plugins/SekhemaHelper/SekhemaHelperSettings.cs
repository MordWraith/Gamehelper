using GameHelper.Plugin;
using System.Collections.Generic;
using System.Numerics;

namespace SekhemaHelper
{
    public sealed class SekhemaHelperSettings : IPSettings
    {
        public string CurrentProfile = "Default";
        public Dictionary<string, ProfileContent> Profiles = new()
        {
            ["Default"] = ProfileContent.CreateDefaultProfile(),
            ["No-Hit"] = ProfileContent.CreateNoHitProfile(),
        };

        public Vector4 BestPathColor = new(0.2f, 1f, 0.2f, 1f);
        public Vector4 TextColor = new(1f, 1f, 1f, 1f);
        public Vector4 BackgroundColor = new(0f, 0f, 0f, 0.75f);
        public float FrameThickness = 4f;

        public bool DrawBestPath = true;
        public bool DebugEnable = false;

        // DEBUG ONLY (requires DebugEnable): draw EVERY room of the floor (incl. UI-hidden ones) at its
        // real world position on the large map, labelled with type + reward, read straight from the
        // client's terrain-generation data (FloorReader: SanctumPlugin picks + placement records). Used
        // to verify the whole-floor read + (next) the door-graph against the Trial map screenshot.
        public bool DebugDrawFloorPlots = false;

        // --- Resource-aware room recommendations (use live Sacred Water / Honour readouts) ---
        // Don't recommend the Merchant reward room while current Sacred Water is BELOW this threshold:
        // with nothing to spend the detour isn't worth it. Skipped when water is unknown (not in a Trial
        // / not yet read). Off = Merchant always weighted by its profile value.
        public bool SuppressMerchantLowWater = true;
        public int MerchantWaterThreshold = 250;        // 100..1000
        // Don't recommend honour-SHRINE reward rooms while current Honour is ABOVE this percentage:
        // already topped up, so the restore is wasted. Fountains are NOT affected (they restore Sacred
        // Water, not Honour). Skipped when honour is unknown. Off = shrines always weighted normally.
        public bool SuppressHonourRestoreHighPct = true;
        public int HonourRestoreThresholdPct = 80;      // 30..100

        // --- "Death crystal" (HourglassLethal) collection route, drawn in-world like Radar ---
        public bool DrawHazardRoute = true;
        public Vector4 HazardRouteColor = new(1f, 0.85f, 0.2f, 1f);
        public Vector4 HazardMarkerColor = new(1f, 0.3f, 0.3f, 1f);
        public float HazardRouteThickness = 1.5f;
        public float HazardMarkerRadius = 9f;
        // Ignore crystals farther than this many grid units from the player (0 = no limit).
        // Guards against the awake-entity list spanning adjacent rooms.
        public float HazardMaxGridDistance = 40f;
        // A Sekhema floor loads EVERY room's crystals into one area instance. Each room's crystals share
        // a contiguous entity-id block, so an id gap larger than this marks a room boundary; only the
        // player's id-group is routed. 0 = disabled (show all rooms). ~50 separates rooms (intra-room
        // gaps are 1; cross-room gaps are hundreds).
        public int HazardIdGroupGap = 10;
        // Only show the route while the player is inside the crystal room: the player must be within the
        // room's crystal bounding box expanded by this many grid units. Keeps the route from appearing
        // when the player stands in an adjacent (non-crystal) room next to a crystal room.
        public float HazardRoomMargin = 30f;
        // Route along walkable terrain (A* over the area's walkability grid, like Radar) instead of
        // straight lines between crystals. Off = direct lines.
        public bool HazardWalkableRoute = true;
        // DEBUG ONLY (requires DebugEnable): force the route through EXACTLY these crystal entity ids
        // (comma/space separated), ignoring active/collected state and the room filter. Lets a specific
        // crystal set be reproduced to investigate a routing bug. Empty = normal behaviour.
        public string HazardDebugCrystalIds = string.Empty;
        // DEBUG ONLY (requires DebugEnable): paint the game's walkability grid around the player on the
        // large map (green = walkable). Shows WHY an A* leg falls back to a straight line — usually the
        // player stands on a cell the grid marks non-walkable, with connected floor a gap away.
        public bool HazardDebugDrawWalkable = false;
        // Half-size (grid units) of the square region painted by HazardDebugDrawWalkable.
        public float HazardDebugWalkableRadius = 150f;

        // --- Final-room chest priority (SEKHEMA_WIP §11.3/§11.5) ---
        // When on, reads MarakethSanctum chest entities, ranks them by the per-content priority below,
        // and marks the best ones on the large map within the per-tier KEY BUDGET (live Bronze/Silver/
        // Gold counts): for each tier it highlights the top-N chests where N = keys of that tier.
        public bool DrawChestPriority = true;
        public Vector4 ChestMarkerColor = new(0.3f, 1f, 0.45f, 0.95f);   // selected (openable best)
        public float ChestMarkerRadius = 6f;

        // Chest CONTENT priority as an ORDERED list (top = best), independent of tier. A chest's content
        // is the suffix of its metadata id (…/{Bronze|Silver|Gold}Chest{Content}{1|2|3}). Rank = position
        // in this list; content types not listed share the lowest priority (see ChestPriority.PriorityOf),
        // so a patch adding a type — or the low-value gear chests we deliberately don't rank — won't break.
        public List<string> ChestPriorityOrder = ChestPriority.DefaultOrder();

        // Content types the user has switched OFF (un-ticked in the priority table). A disabled type keeps
        // its place in ChestPriorityOrder (so it can be re-enabled without losing its rank) but is treated
        // as unranked — never marked. Empty by default = every listed type is tracked. To track only a few
        // types, disable the rest here. Case-insensitive by content name.
        public HashSet<string> ChestDisabledContent = ChestPriority.DefaultDisabled();

        // --- Room interactables on the large map (SEKHEMA_WIP §12) ---
        // Mark only ACTIVE hazard Portals and the un-activated Lever. Both expose a StateMachine whose +0x10
        // byte flips to 1 once used/closed/activated; once that's seen the marker is removed (and stays
        // removed for the area — the byte is transient and reverts when you walk away).
        public bool ShowPortals = true;
        public bool ShowLevers = true;
        public Vector4 PortalColor = new(1f, 0.3f, 0.3f, 0.9f);   // active portal (hazard)
        public Vector4 LeverColor = new(0.3f, 0.7f, 1f, 0.9f);    // un-activated lever (actionable)
        public float RoomObjectMarkerRadius = 8f;
    }
}
