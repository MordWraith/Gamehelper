// <copyright file="RitualHelperSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace RitualHelper
{
    using System.Numerics;
    using GameHelper.Plugin;

    /// <summary>
    /// <see cref="RitualHelper"/> plugin settings class.
    /// </summary>
    public sealed class RitualHelperSettings : IPSettings
    {
        /// <summary>Price source: <see cref="PoeNinjaPriceFetcher.SourcePoeNinja"/> or <see cref="PoeNinjaPriceFetcher.SourcePoe2Scout"/>.</summary>
        public int PriceSource = PoeNinjaPriceFetcher.SourcePoe2Scout;

        /// <summary>PoE2 league name for price lookups.</summary>
        public string League = "Runes of Aldur";

        /// <summary>Automatic price refresh interval in minutes.</summary>
        public int RefreshIntervalMin = 5;

        /// <summary>Display currency: 0 = Divine, 1 = Exalted, 2 = Chaos.</summary>
        public int DisplayCurrency = 1;

        /// <summary>Show ritual item prices on the Favours window.</summary>
        public bool ShowOverlay = true;

        /// <summary>Play a sound when an item exceeds the alert threshold.</summary>
        public bool PlayValueAlert = true;

        /// <summary>Minimum value in Divine to trigger the alert sound.</summary>
        public float AlertMinDivine = 1f;

        /// <summary>Alert sound: 0=Asterisk, 1=Exclamation, 2=Hand, 3=Question, 4=Beep.</summary>
        public int AlertSound = 0;

        /// <summary>Debug mode (shows all inventories).</summary>
        public bool DebugMode = false;

        /// <summary>Diagnose ritual pricing: label every visible tile with the resolved name / rarity
        /// and flag items that produced no price, so you can see which stage failed.</summary>
        public bool DiagnosePricing = false;

        /// <summary>Force the signature BFS fallback for locating the ritual window, bypassing the fast
        /// index chain. For testing the fallback (normally it only engages when the index chain breaks).</summary>
        public bool ForceBfsFallback = false;

        /// <summary>Font scale factor for price labels.</summary>
        public float PriceFontScale = 1.025f;

        /// <summary>Horizontal offset from the bottom-left corner of each item.</summary>
        public float PriceOffsetX = 5f;

        /// <summary>Vertical offset from the bottom-left corner of each item.</summary>
        public float PriceOffsetY = -5f;

        /// <summary>Price value text color (RGBA, 0-1). Default ≈ RGB(255, 235, 140).</summary>
        public Vector4 PriceTextColor = new Vector4(1f, 235f / 255f, 140f / 255f, 1f);

        /// <summary>Hide price labels for items below this value in Exalted orbs. Set to 0 to show all prices.</summary>
        public float MinDisplayExalted = 50f;
    }
}
