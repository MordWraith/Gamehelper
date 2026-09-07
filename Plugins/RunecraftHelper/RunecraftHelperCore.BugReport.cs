namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Numerics;
    using System.Text;
    using GameHelper;
    using ImGuiNET;

    // "Copy for bug report" — the one-shot diagnostic behind the 8+ hole monolith reports.
    //
    // The design constraint is social, not technical: the reporter is a stranger who will run this
    // ONCE. Asking them to reproduce with one more field added is how a bug survives three releases,
    // and a big monolith is rare enough that they may never see another. So the report carries every
    // input the offer rule and the price lookup consume, plus the raw station bytes, in a single pass.
    //
    // The decisive part is the panel-vs-offered diff. A recipe ROW in the live Runeshape Combinations
    // panel exposes its Expedition2Recipes Id (row UiElement + RecipeRowBackPtrOffset → row +0x00),
    // which is language-independent and is exactly the offline catalog's key — so for every recipe the
    // GAME is offering we can re-run our own gate and name the one that rejected it. That turns "the
    // numbers disagree" into "gate X dropped row Y", which is a fix rather than another round of
    // questions.
    public sealed partial class RunecraftHelperCore
    {
        // Enough to cover every field we read (owner +0x10 … panel-open +0xB8) plus the listener
        // sub-object at +0xA0, so a report from a station whose layout MOVED still carries the
        // evidence needed to re-locate those fields offline.
        private const int BugReportStationBytes = 0xE0;
        private const int BugReportMaxOffers = 40;
        private const int BugReportMaxBigRows = 48;

        // A monolith is "big" (worth a report) from 8 holes up. See HasBigMonolith for why that is
        // detected two ways; the Monolith Debug window's own "Copy report" builds the identical text
        // with no condition at all, as the last resort.
        private const int BugReportBigHoles = 8;

        // Ids of catalog recipes needing 8+ holes; built on first use (the catalog loads lazily) and
        // never invalidated because the catalog is read once per session.
        private HashSet<string>? bigRecipeIds;

        private string bugReportText = string.Empty;
        private bool bugReportOpen;
        private bool bugReportCopied;

        // Two independent detectors, because the first is blind in exactly the case worth reporting:
        // if the hole count itself under-reads, N never reaches 8 and the button would vanish
        // precisely when it is needed. The second does not depend on the station at all — an open
        // panel row carries its Expedition2Recipes Id, and a recipe of size >= 8 can only be listed
        // on a monolith that HAS at least 8 holes, whatever we read for N.
        private bool HasBigMonolith()
        {
            foreach (var v in this.monolithViews)
                if (v.HoleCount >= BugReportBigHoles) return true;

            // Runs every frame the rewards window is up, so it must be a hash lookup per visible row:
            // a List.Find over the catalog per row would be a few hundred thousand string compares a
            // second, plus a closure allocation each time, to answer a question that is almost always no.
            if (this.recipes.Count == 0) return false;
            if (this.bigRecipeIds == null)
            {
                this.bigRecipeIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var rec in this.monolithRecipes)
                    if (rec.size >= BugReportBigHoles) this.bigRecipeIds.Add(rec.id);
            }

            foreach (var r in this.recipes)
                if (!string.IsNullOrEmpty(r.Id) && this.bigRecipeIds.Contains(r.Id)) return true;

            return false;
        }

        private void OpenBugReport(MonoView? focus)
        {
            this.bugReportText = this.BuildBugReport(focus);
            this.bugReportCopied = false;
            try
            {
                ImGui.SetClipboardText(this.bugReportText);
                this.bugReportCopied = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RunecraftHelper] clipboard copy failed: {ex.Message}");
            }

            this.bugReportOpen = true;
        }

        // Read-only text window. The text is already on the clipboard when this opens; the box exists
        // so a reporter whose clipboard is blocked — or who simply wants to see what they are sending —
        // can select it by hand.
        private void DrawBugReportWindow()
        {
            if (!this.bugReportOpen) return;

            ImGui.SetNextWindowSize(new Vector2(760f, 560f), ImGuiCond.FirstUseEver);
            bool open = this.bugReportOpen;
            if (ImGui.Begin("Monolith bug report###RunecraftBugReport", ref open))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.82f, 0.32f, 1f));
                ImGui.Text("Send this to Yokken");
                ImGui.PopStyleColor();
                ImGui.TextWrapped(this.bugReportCopied
                    ? "Already copied to your clipboard — just paste it. Nothing here identifies your account."
                    : "Clipboard copy failed — select the text below and copy it manually.");

                if (ImGui.Button("Copy to clipboard"))
                {
                    try
                    {
                        ImGui.SetClipboardText(this.bugReportText);
                        this.bugReportCopied = true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[RunecraftHelper] clipboard copy failed: {ex.Message}");
                    }
                }

                ImGui.SameLine();
                if (ImGui.Button("Refresh")) this.bugReportText = this.BuildBugReport(null);
                ImGui.SameLine();
                if (ImGui.Button("Close")) open = false;
                ImGui.SameLine();
                ImGui.TextDisabled($"{this.bugReportText.Length} chars");

                ImGui.Separator();
                var avail = ImGui.GetContentRegionAvail();
                ImGui.InputTextMultiline(
                    "##RunecraftBugReportText",
                    ref this.bugReportText,
                    (uint)Math.Max(8192, this.bugReportText.Length + 1024),
                    new Vector2(avail.X, Math.Max(120f, avail.Y - 2f)),
                    ImGuiInputTextFlags.ReadOnly);
            }

            ImGui.End();
            this.bugReportOpen = open;
        }

        // ── report ───────────────────────────────────────────────────────────
        private string BuildBugReport(MonoView? focus)
        {
            var sb = new StringBuilder(1 << 15);
            sb.AppendLine("=== RunecraftHelper monolith bug report ===");
            sb.AppendLine("Paste the whole block. Plain text, no account data.");
            sb.AppendLine();

            this.AppendReportEnvironment(sb);

            // The monolith the report is ABOUT: the one whose panel is open (its rows are the ground
            // truth we diff against), else the biggest, else the nearest.
            var target = focus;
            target ??= this.monolithViews.Find(v => v.PanelOpen);
            if (target == null)
            {
                foreach (var v in this.monolithViews)
                    if (v.HoleCount >= BugReportBigHoles && (target == null || v.HoleCount > target.HoleCount))
                        target = v;
            }

            if (target == null && this.monolithViews.Count > 0) target = this.monolithViews[0];

            sb.AppendLine($"monoliths in area: {this.monolithViews.Count}");
            for (int i = 0; i < this.monolithViews.Count; i++)
            {
                var v = this.monolithViews[i];
                bool detail = ReferenceEquals(v, target) || v.HoleCount >= BugReportBigHoles || v.PanelOpen;
                if (detail)
                {
                    this.AppendMonolithDetail(sb, v, i + 1, ReferenceEquals(v, target));
                }
                else
                {
                    sb.AppendLine($"  #{i + 1}  N={v.HoleCount} {v.AnchorName} p={v.AnchorPos} dist={v.Distance:F0} " +
                                  $"mode={v.RecipeMode} offers={v.Candidates.Count} best={v.Best:F1}ex");
                }
            }

            sb.AppendLine();
            this.AppendPanelRows(sb, target);

            return sb.ToString();
        }

        private void AppendReportEnvironment(StringBuilder sb)
        {
            sb.AppendLine($"when       : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
            sb.AppendLine($"plugin     : RunecraftHelper {AsmVersion(typeof(RunecraftHelperCore))}");
            sb.AppendLine($"GameHelper : {AsmVersion(typeof(Core))}");
            sb.AppendLine($"game exe   : {GameExeLine()}");

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            string areaName = string.Empty;
            try
            {
                areaName = Core.States.AreaLoading.CurrentAreaName;
            }
            catch (Exception)
            {
                areaName = "?";
            }

            // The area HASH is deliberately absent: it is the server's instance identifier, so a pasted
            // report carrying it could be traced back to the specific instance -- and thus to the
            // player who sent it. Nothing here needs it (the tag set below is what the gate reads),
            // and RefreshAreaContentTags keeps using it internally as a cache key, never in output.
            sb.AppendLine($"area       : \"{(string.IsNullOrEmpty(areaName) ? "(name unavailable)" : areaName)}\" " +
                          $"lvl={(area != null ? area.CurrentAreaLevel : 0)} " +
                          $"addr=0x{(area != null ? area.Address.ToInt64() : 0):X}");
            sb.AppendLine($"area tags  : [{string.Join(",", this.areaContentTags)}] read_ok={this.areaContentTagsOk}" +
                          "   (HF9 per-area recipe gate; read_ok=false means the gate is not enforced)");

            sb.AppendLine($"prices     : league=\"{this.Settings.League}\" custom={this.Settings.UseCustomLeague} " +
                          $"status={this.priceCache.Status} names={this.priceCache.PriceCount} " +
                          $"div->ex={this.priceCache.DivineToExaltedRate:F1} " +
                          $"synced={(this.priceCache.LastSyncUtc == DateTime.MinValue ? "never" : this.priceCache.LastSyncUtc.ToString("yyyy-MM-dd HH:mm") + "Z")}");
            if (!string.IsNullOrEmpty(this.priceCache.LastError))
                sb.AppendLine($"price error: {this.priceCache.LastError}");

            sb.AppendLine($"catalog    : {CatalogLine(this.DllDirectory)} recipes={this.monolithRecipes.Count} " +
                          $"weightRows={this.partialMinLevel.Count} runeNames={this.runeNames.Count}");

            var sizes = new SortedDictionary<int, int>();
            foreach (var r in this.monolithRecipes)
                sizes[r.size] = sizes.TryGetValue(r.size, out var c) ? c + 1 : 1;
            var sizeText = new StringBuilder();
            foreach (var kv in sizes)
            {
                if (sizeText.Length > 0) sizeText.Append(' ');
                sizeText.Append(kv.Key).Append(':').Append(kv.Value);
            }

            sb.AppendLine($"cat sizes  : {sizeText}");
            sb.AppendLine($"settings   : minEx={this.Settings.MonolithRewardsMinExalted} colorMode={this.Settings.ColorMode} " +
                          $"runeChain={this.Settings.RuneChainEnabled} foreground={Core.Process.Foreground}");
            sb.AppendLine();
        }

        private void AppendMonolithDetail(StringBuilder sb, MonoView v, int ordinal, bool isTarget)
        {
            sb.AppendLine();
            sb.AppendLine($"--- monolith #{ordinal}  N={v.HoleCount} holes{(isTarget ? "   <<< REPORTED" : string.Empty)} ---");
            sb.AppendLine($"  path      : {v.Path}");
            sb.AppendLine($"  device    : 0x{v.EntityId:X}  id={v.EntityNum}  station=0x{v.StationAddr:X}  dist={v.Distance:F0}");
            sb.AppendLine($"  holes     : N={v.HoleCount} (station+0x{StationHoleCountOffset:X2})   SM sockets={v.SocketsState}" +
                          "   [the SM state caps at 6 by design; N is the authoritative one]");
            sb.AppendLine($"  anchor    : {v.AnchorName} idx={v.AnchorIdx} pos={v.AnchorPos}" +
                          (v.AnchorPos >= 0 ? $" (hole {v.AnchorPos + 1} of {v.HoleCount})" : string.Empty) +
                          $"  unique={v.IsUnique}");
            sb.AppendLine($"  state     : mode={v.RecipeMode} (dig=1 unique=3 standalone=0)  foreign={v.IsForeign}  " +
                          $"rerolled={v.IsRerolled}  activated={v.Activated}  empowered={v.RunesEmpowered}  panelOpen={v.PanelOpen}");
            sb.AppendLine($"  selected  : {(string.IsNullOrEmpty(v.SelectedRecipeId) ? "(none — every offer still open)" : v.SelectedRecipeId + "   [COMMITTED: the window then shows ONLY this reward]")}");
            sb.AppendLine($"  glow      : count={v.GlowCount} sockets=[{string.Join(",", v.GlowSockets)}] labels=[{string.Join(" | ", v.GlowRuneLabels)}]");
            sb.AppendLine($"  raw       : +0x40={FmtI(v.Field40)} +0x44={FmtI(v.Field44)}");
            if (!string.IsNullOrEmpty(v.SmStates)) sb.AppendLine($"  SM states : {v.SmStates}");
            if (!string.IsNullOrEmpty(v.StationDiag)) sb.AppendLine($"  RESOLVE FAILED: {v.StationDiag}");

            this.AppendStationBytes(sb, v);

            // What the window is showing right now.
            double best = 0;
            string bestName = "—";
            int unpriced = 0;
            foreach (var c in v.Candidates)
            {
                if (!c.Priced)
                {
                    unpriced++;
                    continue;
                }

                if (c.UnitEx * c.Count > best)
                {
                    best = c.UnitEx * c.Count;
                    bestName = $"{c.Reward} x{c.Count}";
                }
            }

            sb.AppendLine($"  window    : {v.Candidates.Count} row(s), {unpriced} unpriced, best={best:F1}ex ({bestName})");

            // Re-run the gate over the whole catalog. Deliberately independent of v.Candidates: a
            // COMMITTED recipe replaces that list, so this is the only way to see what the rule itself
            // yields — and a difference between the two is diagnostic, not a flaw in the report.
            var census = new int[8];
            var skipAnchor = v.IsUnique;
            foreach (var rec in this.monolithRecipes)
            {
                var verdict = this.OfferVerdict(v, rec, v.AreaLevel, skipAnchor);
                census[(int)verdict]++;
            }

            sb.AppendLine($"  rule      : offered={census[(int)OfferDrop.Offered]} " +
                          $"sizeOverHoles={census[(int)OfferDrop.SizeOverHoles]} " +
                          $"anchorMismatch={census[(int)OfferDrop.AnchorMismatch]} " +
                          $"noRuneAtAnchor={census[(int)OfferDrop.NoRuneAtAnchor]} " +
                          $"levelBand={census[(int)OfferDrop.LevelBand]} " +
                          $"partialWeights={census[(int)OfferDrop.PartialWeights]} " +
                          $"areaTags={census[(int)OfferDrop.AreaTags]}");

            sb.AppendLine($"  offers (top {BugReportMaxOffers} by value):");
            int shown = 0;
            foreach (var c in v.Candidates)
            {
                if (shown >= BugReportMaxOffers) break;
                shown++;
                var total = c.Priced
                    ? (c.UnitEx * c.Count).ToString("F1", CultureInfo.InvariantCulture).PadLeft(10)
                    : "  UNPRICED";
                sb.AppendLine($"    {total}  row{c.Row} size{c.Size} {(c.Full ? "N " : "RW")} cat{c.Category}  " +
                              $"{c.Reward} x{c.Count}  meta={c.RewardId}  lvl{c.MinLevel}-{c.MaxLevel}  " +
                              $"price_via={c.PriceVia}  | {MarkAnchor(c.Runes, v.AnchorPos)}");
            }

            if (v.Candidates.Count > shown) sb.AppendLine($"    (+{v.Candidates.Count - shown} more)");

            // Every big-recipe row with the gate's verdict on it. On an 8-hole monolith these are the
            // rows in dispute, and the catalog holds only a couple of dozen — cheap to print in full,
            // and it answers "why is the expensive one missing" without a second round trip.
            sb.AppendLine($"  catalog rows with size >= {BugReportBigHoles - 1}, and the verdict here:");
            int big = 0;
            foreach (var rec in this.monolithRecipes)
            {
                if (rec.size < BugReportBigHoles - 1) continue;
                if (big >= BugReportMaxBigRows)
                {
                    sb.AppendLine("    (truncated)");
                    break;
                }

                big++;
                var verdict = this.OfferVerdict(v, rec, v.AreaLevel, skipAnchor);
                sb.AppendLine($"    row{rec.row} size{rec.size} {rec.id,-34} {this.VerdictDetail(verdict, v, rec)}");
            }
        }

        // Human-readable verdict WITH the datum that decided it — a bare enum name would leave "why"
        // open (AnchorMismatch, for one, is only actionable once you see which rune the row wants).
        private string VerdictDetail(OfferDrop verdict, MonoView v, MonoRecipe rec)
        {
            switch (verdict)
            {
                case OfferDrop.Offered:
                    return "OFFERED";
                case OfferDrop.SizeOverHoles:
                    return $"dropped: size {rec.size} > N {v.HoleCount}";
                case OfferDrop.NoRuneAtAnchor:
                    return $"dropped: recipe has {(rec.runeIdx?.Count ?? 0)} rune(s), the anchor sits at index {v.AnchorPos}";
                case OfferDrop.AnchorMismatch:
                    var want = rec.runeIdx != null && v.AnchorPos >= 0 && v.AnchorPos < rec.runeIdx.Count
                        ? this.RuneNameByIndex(rec.runeIdx[v.AnchorPos]) ?? $"#{rec.runeIdx[v.AnchorPos]}"
                        : "?";
                    return $"dropped: wants {want} at hole {v.AnchorPos + 1}, the anchor is {v.AnchorName}";
                case OfferDrop.LevelBand:
                    return $"dropped: area lvl {v.AreaLevel} outside {rec.minLevel}-{rec.maxLevel}";
                case OfferDrop.PartialWeights:
                    return $"dropped: no RunesWeights row for (rune {v.AnchorIdx}, pos {v.AnchorPos + 1}, size {rec.size}) at lvl {v.AreaLevel}";
                case OfferDrop.AreaTags:
                    return $"dropped: needs an area tag from [{string.Join(",", rec.areaTags ?? new List<int>())}], the area has [{string.Join(",", this.areaContentTags)}]";
                default:
                    return verdict.ToString();
            }
        }

        private void AppendStationBytes(StringBuilder sb, MonoView v)
        {
            if (v.StationAddr == 0) return;
            if (!this.EnsureProcess()) return;

            var buf = new byte[BugReportStationBytes];
            if (!ReadProcessMemory(this.processHandle, (IntPtr)v.StationAddr, buf, (uint)buf.Length, out _))
            {
                sb.AppendLine("  station bytes: READ FAILED");
                return;
            }

            sb.AppendLine($"  station bytes 0x00..0x{BugReportStationBytes - 1:X2}:");
            for (int off = 0; off < buf.Length; off += 16)
            {
                var line = new StringBuilder(64);
                line.Append("    +").Append(off.ToString("X2")).Append("  ");
                for (int i = 0; i < 16; i++)
                {
                    line.Append(buf[off + i].ToString("X2"));
                    line.Append(i == 7 ? "  " : " ");
                }

                sb.AppendLine(line.ToString());
            }
        }

        // The live panel rows, and the diff that makes the report conclusive.
        private void AppendPanelRows(StringBuilder sb, MonoView? target)
        {
            sb.AppendLine($"--- live Runeshape Combinations panel: {this.recipes.Count} visible row(s) ---");
            if (this.recipes.Count == 0)
            {
                sb.AppendLine("  EMPTY — the panel was not open (or the game lost focus, which clears the rows).");
                sb.AppendLine("  For the useful version of this report: stand at the monolith, OPEN its Runeshape");
                sb.AppendLine("  Combinations panel, and press the button again with the panel still open.");
                return;
            }

            int noId = 0, maxPanelSize = 0;
            foreach (var r in this.recipes)
            {
                var (price, branch) = this.TraceRecipePrice(in r);
                if (string.IsNullOrEmpty(r.Id)) noId++;
                var known = string.IsNullOrEmpty(r.Id)
                    ? null
                    : this.monolithRecipes.Find(x => string.Equals(x.id, r.Id, StringComparison.Ordinal));
                if (known != null && known.size > maxPanelSize) maxPanelSize = known.size;
                var priceText = price > 0
                    ? price.ToString("F1", CultureInfo.InvariantCulture).PadLeft(10)
                    : "         —";
                sb.AppendLine($"  x{r.Count} {priceText}  size{(known != null ? known.size.ToString() : "?")}  " +
                              $"id={(string.IsNullOrEmpty(r.Id) ? "(none)" : r.Id)}  " +
                              $"meta={(string.IsNullOrEmpty(r.MetaId) ? "—" : r.MetaId)}  " +
                              $"dds={(string.IsNullOrEmpty(r.DdsArt) ? "—" : r.DdsArt)}  price_branch={branch}");
            }

            // The hole count implied by the game's OWN list, independent of station+0x38: a recipe of
            // size S is only ever listed on a monolith with at least S holes. A disagreement here is
            // itself the finding, and it survives the case where our N is the thing that is wrong.
            if (maxPanelSize > 0)
                sb.AppendLine($"  panel implies at least {maxPanelSize} hole(s) (largest recipe the game lists here)" +
                              (target != null && maxPanelSize > target.HoleCount
                                  ? $"   <<< we read N={target.HoleCount} — HOLE COUNT UNDER-READ"
                                  : string.Empty));

            if (noId > 0)
                sb.AppendLine($"  !! {noId} row(s) gave no recipe Id — the row->recipe back-pointer offset " +
                              $"(0x{RecipeRowBackPtrOffset:X}) is probably stale for this build, and nothing below can be trusted.");

            if (target == null) return;

            // Only one direction of this diff is evidence. `recipes` holds the rows currently ON SCREEN
            // (the scan skips invisible ones), so "offered but not in the panel" is the normal state of
            // a scrolled list and means nothing. "In the panel but not offered" cannot be explained that
            // way: the game is offering a recipe our rule rejects, and the verdict names the gate.
            sb.AppendLine();
            sb.AppendLine($"--- panel vs offer rule (monolith N={target.HoleCount}, anchor {target.AnchorName} p={target.AnchorPos}) ---");
            sb.AppendLine("  (only 'in panel but rejected' is a fault — the panel scrolls, so the other direction is normal)");

            int faults = 0, priceGaps = 0;
            bool skipAnchor = target.IsUnique;
            foreach (var r in this.recipes)
            {
                if (string.IsNullOrEmpty(r.Id)) continue;
                var rec = this.monolithRecipes.Find(x => string.Equals(x.id, r.Id, StringComparison.Ordinal));
                if (rec == null)
                {
                    sb.AppendLine($"  UNKNOWN RECIPE: panel row \"{r.Id}\" is not in the catalog — the plugin's data is older than the game.");
                    faults++;
                    continue;
                }

                var verdict = this.OfferVerdict(target, rec, target.AreaLevel, skipAnchor);
                if (verdict != OfferDrop.Offered)
                {
                    sb.AppendLine($"  REJECTED BUT OFFERED IN GAME: {rec.id} (size{rec.size}, " +
                                  $"{rec.reward?.name ?? rec.description} x{Math.Max(1, rec.rewardCount)}) " +
                                  $"-> {this.VerdictDetail(verdict, target, rec)}");
                    faults++;
                    continue;
                }

                // Same recipe on both sides: do the two price paths agree? The panel overlay resolves a
                // price through metaId / dds-art with several fallbacks, while the rewards window prices
                // by the catalog's English reward NAME alone — so a name poe.ninja does not key shows a
                // price on the panel row and none in the window. That is exactly the shape of a report
                // saying "the panel says 113 ex, the window says 21 ex".
                var (panelPrice, panelBranch) = this.TraceRecipePrice(in r);
                double windowPrice = 0;
                bool windowPriced = rec.reward != null && !string.IsNullOrEmpty(rec.reward.name) &&
                                    this.priceCache.TryGetExaltedPrice(rec.reward.name, out windowPrice) &&
                                    windowPrice > 0;
                if (!windowPriced && panelPrice > 0)
                {
                    sb.AppendLine($"  PRICE GAP: {rec.id} panel={panelPrice:F1}ex via {panelBranch}, window=UNPRICED " +
                                  $"(name lookup \"{rec.reward?.name}\" missed)");
                    priceGaps++;
                }
                else if (windowPriced && panelPrice > 0 &&
                         Math.Abs(windowPrice - panelPrice) > Math.Max(0.5, panelPrice * 0.02))
                {
                    sb.AppendLine($"  PRICE DIFF: {rec.id} panel={panelPrice:F1}ex via {panelBranch}, " +
                                  $"window={windowPrice:F1}ex by name \"{rec.reward?.name}\"");
                    priceGaps++;
                }
            }

            if (faults == 0 && priceGaps == 0)
                sb.AppendLine("  no disagreement on the visible rows — scroll the panel to the expensive recipe and press again.");
            else
                sb.AppendLine($"  => {faults} offer-rule fault(s), {priceGaps} price disagreement(s)");
        }

        // ── small helpers ────────────────────────────────────────────────────
        private static string AsmVersion(Type t)
        {
            try
            {
                return t.Assembly.GetName().Version?.ToString() ?? "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        // A patch mismatch is the first thing to rule out on any offset report, so identify the client
        // by version AND build date — PoE2's FileVersion has been known to sit still across hotfixes.
        private static string GameExeLine()
        {
            try
            {
                int pid = (int)Core.Process.Pid;
                if (pid == 0) return "not attached";
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                var file = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(file)) return $"pid {pid} (module unavailable)";
                var fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(file);
                return $"{System.IO.Path.GetFileName(file)} v{fv.FileVersion ?? "?"} " +
                       $"built {System.IO.File.GetLastWriteTimeUtc(file):yyyy-MM-dd HH:mm}Z (pid {pid})";
            }
            catch (Exception ex)
            {
                return $"unavailable ({ex.GetType().Name})";
            }
        }

        // Identify the bundled data file — a stale expedition2_recipes.json explains a whole class of
        // "wrong reward" reports and is invisible from the plugin version alone.
        private static string CatalogLine(string dir)
        {
            try
            {
                var path = System.IO.Path.Join(dir, "expedition2_recipes.json");
                if (!System.IO.File.Exists(path)) return "expedition2_recipes.json MISSING";
                var fi = new System.IO.FileInfo(path);
                return $"json {fi.Length}B {fi.LastWriteTimeUtc:yyyy-MM-dd}";
            }
            catch (Exception ex)
            {
                return $"json ? ({ex.GetType().Name})";
            }
        }
    }
}
