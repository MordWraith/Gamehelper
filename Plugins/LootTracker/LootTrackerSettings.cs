namespace LootTracker
{
    using System;
    using GameHelper.Plugin;

    public sealed class LootTrackerSettings : IPSettings
    {
        // How many completed-map rows to keep in the session history (table + memory); oldest dropped past this.
        public int HistorySize = 50;

        // How many finished sessions to keep on disk (config/sessions); oldest deleted past this.
        public int MaxSessions = 30;

        // poe.ninja PoE2 league slug (spaces become '+'). Update each league launch. Shared default
        // with RunecraftHelper as of 2026-06.
        public string League = "Runes of Aldur";

        // How long cached prices stay valid before a re-fetch (minutes; UI slider 5–60).
        public int CacheTtlMinutes = 60;

        // Last successful price sync (UTC). Zero = never fetched.
        public DateTime LastSyncUtc = DateTime.MinValue;

        // Pixels from the bottom of the game window to the map strip's bottom edge. Used only as a
        // fallback when the experience-bar element can't be located (otherwise the strip auto-anchors
        // just above it).
        public float BarBottomOffset = 5f;

        // Horizontal side for the map strip: true = right (default), false = left.
        public bool BarOnRight = true;

        // Background opacity of the map strip and the compact hideout bar (0 = transparent, 1 = opaque).
        public float BarOpacity = 0.55f;

        // Show the per-rarity monster-kill counts (Normal/Magic/Rare/Unique) on the map strip.
        public bool ShowKills = true;

        // Height (px) of the compact hideout bar.
        public float CompactHeight = 115f;

        // Requested width (px) of the compact hideout bar. The effective width is clamped to the
        // experience-bar width so the bar never spills past it; this is the cap the user dials in.
        public float CompactWidth = 730f;

        // Manual multiplier on top of the auto game-UI scale (DisplaySize.Y / 1600) applied to the
        // bars' font and fixed metrics, so the overlay shrinks/grows with the game HUD across
        // resolutions. 1.0 = pure auto scale; tune per setup if the auto factor isn't ideal.
        public float UiScale = 1.2f;

        // ── Pickup notifications (toasts above the map strip) ──
        // Show a brief toast (item name + value) when an item is picked up on a map. Off by default.
        public bool ShowPickupToasts = false;

        // Only toast a pickup whose poe.ninja value is at least this many Exalted. Unpriced pickups
        // (no listed value) never toast. Keeps everything but the worthwhile drops off the stack.
        public float NotifyMinEx = 20f;

        // How long each toast stays on screen before it fades out (seconds; the last ~0.6s is the fade).
        public float NotifyDurationSec = 2.5f;

        // Show all overlay values in Divine only: hide the Exalted figures (map strip, compact bar,
        // toasts) and show the Divine equivalent instead, including fractions (e.g. 0.5 div). Falls back
        // to Exalted while the Divine→Exalted rate is unknown.
        public bool ShowPricesInDivineOnly = false;
    }
}
