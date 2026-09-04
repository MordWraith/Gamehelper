// SHARED FILE — kept byte-identical (except the namespace) in LootTracker / RunecraftHelper. Правишь здесь — перенеси в остальные копии.
namespace LootTracker
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    // One entry of poe.ninja's `economyLeagues` array (GET /poe2/api/data/index-state):
    //   {"name":"Runes of Aldur","url":"runesofaldur","displayName":"Runes of Aldur",
    //    "hardcore":false,"indexed":true}
    //
    // No Json attributes on purpose: Newtonsoft matches camelCase JSON to PascalCase members
    // case-insensitively, which is what the PoE1 reference implementation relies on too.
    //
    // `Name` is the ONLY field that may be sent as `?league=` — the API rejects (silently: 200 with an
    // empty body) the web slug in `Url`. `Url` is kept only for a potential poe.ninja deep link.
    // `Indexed` is NOT "has prices": leagues with indexed=false (and even finished leagues out of
    // `oldEconomyLeagues`) return full price data. It marks poe.ninja's current default league, so it
    // may be used to pick a default — never to label a league in the UI as having/not having economy.
    public sealed class NinjaLeague
    {
        public string Name { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public bool Hardcore { get; set; }

        public bool Indexed { get; set; }
    }

    // The poe.ninja PoE2 league list: fetched once, cached on disk, exposed as an immutable snapshot.
    // Shaped after PriceCache (lock-guarded snapshot, Status/LastError, fire-and-forget refresh) and
    // reuses its PriceSyncStatus enum rather than introducing a second near-identical one.
    public static class NinjaLeagues
    {
        // The list changes at most once per league launch; 12h is generous and keeps the plugin from
        // hammering the endpoint. Deliberately not a UI slider.
        private const int LeagueListTtlHours = 12;

        private const string IndexStateUrl = "https://poe.ninja/poe2/api/data/index-state";

        // Offline fallback: no network AND no cache file. Every entry is Indexed = false on purpose —
        // a hardcoded Indexed = true would go stale one league later and would then silently move the
        // user onto a dead league.
        private static readonly NinjaLeague[] BuiltIn =
        {
            new() { Name = "Runes of Aldur", Url = "runesofaldur", DisplayName = "Runes of Aldur", Hardcore = false, Indexed = false },
            new() { Name = "HC Runes of Aldur", Url = "runesofaldurhc", DisplayName = "HC Runes of Aldur", Hardcore = true, Indexed = false },
            new() { Name = "Standard", Url = "standard", DisplayName = "Standard", Hardcore = false, Indexed = false },
            new() { Name = "Hardcore", Url = "hardcore", DisplayName = "Hardcore", Hardcore = true, Indexed = false },
        };

        // Always present in the combo even when the API list is unavailable — both leagues exist
        // permanently on poe2 and give the user something that works offline.
        private static readonly string[] AlwaysOffered = { "Standard", "Hardcore" };

        private static readonly HttpClient http = CreateHttpClient();

        private static readonly object gate = new();

        private static IReadOnlyList<NinjaLeague> all = BuiltIn;
        private static DateTime fetchedUtc = DateTime.MinValue;
        private static PriceSyncStatus status = PriceSyncStatus.Idle;
        private static string lastError = string.Empty;

        // The TTL callers should pass to TryLoadFromDisk — so the value lives in exactly one place.
        public static int DefaultTtlHours => LeagueListTtlHours;

        private static HttpClient CreateHttpClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // UA выводим из namespace: файл общий для нескольких плагинов, а литерал врал бы
            // про источник запроса в том плагине, куда он скопирован.
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"{typeof(NinjaLeagues).Namespace}/1.0 (gamehelper2-fork plugin)");
            return c;
        }

        // Immutable snapshot. NEVER empty: it starts as the built-in fallback and is only ever
        // replaced by a non-empty list.
        public static IReadOnlyList<NinjaLeague> All
        {
            get { lock (gate) return all; }
        }

        // When the currently-held list was produced (disk or network). MinValue = built-in fallback.
        public static DateTime FetchedUtc
        {
            get { lock (gate) return fetchedUtc; }
        }

        // False while All is still the built-in fallback — i.e. we have no idea what the real league
        // list looks like, so "the saved league is gone" can't be concluded yet.
        public static bool IsLoaded
        {
            get { lock (gate) return fetchedUtc != DateTime.MinValue; }
        }

        public static PriceSyncStatus Status
        {
            get { lock (gate) return status; }
        }

        public static string LastError
        {
            get { lock (gate) return lastError; }
        }

        // True when the list is older than the TTL (or was never fetched) — caller should refresh.
        public static bool IsStale
        {
            get
            {
                lock (gate)
                {
                    if (fetchedUtc == DateTime.MinValue) return true;
                    return DateTime.UtcNow - fetchedUtc > TimeSpan.FromHours(LeagueListTtlHours);
                }
            }
        }

        // Load a previously-saved snapshot. Same semantics as PriceCache.TryLoadFromDisk:
        // true  = loaded AND within the TTL (caller may skip the network refresh);
        // false = either nothing usable was loaded, or data was loaded but is stale (refresh it).
        public static bool TryLoadFromDisk(string path, int ttlHours)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                var content = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(content)) return false;
                var dto = JsonConvert.DeserializeObject<LeagueCacheDto>(content);
                if (dto?.Leagues == null || dto.Leagues.Count == 0) return false;

                var clean = Sanitize(dto.Leagues);
                if (clean.Count == 0) return false;

                lock (gate)
                {
                    all = clean;
                    fetchedUtc = dto.FetchedUtc;
                    status = PriceSyncStatus.Ready;
                }

                return DateTime.UtcNow - dto.FetchedUtc <= TimeSpan.FromHours(Math.Max(1, ttlHours));
            }
            catch (Exception ex)
            {
                // Missing / empty / corrupt / wrong-schema file: keep whatever we already have (the
                // built-in fallback at worst) and let the caller trigger a network refresh.
                lock (gate) lastError = $"load failed: {ex.Message}";
                return false;
            }
        }

        // Fire-and-forget. Status flips Syncing → Ready / Error. Safe to spam-call: a second call
        // while one is in flight returns immediately.
        public static void StartRefresh(string path)
        {
            lock (gate)
            {
                if (status == PriceSyncStatus.Syncing) return;
                status = PriceSyncStatus.Syncing;
            }

            _ = Task.Run(() => RefreshAsync(path));
        }

        // Final combo content (see docs/obsidian poe2/PoeNinjaEconomyApi.md §1/§4):
        //   every economyLeagues entry as-is, + Standard/Hardcore as an offline fallback,
        //   + whatever the user currently has saved (so a league the API dropped, or a hand-typed
        //   one, never disappears from under the user). Deduped case-insensitively, saved value first.
        // No filtering by hardcore/indexed — the UI only groups.
        public static IEnumerable<string> ComboItems(string savedLeague)
        {
            var seen = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            var result = new List<string>();

            void Add(string? name)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                var n = name!.Trim();
                if (seen.Add(n)) result.Add(n);
            }

            Add(savedLeague);
            foreach (var lg in All) Add(lg.Name);
            foreach (var n in AlwaysOffered) Add(n);
            return result;
        }

        // The league entry behind a combo name. A name we don't know (hand-typed, or dropped by the
        // API) is returned as a bare entry rather than guessed at — no name-normalization layer here.
        public static NinjaLeague Resolve(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                foreach (var lg in All)
                {
                    if (string.Equals(lg.Name, name, StringComparison.InvariantCultureIgnoreCase)) return lg;
                }
            }

            return new NinjaLeague { Name = name ?? string.Empty, DisplayName = name ?? string.Empty };
        }

        public static string LabelOf(NinjaLeague lg) =>
            lg == null ? string.Empty : (string.IsNullOrWhiteSpace(lg.DisplayName) ? lg.Name : lg.DisplayName);

        // Is this league still offered by the API? Only meaningful while IsLoaded is true.
        public static bool Contains(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (var lg in All)
            {
                if (string.Equals(lg.Name, name, StringComparison.InvariantCultureIgnoreCase)) return true;
            }

            return false;
        }

        // poe.ninja's own default league: the indexed softcore one, else any indexed one. Used ONLY to
        // pick a default on a first run / when the saved league vanished from economyLeagues.
        public static bool TryPickDefault(out string name)
        {
            name = string.Empty;
            var snapshot = All;
            foreach (var lg in snapshot)
            {
                if (lg.Indexed && !lg.Hardcore && !string.IsNullOrWhiteSpace(lg.Name))
                {
                    name = lg.Name;
                    return true;
                }
            }

            foreach (var lg in snapshot)
            {
                if (lg.Indexed && !string.IsNullOrWhiteSpace(lg.Name))
                {
                    name = lg.Name;
                    return true;
                }
            }

            return false;
        }

        private static async Task RefreshAsync(string path)
        {
            try
            {
                using var resp = await http.GetAsync(IndexStateUrl).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                // Only `economyLeagues` matters: buildLeagues/snapshotVersions carry SSF variants that
                // have no economy at all, and oldEconomyLeagues are finished leagues.
                var leagues = JObject.Parse(json)["economyLeagues"]?.ToObject<List<NinjaLeague>>();
                var clean = Sanitize(leagues);
                if (clean.Count == 0)
                    throw new InvalidOperationException("index-state returned no economyLeagues entries");

                var fetched = DateTime.UtcNow;
                lock (gate)
                {
                    all = clean;
                    fetchedUtc = fetched;
                    status = PriceSyncStatus.Ready;
                    lastError = string.Empty;
                }

                if (!string.IsNullOrEmpty(path))
                {
                    var dto = new LeagueCacheDto { FetchedUtc = fetched, Leagues = new List<NinjaLeague>(clean) };
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonConvert.SerializeObject(dto, Formatting.Indented));
                }
            }
            catch (Exception ex)
            {
                // Keep the previous list (disk snapshot or built-in fallback) — All must never be empty.
                lock (gate)
                {
                    status = PriceSyncStatus.Error;
                    lastError = ex.Message;
                }
            }
        }

        // Drop null / nameless entries; a league without a `name` can't be sent to the API at all.
        private static List<NinjaLeague> Sanitize(List<NinjaLeague>? raw)
        {
            var result = new List<NinjaLeague>();
            if (raw == null) return result;
            var seen = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var lg in raw)
            {
                if (lg == null || string.IsNullOrWhiteSpace(lg.Name)) continue;
                lg.Name = lg.Name.Trim();
                if (!seen.Add(lg.Name)) continue;
                result.Add(lg);
            }

            return result;
        }

        private sealed class LeagueCacheDto
        {
            public DateTime FetchedUtc { get; set; }

            public List<NinjaLeague> Leagues { get; set; } = new();
        }
    }
}
