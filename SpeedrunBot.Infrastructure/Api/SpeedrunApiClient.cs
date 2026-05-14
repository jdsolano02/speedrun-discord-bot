using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Infrastructure.Api;

// Implementation of the Speedrun.com API client using HttpClient.
public class SpeedrunApiClient(HttpClient httpClient) : ISpeedrunApi
{
    private const string BaseUrl = "https://www.speedrun.com/api/v1";
    private const int ApiDelayMs = 1000; // Time to wait between requests to avoid rate limits.

    // Executes a GET request with automatic retry logic for transient errors, but FAILS FAST on Rate Limits.
    private async Task<T?> GetWithRateLimitAsync<T>(string url)
    {
        int maxRetries = 3;
        for (int i = 0; i < maxRetries; i++)
        {
            var response = await httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<T>();

            // If it's a 420 or 429, we DO NOT wait internally. We throw an exception IMMEDIATELY
            // so the Worker catches it and triggers the global "Gearbox" cooldown and concurrency reduction.
            if ((int)response.StatusCode == 429 || (int)response.StatusCode == 420)
            {
                throw new HttpRequestException($"RateLimit_{(int)response.StatusCode}");
            }

            if ((int)response.StatusCode == 404) return default;

            response.EnsureSuccessStatusCode();
        }
        return default;
    }

