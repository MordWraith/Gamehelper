namespace LootTracker
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Text;
    using GameHelper;
    using GameHelper.RemoteObjects.UiElement;
    using ImGuiNET;

    public sealed partial class LootTrackerCore
    {
        // Game-UI scale factor applied to the bars' font and fixed metrics: the auto factor
        // (DisplaySize.Y / 1600 — the game's own UI height-scale base) times the manual UiScale knob,
        // so the overlay shrinks/grows with the HUD across resolutions instead of clipping its content.
        private float UiScaleFactor()
        {
            float auto = ImGui.GetIO().DisplaySize.Y / 1600f;
            return Math.Clamp(auto * this.Settings.UiScale, 0.5f, 3f);
        }

        // True when overlay values should be shown in Divine only: the user opted in AND a live
        // Divine→Exalted rate exists to convert by. While the rate is unknown we keep Exalted so the
        // numbers never blank out.
        private bool DivineOnly =>
            this.Settings.ShowPricesInDivineOnly && this.priceCache.DivineToExaltedRate > 0;

        // ── Map strip ────────────────────────────────────────────────────
        // A slim, click-through line pinned to the bottom of the game window (right side by default),
        // shown while on a map so it doesn't block the run: "<map> · <timer> · <+X ex>". Sits just
        // above the experience bar via a manual offset (BarBottomOffset) — GameHelper doesn't expose
        // the XP element's scaled screen rect to plugins, so we anchor to the viewport bottom edge.
        private void DrawMapBar()
        {
            // Anchor the strip's bottom edge to the experience-bar element (its real scaled screen rect,
            // so it tracks resolution / UI scale), on the chosen side, then lift it by BarBottomOffset so
            // it clears the bar. Falls back to the viewport bottom if the element can't be resolved.
            float anchorX, anchorY;
            if (TryGetExperienceBarRect(out var xpPos, out var xpSize))
            {
                anchorY = xpPos.Y - this.Settings.BarBottomOffset;
                anchorX = this.Settings.BarOnRight ? xpPos.X + xpSize.X : xpPos.X;
            }
            else
            {
                var vp = ImGui.GetMainViewport();
                const float margin = 8f;
                anchorX = this.Settings.BarOnRight ? vp.Pos.X + vp.Size.X - margin : vp.Pos.X + margin;
                anchorY = vp.Pos.Y + vp.Size.Y - this.Settings.BarBottomOffset;
            }

            var pivot = new System.Numerics.Vector2(this.Settings.BarOnRight ? 1f : 0f, 1f);
            ImGui.SetNextWindowPos(new System.Numerics.Vector2(anchorX, anchorY), ImGuiCond.Always, pivot);
            ImGui.SetNextWindowBgAlpha(Math.Clamp(this.Settings.BarOpacity, 0f, 1f));
            const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs |
                ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoMove;
            if (!ImGui.Begin("##loottracker_bar", flags))
            {
                ImGui.End();
                return;
            }

            ImGui.SetWindowFontScale(this.UiScaleFactor());

            var gained = this.CurrentGainedLive();
            double ex = this.ValueOf(gained, out _, out _);
            string name = this.current?.Name ?? "—";
            var col = ex >= 0 ? GreenCol : RedCol;
            const float gap = 14f;   // space between segments
            const float pad = 5f;    // space between an icon and its value

            // [map] name
            if (this.DrawInlineIcon("Map")) ImGui.SameLine(0f, pad);
            ImGui.TextUnformatted(name);

            // [time] mm:ss
            ImGui.SameLine(0f, gap);
            if (this.DrawInlineIcon("Time")) ImGui.SameLine(0f, pad);
            ImGui.TextUnformatted(FormatDuration(this.CurrentLiveTime()));

            var rate = this.priceCache.DivineToExaltedRate;
            if (this.DivineOnly)
            {
                // +Y [divine] only — Exalted hidden by user choice.
                ImGui.SameLine(0f, gap);
                ImGui.TextColored(col, $"{ex / rate:+0.##;-0.##;0}");
                ImGui.SameLine(0f, pad);
                this.DrawInlineIcon("Divine");
            }
            else
            {
                // +X [exalt]
                ImGui.SameLine(0f, gap);
                ImGui.TextColored(col, $"{ex:+0;-0;0}");
                ImGui.SameLine(0f, pad);
                this.DrawInlineIcon("Exalt");

                // (+Y [divine])  — same profit converted to Divine, shown only when the rate is known
                if (rate > 0)
                {
                    var div = ex / rate;
                    ImGui.SameLine(0f, gap);
                    ImGui.TextColored(col, $"({div:+0.0;-0.0;0}");
                    ImGui.SameLine(0f, pad);
                    if (this.DrawInlineIcon("Divine")) ImGui.SameLine(0f, 1f);
                    ImGui.TextColored(col, ")");
                }
            }

            // <n>[normal] <n>[magic] <n>[rare] <n>[unique]  — monsters slain this run, by rarity.
            if (this.Settings.ShowKills && this.current != null)
            {
                var k = this.current.Kills;
                ImGui.SameLine(0f, gap);
                this.DrawKillStat("NormalMob", k[0]);
                ImGui.SameLine(0f, gap);
                this.DrawKillStat("MagicMob", k[1]);
                ImGui.SameLine(0f, gap);
                this.DrawKillStat("RareMob", k[2]);
                ImGui.SameLine(0f, gap);
                this.DrawKillStat("UniqueMob", k[3]);
            }

            ImGui.End();
        }

        // (Map-strip unpriced "+N?" hint intentionally omitted — too noisy while running.)

        // Renders "<count><icon>" inline (count then the rarity icon), the layout used for the
        // kill tallies on the strip. Falls back to count-only text when the icon isn't loaded.
        private void DrawKillStat(string iconKey, int count)
        {
            ImGui.TextUnformatted(count.ToString());
            if (this.iconHandles.TryGetValue(iconKey, out var handle) && handle != IntPtr.Zero)
            {
                ImGui.SameLine(0f, 4f);
                var s = ImGui.GetTextLineHeight();
                ImGui.Image(handle, new System.Numerics.Vector2(s, s));
            }
        }

        // ── Compact hideout bar ──────────────────────────────────────────
        // A session readout pinned into the empty band above the bottom HUD; the sole hideout/town UI.
        // Its bottom edge sits at the same viewport line as the map strip (BarBottomOffset up from the
        // window bottom) so it clears the HUD. Its width is the user-set CompactWidth but is always
        // clamped to the experience-bar width so it can't spill past it; once narrower than the bar it's
        // pinned to the chosen side of the XP bar via the same BarOnRight knob as the map strip. A pure
        // read-out (the New-session button lives in the plugin settings now).
        private void DrawCompactBar()
        {
            float h = Math.Clamp(this.Settings.CompactHeight, 60f, 400f);
            float reqW = Math.Max(this.Settings.CompactWidth, 200f);
            float x, y, w, pivotX;
            if (TryGetExperienceBarRect(out var xpPos, out var xpSize))
            {
                w = Math.Min(xpSize.X, reqW);
                y = xpPos.Y - this.Settings.BarBottomOffset; // bottom edge lifted off the XP bar
                // Capped narrower than the XP bar — pin it to the chosen side of the bar.
                x = this.Settings.BarOnRight ? xpPos.X + xpSize.X : xpPos.X;
                pivotX = this.Settings.BarOnRight ? 1f : 0f;
            }
            else
            {
                var vp = ImGui.GetMainViewport();
                const float margin = 8f;
                w = Math.Min(vp.Size.X - 220f, reqW);
                x = this.Settings.BarOnRight ? vp.Pos.X + vp.Size.X - margin : vp.Pos.X + margin;
                y = vp.Pos.Y + vp.Size.Y - this.Settings.BarBottomOffset;
                pivotX = this.Settings.BarOnRight ? 1f : 0f;
            }

            ImGui.SetNextWindowPos(new System.Numerics.Vector2(x, y), ImGuiCond.Always, new System.Numerics.Vector2(pivotX, 1f));
            ImGui.SetNextWindowSize(new System.Numerics.Vector2(w, h), ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(Math.Clamp(this.Settings.BarOpacity, 0f, 1f));
            const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoNav |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar;
            if (!ImGui.Begin("##loottracker_compact", flags))
            {
                ImGui.End();
                return;
            }

            float s = this.UiScaleFactor();
            ImGui.SetWindowFontScale(s);

            this.SessionTotals(out var totalActive, out var totalEx);
            int maps = this.completed.Count;
            double perHour = totalActive.TotalHours > 0 ? totalEx / totalActive.TotalHours : 0;
            TimeSpan avgTime = maps > 0 ? totalActive / maps : TimeSpan.Zero;
            double avgProfit = maps > 0 ? totalEx / maps : 0;
            var rate = this.priceCache.DivineToExaltedRate;
            float colGap = 28f * s;

            // Column 1 — per-map aggregates (with icons). The New-session button now lives in the
            // plugin settings, so the bar is a pure read-out and the left band is freed for the table.
            ImGui.BeginGroup();
            if (this.DrawInlineIcon("Map")) ImGui.SameLine(0f, 5f);
            ImGui.Text($"Maps: {maps}");
            if (this.DrawInlineIcon("Time")) ImGui.SameLine(0f, 5f);
            ImGui.Text($"AVG Time: {FormatDuration(avgTime)}");
            if (this.DivineOnly)
            {
                if (this.DrawInlineIcon("Divine")) ImGui.SameLine(0f, 5f);
                ImGui.Text($"AVG Profit: {avgProfit / rate:0.##} Div");
            }
            else
            {
                if (this.DrawInlineIcon("Exalt")) ImGui.SameLine(0f, 5f);
                ImGui.Text($"AVG Profit: {avgProfit:0} Ex");
            }

            ImGui.EndGroup();

            // Column 2 — totals in Divine + the session clock (kept narrow so the table gets the width).
            ImGui.SameLine(0f, colGap);
            ImGui.BeginGroup();
            var totalCol = totalEx >= 0 ? GreenCol : RedCol;
            var hourCol = perHour >= 0 ? GreenCol : RedCol;
            string totalDiv = rate > 0 ? $"{totalEx / rate:0.0}" : "—";
            string hourDiv = rate > 0 ? $"{perHour / rate:0.0}" : "—";

            // Total [div] <n>
            ImGui.TextColored(totalCol, "Total");
            ImGui.SameLine(0f, 5f);
            if (this.DrawInlineIcon("Divine")) ImGui.SameLine(0f, 4f);
            ImGui.TextColored(totalCol, totalDiv);

            // [div] <n> / hour
            if (this.DrawInlineIcon("Divine")) ImGui.SameLine(0f, 4f);
            ImGui.TextColored(hourCol, $"{hourDiv} / hour");

            // Session: <timer> (moved here from the old controls column so each band is 3 rows).
            if (this.DrawInlineIcon("Time")) ImGui.SameLine(0f, 5f);
            ImGui.Text($"Session: {FormatDuration(DateTime.UtcNow - this.sessionStartUtc)}");
            ImGui.EndGroup();

            // Column 3 — completed-map table (newest first), filling the remaining width/height.
            ImGui.SameLine(0f, colGap);
            ImGui.BeginGroup();
            if (ImGui.BeginTable("compact_runs", 3,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                    new System.Numerics.Vector2(0f, h - (24f * s))))
            {
                ImGui.TableSetupColumn("Map");
                ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 60f * s);
                ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.WidthFixed, 90f * s);
                ImGui.TableHeadersRow();
                for (int i = this.completed.Count - 1; i >= 0; i--)
                {
                    var r = this.completed[i];
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(r.Name);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatDuration(r.ActiveTime));
                    ImGui.TableNextColumn();
                    double ex = this.ValueOf(r.Gained, out _, out _);
                    ImGui.TextColored(ex >= 0 ? GreenCol : RedCol, this.DivineOnly
                        ? $"{ex / rate:+0.##;-0.##;0} div"
                        : $"{ex:+0.0;-0.0;0} ex");
                }

                ImGui.EndTable();
            }

            ImGui.EndGroup();

            ImGui.End();
        }

        // Draws a loaded icon (icons\<key>.png) inline at the current text-line height so it lines up
        // with adjacent text. Returns false (drawing nothing) if the icon isn't loaded, so callers can
        // skip the trailing spacing.
        private bool DrawInlineIcon(string key)
        {
            if (!this.iconHandles.TryGetValue(key, out var handle) || handle == IntPtr.Zero)
            {
                return false;
            }

            var s = ImGui.GetTextLineHeight();
            ImGui.Image(handle, new System.Numerics.Vector2(s, s));
            return true;
        }

        // ── Soft binding to fork-only core members ───────────────────────
        // LootTracker may be loaded against a stock GameHelper whose ImportantUiElements lacks the
        // fork's additions (ExperienceBar, IsAnyLargePanelOpen). Binding to them by name via reflection
        // — rather than a direct member access — means a missing member degrades gracefully (the bar
        // anchors to the viewport instead of the XP bar; it no longer auto-hides on the Atlas) instead
        // of throwing MissingMethodException when the overlay method is JIT-compiled, which would blank
        // the whole plugin. The PropertyInfos are resolved once and cached.
        private static bool gameUiReflectionReady;
        private static PropertyInfo? expBarProp;
        private static PropertyInfo? largePanelProp;
        private static PropertyInfo? atlasProp;
        private static PropertyInfo? worldMapProp;

        private static void EnsureGameUiReflection(object gameUi)
        {
            if (gameUiReflectionReady)
            {
                return;
            }

            var t = gameUi.GetType();
            expBarProp = t.GetProperty("ExperienceBar");
            largePanelProp = t.GetProperty("IsAnyLargePanelOpen");
            // The endgame Atlas is a separate panel from the world-travel map and isn't always folded
            // into IsAnyLargePanelOpen by every fork, so we also probe these element members directly.
            atlasProp = t.GetProperty("Atlas");
            worldMapProp = t.GetProperty("WorldMapPanel");
            gameUiReflectionReady = true;
        }

        // True when a large blocking panel covers the screen (inventory / passive tree / world map /
        // endgame Atlas). We OR the core's own IsAnyLargePanelOpen (when present) with a direct check of
        // the Atlas / WorldMapPanel elements, because not every fork folds the endgame Atlas into that
        // flag. Members absent on the running core are simply skipped (compact bar just stays up).
        private static bool IsLargePanelOpen()
        {
            var gameUi = Core.States.InGameStateObject?.GameUi;
            if (gameUi == null)
            {
                return false;
            }

            EnsureGameUiReflection(gameUi);
            if (largePanelProp?.GetValue(gameUi) is bool open && open)
            {
                return true;
            }

            return IsElementVisible(atlasProp, gameUi) || IsElementVisible(worldMapProp, gameUi);
        }

        // Reads a UiElementBase-typed GameUi property by reflection and returns its IsVisible, false if
        // the member is absent / null / not a UiElementBase on the running core.
        private static bool IsElementVisible(PropertyInfo? prop, object gameUi)
        {
            return prop?.GetValue(gameUi) is UiElementBase el && el.Address != IntPtr.Zero && el.IsVisible;
        }

        // Scaled screen rect of the experience bar. Primary path resolves it by Flags fingerprint by
        // walking GameUi's UI tree ourselves (fork-independent — see LootTrackerCore.UiTree.cs); the
        // reflection path is a fallback for a fork that exposes GameUi.ExperienceBar directly.
        private bool TryGetExperienceBarRect(out System.Numerics.Vector2 pos, out System.Numerics.Vector2 size)
        {
            if (this.TryGetExperienceBarRectByFp(out pos, out size))
            {
                return true;
            }

            return TryGetExperienceBarRectByReflection(out pos, out size);
        }

        // Fallback: a fork whose ImportantUiElements exposes ExperienceBar (its UiElementBase already
        // carries the scaled rect). Absent on most forks, in which case this returns false.
        private static bool TryGetExperienceBarRectByReflection(out System.Numerics.Vector2 pos, out System.Numerics.Vector2 size)
        {
            pos = default;
            size = default;
            var gameUi = Core.States.InGameStateObject?.GameUi;
            if (gameUi == null)
            {
                return false;
            }

            EnsureGameUiReflection(gameUi);
            if (expBarProp?.GetValue(gameUi) is not UiElementBase xp ||
                xp.Address == IntPtr.Zero || !xp.IsVisible)
            {
                return false;
            }

            pos = xp.Position;
            size = xp.Size;
            return size.X > 1f && size.Y > 1f;
        }

        private static readonly System.Numerics.Vector4 GreenCol = new(0.5f, 0.9f, 0.5f, 1f);
        private static readonly System.Numerics.Vector4 RedCol = new(0.9f, 0.5f, 0.5f, 1f);

        // ── Snapshot / delta ─────────────────────────────────────────────
        // Aggregated MainInventory1 contents: itemPath -> total count (stack-aware). False if unreadable.
        private bool TrySnapshotInventory(out Dictionary<string, long> snap)
        {
            snap = new Dictionary<string, long>(StringComparer.Ordinal);
            if (!this.TryReadMainInventory(out _, out _, out _, out var items))
            {
                return false;
            }

            foreach (var (path, count) in items)
            {
                snap.TryGetValue(path, out var c);
                snap[path] = c + count;
            }

            return true;
        }

        // now - baseline, per item path; only non-zero deltas are kept.
        private static Dictionary<string, long> Diff(Dictionary<string, long> now, Dictionary<string, long> baseline)
        {
            var d = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var kv in now)
            {
                baseline.TryGetValue(kv.Key, out var b);
                long delta = kv.Value - b;
                if (delta != 0) d[kv.Key] = delta;
            }

            foreach (var kv in baseline)
            {
                if (!now.ContainsKey(kv.Key)) d[kv.Key] = -kv.Value;
            }

            return d;
        }

        private static void MergeInto(Dictionary<string, long> acc, Dictionary<string, long> delta)
        {
            foreach (var kv in delta)
            {
                acc.TryGetValue(kv.Key, out var c);
                long v = c + kv.Value;
                if (v == 0) acc.Remove(kv.Key);
                else acc[kv.Key] = v;
            }
        }

        private static string LastSegment(string path)
        {
            int i = path.LastIndexOf('/');
            return i >= 0 && i < path.Length - 1 ? path[(i + 1)..] : path;
        }

        // ── Inventory raw read (self-contained; offsets documented in LootTrackerCore.cs) ──
        // Resolves MainInventory1 and returns the metadata path + stack count of every distinct item.
        private bool TryReadMainInventory(out IntPtr inventoryAddr, out int boxesX, out int boxesY,
            out List<(string Path, int Count)> items)
        {
            inventoryAddr = IntPtr.Zero;
            boxesX = 0;
            boxesY = 0;
            items = new List<(string, int)>();

            if (!this.EnsureProcess())
            {
                return false;
            }

            var serverData = Core.States.InGameStateObject.CurrentAreaInstance.ServerDataObject;
            if (serverData == null || serverData.Address == IntPtr.Zero)
            {
                return false;
            }

            // ServerData +0x48 -> vector<IntPtr>; element [0] is the player data block.
            if (!this.TryReadStdVector(serverData.Address + ServerDataPlayerVectorOffset, out var pdFirst, out _))
            {
                return false;
            }

            IntPtr playerData = this.ReadPtr(pdFirst);
            if (playerData == IntPtr.Zero)
            {
                return false;
            }

            // playerData +0x320 -> vector<InventoryArrayStruct> (stride 0x18).
            if (!this.TryReadStdVector(playerData + PlayerInventoriesVectorOffset, out var invFirst, out var invLast))
            {
                return false;
            }

            long count = ((long)invLast - (long)invFirst) / InventoryArrayStride;
            if (count <= 0 || count > 4096)
            {
                return false;
            }

            for (long i = 0; i < count; i++)
            {
                IntPtr entry = invFirst + (int)(i * InventoryArrayStride);
                int id = this.ReadInt(entry + InventoryArrayIdOffset);
                if (id == MainInventory1Id)
                {
                    inventoryAddr = this.ReadPtr(entry + InventoryArrayPtr0Offset);
                    break;
                }
            }

            if (inventoryAddr == IntPtr.Zero)
            {
                return false;
            }

            boxesX = this.ReadInt(inventoryAddr + InventoryTotalBoxesOffset);
            boxesY = this.ReadInt(inventoryAddr + InventoryTotalBoxesOffset + 4);

            // ItemList: vector<IntPtr> slot->invItemPtr mapping (length X*Y; Zero = empty; dups for big items).
            if (!this.TryReadStdVector(inventoryAddr + InventoryItemListOffset, out var itFirst, out var itLast))
            {
                return true; // empty inventory is still a valid read
            }

            long slots = ((long)itLast - (long)itFirst) / 8;
            if (slots <= 0 || slots > 4096)
            {
                return true;
            }

            var seen = new HashSet<long>();
            for (long i = 0; i < slots; i++)
            {
                IntPtr invItemPtr = this.ReadPtr(itFirst + (int)(i * 8));
                if (invItemPtr == IntPtr.Zero || !seen.Add(invItemPtr.ToInt64()))
                {
                    continue;
                }

                IntPtr entity = this.ReadPtr(invItemPtr + InventoryItemItemOffset);
                if (entity == IntPtr.Zero)
                {
                    continue;
                }

                IntPtr detailsPtr = this.ReadPtr(entity + EntityDetailsPtrOffset);
                if (detailsPtr == IntPtr.Zero)
                {
                    continue;
                }

                string path = this.ReadStdWString(detailsPtr + EntityDetailsNameOffset);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                this.ReadItemFacts(entity, detailsPtr, out int stack, out int rarity, out var renderArt);
                items.Add((BuildItemKey(rarity, path, renderArt), stack));
            }

            return true;
        }

        // Reads an item's Stack.Count, Mods.Rarity and (for uniques) its rendered-icon art id in a SINGLE
        // walk of the entity's component name→index map (cheaper than one walk per component, run for every
        // inventory item at up to ~2 Hz). Defaults: stack 1 (non-stackable), rarity 0 (Normal — also when
        // the item lacks the Mods component, true for stackable currency), renderArt "" (read only for
        // uniques, where the shared base metapath can't tell one unique from another — see RenderItemArtOffset).
        private void ReadItemFacts(IntPtr entity, IntPtr detailsPtr, out int stack, out int rarity, out string renderArt)
        {
            stack = 1;
            rarity = 0;
            renderArt = string.Empty;

            // entity +0x10 -> vector<IntPtr> of component addresses (indexed by the name->index map).
            if (!this.TryReadStdVector(entity + EntityComponentListOffset, out var compFirst, out var compLast))
            {
                return;
            }

            long compCount = ((long)compLast - (long)compFirst) / 8;
            if (compCount <= 0 || compCount > 256)
            {
                return;
            }

            // EntityDetails +0x28 -> ComponentLookUpStruct; +0x28 -> ComponentsNameAndIndex (StdBucket;
            // its Data vector holds {NamePtr@+0, Index@+8} records, stride 0x10).
            IntPtr lookup = this.ReadPtr(detailsPtr + EntityComponentLookupOffset);
            if (lookup == IntPtr.Zero)
            {
                return;
            }

            if (!this.TryReadStdVector(lookup + ComponentLookupBucketOffset, out var niFirst, out var niLast))
            {
                return;
            }

            long niCount = ((long)niLast - (long)niFirst) / ComponentNameIndexStride;
            if (niCount <= 0 || niCount > 256)
            {
                return;
            }

            int stackIndex = -1, modsIndex = -1, renderIndex = -1;
            for (long i = 0; i < niCount && (stackIndex < 0 || modsIndex < 0 || renderIndex < 0); i++)
            {
                IntPtr rec = niFirst + (int)(i * ComponentNameIndexStride);
                IntPtr namePtr = this.ReadPtr(rec);
                if (namePtr == IntPtr.Zero)
                {
                    continue;
                }

                var name = this.ReadCString(namePtr, 40);
                if (stackIndex < 0 && name == "Stack")
                {
                    stackIndex = this.ReadInt(rec + 8);
                }
                else if (modsIndex < 0 && name == "Mods")
                {
                    modsIndex = this.ReadInt(rec + 8);
                }
                else if (renderIndex < 0 && name == "RenderItem")
                {
                    renderIndex = this.ReadInt(rec + 8);
                }
            }

            if (stackIndex >= 0 && stackIndex < compCount)
            {
                IntPtr c = this.ReadPtr(compFirst + (int)(stackIndex * 8));
                if (c != IntPtr.Zero)
                {
                    int n = this.ReadInt(c + StackCountOffset);
                    if (n > 0) stack = n;
                }
            }

            if (modsIndex >= 0 && modsIndex < compCount)
            {
                IntPtr c = this.ReadPtr(compFirst + (int)(modsIndex * 8));
                if (c != IntPtr.Zero)
                {
                    int r = this.ReadInt(c + ModsRarityOffset);
                    if (r >= 0 && r <= 3) rarity = r;
                }
            }

            // Only uniques need the rendered icon (to tell apart uniques sharing one base metapath); skip
            // the extra wstring read for everything else.
            if (rarity == 3 && renderIndex >= 0 && renderIndex < compCount)
            {
                IntPtr c = this.ReadPtr(compFirst + (int)(renderIndex * 8));
                if (c != IntPtr.Zero)
                {
                    var ddsPath = this.ReadStdWString(c + RenderItemArtOffset);
                    renderArt = ArtIdFromDdsPath(ddsPath);
                }
            }
        }

        // "Art/2DItems/.../PrecursorTabletDeliriumUnique1.dds" → "PrecursorTabletDeliriumUnique1" (the
        // language-independent art id poe.ninja keys by). Returns "" for an empty/odd path.
        private static string ArtIdFromDdsPath(string ddsPath)
        {
            if (string.IsNullOrEmpty(ddsPath)) return string.Empty;
            var seg = LastSegment(ddsPath);
            int dot = seg.LastIndexOf('.');
            return dot > 0 ? seg[..dot] : seg;
        }

        private static string FormatDuration(TimeSpan t)
        {
            if (t.TotalHours >= 1)
            {
                return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
            }

            return $"{t.Minutes:D2}:{t.Seconds:D2}";
        }

        // ── Raw process-memory helpers (mirrors RunecraftHelperCore; self-contained) ──
        private bool EnsureProcess()
        {
            int pid = (int)Core.Process.Pid;
            if (pid == 0)
            {
                if (this.handlePid != 0) this.ResetHandle();
                return false;
            }

            if (pid == this.handlePid && this.processHandle != IntPtr.Zero) return true;

            this.ResetHandle();
            this.processHandle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
            if (this.processHandle == IntPtr.Zero) return false;
            this.handlePid = pid;
            return true;
        }

        private void ResetHandle()
        {
            if (this.processHandle != IntPtr.Zero)
            {
                CloseHandle(this.processHandle);
                this.processHandle = IntPtr.Zero;
            }

            this.handlePid = 0;
        }

        private IntPtr ReadPtr(IntPtr addr)
        {
            if (addr == IntPtr.Zero) return IntPtr.Zero;
            var buf = new byte[8];
            if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out _)) return IntPtr.Zero;
            long v = BitConverter.ToInt64(buf, 0);
            if (v < 0x10000 || v > 0x7FFFFFFFFFFF) return IntPtr.Zero;
            return (IntPtr)v;
        }

        private int ReadInt(IntPtr addr)
        {
            if (addr == IntPtr.Zero) return 0;
            var buf = new byte[4];
            if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out _)) return 0;
            return BitConverter.ToInt32(buf, 0);
        }

        private bool TryReadStdVector(IntPtr addr, out IntPtr first, out IntPtr last)
        {
            first = IntPtr.Zero;
            last = IntPtr.Zero;
            var buf = new byte[16];
            if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out _)) return false;
            first = (IntPtr)BitConverter.ToInt64(buf, 0);
            last = (IntPtr)BitConverter.ToInt64(buf, 8);
            if (first == IntPtr.Zero) return false;
            ulong f = (ulong)(long)first;
            if (f < 0x10000 || f > 0x7FFFFFFFFFFFul) return false;
            if ((long)last < (long)first) return false;
            return true;
        }

        // Null-terminated ASCII string (used for component names).
        private string ReadCString(IntPtr addr, int max)
        {
            if (addr == IntPtr.Zero) return string.Empty;
            var buf = new byte[max];
            if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out _)) return string.Empty;
            int n = Array.IndexOf(buf, (byte)0);
            if (n < 0) n = max;
            return Encoding.ASCII.GetString(buf, 0, n);
        }

        // MSVC std::wstring: buffer ptr at +0x00 (or 8 chars inline if cap < 8), length at +0x10, capacity at +0x18.
        private string ReadStdWString(IntPtr addr)
        {
            var buf = new byte[0x20];
            if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out _)) return string.Empty;

            int len = BitConverter.ToInt32(buf, 0x10);
            if (len <= 0 || len > 256) return string.Empty;
            int cap = BitConverter.ToInt32(buf, 0x18);
            if (cap < len) return string.Empty;

            if (cap < 8)
            {
                int byteLen = Math.Min(len * 2, 16);
                return Encoding.Unicode.GetString(buf, 0, byteLen);
            }

            long ptr = BitConverter.ToInt64(buf, 0);
            if (ptr < 0x10000 || ptr > 0x7FFFFFFFFFFF) return string.Empty;
            var outBuf = new byte[len * 2];
            if (!ReadProcessMemory(this.processHandle, (IntPtr)ptr, outBuf, (uint)outBuf.Length, out _)) return string.Empty;
            return Encoding.Unicode.GetString(outBuf);
        }

        // ── P/Invoke ─────────────────────────────────────────────────────
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint dwSize, out int lpNumberOfBytesRead);
    }
}
