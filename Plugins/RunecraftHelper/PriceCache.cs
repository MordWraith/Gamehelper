namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Text;
    using System.Threading.Tasks;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public enum PriceSyncStatus { Idle, Syncing, Ready, Error }

    // Fetches PoE2 prices from poe.ninja and exposes them keyed by in-game item name.
    //
    // poe.ninja's PoE2 economy API returns 3 top-level fields per overview type:
    //   core.rates.exalted        → 1 Divine in Exalted Orb units (the conversion rate)
    //   items[{id,name,...}]      → id → display-name lookup
    //   lines[{id,primaryValue}]  → id → price in Divine (the primary currency)
    //
    // We join lines+items on id, multiply primaryValue by the exalted rate, and keep the result
    // keyed by a normalized form of `name` (lowercase + alphanumerics only) so in-game names like
    // "Mystic Alloy" / "Orb of Alchemy" match regardless of spacing/case quirks.
    public sealed class PriceCache
    {
        private static readonly string[] OverviewTypes =
            { "Currency", "UncutGems", "Runes", "Idols", "Verisium", "Expedition" };

        private static readonly HttpClient http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("RunecraftHelper/1.0 (gamehelper2-fork plugin)");
            return c;
        }

        private readonly object gate = new();
        private Dictionary<string, double> pricesExalted = new(StringComparer.Ordinal);
        // Same prices keyed by the item's internal art id (icon filename, e.g. "CurrencyUpgradeToRare").
        // Language-independent — used to match items read off a non-English game client.
        private Dictionary<string, double> pricesByArt = new(StringComparer.Ordinal);
        // art-id → poe.ninja English display name. Lets the overlay show a readable English label for
        // items read off a non-English client (whose localized name the ImGui font may not render).
        private Dictionary<string, string> namesByArt = new(StringComparer.Ordinal);

        public PriceSyncStatus Status { get; private set; } = PriceSyncStatus.Idle;
        public DateTime LastSyncUtc { get; private set; } = DateTime.MinValue;
        public string LastError { get; private set; } = string.Empty;
        public double DivineToExaltedRate { get; private set; }

        public int PriceCount
        {
            get { lock (this.gate) return this.pricesExalted.Count; }
        }

        public bool TryGetExaltedPrice(string itemName, out double exaltedPrice)
        {
            exaltedPrice = 0;
            if (string.IsNullOrEmpty(itemName)) return false;
            var key = Normalize(itemName);
            if (key.Length == 0) return false;
            lock (this.gate)
            {
                return this.pricesExalted.TryGetValue(key, out exaltedPrice);
            }
        }

        // Match by internal art id (icon filename without extension), e.g. "CurrencyUpgradeToRare".
        // Works regardless of game-client language. Caller should try this first, then fall back
        // to TryGetExaltedPrice(displayName) for English clients / unmapped items.
        public bool TryGetPriceByArtId(string artId, out double exaltedPrice)
        {
            exaltedPrice = 0;
            if (string.IsNullOrEmpty(artId)) return false;
            var key = Normalize(artId);
            if (key.Length == 0) return false;
            lock (this.gate)
            {
                return this.pricesByArt.TryGetValue(key, out exaltedPrice);
            }
        }

        // poe.ninja English display name for an internal art id, e.g. "ColdRune" → "...". Used only to
        // show a readable label on non-English clients; matching/pricing is still by art id.
        public bool TryGetNameByArtId(string artId, out string name)
        {
            name = string.Empty;
            if (string.IsNullOrEmpty(artId)) return false;
            var key = Normalize(artId);
            if (key.Length == 0) return false;
            lock (this.gate)
            {
                return this.namesByArt.TryGetValue(key, out name!);
            }
        }

        // Load a previously-saved snapshot. Returns true if the file existed AND its data is
        // within the TTL — caller skips a network refresh in that case. A return of false with
        // a populated Status (Ready) means stale data is loaded and usable while a refresh runs.
        //
        // `expectedLeague` guards against showing another league's prices: the snapshot records the
        // league it was fetched for, and a mismatch is rejected BEFORE the dictionaries are filled,
        // so the overlay never gets a chance to render foreign prices as fresh. A pre-League cache
        // file deserializes to "" and therefore never matches — one extra price reload on the first
        // launch of this version, deliberately preferred over trusting an unlabeled cache.
        public bool TryLoadFromDisk(string filePath, int ttlMinutes, string expectedLeague)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                var content = File.ReadAllText(filePath);
                var dto = JsonConvert.DeserializeObject<PriceCacheDto>(content);
                if (dto == null || dto.Prices == null) return false;
                if (!string.Equals(dto.League ?? string.Empty, expectedLeague ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                lock (this.gate)
                {
                    this.pricesExalted = new Dictionary<string, double>(dto.Prices, StringComparer.Ordinal);
                    this.pricesByArt = dto.ArtPrices != null
                        ? new Dictionary<string, double>(dto.ArtPrices, StringComparer.Ordinal)
                        : new Dictionary<string, double>(StringComparer.Ordinal);
                    this.namesByArt = dto.ArtNames != null
                        ? new Dictionary<string, string>(dto.ArtNames, StringComparer.Ordinal)
                        : new Dictionary<string, string>(StringComparer.Ordinal);
                    this.DivineToExaltedRate = dto.DivineToExaltedRate;
                    this.LastSyncUtc = dto.LastSyncUtc;
                    this.Status = PriceSyncStatus.Ready;
                }

                var age = DateTime.UtcNow - this.LastSyncUtc;
                return age <= TimeSpan.FromMinutes(Math.Max(1, ttlMinutes));
            }
            catch (Exception ex)
            {
                lock (this.gate) this.LastError = $"load failed: {ex.Message}";
                return false;
            }
        }

        // Fire-and-forget. Status flips to Syncing → Ready / Error. Safe to spam-call:
        // a second call while one is in flight returns immediately.
        public void StartRefresh(string league, string filePath)
        {
            lock (this.gate)
            {
                if (this.Status == PriceSyncStatus.Syncing) return;
                this.Status = PriceSyncStatus.Syncing;
            }
            _ = Task.Run(() => this.RefreshAsync(league, filePath));
        }

        private async Task RefreshAsync(string league, string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(league))
                    throw new InvalidOperationException("League name is empty.");

                var aggregated = new Dictionary<string, double>(StringComparer.Ordinal);
                var aggregatedArt = new Dictionary<string, double>(StringComparer.Ordinal);
                var aggregatedArtNames = new Dictionary<string, string>(StringComparer.Ordinal);
                double divToEx = 0;

                // The API expects the economyLeagues `name` verbatim ("Runes of Aldur"), NOT the web
                // slug ("runesofaldur") — the slug yields 200 with an empty body.
                var leagueName = league.Trim();
                var leagueParam = Uri.EscapeDataString(leagueName).Replace("%20", "+");

                // One bad overview type (404 / renamed slug) must not nuke every other price, so
                // each type is fetched independently and failures are collected, not thrown.
                var failedTypes = new List<string>();

                // Tally of "answered 200 and parsed" vs "actually carried rows". Every type here comes
                // from ONE endpoint family (exchange), which answers a wrong league with 200 + an empty
                // lines[] rather than an error — so without counting rows a bogus league name is
                // indistinguishable from "no HTTP failures at all".
                var typesOk = 0;
                var rowsTotal = 0;
                foreach (var type in OverviewTypes)
                {
                    try
                    {
                        var url = $"https://poe.ninja/poe2/api/economy/exchange/current/overview?league={leagueParam}&type={type}";
                        using var resp = await http.GetAsync(url).ConfigureAwait(false);
                        resp.EnsureSuccessStatusCode();
                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                        var parsed = JObject.Parse(json);
                        typesOk++;
                        rowsTotal += (parsed["lines"] as JArray)?.Count ?? 0;
                        var localRate = parsed["core"]?["rates"]?["exalted"]?.Value<double>() ?? 0;
                        if (localRate > 0) divToEx = localRate;

                        var nameById = new Dictionary<string, string>(StringComparer.Ordinal);
                        // {ninjaId → composite key (art-id + grade suffix when grade>1)}. The composite
                        // key matches the game's BaseItemType.Id last segment (e.g. Greater Regal Orb
                        // = "CurrencyUpgradeMagicToRare2"), so 3 currency tiers that share one .dds icon
                        // get 3 distinct keys instead of all overwriting each other.
                        var artById = new Dictionary<string, string>(StringComparer.Ordinal);
                        // {ninjaId → art-id + level}. Leveled items (UncutGems / Thaumaturgic Flux)
                        // reuse ONE icon across all levels; poe.ninja encodes the level as a trailing
                        // "-<n>" in its id. Pairing art + level gives each level its own key, matched
                        // game-side by dds-art + the level parsed from BaseItemType.Id "…Level<n>".
                        var levelKeyById = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (parsed["items"] is JArray itemsArr)
                        {
                            foreach (var it in itemsArr)
                            {
                                var id = it["id"]?.Value<string>();
                                if (string.IsNullOrEmpty(id)) continue;
                                var name = it["name"]?.Value<string>();
                                if (!string.IsNullOrEmpty(name)) nameById[id!] = name!;
                                // image filename (no extension) is the game's internal art id, e.g.
                                // ".../CurrencyUpgradeToRare.png" → "CurrencyUpgradeToRare".
                                var art = ExtractArtId(it["image"]?.Value<string>());
                                if (string.IsNullOrEmpty(art)) continue;
                                var grade = DetectCurrencyGrade(id!);
                                // The game's BaseItemType.Id (our lookup key) equals the art-asset name,
                                // EXCEPT for shared-icon tier families (Regal/Transmute/Augment: every tier
                                // reuses one icon with no tier digit) where the game appends a single tier
                                // digit. So append the grade digit only when the art filename doesn't
                                // already carry its own tier digit — e.g. Jeweller's "...01/02/03" already
                                // match the game id verbatim, and appending would corrupt them ("...022").
                                var artKey = (grade == 1 || EndsWithDigit(art!)) ? art! : art! + grade.ToString();
                                artById[id!] = artKey;
                                // art-id → English name for ALL items (priced or not), so the overlay
                                // can show a readable label regardless of client language.
                                if (!string.IsNullOrEmpty(name))
                                {
                                    var nk = Normalize(artKey);
                                    if (nk.Length > 0) aggregatedArtNames[nk] = name!;
                                }

                                var level = TrailingLevel(id!);
                                if (level >= 0)
                                {
                                    var levelKey = art! + level.ToString();
                                    levelKeyById[id!] = levelKey;
                                    if (!string.IsNullOrEmpty(name))
                                    {
                                        var lnk = Normalize(levelKey);
                                        if (lnk.Length > 0) aggregatedArtNames[lnk] = name!;
                                    }
                                }
                            }
                        }

                        if (parsed["lines"] is JArray lines)
                        {
                            foreach (var ln in lines)
                            {
                                var id = ln["id"]?.Value<string>();
                                var primary = ln["primaryValue"]?.Value<double?>() ?? 0;
                                if (string.IsNullOrEmpty(id) || primary <= 0) continue;
                                var price = primary * localRate;

                                if (nameById.TryGetValue(id!, out var name))
                                {
                                    var key = Normalize(name);
                                    if (key.Length > 0) aggregated[key] = price;
                                }
                                if (artById.TryGetValue(id!, out var artKey))
                                {
                                    var k = Normalize(artKey);
                                    if (k.Length > 0) aggregatedArt[k] = price;
                                }
                                if (levelKeyById.TryGetValue(id!, out var levelKey))
                                {
                                    var lk = Normalize(levelKey);
                                    if (lk.Length > 0) aggregatedArt[lk] = price;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failedTypes.Add($"{type}: {ex.Message}");
                    }
                }

                if (aggregated.Count == 0)
                {
                    var breakdown = $"{typesOk} ok/{rowsTotal} rows";
                    throw new InvalidOperationException(typesOk > 0 && rowsTotal == 0
                        ? $"league \"{leagueName}\": API answered 200 for {typesOk} type(s) but returned 0 rows " +
                          $"— wrong league name (use the API name, not the web slug) or no economy data " +
                          $"for this league [{breakdown}]"
                        : $"league \"{leagueName}\": no prices fetched — {typesOk} type(s) answered, " +
                          $"{rowsTotal} row(s) total [{breakdown}]; failures: {string.Join("; ", failedTypes)}");
                }

                // Ensure the reference currencies themselves are queryable. lines[] for Currency
                // includes them (their primaryValue is their own price in Divine), but defending
                // against future API changes is cheap.
                if (divToEx > 0)
                {
                    aggregated[Normalize("Exalted Orb")] = 1.0;
                    aggregated[Normalize("Divine Orb")] = divToEx;
                }

                lock (this.gate)
                {
                    this.pricesExalted = aggregated;
                    this.pricesByArt = aggregatedArt;
                    this.namesByArt = aggregatedArtNames;
                    this.DivineToExaltedRate = divToEx;
                    this.LastSyncUtc = DateTime.UtcNow;
                    this.Status = PriceSyncStatus.Ready;
                    this.LastError = failedTypes.Count == 0
                        ? string.Empty
                        : $"partial — skipped {string.Join(", ", failedTypes)}";
                }

                var dto = new PriceCacheDto
                {
                    League = leagueName,
                    LastSyncUtc = this.LastSyncUtc,
                    DivineToExaltedRate = divToEx,
                    Prices = aggregated,
                    ArtPrices = aggregatedArt,
                    ArtNames = aggregatedArtNames,
                };
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                File.WriteAllText(filePath, JsonConvert.SerializeObject(dto, Formatting.Indented));
            }
            catch (Exception ex)
            {
                lock (this.gate)
                {
                    this.Status = PriceSyncStatus.Error;
                    this.LastError = ex.Message;
                }
            }
        }

        // Lowercase + drop everything that isn't a-z 0-9. Matches what the game UI shows
        // ("Mystic Alloy" / "Orb of Alchemy") against poe.ninja item names.
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c >= 'A' && c <= 'Z') sb.Append((char)(c + 32));
                else if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            }
            return sb.ToString();
        }

        // PoE2 currencies have up to 3 tiers — base, Greater, Perfect — that all reuse the same
        // .dds icon (Regal / Greater Regal / Perfect Regal → "CurrencyUpgradeMagicToRare.png").
        // poe.ninja encodes the tier in its `id` slug ("regal", "greater-regal-orb",
        // "perfect-regal-orb"); the game encodes it in BaseItemType.Id's trailing digit
        // ("CurrencyUpgradeMagicToRare", "...2", "...3"). Recover the tier from poe.ninja so we
        // can mirror the game's key format on both sides.
        // NB: this does NOT cover Uncut Gem level variants ("uncut-skill-gem-15") or Thaumaturgic
        // Flux levels — those need a different keying scheme (level is stored on the item
        // instance, not in BaseItemType).
        private static bool EndsWithDigit(string s) => s.Length > 0 && char.IsDigit(s[^1]);

        // poe.ninja id slug → trailing "-<n>" level (e.g. "thaumaturgic-flux-9" → 9), or -1 if none.
        // Requires the digits be preceded by '-' so "greater-regal-orb" / "perfect-flux" stay -1.
        private static int TrailingLevel(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            int i = id.Length;
            while (i > 0 && char.IsDigit(id[i - 1])) i--;
            if (i == id.Length || i == 0 || id[i - 1] != '-') return -1;
            return int.TryParse(id.AsSpan(i), out var n) ? n : -1;
        }

        public static int DetectCurrencyGrade(string ninjaId)
        {
            if (string.IsNullOrEmpty(ninjaId)) return 1;
            if (ninjaId.StartsWith("perfect-", StringComparison.Ordinal)) return 3;
            if (ninjaId.StartsWith("greater-", StringComparison.Ordinal)) return 2;
            return 1;
        }

        // poe.ninja image URLs end with the item's art asset, e.g.
        //   "/gen/image/<base64>/<hash>/CurrencyUpgradeToRare.png"
        // The filename (without extension) is the game's language-independent internal art id.
        public static string ExtractArtId(string? imageUrl)
        {
            if (string.IsNullOrEmpty(imageUrl)) return string.Empty;
            var s = imageUrl;
            int q = s.IndexOf('?');
            if (q >= 0) s = s.Substring(0, q);
            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s.Substring(slash + 1);
            int dot = s.LastIndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);
            return s;
        }

        private sealed class PriceCacheDto
        {
            public DateTime LastSyncUtc { get; set; }
            public double DivineToExaltedRate { get; set; }
            // League this snapshot was fetched for (poe.ninja API `name`). Empty in files written by
            // a pre-2026-09 build → treated as "unknown league" and rejected on load.
            public string League { get; set; } = string.Empty;

            public Dictionary<string, double> Prices { get; set; } = new();
            public Dictionary<string, double> ArtPrices { get; set; } = new();
            public Dictionary<string, string> ArtNames { get; set; } = new();
        }
    }
}