    // Main scan logic to find latest runs for a specific country across multiple games.
    public async Task<List<RunRecord>> GetLatestCountryRunsAsync(string countryCode, string[] gamesToScan)
    {
        var allRuns = new List<RunRecord>();

        foreach (var abbr in gamesToScan)
        {
            try
            {
                var gameInfo = await GetWithRateLimitAsync<ApiResponse<GameDto>>($"{BaseUrl}/games/{abbr}");
                if (gameInfo?.Data == null) continue;

                var categoriesResponse = await GetWithRateLimitAsync<ApiResponse<List<CategoryDto>>>($"{BaseUrl}/games/{abbr}/categories");
                if (categoriesResponse?.Data == null) continue;

                // Only scan full-game categories
                foreach (var cat in categoriesResponse.Data.Where(c => c.Type == "per-game"))
                {
                    var variablesResponse = await GetWithRateLimitAsync<ApiResponse<List<VariableDto>>>($"{BaseUrl}/categories/{cat.Id}/variables");
                    var subcategories = variablesResponse?.Data?.Where(v => v.IsSubcategory).ToList() ?? new List<VariableDto>();

                    await Task.Delay(ApiDelayMs);

                    if (subcategories.Count == 0)
                    {
                        await FetchAndParseRuns(abbr, cat.Id, "", cat.Name, countryCode, gameInfo.Data, allRuns);
                    }
                    else
                    {
                        var combinations = GenerateCombinations(subcategories);
                        foreach (var combo in combinations)
                        {
                            await FetchAndParseRuns(abbr, cat.Id, combo.QueryString, $"{cat.Name} ({combo.Label})", countryCode, gameInfo.Data, allRuns);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // If the exception is our specific RateLimit, let it bubble up directly to the Worker without logging it here.
                if (ex.Message.Contains("RateLimit_420") || ex.Message.Contains("RateLimit_429"))
                    throw;

                // Log any other unexpected errors normally.
                Console.WriteLine($"❌ Error processing game '{abbr}': {ex.Message}");
            }
        }
        return allRuns;
    }

    // Resolves a username to a unique Speedrun.com User ID.
    public async Task<(string Id, string Name)?> GetUserByNameAsync(string username)
    {
        var url = $"{BaseUrl}/users/{username}";
        var response = await GetWithRateLimitAsync<ApiResponse<PlayerDetailDto>>(url);

        if (response?.Data == null) return null;
        return (response.Data.Id!, response.Data.Names.International);
    }

    // Retrieves all verified full-game personal bests for a single runner.
    public async Task<List<RunRecord>> GetUserPersonalBestsAsync(string userId, string userName)
    {
        var url = $"{BaseUrl}/users/{userId}/personal-bests?embed=game,category";
        var response = await GetWithRateLimitAsync<ApiResponse<List<UserPbItemDto>>>(url);

        if (response?.Data == null) return new List<RunRecord>();

        var runs = new List<RunRecord>();
        foreach (var item in response.Data)
        {
            if (!string.IsNullOrEmpty(item.Run.Level)) continue; // Skip individual levels.

            var gameInfo = item.Game.Data;
            var catInfo = item.Category.Data;

            string varString = item.Run.Values != null && item.Run.Values.Any()
                ? string.Join("&", item.Run.Values.Select(kvp => $"var-{kvp.Key}={kvp.Value}"))
                : "";

            runs.Add(new RunRecord
            {
                RunnerId = userId,
                RunnerName = userName,
                GameId = gameInfo.Id,
                GameFullName = gameInfo.Names.International,

                // NEW: Mapped the Category ID to store it in the database
                CategoryId = catInfo.Id,

                CategoryName = catInfo.Name,
                TimeInSeconds = item.Run.Times.PrimaryT,
                RunLink = item.Run.Weblink,
                GameThumbnail = gameInfo.Assets.CoverLarge.Uri,
                WorldRank = item.Place,
                DateSubmitted = item.Run.Submitted ?? item.Run.Date,
                TotalGlobalRunners = 0,
                VariablesString = varString
            });
        }

        await Task.Delay(ApiDelayMs);
        return runs;
    }

    // Internal helper to fetch leaderboards and filter runners by country code.
    private async Task FetchAndParseRuns(string abbr, string catId, string queryParams, string fullCategoryName, string countryCode, GameDto gameInfo, List<RunRecord> allRuns)
    {
        var url = $"{BaseUrl}/leaderboards/{abbr}/category/{catId}?embed=players";
        if (!string.IsNullOrEmpty(queryParams)) url += $"&{queryParams}";

        var lbResponse = await GetWithRateLimitAsync<ApiResponse<LeaderboardDto>>(url);
        if (lbResponse?.Data == null) return;

        //Count the total number of runs in this leaderboard to calculate competitive weight later.
        int totalRunnersInLeaderboard = lbResponse.Data.Runs.Count;

        // Strict country code matching to prevent false positives (fixes Pou bug)
        var crPlayers = lbResponse.Data.Players.Data
            .Where(p => !string.IsNullOrEmpty(p.Id) &&
                        p.Location?.Country?.Code != null &&
                        p.Location.Country.Code.Equals(countryCode, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(p => p.Id)
            .ToDictionary(p => p.Id!, p => p.Names.International);

        foreach (var runItem in lbResponse.Data.Runs)
        {
            var validCountryPlayers = runItem.Run.Players
                .Where(pl => !string.IsNullOrEmpty(pl.Id) && crPlayers.ContainsKey(pl.Id));

            foreach (var pLink in validCountryPlayers)
            {
                allRuns.Add(new RunRecord
                {
                    RunnerId = pLink.Id!,
                    RunnerName = crPlayers[pLink.Id!],
                    GameId = gameInfo.Id,
                    GameFullName = gameInfo.Names.International,

                    // NEW: Mapped the Category ID during global massive scans
                    CategoryId = catId,

                    CategoryName = fullCategoryName,
                    TimeInSeconds = runItem.Run.Times.PrimaryT,
                    RunLink = runItem.Run.Weblink,
                    GameThumbnail = gameInfo.Assets.CoverLarge.Uri,
                    WorldRank = runItem.Place,
                    DateSubmitted = runItem.Run.Submitted ?? runItem.Run.Date,
                    TotalGlobalRunners = totalRunnersInLeaderboard,
                    VariablesString = queryParams ?? ""
                });
            }
        }
        await Task.Delay(ApiDelayMs);
    }

    // Recursive logic to handle all possible combinations of subcategories (Variables).
    private List<(string QueryString, string Label)> GenerateCombinations(List<VariableDto> vars)
    {
        var results = new List<(string, string)>();
        GenerateRecursive(results, vars, 0, "", "");
        return results;
    }

    private void GenerateRecursive(List<(string, string)> results, List<VariableDto> vars, int index, string currentQuery, string currentLabel)
    {
        if (index == vars.Count)
        {
            results.Add((currentQuery.Trim('&'), currentLabel.TrimEnd(',', ' ')));
            return;
        }

        var currentVar = vars[index];
        foreach (var val in currentVar.Values.Values)
        {
            GenerateRecursive(results, vars, index + 1, $"{currentQuery}var-{currentVar.Id}={val.Key}&", $"{currentLabel}{val.Value.Label}, ");
        }
    }

    // --- DTOs for Speedrun.com JSON mapping ---

    public record ApiResponse<T>(T Data);
    public record GameDto(string Id, GameNames Names, GameAssets Assets);
    public record GameNames(string International);
    public record GameAssets([property: JsonPropertyName("cover-large")] AssetUri CoverLarge);
    public record AssetUri(string Uri);
    public record CategoryDto(string Id, string Name, string Type);
    public record VariableDto(string Id, [property: JsonPropertyName("is-subcategory")] bool IsSubcategory, VariableValuesDto Values);
    public record VariableValuesDto([property: JsonPropertyName("values")] Dictionary<string, VariableValueItemDto> Values);
    public record VariableValueItemDto(string Label);
    public record LeaderboardDto(List<RunItemDto> Runs, PlayersDto Players);
    public record RunItemDto(int Place, RunDto Run);
    public record RunDto(TimesDto Times, string Weblink, List<PlayerLinkDto> Players, string? Level, DateTime? Date, DateTime? Submitted, [property: JsonPropertyName("values")] Dictionary<string, string>? Values);
    public record TimesDto([property: JsonPropertyName("primary_t")] double PrimaryT);
    public record PlayerLinkDto(string? Id);
    public record PlayersDto([property: JsonPropertyName("data")] List<PlayerDetailDto> Data);
    public record PlayerDetailDto(string? Id, PlayerNamesDto Names, LocDto? Location);
    public record PlayerNamesDto(string International);
    public record LocDto(CountryDto? Country);
    public record CountryDto(string Code);
    public record UserPbItemDto(int Place, RunDto Run, SingleGameDto Game, SingleCategoryDto Category);
    public record SingleGameDto(GameDto Data);
    public record SingleCategoryDto(CategoryDto Data);
}