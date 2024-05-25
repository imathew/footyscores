using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FootyScores
{
    public partial class PlayerScores
    {
        // our custom scoring
        private static readonly Dictionary<string, int> SCORING = new()
        {
            { "K", 4 }, { "H", 2 }, { "G", 8 }, { "B", 1 }, { "T", 4 },
            { "FF", 2 }, { "FA", -2 }, { "SP", 2 }, { "IED", -2 },
            { "M", 1 }, { "HO", 1 }, { "CP", 1 }, { "R50", 1 }
        };

        // player positions
        private static readonly Dictionary<int, string> POSITIONS = new()
        {
            {1, "B" }, {2, "C" }, {3, "R" }, {4, "F"}
        };

        // squads and venues are fairly static so just define them here
        private const string SQUADS = """
            [{"id":10,"full_name":"City of Churches","name":"Crows","short_name":"ADE"}
            ,{"id":20,"full_name":"Fitzroy","name":"Lions","short_name":"BRL"}
            ,{"id":30,"full_name":"Premiers 2024","name":"Blues","short_name":"CAR"}
            ,{"id":40,"full_name":"Eastern Collingwood","name":"Magpies","short_name":"COL"}
            ,{"id":50,"full_name":"Essington","name":"Bombers","short_name":"ESS"}
            ,{"id":60,"full_name":"Yay Dockers!","name":"Dockers","short_name":"FRE"}
            ,{"id":70,"full_name":"Kingdom of Billy Brownless","name":"Cats","short_name":"GEE"}
            ,{"id":1000,"full_name":"Pointless Expansion Team #1","name":"Suns","short_name":"GCS"}
            ,{"id":1010,"full_name":"Greater Metropolitan Shire Of Western Sydney","name":"Giants","short_name":"GWS"}
            ,{"id":80,"full_name":"Wees and Poos","name":"Hawks","short_name":"HAW"}
            ,{"id":90,"full_name":"Central Business District","name":"Demons","short_name":"MEL"}
            ,{"id":100,"full_name":"Tim&apos;s latest team","name":"Kangaroos","short_name":"NTH"}
            ,{"id":110,"full_name":"Western Collingwood","name":"Power","short_name":"PTA"}
            ,{"id":120,"full_name":"Toby&apos;s Team","name":"Tigers","short_name":"RIC"}
            ,{"id":130,"full_name":"Jane Franklin Hall","name":"Saints","short_name":"STK"}
            ,{"id":160,"full_name":"South Melbourne","name":"Swans","short_name":"SYD"}
            ,{"id":150,"full_name":"All of WA except Freo","name":"Eagles","short_name":"WCE"}
            ,{"id":140,"full_name":"Footscray","name":"Bulldogs","short_name":"WBD"}]
            """;

        private const string VENUES = """
            [{"id":2,"name":"Blundstone Arena","short_name":"Bellerive","timezone":"Australia\/Hobart"}
            ,{"id":6,"name":"Adelaide Oval","short_name":"Adelaide","timezone":"Australia\/Adelaide"}
            ,{"id":9,"name":"Accor Stadium","short_name":"Stadium Aus","timezone":"Australia\/Sydney"}
            ,{"id":20,"name":"Gabba","short_name":"Gabba","timezone":"Australia\/Brisbane"}
            ,{"id":30,"name":"GMHBA Stadium","short_name":"Kardinia","timezone":"Australia\/Melbourne"}
            ,{"id":40,"name":"Melbourne Cricket Ground","short_name":"MCG","timezone":"Australia\/Melbourne"}
            ,{"id":43,"name":"ENGIE Stadium","short_name":"Showgrounds","timezone":"Australia\/Sydney"}
            ,{"id":50,"name":"Ikon Park","short_name":"Princes Park","timezone":"Australia\/Melbourne"}
            ,{"id":60,"name":"Sydney Cricket Ground","short_name":"SCG","timezone":"Australia\/Sydney"}
            ,{"id":81,"name":"People First Stadium","short_name":"Carrara","timezone":"Australia\/Brisbane"}
            ,{"id":150,"name":"Manuka Oval","short_name":"Manuka","timezone":"Australia\/Canberra"}
            ,{"id":160,"name":"TIO Stadium","short_name":"Darwin","timezone":"Australia\/Darwin"}
            ,{"id":181,"name":"Cazalys Stadium","short_name":"Cairns","timezone":"Australia\/Brisbane"}
            ,{"id":190,"name":"Marvel Stadium","short_name":"Docklands","timezone":"Australia\/Melbourne"}
            ,{"id":200,"name":"University of Tasmania Stadium","short_name":"York Park","timezone":"Australia\/Hobart"}
            ,{"id":313,"name":"Mars Stadium","short_name":"Ballarat","timezone":"Australia\/Melbourne"}
            ,{"id":374,"name":"Norwood Oval","short_name":"Norwood","timezone":"Australia\/Adelaide"}
            ,{"id":386,"name":"TIO Traeger Park","short_name":"Alice Springs","timezone":"Australia\/Darwin"}
            ,{"id":2925,"name":"Optus Stadium","short_name":"Perth","timezone":"Australia\/Perth"}
            ,{"id":3805,"name":"Adelaide Hills","short_name":"Adelaide Hills","timezone":"Australia\/Adelaide"}]
            """;

        private static readonly JsonArray? _squads = JsonNode.Parse(SQUADS)?.AsArray();
        private static readonly JsonArray? _venues = JsonNode.Parse(VENUES)?.AsArray();
        private static readonly HttpClient _httpClient = new();
        private static readonly Dictionary<string, string> _headers;
        private static readonly BlobServiceClient _blobServiceClient = new(Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING"));
        private static readonly BlobContainerClient _containerClient = _blobServiceClient.GetBlobContainerClient("playerscores-cache");
        private static readonly TimeZoneInfo _timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Australia/Melbourne");
        private static DateTimeOffset _now;
        private static DateTime _nowDate;

        private static readonly string _allowedOrigin;
        private static readonly string _apiBaseUrl;
        private static readonly string _apiRoundsUrl;
        private static readonly string _apiPlayersUrl;
        private static readonly string _outputCacheFilename;
        private static readonly string _playersCacheFilename;
        private static readonly int _playerPreviewCount;
        private static readonly int _playerNameLengthSquish;
        private static readonly int _minCacheLifetimeSeconds;
        private static readonly int _roundChangeDays;
        private static int? _cachedCurrentRoundId;

        static PlayerScores()
        {
            var config = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .Build();

            _allowedOrigin = config["AllowedOrigin"]!;
            _apiBaseUrl = config["API_BASE_URL"]!;
            _apiRoundsUrl = config["API_ROUNDS_URL"]!;
            _apiPlayersUrl = config["API_PLAYERS_URL"]!;
            _outputCacheFilename = config["OUTPUT_CACHE_FILENAME"]!;
            _playersCacheFilename = config["PLAYERS_CACHE_FILENAME"]!;

            _playerPreviewCount = config.GetValue("PLAYER_PREVIEW_COUNT", 0);               // the minimum top players to show for upcoming matches (but will always show at least one from each team)
            _playerNameLengthSquish = config.GetValue("PLAYER_NAME_LENGTH_SQUISH", 20);     // squash the font of longer names to reduce table size
            _minCacheLifetimeSeconds = config.GetValue("MIN_CACHE_LIFETIME_SECONDS", 30);   // cache any API calls for at least this long
            _roundChangeDays = config.GetValue("ROUND_CHANGE_DAYS", 2);                     // how many days from the next round do we switch to it?             

            _headers = new Dictionary<string, string>
            {
                { "Content-Type", "text/plain; charset=utf-8" },
                { "Access-Control-Allow-Origin", _allowedOrigin },
                { "Access-Control-Allow-Methods", "GET, POST, OPTIONS" },
                { "Access-Control-Allow-Headers", "Content-Type" },
                { "X-Robots-Tag", "noindex, nofollow"}
            };
        }

        [Function("PlayerScores")]
        public static async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
        {
            // get the current time to use for the rest of this call
            _now = new DateTimeOffset(TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.UtcNow.DateTime, _timeZoneInfo), _timeZoneInfo.GetUtcOffset(DateTimeOffset.UtcNow));
            _nowDate = _now.DateTime.Date;

            // ensure the storage container is present
            _containerClient.CreateIfNotExistsAsync().Wait();

            // check if the "round" parameter is present in the query string
            var roundValue = req.Query.Get("round");
            var roundNumber = !string.IsNullOrEmpty(roundValue) && int.TryParse(roundValue, out var r) ? r : (int?)null;

            var freshValue = req.Query.Get("fresh");
            var fresh = !string.IsNullOrEmpty(freshValue) && freshValue == "1";

            var currentRound = await GetCurrentRoundAsync(roundNumber);

            if (currentRound != null)
            {
                var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
                foreach (var header in _headers)
                {
                    response.Headers.Add(header.Key, header.Value);
                }

                using (var outputStream = new MemoryStream())
                {
                    using (var gzipStream = new GZipStream(outputStream, CompressionMode.Compress))
                    {
                        // generate the HTML output
                        string htmlOutput = await GenerateHtmlOutputAsync(currentRound, fresh);
                        var bytes = Encoding.UTF8.GetBytes(htmlOutput);
                        await gzipStream.WriteAsync(bytes);
                    }

                    // compress and b64 encode
                    var compressedBytes = outputStream.ToArray();
                    var base64Data = Convert.ToBase64String(compressedBytes);
                    await response.WriteStringAsync(base64Data);
                }

                return response;
            }

            // return an error if it gets this far
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            foreach (var header in _headers)
            {
                errorResponse.Headers.Add(header.Key, header.Value);
            }
            return errorResponse;
        }

        private static async Task<string> GenerateHtmlOutputAsync(JsonNode currentRound, bool fresh = false)
        {
            var htmlBuilder = new StringBuilder();
            DateTimeOffset lastModified = GetNow();
            var roundStr = currentRound["id"];

            // get and/or save to the cache, as appropriate
            (string? cachedHtml, DateTimeOffset cachedLastModified) = await GetCachedDataAsync($"{roundStr}_{_outputCacheFilename}",
                () => GetCacheExpiry(currentRound),
                async () =>
                {
                    htmlBuilder.Append($@"
<h1 title='Updated: {lastModified:MMMM d, h:mmtt}' class='refresh-button'>The Masters &ndash; Round {roundStr}</h1>
<table>
");

                    if (currentRound["matches"] is JsonArray matches)
                    {
                        var (players, _) = await GetPlayerDataAsync(currentRound, fresh);
                        var statsData = await GetPlayerStatsAsync(currentRound);
                        var scores = statsData?["playerScores"] as JsonObject;

                        htmlBuilder.Append(GenerateMatchHtml(matches, players!, scores!, _squads!, _venues!, statsData));
                    }

                    htmlBuilder.AppendLine(@"</table>");

                    // cache the data compressed
                    return CompressData(htmlBuilder.ToString());
                }
                , fresh);

            if (cachedHtml == null)
            {
                cachedHtml = htmlBuilder.ToString();
                lastModified = GetNow();
            }
            else
            {
                // decompress the data
                cachedHtml = DecompressData(cachedHtml);
                lastModified = cachedLastModified;
            }

            var nowStr = lastModified.ToString("MMMM d, h:mmtt");
            var htmlOutput = cachedHtml.Replace("class='refresh-button'>", $"title='{nowStr}' class='refresh-button'>");

            return htmlOutput;
        }

        private static string GenerateMatchHtml(JsonArray matches, JsonArray players, JsonObject scores, JsonArray squads, JsonArray venues, JsonObject? statsData)
        {
            var htmlBuilder = new StringBuilder();

            var sortedMatches = matches.OrderBy(m => m, new MatchComparer());

            foreach (var m in sortedMatches)
            {
                var status = m!["status"]?.ToString() ?? string.Empty;

                htmlBuilder.AppendLine($@"
<thead><tr class='blank_header'><td colspan='18'></td></tr>");

                htmlBuilder.Append(GetMatchHtml(m, squads!, venues!));

                htmlBuilder.AppendLine($@"
<tr class='stats_header'><th colspan='2' class='minion'>Minion</th><th class='pos_head'>Pos</th><th>AF</th><th>ToG</th>{string.Join(string.Empty, SCORING.Select(stat => $"<th title='{stat.Value}' class='stat_head'>{stat.Key}</th>"))}</tr></thead>");

                if (players != null)
                {
                    var matchPlayers = GetMatchPlayers(m, players);
                    htmlBuilder.Append(GetPlayersHtml(matchPlayers.Cast<JsonNode>(), scores!, squads!, statsData, status));
                }
                else
                {
                    htmlBuilder.AppendLine(@"</thead>");
                }
            }

            return htmlBuilder.ToString();
        }

        private static async Task<JsonNode?> GetCurrentRoundAsync(int? roundNumber = null)
        {
            if (await MakeRequestAsync($"{_apiBaseUrl}/{_apiRoundsUrl}") is JsonArray roundsData)
            {
                if (roundNumber.HasValue)
                {
                    // return the round with the specified roundNumber
                    return roundsData.FirstOrDefault(roundData => roundData?["id"]?.GetValue<int>() == roundNumber.Value);
                }
                else
                {
                    // check if the memorycached round id is available
                    if (_cachedCurrentRoundId.HasValue)
                    {
                        // return the round with the cached round id
                        return roundsData.FirstOrDefault(roundData => roundData?["id"]?.GetValue<int>() == _cachedCurrentRoundId.Value);
                    }
                    else
                    {
                        // otherwise determine the current round based on our criteria
                        var now = GetNow();
                        var today = now.Date;
                        var roundChange = today.AddDays(_roundChangeDays);

                        var currentRound = roundsData
                            .Select(roundData =>
                            {
                                var start = DateTimeOffset.Parse(roundData!["start"]!.GetValue<string>());
                                var end = DateTimeOffset.Parse(roundData!["end"]!.GetValue<string>());
                                return new { Round = roundData, Start = start, End = end };
                            })
                            .OrderByDescending(roundInfo => roundInfo.Start)
                            .FirstOrDefault(roundInfo =>
                            {
                                var startDate = roundInfo.Start.Date;
                                return startDate <= today || (startDate >= today && startDate <= roundChange);
                            });

                        // cache the current round id in memory (will only last the life of the process)
                        _cachedCurrentRoundId = currentRound?.Round["id"]?.GetValue<int>();

                        return currentRound?.Round;
                    }
                }
            }

            return null;
        }

        private static async Task<(JsonArray? data, DateTimeOffset lastModified)> GetPlayerDataAsync(JsonNode currentRound, bool fresh = false)
        {
            // get and/or save to the cache, as appropriate
            (string? cachedData, DateTimeOffset lastModified) = await GetCachedDataAsync($"{_playersCacheFilename}",
                () => GetCacheExpiry(currentRound, false),
                async () => {
                    var jsonData = await MakeRequestAsync($"{_apiBaseUrl}/{_apiPlayersUrl}");
                    return CompressData(jsonData?.ToJsonString()); // cache the compressed data
                }
                , fresh);

            if (cachedData != null)
            {
                var decompressedData = DecompressData(cachedData); // return decompressed data
                return (JsonNode.Parse(decompressedData)?.AsArray(), lastModified);
            }
            return (null, lastModified);
        }

        private static string CompressData(string? data)
        {
            if (string.IsNullOrEmpty(data))
                return string.Empty;

            using var outputStream = new MemoryStream();
            using (var gZipStream = new GZipStream(outputStream, CompressionMode.Compress))
            using (var writer = new StreamWriter(gZipStream))
            {
                writer.Write(data);
            }
            return Convert.ToBase64String(outputStream.ToArray());
        }

        private static string DecompressData(string compressedData)
        {
            if (string.IsNullOrEmpty(compressedData))
                return string.Empty;

            var gZipBuffer = Convert.FromBase64String(compressedData);
            using var inputStream = new MemoryStream(gZipBuffer);
            using var gZipStream = new GZipStream(inputStream, CompressionMode.Decompress);
            using var reader = new StreamReader(gZipStream);
            return reader.ReadToEnd();
        }

        private static async Task<JsonObject?> GetPlayerStatsAsync(JsonNode currentRound)
        {
            int roundNumber = currentRound["id"]!.GetValue<int>();

            JsonObject? statsData = await MakeRequestAsync($"{_apiBaseUrl}/stats/{roundNumber}.json") as JsonObject;

            if (statsData != null)
            {
                var playerScores = new JsonObject();
                foreach (var playerStats in statsData)
                {
                    var playerId = playerStats.Key;
                    var score = playerStats.Value?.AsObject().Sum(p => SCORING.GetValueOrDefault(p.Key, 0) * p.Value!.GetValue<int>()) ?? 0;
                    playerScores[playerId] = score;
                }
                statsData["playerScores"] = playerScores;
            }

            return statsData;
        }

        private static string GetPlayersHtml(IEnumerable<JsonNode> players, JsonObject scores, JsonArray squads, JsonObject? statsData, string matchStatus)
        {
            var sortedPlayers = players
                .OrderByDescending(p =>
                {
                    var score = scores[p["id"]!.ToString()];
                    return score != null ? score.GetValue<int>() : 0;
                })
                .ThenBy(p =>
                {
                    var seasonRank = p["stats"]?["season_rank"]?.GetValue<int>();
                    return seasonRank == 0 ? int.MaxValue : seasonRank ?? int.MaxValue;
                });

            var htmlBuilder = new StringBuilder();
            var representedTeams = new HashSet<int>();
            int gameRank = 0;

            foreach (var player in sortedPlayers)
            {
                int playerSquadId = player["squad_id"]!.GetValue<int>();
                var squad = squads.FirstOrDefault(s => s!["id"]!.GetValue<int>() == playerSquadId);
                var playerHtml = GetPlayerHtml(player, scores, squad, statsData, matchStatus, gameRank);
                if (!string.IsNullOrEmpty(playerHtml))
                {
                    htmlBuilder.AppendLine(playerHtml);
                    representedTeams.Add(playerSquadId);
                }
                gameRank++;

                // if the match is only scheduled, only keep showing players until both teams are represented
                if (matchStatus == "scheduled" && gameRank >= _playerPreviewCount && representedTeams.Count >= 2)
                {
                    break;
                }
            }

            // return a "pending" notice if there's no data at this point, as it's probably near the start of a live match
            string ret = htmlBuilder.ToString();
            return string.IsNullOrWhiteSpace(ret)
                ? "<tr class='stats_row'><td></td><td class='pending' colspan='17'>No data yet</td></tr>"
                : ret;
        }

        private static string GetPlayerHtml(JsonNode player, JsonObject scores, JsonNode? squad, JsonObject? statsData, string matchStatus, int gameRank)
        {
            //var squad = squads.FirstOrDefault(s => s!["id"]!.GetValue<int>() == player["squad_id"]!.GetValue<int>());
            var team = squad?["name"]?.GetValue<string>() ?? "Unknown";
            var teamShort = squad?["short_name"]?.GetValue<string>() ?? "UNK";

            var playerName = $"{player["first_name"]} {player["last_name"]}";
            var playerClass = playerName.Length >= _playerNameLengthSquish ? "playername long" : "playername";
            var playerRank = $"Season rank: {player["stats"]?["season_rank"]?.GetValue<int>() ?? 0}";
            var playerAge = GetAgeString(player["dob"]?.ToString() ?? String.Empty);

            var playerPositions = player["positions"]?.AsArray()?.Select(p => p!.GetValue<int>()).ToList() ?? [];
            var sortedPositions = playerPositions.Select(p => new { Id = p, Letter = POSITIONS.GetValueOrDefault(p, "") })
                                                 .OrderBy(p => p.Id)
                                                 .Select(p => p.Letter);

            var positionString = string.Concat(sortedPositions);

            var playerRecord = statsData?[player["id"]!.ToString()];

            var statCells = new StringBuilder();

            if (playerRecord != null)
            {
                var score = scores[player["id"]!.GetValue<int>().ToString()]!.GetValue<int>();

                var playerStats = playerRecord?.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<int>()) ?? [];
                var tog = playerStats.GetValueOrDefault("TOG", 0);

                foreach (var stat in SCORING)
                {
                    int statValue = playerStats.GetValueOrDefault(stat.Key, 0);
                    statCells.Append($"<td title='{stat.Value * statValue}' class='stat'>{statValue}</td>");
                }

                return $@"<tr class='stats_row'><td title='{team}' class='playerteam {team.ToLower()}'>{teamShort}</td><td title='{playerAge}' class='{playerClass}'>{playerName}</td><td title='{playerRank}' class='pos'>{positionString}</td><td title='Game rank: {gameRank+1}' class='af'>{score}</td><td title='{GetTogScore(score, tog)}' class='tog'>{tog}</td>{statCells}</tr>";
            }
            // sub players won't have a playerrecord at the start of the match, so we can fill in the gaps
            else if (matchStatus != "complete")
            {
                foreach (var stat in SCORING)
                {
                    statCells.Append("<td class='stat'>0</td>");
                }

                return $@"<tr class='stats_row'><td title='{team}' class='playerteam {team.ToLower()}'>{teamShort}</td><td title='{playerAge}' class='{playerClass}'>{playerName}</td><td title='{playerRank}' class='pos'>{positionString}</td><td class='af'>0</td><td class='tog'>0</td>{statCells}</tr>";
            }

            return string.Empty;
        }

        private static string GetMatchHtml(JsonNode match, JsonArray squads, JsonArray venues)
        {
            var homeSquad = squads.FirstOrDefault(squad => squad!["id"]!.GetValue<int>() == match["home_squad_id"]!.GetValue<int>());
            var awaySquad = squads.FirstOrDefault(squad => squad!["id"]!.GetValue<int>() == match["away_squad_id"]!.GetValue<int>());
            var venueElem = venues.FirstOrDefault(v => v!["id"]!.GetValue<int>() == match["venue_id"]!.GetValue<int>());

            var homeTeam = homeSquad?["name"]?.ToString() ?? "Unknown";
            var homeTeamFull = homeSquad?["full_name"]?.ToString() ?? "Unknown Team";
            var awayTeam = awaySquad?["name"]?.ToString() ?? "Unknown";
            var awayTeamFull = awaySquad?["full_name"]?.ToString() ?? "Unknown Team";
            var venue = venueElem?["short_name"]?.ToString() ?? "Unknown";
            var venueAlt = venueElem?["name"]?.ToString() ?? "Unknown Ground";

            var timeStr = FormatMatchTime(match, venueElem?["timezone"]?.ToString() ?? "Australia/Melbourne");
            var matchScore = FormatMatchScore(match);

            return $@"<tr class='match_header {homeTeam.ToLower()}'><td colspan='18'><span title='{homeTeamFull}' class='teamname home'>{homeTeam}</span>{matchScore["home"]} - {matchScore["away"]}<span title='{awayTeamFull}' class='teamname away'>{awayTeam}</span><span class='matchtime'>{timeStr}</span> @ <span title='{venueAlt}' class='venuename'>{venue}</span></td></tr>";
        }

        private static Dictionary<string, string> FormatMatchScore(JsonNode match)
        {
            var homeGoals = match["home_goals"]?.GetValue<int?>() ?? 0;
            var homeBehinds = match["home_behinds"]?.GetValue<int?>() ?? 0;
            var homeScore = match["home_score"]?.GetValue<int?>() ?? 0;

            var awayGoals = match["away_goals"]?.GetValue<int?>() ?? 0;
            var awayBehinds = match["away_behinds"]?.GetValue<int?>() ?? 0;
            var awayScore = match["away_score"]?.GetValue<int?>() ?? 0;

            return new Dictionary<string, string>
            {
                { "home", $"<span title='{homeGoals}.{homeBehinds}' class='totalscore home'>{homeScore}</span>" },
                { "away", $"<span title='{awayGoals}.{awayBehinds}' class='totalscore away'>{awayScore}</span>" }
            };
        }

        private static string FormatMatchTime(JsonNode match, string timezoneStr)
        {
            if (match["status"]?.ToString() == "complete")
            {
                return "FT";
            }

            if (match["clock"] is JsonNode clock)
            {
                var quarter = clock["p"]?.ToString();
                var seconds = clock["s"]!.GetValue<int>();

                if (seconds < 0)
                {
                    return quarter switch
                    {
                        "Q1" => "QT",
                        "Q2" => "HT",
                        "Q3" => "3QT",
                        "Q4" => "FT",
                        _ => quarter ?? string.Empty
                    };
                }

                var minutes = seconds / 60;
                seconds %= 60;
                return $"{quarter} {minutes}:{seconds:D2}";
            }

            var matchDate = DateTimeOffset.Parse(match["date"]!.ToString());
            var matchDay = matchDate.DayOfWeek;

            // Check if matchDay is the next instance of that day of the week
            var nextInstanceOfMatchDay = DateTime.Today.AddDays(((int)matchDay - (int)DateTime.Today.DayOfWeek + 7) % 7);

            string matchDateStr;
            if (matchDate.Date == nextInstanceOfMatchDay)
            {
                matchDateStr = $"{matchDay.ToString()[..3]} {matchDate:h:mmtt}";
            }
            else
            {
                matchDateStr = $"{matchDay.ToString()[..3]} {matchDate:d MMM h:mmtt}";
            }

            // convert match time to local time
            var localDate = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(matchDate, timezoneStr);

            // check if local time is different from match time
            if (localDate.TotalOffsetMinutes != matchDate.TotalOffsetMinutes)
            {
                return $"{matchDateStr} ({localDate:h:mmtt})";
            }

            return matchDateStr;
        }

        private static IEnumerable<JsonNode?> GetMatchPlayers(JsonNode match, JsonArray players)
        {
            var homeSquadId = match["home_squad_id"]!.GetValue<int>();
            var awaySquadId = match["away_squad_id"]!.GetValue<int>();
            var matchIsPlaying = match["status"]?.GetValue<string>() == "playing";

            return players.Where(player =>
            {
                var squadId = player!["squad_id"]!.GetValue<int>();
                var playerIsPlaying = player!["status"]?.GetValue<string>() == "playing";
                return (squadId == homeSquadId || squadId == awaySquadId) && (playerIsPlaying || !matchIsPlaying);
            });
        }

        private static string GetAgeString(string dateOfBirth)
        {
            if (DateTime.TryParse(dateOfBirth, out DateTime dob))
            {
                var today = DateTime.Today;
                var age = today.Year - dob.Year;
                var months = today.Month - dob.Month;

                if (today.Day < dob.Day)
                {
                    months--;
                }

                if (months < 0)
                {
                    age--;
                    months += 12;
                }

                return $"{age} years, {months} months";
            }

            return "Ageless";
        }

        private static decimal GetTogScore(int score, int tog)
        {
            return tog == 0 ? 0 : Math.Round(score * (100 / (decimal)tog), 0);
        }

        private static async Task<JsonNode?> MakeRequestAsync(string url)
        {
            url = $"{url}?t={GetNow().ToUnixTimeSeconds()}";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            using var content = response.Content;
            var contentEncoding = content.Headers.ContentEncoding.FirstOrDefault();
            var stream = await content.ReadAsStreamAsync();

            if (contentEncoding != null && contentEncoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
            {
                using var gzipStream = new GZipStream(stream, CompressionMode.Decompress);
                return await JsonSerializer.DeserializeAsync<JsonNode>(gzipStream);
            }
            else
            {
                return await JsonSerializer.DeserializeAsync<JsonNode>(stream);
            }
        }

		private class MatchComparer : IComparer<JsonNode?>
		{
			public int Compare(JsonNode? x, JsonNode? y)
			{
				string? statusX = x?["status"]?.ToString();
				string? statusY = y?["status"]?.ToString();
		
				DateTime dateX = x?["date"]?.ToString()?.ToDateTime() ?? DateTime.MinValue;
				DateTime dateY = y?["date"]?.ToString()?.ToDateTime() ?? DateTime.MinValue;
		
				bool isTodayX = dateX.Date == _nowDate;
				bool isTodayY = dateY.Date == _nowDate;
		
				bool isPlayingX = statusX is not null && statusX.Equals("playing", StringComparison.OrdinalIgnoreCase);
				bool isPlayingY = statusY is not null && statusY.Equals("playing", StringComparison.OrdinalIgnoreCase);
		
				// 1. Playing games (sorted by reverse date)
				if (isPlayingX || isPlayingY)
					return isPlayingX == isPlayingY ? DateTime.Compare(dateY, dateX) : (isPlayingX ? -1 : 1);
		
				bool isScheduledX = statusX is not null && statusX.Equals("scheduled", StringComparison.OrdinalIgnoreCase);
				bool isScheduledY = statusY is not null && statusY.Equals("scheduled", StringComparison.OrdinalIgnoreCase);
		
				// 2. Today's scheduled games (sorted by date)
				if (isTodayX && isScheduledX || isTodayY && isScheduledY)
					return isTodayX && isScheduledX == isTodayY && isScheduledY ? DateTime.Compare(dateX, dateY) : (isTodayX && isScheduledX ? -1 : 1);
		
				// 3. Today's completed games (sorted by reverse date)
				if (isTodayX || isTodayY)
					return isTodayX == isTodayY ? DateTime.Compare(dateY, dateX) : (isTodayX ? -1 : 1);
		
				// 4. Other scheduled games (sorted by date)
				if (isScheduledX || isScheduledY)
					return isScheduledX == isScheduledY ? DateTime.Compare(dateX, dateY) : (isScheduledX ? -1 : 1);
		
				// 5. Other (sorted by reverse date) (should just be completed games from previous days)
				return DateTime.Compare(dateY, dateX);
			}
		}

        private static async Task<(string? data, DateTimeOffset lastModified)> GetCachedDataAsync(string blobName, Func<DateTimeOffset> getCacheExpiry, Func<Task<string?>> fetchDataAsync, bool fresh = false)
        {
            var blobClient = _containerClient.GetBlobClient(blobName);
            var cacheExpiry = getCacheExpiry();

            if (!fresh && cacheExpiry > GetNow() && await IsCacheValid(blobClient))
            {
                return await GetCachedData(blobClient);
            }

            string? fetchedData = await fetchDataAsync();
            if (fetchedData != null && cacheExpiry > GetNow())
            {
                await UpdateCache(blobClient, fetchedData, cacheExpiry);
            }

            return (fetchedData, GetNow());
        }

        private static async Task<bool> IsCacheValid(BlobClient blobClient)
        {
            if (!await blobClient.ExistsAsync())
            {
                return false;
            }

            var blobProperties = await blobClient.GetPropertiesAsync();
            if (!blobProperties.Value.Metadata.TryGetValue("ExpiresOn", out var expiresOnString) ||
                !DateTimeOffset.TryParse(expiresOnString, out var cachedExpiresOn) ||
                cachedExpiresOn <= GetNow())
            {
                return false;
            }

            return true;
        }

        private static async Task<(string data, DateTimeOffset lastModified)> GetCachedData(BlobClient blobClient)
        {
            var blobProperties = await blobClient.GetPropertiesAsync();
            var lastModified = blobProperties.Value.LastModified;
            using var stream = new MemoryStream();
            await blobClient.DownloadToAsync(stream);
            stream.Position = 0;
            using var reader = new StreamReader(stream);
            var data = await reader.ReadToEndAsync();
            return (data, lastModified);
        }

        private static async Task UpdateCache(BlobClient blobClient, string data, DateTimeOffset expiresOn)
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            using var stream = new MemoryStream(bytes);
            await blobClient.UploadAsync(stream, overwrite: true);
            var metadata = new Dictionary<string, string> { { "ExpiresOn", expiresOn.ToString("o") } };
            await blobClient.SetMetadataAsync(metadata);
            var httpHeaders = new BlobHttpHeaders
            {
                ContentType = "text/plain",
                CacheControl = $"public, max-age={(int)(expiresOn - GetNow()).TotalSeconds}"
            };
            await blobClient.SetHttpHeadersAsync(httpHeaders);
        }

        private static DateTimeOffset GetCacheExpiry(JsonNode currentRound, bool useShortCache = true)
        {
            var now = GetNow();
            var midnight = GetMidnight();
            var cacheExpiry = now; // if there's no round it's probabably the first load, so don't cache

            if (currentRound is JsonObject roundObject && roundObject["matches"] is JsonArray matches)
            {
                // use the short cache if any match in the current round is live
                if (useShortCache && matches.Any(match => match?["status"]?.ToString() == "playing"))
                {
                    cacheExpiry = now.AddSeconds(_minCacheLifetimeSeconds);
                }
                else
                {
                    // find the next scheduled match in the round
                    var nextScheduledMatch = matches.FirstOrDefault(match => match?["status"]?.ToString() == "scheduled");
                    if (nextScheduledMatch != null)
                    {
                        // cache until the start time of the next scheduled match
                        cacheExpiry = DateTimeOffset.Parse(nextScheduledMatch["date"]!.ToString());
                    }
                    else
                    {
                        // cache until midnight if no scheduled matches remain
                        cacheExpiry = midnight;
                    }
                }
            }

            // return the earliest option (latest cache is midnight)
            return cacheExpiry < midnight ? cacheExpiry : midnight;
        }

        private static DateTimeOffset GetMidnight()
        {
            // return the first moment of tomorrow
            var currentTime = GetNow();
            return new
                DateTimeOffset(currentTime.Year, currentTime.Month, currentTime.Day, 0, 0, 0, _timeZoneInfo.BaseUtcOffset)
                .AddDays(1);
        }

        // melb time, baby!
        private static DateTimeOffset GetNow()
        {
            return _now;
        }

    }

    public static class StringExtensions
    {
        public static DateTime? ToDateTime(this string? str)
        {
            return DateTime.TryParse(str, out DateTime date) ? date : null;
        }
    }
}
