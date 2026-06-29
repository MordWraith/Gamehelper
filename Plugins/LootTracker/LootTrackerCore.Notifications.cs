namespace LootTracker
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using ImGuiNET;

    // ── Pickup toasts ────────────────────────────────────────────────────────────────────────────
    // A short stack of fading notifications shown just above the map strip when an item is picked up
    // on a map. Pickups are detected in UpdateLiveInventory (LootTrackerCore.cs) by diffing each ~2 Hz
    // inventory snapshot against the previous one — a positive per-item delta is a fresh pickup. Only
    // priced pickups worth ≥ Settings.NotifyMinEx toast (keeps junk from spamming). At most 3 are
    // visible at once; extras queue and promote as visible ones expire. Same-item pickups within the
    // window merge into one toast (count += , timer resets) rather than stacking duplicate lines.
    public sealed partial class LootTrackerCore
    {
        private sealed class PickupToast
        {
            public string Label = string.Empty;
            public long Count;
            public double TotalEx;     // Σ value of this toast's count, in Exalted
            public DateTime ShownUtc;  // when it became visible (fade timer base); set on promotion
        }

        private const int MaxVisibleToasts = 3;
        private const int MaxPendingToasts = 30;   // hard cap so a loot explosion can't grow the queue unbounded
        private const float ToastFadeSec = 0.6f;   // trailing fade portion of a toast's lifetime

        private readonly List<PickupToast> activeToasts = new();
        private readonly Queue<PickupToast> pendingToasts = new();

        private void ClearPickupToasts()
        {
            this.activeToasts.Clear();
            this.pendingToasts.Clear();
        }

        // Diff a fresh snapshot against the previous one; a positive per-item delta is a pickup. Price it,
        // apply the value threshold, and enqueue a toast (merging same-item pickups).
        private void DetectPickups(Dictionary<string, long> snap)
        {
            if (this.prevSnapshot == null)
            {
                return;
            }

            foreach (var kv in snap)
            {
                this.prevSnapshot.TryGetValue(kv.Key, out var before);
                long gained = kv.Value - before;
                if (gained <= 0)
                {
                    continue;
                }

                // Only priced items qualify, and only at/above the value threshold. Unpriced pickups
                // (rares, unmapped bases) have no value to show and never toast.
                if (!this.TryPriceItem(kv.Key, out var unit, out var label) || unit <= 0)
                {
                    continue;
                }

                double total = unit * gained;
                if (total < this.Settings.NotifyMinEx)
                {
                    continue;
                }

                this.EnqueuePickup(label, gained, total);
            }
        }

        // Merge into an existing toast for the same item (refreshing its timer) if one is live; else add
        // a new toast — visible if there's a free slot, queued otherwise.
        private void EnqueuePickup(string label, long count, double total)
        {
            var now = DateTime.UtcNow;

            foreach (var t in this.activeToasts)
            {
                if (t.Label == label)
                {
                    t.Count += count;
                    t.TotalEx += total;
                    t.ShownUtc = now; // re-arm the fade so the merged pickup stays visible
                    return;
                }
            }

            foreach (var t in this.pendingToasts)
            {
                if (t.Label == label)
                {
                    t.Count += count;
                    t.TotalEx += total;
                    return;
                }
            }

            var toast = new PickupToast { Label = label, Count = count, TotalEx = total };
            if (this.activeToasts.Count < MaxVisibleToasts)
            {
                toast.ShownUtc = now;
                this.activeToasts.Add(toast);
            }
            else if (this.pendingToasts.Count < MaxPendingToasts)
            {
                this.pendingToasts.Enqueue(toast);
            }
        }

        // Expire finished toasts, promote queued ones into freed slots, then render the stack as a single
        // bottom-anchored window sitting just above the map strip (same side as the bars). Each row fades
        // independently over its last ToastFadeSec; the window background tracks the brightest row.
        private void DrawPickupToasts()
        {
            if (this.activeToasts.Count == 0 && this.pendingToasts.Count == 0)
            {
                return;
            }

            if (IsLargePanelOpen())
            {
                return;
            }

            var now = DateTime.UtcNow;
            float life = Math.Clamp(this.Settings.NotifyDurationSec, 1f, 6f);
            this.activeToasts.RemoveAll(t => (now - t.ShownUtc).TotalSeconds >= life);
            while (this.activeToasts.Count < MaxVisibleToasts && this.pendingToasts.Count > 0)
            {
                var t = this.pendingToasts.Dequeue();
                t.ShownUtc = now;
                this.activeToasts.Add(t);
            }

            if (this.activeToasts.Count == 0)
            {
                return;
            }

            if (!this.TryGetExperienceBarRect(out var xpPos, out var xpSize))
            {
                return;
            }

            float s = this.UiScaleFactor();

            // Anchor the stack's bottom just above where the map strip sits (XP bar top − BarBottomOffset),
            // clearing the strip's own ~1 text-line height. Same side/X as the bars.
            float stripClear = (ImGui.GetTextLineHeightWithSpacing() * s) + 14f;
            float anchorX = this.Settings.BarOnRight ? xpPos.X + xpSize.X : xpPos.X;
            float anchorY = xpPos.Y - this.Settings.BarBottomOffset - stripClear;
            var pivot = new Vector2(this.Settings.BarOnRight ? 1f : 0f, 1f);

            float maxAlpha = 0f;
            foreach (var t in this.activeToasts)
            {
                maxAlpha = Math.Max(maxAlpha, ToastAlpha(t, now, life));
            }

            ImGui.SetNextWindowPos(new Vector2(anchorX, anchorY), ImGuiCond.Always, pivot);
            ImGui.SetNextWindowBgAlpha(Math.Clamp(this.Settings.BarOpacity, 0f, 1f) * maxAlpha);
            const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs |
                ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoMove;
            if (!ImGui.Begin("##loottracker_toasts", flags))
            {
                ImGui.End();
                return;
            }

            ImGui.SetWindowFontScale(s);

            var rate = this.priceCache.DivineToExaltedRate;
            bool divineOnly = this.DivineOnly;

            // Oldest on top; newest at the bottom (closest to the strip). Bottom-pivot auto-resize grows
            // the window upward as rows are added, so the stack visually rises off the strip.
            foreach (var t in this.activeToasts)
            {
                float a = ToastAlpha(t, now, life);

                // [coin] Name xN  — coin matches the value's currency (Divine in divine-only mode).
                if (this.DrawInlineIconAlpha(divineOnly ? "Divine" : "Exalt", a))
                {
                    ImGui.SameLine(0f, 5f);
                }

                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.92f, 0.92f, 0.92f, a));
                ImGui.TextUnformatted(t.Count > 1 ? $"{t.Label} x{t.Count}" : t.Label);
                ImGui.PopStyleColor();

                ImGui.SameLine(0f, 10f * s);
                if (divineOnly)
                {
                    // +D div only (fractional, e.g. +0.5 div) — Exalted hidden by user choice.
                    ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, a), $"+{t.TotalEx / rate:0.##} div");
                }
                else
                {
                    // +V ex (+D div)
                    ImGui.TextColored(new Vector4(GreenCol.X, GreenCol.Y, GreenCol.Z, a), $"+{t.TotalEx:0.#} ex");
                    if (rate > 0 && t.TotalEx >= rate * 0.05)
                    {
                        ImGui.SameLine(0f, 6f);
                        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, a), $"({t.TotalEx / rate:0.00} div)");
                    }
                }
            }

            ImGui.End();
        }

        private static float ToastAlpha(PickupToast t, DateTime now, float life)
        {
            float age = (float)(now - t.ShownUtc).TotalSeconds;
            if (age <= life - ToastFadeSec)
            {
                return 1f;
            }

            return Math.Clamp((life - age) / ToastFadeSec, 0f, 1f);
        }

        // Inline icon variant that honours an alpha (for the fade). Mirrors DrawInlineIcon but tints the
        // image; returns false (drawing nothing) when the icon isn't loaded so callers skip the spacing.
        private bool DrawInlineIconAlpha(string key, float alpha)
        {
            if (!this.iconHandles.TryGetValue(key, out var handle) || handle == IntPtr.Zero)
            {
                return false;
            }

            var sz = ImGui.GetTextLineHeight();
            ImGui.Image(handle, new Vector2(sz, sz), Vector2.Zero, Vector2.One, new Vector4(1f, 1f, 1f, alpha));
            return true;
        }
    }
}
