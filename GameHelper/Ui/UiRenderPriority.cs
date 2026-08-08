// <copyright file="UiRenderPriority.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Ui
{
    /// <summary>
    ///     Defines coroutine priorities for ImGui render passes.
    /// </summary>
    internal static class UiRenderPriority
    {
        /// <summary>
        ///     Core management windows render after plugin UI. Coroutine advances higher
        ///     priorities first, so the minimum value is the final OnRender pass.
        /// </summary>
        internal const int CoreWindows = int.MinValue;
    }
}
