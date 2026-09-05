// <copyright file="RitualWispAlertSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace RitualWispAlert
{
    using System.Numerics;
    using GameHelper.Plugin;

    /// <summary>Settings for the Ritual wisp range indicator.</summary>
    public sealed class RitualWispAlertSettings : IPSettings
    {
        /// <summary>Draw the range circle around Ritual wisps.</summary>
        public bool EnableOverlay = true;

        /// <summary>Hide the circle while the game is unfocused or paused.</summary>
        public bool HideWhenGameUnfocusedOrPaused = false;

        /// <summary>Circle radius in metres.</summary>
        public float RadiusMeters = 3f;

        /// <summary>Circle line thickness in pixels.</summary>
        public float Thickness = 2f;

        /// <summary>Circle color while the player is inside its range.</summary>
        public Vector4 InsideColor = new(0f, 1f, 0f, 1f);

        /// <summary>Circle color while the player is outside its range.</summary>
        public Vector4 OutsideColor = new(1f, 0.9f, 0f, 1f);

        /// <summary>World-space X adjustment for the circle center.</summary>
        public float OffsetX;

        /// <summary>World-space Y adjustment for the circle center.</summary>
        public float OffsetY;

        /// <summary>World-space height adjustment for the circle.</summary>
        public float OffsetZ;
    }
}
