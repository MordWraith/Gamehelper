namespace LootTracker
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using ImGuiNET;
    using Newtonsoft.Json;

    // ── Persisted session-history model ──────────────────────────────────
    // One priced line of a map's net gains, snapshotted at session-save time (so a historical record
    // stays stable regardless of later price moves).
    public sealed class SessionLootLine
    {
        public string Label = string.Empty; // English display name (or art id fallback)
        public long Count;
        public double Ex;                    // total Exalted value of this line at save time (0 if unpriced)
        public bool Priced;
    }

    // A completed map within a saved session.
    public sealed class SessionMap
    {
        public string Name = string.Empty;
        public double ActiveSeconds;         // wall time spent inside the map
        public double ProfitEx;              // Σ line.Ex
        public List<SessionLootLine> Loot = new();
    }

    // A finished tracking session (everything between two "New session" presses).
    public sealed class SessionRecord
    {
        public DateTime StartUtc;
        public DateTime EndUtc;
        public double DivineRate;            // Divine→Exalted rate at save time (0 = unknown)
        public List<SessionMap> Maps = new();

        // On-disk path, filled on load so the row can be deleted. Not serialized.
        [JsonIgnore]
        public string FilePath = string.Empty;

        public double TotalEx()
        {
            double s = 0;
            foreach (var m in this.Maps) s += m.ProfitEx;
            return s;
        }

        public double TotalActiveSeconds()
        {
            double s = 0;
            foreach (var m in this.Maps) s += m.ActiveSeconds;
            return s;
        }

        public double PerHourEx()
        {
            double h = this.TotalActiveSeconds() / 3600.0;
            return h > 0 ? this.TotalEx() / h : 0;
        }

        public double ToDivine(double ex) => this.DivineRate > 0 ? ex / this.DivineRate : 0;
    }

    // The live (in-progress) session, autosaved separately from the archived history so a GH close or a
    // game crash doesn't lose it. Holds the raw MapRun list (unpriced deltas) so pricing stays live on
    // restore — unlike SessionRecord, which snapshots prices at archive time.
    public sealed class ActiveSessionState
    {
        public DateTime StartUtc;
        public List<MapRun> Completed = new();
    }

    public sealed partial class LootTrackerCore
    {
        private string SessionsDir => Path.Join(this.DllDirectory, "config", "sessions");
        private string ActiveSessionPathname => Path.Join(this.DllDirectory, "config", "active.json");
        private DateTime nextAutoSaveUtc = DateTime.MinValue;

        // Autosave the live session to active.json. Called on zone transitions, on a ~20s timer (to catch
        // mid-map loot), and on disable. The currently-active run gets its un-folded live leg + running
        // time baked into the saved copy, so a crash keeps progress up to the last autosave; the in-memory
        // state is untouched (active.json is a mirror, never read back during normal play). Best-effort.
        private void SaveActiveState()
        {
            try
            {
                if (this.completed.Count == 0)
                {
                    this.DeleteActiveState();
                    return;
                }

                var state = new ActiveSessionState { StartUtc = this.sessionStartUtc };
                foreach (var r in this.completed)
                {
                    bool isActive = ReferenceEquals(r, this.current) && this.runStartUtc != null;
                    state.Completed.Add(new MapRun
                    {
                        Name = r.Name,
                        Hash = r.Hash,
                        AreaLevel = r.AreaLevel,
                        ActiveTime = isActive ? this.CurrentLiveTime() : r.ActiveTime,
                        Gained = new Dictionary<string, long>(isActive ? this.CurrentGainedLive() : r.Gained, StringComparer.Ordinal),
                        Kills = (int[])r.Kills.Clone(),
                    });
                }

                Directory.CreateDirectory(Path.GetDirectoryName(this.ActiveSessionPathname)!);
                File.WriteAllText(this.ActiveSessionPathname, JsonConvert.SerializeObject(state, Formatting.Indented));
            }
            catch
            {
                // best-effort; a failed autosave just means this tick isn't crash-protected.
            }
        }

        // Restore a live session from active.json (called on enable). Brings back the completed map list
        // (loot/time/kills) with the timer paused; re-entering a map resumes its run by hash and keeps
        // accumulating. No-op if there's nothing to restore.
        private void LoadActiveState()
        {
            try
            {
                if (!File.Exists(this.ActiveSessionPathname))
                {
                    return;
                }

                var state = JsonConvert.DeserializeObject<ActiveSessionState>(File.ReadAllText(this.ActiveSessionPathname));
                if (state?.Completed == null || state.Completed.Count == 0)
                {
                    return;
                }

                this.completed.Clear();
                this.completed.AddRange(state.Completed);
                this.sessionStartUtc = state.StartUtc == default ? DateTime.UtcNow : state.StartUtc;
                this.current = null;
                this.runStartUtc = null;
                this.baseline = null;
                this.baselinePending = false;
                this.prevSnapshot = null;
                this.lastProcessedZoneHash = string.Empty;
            }
            catch
            {
                // a corrupt autosave just means we start fresh.
            }
        }

        private void DeleteActiveState()
        {
            try
            {
                if (File.Exists(this.ActiveSessionPathname))
                {
                    File.Delete(this.ActiveSessionPathname);
                }
            }
            catch
            {
                // ignore
            }
        }

        // History-window state (transient, not persisted).
        private bool showSessionHistory;
        private List<SessionRecord>? sessionCache;
        private SessionRecord? detailSession;
        private SessionMap? lootMap;        // history loot view (priced snapshot at archive time)
        private MapRun? lootRun;            // active-session loot view (re-priced live each frame)

        // ── Persistence ──────────────────────────────────────────────────
        // Snapshot the session being ended (the completed runs, plus the in-progress one banked first)
        // and write it to its own json under config/sessions. No-op when nothing was run.
        private void SaveCurrentSession()
        {
            this.BankActiveTime(DateTime.UtcNow); // include the active run's still-running time
            if (this.completed.Count == 0)
            {
                return;
            }

            var rec = this.BuildSessionRecord(this.sessionStartUtc, DateTime.UtcNow);
            this.WriteSession(rec);
            this.TrimSessions();
        }

        // Price every completed run's net gains at the current rates and assemble the record.
        private SessionRecord BuildSessionRecord(DateTime startUtc, DateTime endUtc)
        {
            var rec = new SessionRecord
            {
                StartUtc = startUtc,
                EndUtc = endUtc,
                DivineRate = this.priceCache.DivineToExaltedRate,
            };

            foreach (var r in this.completed)
            {
                rec.Maps.Add(this.BuildSessionMap(r));
            }

            return rec;
        }

        // Price one run's net gains at the CURRENT rates into a SessionMap (loot lines sorted by value).
        // Shared by session archiving and the live "Active session" loot view in settings.
        private SessionMap BuildSessionMap(MapRun r)
        {
            var m = new SessionMap { Name = r.Name, ActiveSeconds = r.ActiveTime.TotalSeconds };
            double profit = 0;
            foreach (var kv in r.Gained)
            {
                if (kv.Value == 0) continue;
                bool priced = this.TryPriceItem(kv.Key, out var unit, out var label);
                double ex = priced ? unit * kv.Value : 0;
                m.Loot.Add(new SessionLootLine { Label = label, Count = kv.Value, Ex = ex, Priced = priced });
                profit += ex;
            }

            m.Loot.Sort((a, b) => Math.Abs(b.Ex).CompareTo(Math.Abs(a.Ex)));
            m.ProfitEx = profit;
            return m;
        }

        private void WriteSession(SessionRecord rec)
        {
            try
            {
                Directory.CreateDirectory(this.SessionsDir);
                // Timestamp filename → lexical order == chronological order (used by the trim).
                var name = $"session_{rec.StartUtc:yyyyMMdd_HHmmss_fff}.json";
                File.WriteAllText(Path.Join(this.SessionsDir, name), JsonConvert.SerializeObject(rec, Formatting.Indented));
            }
            catch
            {
                // history is best-effort; a failed write just means this session isn't archived.
            }
        }

        // Drop the oldest session files past the keep-limit.
        private void TrimSessions()
        {
            try
            {
                if (!Directory.Exists(this.SessionsDir)) return;
                var files = Directory.GetFiles(this.SessionsDir, "session_*.json");
                int max = Math.Max(1, this.Settings.MaxSessions);
                if (files.Length <= max) return;
                Array.Sort(files, StringComparer.Ordinal); // oldest first
                for (int i = 0; i < files.Length - max; i++)
                {
                    File.Delete(files[i]);
                }
            }
            catch
            {
                // ignore
            }
        }

        // (Re)load all saved sessions from disk into the cache, newest first.
        private void LoadSessions()
        {
            var list = new List<SessionRecord>();
            try
            {
                if (Directory.Exists(this.SessionsDir))
                {
                    foreach (var path in Directory.GetFiles(this.SessionsDir, "session_*.json"))
                    {
                        try
                        {
                            var rec = JsonConvert.DeserializeObject<SessionRecord>(File.ReadAllText(path));
                            if (rec != null)
                            {
                                rec.FilePath = path;
                                list.Add(rec);
                            }
                        }
                        catch
                        {
                            // skip a corrupt file
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            list.Sort((a, b) => b.StartUtc.CompareTo(a.StartUtc));
            this.sessionCache = list;
        }

        private void DeleteSession(SessionRecord rec)
        {
            try
            {
                if (!string.IsNullOrEmpty(rec.FilePath) && File.Exists(rec.FilePath))
                {
                    File.Delete(rec.FilePath);
                }
            }
            catch
            {
                // ignore
            }

            this.sessionCache?.Remove(rec);
            if (ReferenceEquals(this.detailSession, rec))
            {
                this.detailSession = null;
                this.lootMap = null;
            }
        }

        // ── History windows ──────────────────────────────────────────────
        // Top-level list of saved sessions: date · length · maps · total Divine · Divine/h, with
        // per-row Details / Delete. Opened from the plugin settings button.
        private void DrawSessionHistoryWindow()
        {
            if (!this.showSessionHistory)
            {
                return;
            }

            ImGui.SetNextWindowSize(new System.Numerics.Vector2(760, 380), ImGuiCond.FirstUseEver);
            bool open = true;
            if (ImGui.Begin("Session history", ref open))
            {
                if (ImGui.Button("Refresh"))
                {
                    this.LoadSessions();
                }

                ImGui.SameLine();
                ImGui.TextDisabled($"{this.sessionCache?.Count ?? 0} saved · keeping last {this.Settings.MaxSessions}");

                if (ImGui.BeginTable("sessions_tbl", 6,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
                {
                    ImGui.TableSetupColumn("Date");
                    ImGui.TableSetupColumn("Length", ImGuiTableColumnFlags.WidthFixed, 70);
                    ImGui.TableSetupColumn("Maps", ImGuiTableColumnFlags.WidthFixed, 50);
                    ImGui.TableSetupColumn("Total div", ImGuiTableColumnFlags.WidthFixed, 80);
                    ImGui.TableSetupColumn("Div/h", ImGuiTableColumnFlags.WidthFixed, 70);
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 150);
                    ImGui.TableHeadersRow();

                    SessionRecord? toDelete = null;
                    var cache = this.sessionCache;
                    if (cache != null)
                    {
                        for (int i = 0; i < cache.Count; i++)
                        {
                            var rec = cache[i];
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted($"{rec.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(FormatDuration(rec.EndUtc - rec.StartUtc));
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(rec.Maps.Count.ToString());
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(rec.DivineRate > 0 ? $"{rec.ToDivine(rec.TotalEx()):0.0}" : "—");
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(rec.DivineRate > 0 ? $"{rec.ToDivine(rec.PerHourEx()):0.0}" : "—");
                            ImGui.TableNextColumn();
                            if (ImGui.SmallButton($"Details##s{i}"))
                            {
                                this.detailSession = rec;
                                this.lootMap = null;
                            }

                            ImGui.SameLine();
                            if (ImGui.SmallButton($"Delete##s{i}"))
                            {
                                toDelete = rec;
                            }
                        }
                    }

                    ImGui.EndTable();

                    if (toDelete != null)
                    {
                        this.DeleteSession(toDelete);
                    }
                }
            }

            ImGui.End();
            if (!open)
            {
                this.showSessionHistory = false;
            }
        }

        // One session in full: the six aggregates + the per-map table, each map openable for its loot.
        private void DrawSessionDetailWindow()
        {
            var rec = this.detailSession;
            if (rec == null)
            {
                return;
            }

            ImGui.SetNextWindowSize(new System.Numerics.Vector2(620, 420), ImGuiCond.FirstUseEver);
            bool open = true;
            if (ImGui.Begin($"Session {rec.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm}###session_detail", ref open))
            {
                int maps = rec.Maps.Count;
                double totalEx = rec.TotalEx();
                double activeS = rec.TotalActiveSeconds();
                var avgTime = maps > 0 ? TimeSpan.FromSeconds(activeS / maps) : TimeSpan.Zero;
                double avgEx = maps > 0 ? totalEx / maps : 0;
                string totalDiv = rec.DivineRate > 0 ? $"{rec.ToDivine(totalEx):0.0}" : "—";
                string hourDiv = rec.DivineRate > 0 ? $"{rec.ToDivine(rec.PerHourEx()):0.0}" : "—";

                // Divine-only display converts by the session's OWN saved rate (rec.DivineRate), never the
                // live rate — a two-week-old session keeps the value it had then.
                bool divHist = this.Settings.ShowPricesInDivineOnly && rec.DivineRate > 0;

                ImGui.Text($"Duration: {FormatDuration(rec.EndUtc - rec.StartUtc)}");
                ImGui.Text($"Maps completed: {maps}");
                ImGui.Text($"AVG time in map: {FormatDuration(avgTime)}");
                ImGui.Text(divHist ? $"AVG profit: {rec.ToDivine(avgEx):0.##} Div" : $"AVG profit: {avgEx:0} Ex");
                ImGui.Text($"Total: {totalDiv} Divine");
                ImGui.Text($"Per hour: {hourDiv} Divine / hour");
                ImGui.Separator();

                if (ImGui.BeginTable("detail_maps_tbl", 4,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
                {
                    ImGui.TableSetupColumn("Map");
                    ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 70);
                    ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.WidthFixed, 90);
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70);
                    ImGui.TableHeadersRow();

                    for (int i = 0; i < rec.Maps.Count; i++)
                    {
                        var m = rec.Maps[i];
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(m.Name);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(FormatDuration(TimeSpan.FromSeconds(m.ActiveSeconds)));
                        ImGui.TableNextColumn();
                        ImGui.TextColored(m.ProfitEx >= 0 ? GreenCol : RedCol, divHist
                            ? $"{rec.ToDivine(m.ProfitEx):+0.##;-0.##;0} div"
                            : $"{m.ProfitEx:+0.0;-0.0;0} ex");
                        ImGui.TableNextColumn();
                        if (ImGui.SmallButton($"Loot##m{i}"))
                        {
                            this.lootMap = m;
                            this.lootRun = null; // history view takes over from any active-session view
                        }
                    }

                    ImGui.EndTable();
                }
            }

            ImGui.End();
            if (!open)
            {
                this.detailSession = null;
                this.lootMap = null;
            }
        }

        // What a single map dropped. Two sources share this window: a history map (priced snapshot,
        // converted by the saved session rate) and a live active-session run (re-priced every frame at
        // the current rate). lootRun takes priority when both happen to be set.
        private void DrawMapLootWindow()
        {
            SessionMap? m;
            double rate;
            if (this.lootRun != null)
            {
                m = this.BuildSessionMap(this.lootRun);
                rate = this.priceCache.DivineToExaltedRate; // live run → current rate
            }
            else
            {
                m = this.lootMap;
                rate = this.detailSession?.DivineRate ?? 0; // history → that session's saved rate
            }

            if (m == null)
            {
                return;
            }

            bool divHist = this.Settings.ShowPricesInDivineOnly && rate > 0;

            ImGui.SetNextWindowSize(new System.Numerics.Vector2(420, 360), ImGuiCond.FirstUseEver);
            bool open = true;
            if (ImGui.Begin($"Loot — {m.Name}###map_loot", ref open))
            {
                ImGui.TextColored(m.ProfitEx >= 0 ? GreenCol : RedCol, divHist
                    ? $"{m.ProfitEx / rate:+0.##;-0.##;0} div"
                    : $"{m.ProfitEx:+0.0;-0.0;0} ex");
                ImGui.SameLine();
                ImGui.TextDisabled($"· {FormatDuration(TimeSpan.FromSeconds(m.ActiveSeconds))}");
                ImGui.Separator();

                if (ImGui.BeginTable("map_loot_tbl", 3,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
                {
                    ImGui.TableSetupColumn("Item");
                    ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 60);
                    ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 90);
                    ImGui.TableHeadersRow();

                    foreach (var line in m.Loot)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(line.Label);
                        ImGui.TableNextColumn();
                        ImGui.TextColored(line.Count >= 0 ? GreenCol : RedCol, $"{line.Count:+0;-0}");
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(!line.Priced ? "—"
                            : divHist ? $"{line.Ex / rate:+0.##;-0.##;0} div"
                            : $"{line.Ex:+0.0;-0.0;0} ex");
                    }

                    ImGui.EndTable();
                }
            }

            ImGui.End();
            if (!open)
            {
                this.lootMap = null;
                this.lootRun = null;
            }
        }

        // Compact-style table of the current session's maps (newest first) for the plugin settings, with
        // a per-row Loot button that opens the same loot window history uses — re-priced live.
        private void DrawActiveSessionTable()
        {
            if (this.completed.Count == 0)
            {
                ImGui.TextDisabled("No maps completed yet this session.");
                return;
            }

            var rate = this.priceCache.DivineToExaltedRate;
            bool div = this.Settings.ShowPricesInDivineOnly && rate > 0;

            if (ImGui.BeginTable("active_runs_tbl", 4,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                    new System.Numerics.Vector2(0f, 200f)))
            {
                ImGui.TableSetupColumn("Map");
                ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableHeadersRow();

                MapRun? toOpen = null;
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
                    ImGui.TextColored(ex >= 0 ? GreenCol : RedCol, div
                        ? $"{ex / rate:+0.##;-0.##;0} div"
                        : $"{ex:+0.0;-0.0;0} ex");
                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton($"Loot##a{i}"))
                    {
                        toOpen = r;
                    }
                }

                ImGui.EndTable();

                if (toOpen != null)
                {
                    this.lootMap = null;
                    this.lootRun = toOpen;
                }
            }
        }
    }
}
