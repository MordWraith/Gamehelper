namespace LootTracker
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
        // poe.ninja PoE2 has TWO economy endpoint families. Both share the response shape we need
        // (core.rates.exalted = 1 Divine in Exalted; lines[].primaryValue = price in Divine), but
        // live under different paths and key different item kinds. The website's route slug differs
        // from the API `type=` param — the map below is captured from the site's own network calls
        // (see docs/poe-ninja-api.md). Adding a category means appending its API type here.

        // exchange/current/overview — the fungible/stackable economy (currency-like). lines[] join
        // items[] by id. Route slug → API type:
        //   currency→Currency  fragments→Fragments  abyssal-bones→Abyss  uncut-gems→UncutGems
        //   lineage-support-gems→LineageSupportGems  essences→Essences  soul-cores→SoulCores
        //   idols→Idols  runes→Runes  omens→Ritual  expedition→Expedition
        //   liquid-emotions→Delirium  breach-catalyst→Breach  verisium→Verisium
        private static readonly string[] ExchangeTypes =
        {
            "Currency", "Fragments", "Abyss", "UncutGems", "LineageSupportGems", "Essences",
            "SoulCores", "Idols", "Runes", "Ritual", "Expedition", "Delirium", "Breach", "Verisium",
        };

        // stash/current/item/overview — individually-listed gear (uniques, tablets). lines[] carry
        // name/baseType/icon/primaryValue inline (no items[] join). Route slug → API type:
        //   unique-jewels→UniqueJewels  unique-charms→UniqueCharms  unique-tablets→UniqueTablets
        //   precursor-tablets→PrecursorTablets
        private static readonly string[] ItemTypes =
        {
            "UniqueJewels", "UniqueCharms", "UniqueTablets", "PrecursorTablets",
        };

        private static readonly HttpClient http = CreateHttpClient();

        // poe.ninja quotes every `primaryValue` in the league's OWN base currency -- named by
        // `core.primary` -- and `core.rates` says how many of each OTHER currency one base unit buys,
        // so the base currency itself is absent from `rates`. The base is per league, not fixed:
        //   Runes of Aldur   primary="divine"   rates={exalted:520.2, chaos:10.4}
        //   Forbidden Rites  primary="exalted"  rates={divine:0.02028, chaos:0.5905}   <- no "exalted"
        // Reading rates.exalted unconditionally therefore returned 0 in Forbidden Rites, every price
        // became primaryValue * 0, and the panel showed no prices at all while the requests all
        // answered 200 with full rows -- nothing in the fetch path looked wrong.
        private static void ReadRates(JObject parsed, out double baseToEx, out double divToEx)
        {
            var core = parsed["core"];
            var rates = core?["rates"];
            var primary = core?["primary"]?.Value<string>() ?? string.Empty;

            // Units of `ccy` per one base unit. The base currency is implicitly 1 and never listed.
            double Per(string ccy) => string.Equals(primary, ccy, StringComparison.OrdinalIgnoreCase)
                ? 1.0
                : rates?[ccy]?.Value<double>() ?? 0;

            baseToEx = Per("exalted");
            var divPerBase = Per("divine");
            divToEx = divPerBase > 0 ? baseToEx / divPerBase : 0;
        }


        private static HttpClient CreateHttpClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("LootTracker/1.0 (gamehelper2-fork plugin)");
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

                // Per-FAMILY tallies of "answered 200 and parsed" vs "actually carried rows". Kept
                // apart because the two families react differently to an unknown type (exchange →
                // 200 with an empty lines[]; stash/item → an honest 404), and because a legitimately
                // dead league like Hardcore returns 0 exchange rows as its normal answer. Without
                // this split a wrong league name is indistinguishable from "no HTTP failures at all",
                // which is exactly the misleading case the old "all overview types failed ()" hit.
                var exchangeOk = 0;
                var exchangeRows = 0;
                var itemOk = 0;
                var itemRows = 0;

                foreach (var type in ExchangeTypes)
                {
                    try
                    {
                        var url = $"https://poe.ninja/poe2/api/economy/exchange/current/overview?league={leagueParam}&type={type}";
                        using var resp = await http.GetAsync(url).ConfigureAwait(false);
                        resp.EnsureSuccessStatusCode();
                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                        var parsed = JObject.Parse(json);
                        exchangeOk++;
                        exchangeRows += (parsed["lines"] as JArray)?.Count ?? 0;
                        ReadRates(parsed, out var localRate, out var localDivToEx);
                        if (localDivToEx > 0) divToEx = localDivToEx;

                        var nameById = new Dictionary<string, string>(StringComparer.Ordinal);
                        // {ninjaId → bare art-id} and {ninjaId → grade}, plus {bare art → set of grades}
                        // so we can tell a shared-icon tier family from a per-tier-icon family AFTER
                        // seeing every item in this type (the grade suffix is only valid for the former).
                        var bareArtById = new Dictionary<string, string>(StringComparer.Ordinal);
                        var gradeById = new Dictionary<string, int>(StringComparer.Ordinal);
                        var gradesPerArt = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
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
                                bareArtById[id!] = art!;
                                gradeById[id!] = grade;
                                if (!gradesPerArt.TryGetValue(art!, out var grades))
                                {
                                    grades = new HashSet<int>();
                                    gradesPerArt[art!] = grades;
                                }
                                grades.Add(grade);

                                var level = TrailingLevel(id!);
                                if (level >= 0) levelKeyById[id!] = art! + level.ToString();
                            }
                        }

                        // The game's BaseItemType.Id (our lookup key) equals the art-asset name, EXCEPT
                        // for shared-icon tier families (base / Greater / Perfect reuse ONE icon, e.g.
                        // Chaos = "CurrencyRerollRare", Greater = "…2", Perfect = "…3"). There the game
                        // appends a tier digit, so we mirror it. When each tier has its OWN icon
                        // (essences, soul cores — poe.ninja still prefixes the id "greater-"/"perfect-"
                        // but the art already encodes the tier), appending a digit invents a key that
                        // matches nothing. We detect a shared icon as one art that carries >1 grade.
                        var artById = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var kv in bareArtById)
                        {
                            var art = kv.Value;
                            var grade = gradeById[kv.Key];
                            var shared = gradesPerArt[art].Count > 1;
                            var artKey = (shared && grade > 1) ? art + grade.ToString() : art;
                            artById[kv.Key] = artKey;
                            // art-id → English name for ALL items (priced or not), so the overlay can
                            // show a readable label regardless of client language.
                            if (nameById.TryGetValue(kv.Key, out var nm))
                            {
                                var nk = Normalize(artKey);
                                if (nk.Length > 0) aggregatedArtNames[nk] = nm;
                                if (levelKeyById.TryGetValue(kv.Key, out var lvl))
                                {
                                    var lnk = Normalize(lvl);
                                    if (lnk.Length > 0) aggregatedArtNames[lnk] = nm;
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

                // Item/stash family: uniques & tablets. lines[] are inline (name + baseType + icon +
                // primaryValue), so there's no items[] id-join. Each line's icon is the item's own
                // art asset — for uniques that's the unique-specific .dds (e.g. ".../Voices.png"),
                // distinct from any base-item icon, so art-id keys don't collide. We key by both the
                // English display name and the art id; matching a memory-read unique still needs its
                // rendered name or visual-identity art (the overlay reads only the base metapath today),
                // so these mostly populate the English-name table and the art→name label map for now.
                foreach (var type in ItemTypes)
                {
                    try
                    {
                        var url = $"https://poe.ninja/poe2/api/economy/stash/current/item/overview?league={leagueParam}&type={type}";
                        using var resp = await http.GetAsync(url).ConfigureAwait(false);
                        resp.EnsureSuccessStatusCode();
                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                        var parsed = JObject.Parse(json);
                        itemOk++;
                        itemRows += (parsed["lines"] as JArray)?.Count ?? 0;
                        ReadRates(parsed, out var localRate, out var localDivToEx);
                        if (localDivToEx > 0) divToEx = localDivToEx;
                        var rate = localRate > 0 ? localRate : divToEx;

                        if (parsed["lines"] is JArray lines)
                        {
                            foreach (var ln in lines)
                            {
                                var name = ln["name"]?.Value<string>();
                                var primary = ln["primaryValue"]?.Value<double?>() ?? 0;
                                if (string.IsNullOrEmpty(name) || primary <= 0 || rate <= 0) continue;
                                var price = primary * rate;
                                // "Normal" / "Magic" / "Rare" for tablets; a roll-variant name for uniques.
                                var variant = ln["variant"]?.Value<string>();

                                // Name/art MAX across every variant — the stable fallback used for
                                // uniques (whose icon is already item-specific) and when the read item's
                                // exact rarity isn't listed.
                                var nk = Normalize(name!);
                                if (nk.Length > 0 && (!aggregated.TryGetValue(nk, out var pn) || price > pn))
                                    aggregated[nk] = price;

                                var art = ExtractArtId(ln["icon"]?.Value<string>());
                                if (!string.IsNullOrEmpty(art))
                                {
                                    var ak = Normalize(art);
                                    if (ak.Length > 0 && (!aggregatedArt.TryGetValue(ak, out var pa) || price > pa))
                                    {
                                        aggregatedArt[ak] = price;
                                        aggregatedArtNames[ak] = name!;
                                    }

                                    // Per-rarity key. Tablets list Normal/Magic/Rare under ONE shared
                                    // icon at very different prices, so a bare-art MAX would value a
                                    // white tablet as the rare-rolled ceiling. Keying by art+variant
                                    // keeps each rarity separate; the overlay matches it with the rarity
                                    // it reads off the item. (MAX still collapses same-rarity roll-variants.)
                                    if (!string.IsNullOrEmpty(variant))
                                    {
                                        var vk = Normalize(art + variant);
                                        if (vk.Length > 0 && (!aggregatedArt.TryGetValue(vk, out var pv) || price > pv))
                                        {
                                            aggregatedArt[vk] = price;
                                            aggregatedArtNames[vk] = name!;
                                        }
                                    }
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
                    var okTypes = exchangeOk + itemOk;
                    var rowsTotal = exchangeRows + itemRows;
                    var breakdown = $"exchange {exchangeOk} ok/{exchangeRows} rows, item {itemOk} ok/{itemRows} rows";
                    throw new InvalidOperationException(okTypes > 0 && rowsTotal == 0
                        ? $"league \"{leagueName}\": API answered 200 for {okTypes} type(s) but returned 0 rows " +
                          $"— wrong league name (use the API name, not the web slug) or no economy data " +
                          $"for this league [{breakdown}]"
                        : $"league \"{leagueName}\": no prices fetched — {okTypes} type(s) answered, " +
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
                        : $"league \"{leagueName}\": partial — skipped {string.Join(", ", failedTypes)}";
                }

                var dto = new PriceCacheDto
                {
                    League = league.Trim(),
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

        // Tier of a poe.ninja currency id slug: base=1, "greater-…"=2, "perfect-…"=3. Only meaningful
        // for shared-icon tier families (see the artById build above) — a per-tier-icon family carries
        // the tier in the art name itself and ignores this.
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
