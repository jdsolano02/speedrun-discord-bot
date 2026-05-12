using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Infrastructure.Api;

public class SpeedrunApiClient(HttpClient httpClient) : ISpeedrunApi
{
    private const string BaseUrl = "https://www.speedrun.com/api/v1";
    private const int ApiDelayMs = 1000;

    private async Task<T?> GetWithRateLimitAsync<T>(string url)
    {
        int maxRetries = 3;
        for (int i = 0; i < maxRetries; i++)
        {
            var response = await httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<T>();
            }

            if ((int)response.StatusCode == 429)
            {
                Console.WriteLine($"⚠️ Límite de la API alcanzado. Pausando 60 segundos... (Intento {i + 1}/{maxRetries})");
                await Task.Delay(60000);
                continue;
            }

            if ((int)response.StatusCode == 404)
            {
                return default;
            }

            response.EnsureSuccessStatusCode();
        }
        return default;
    }

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
                Console.WriteLine($"❌ Error procesando el juego '{abbr}': {ex.Message}");
                throw;
            }
        }
        return allRuns;
    }

    public async Task<(string Id, string Name)?> GetUserByNameAsync(string username)
    {
        var url = $"{BaseUrl}/users/{username}";
        var response = await GetWithRateLimitAsync<ApiResponse<PlayerDetailDto>>(url);

        if (response?.Data == null) return null;
        return (response.Data.Id!, response.Data.Names.International);
    }

    public async Task<List<RunRecord>> GetUserPersonalBestsAsync(string userId, string userName)
    {
        var url = $"{BaseUrl}/users/{userId}/personal-bests?embed=game,category";
        var response = await GetWithRateLimitAsync<ApiResponse<List<UserPbItemDto>>>(url);

        if (response?.Data == null) return new List<RunRecord>();

        var runs = new List<RunRecord>();
        foreach (var item in response.Data)
        {
            if (!string.IsNullOrEmpty(item.Run.Level)) continue;
            var gameInfo = item.Game.Data;
            var catInfo = item.Category.Data;
            var runInfo = item.Run;

            // --- AQUI ESTA EL CAMBIO #1 ---
            runs.Add(new RunRecord
            {
                RunnerId = userId,
                RunnerName = userName,
                GameId = gameInfo.Id,
                GameFullName = gameInfo.Names.International,
                CategoryName = catInfo.Name,
                TimeInSeconds = runInfo.Times.PrimaryT,
                RunLink = runInfo.Weblink,
                GameThumbnail = gameInfo.Assets.CoverLarge.Uri,
                WorldRank = item.Place
            });
        }

        await Task.Delay(ApiDelayMs);
        return runs;
    }

    private async Task FetchAndParseRuns(string abbr, string catId, string queryParams, string fullCategoryName, string countryCode, GameDto gameInfo, List<RunRecord> allRuns)
    {
        var url = $"{BaseUrl}/leaderboards/{abbr}/category/{catId}?embed=players";
        if (!string.IsNullOrEmpty(queryParams)) url += $"&{queryParams}";

        var lbResponse = await GetWithRateLimitAsync<ApiResponse<LeaderboardDto>>(url);
        if (lbResponse?.Data == null) return;

        var crPlayers = lbResponse.Data.Players.Data
            .Where(p => !string.IsNullOrEmpty(p.Id) && p.Location?.Country?.Code == countryCode)
            .DistinctBy(p => p.Id)
            .ToDictionary(p => p.Id!, p => p.Names.International);

        foreach (var runItem in lbResponse.Data.Runs)
        {
            var validCountryPlayers = runItem.Run.Players
                .Where(pl => !string.IsNullOrEmpty(pl.Id) && crPlayers.ContainsKey(pl.Id));

            foreach (var pLink in validCountryPlayers)
            {
                // --- AQUI ESTA EL CAMBIO #2 ---
                allRuns.Add(new RunRecord
                {
                    RunnerId = pLink.Id!,
                    RunnerName = crPlayers[pLink.Id!],
                    GameId = gameInfo.Id,
                    GameFullName = gameInfo.Names.International,
                    CategoryName = fullCategoryName,
                    TimeInSeconds = runItem.Run.Times.PrimaryT,
                    RunLink = runItem.Run.Weblink,
                    GameThumbnail = gameInfo.Assets.CoverLarge.Uri,
                    WorldRank = runItem.Place
                });
            }
        }
        await Task.Delay(ApiDelayMs);
    }

    private List<(string QueryString, string Label)> GenerateCombinations(List<VariableDto> vars)
    {
        var results = new List<(string, string)>();
        GenerateRecursive(vars, 0, "", "", results);
        return results;
    }

    private void GenerateRecursive(List<VariableDto> vars, int index, string currentQuery, string currentLabel, List<(string, string)> results)
    {
        if (index == vars.Count)
        {
            results.Add((currentQuery.Trim('&'), currentLabel.TrimEnd(',', ' ')));
            return;
        }

        var currentVar = vars[index];
        foreach (var val in currentVar.Values.Values)
        {
            GenerateRecursive(vars, index + 1, $"{currentQuery}var-{currentVar.Id}={val.Key}&", $"{currentLabel}{val.Value.Label}, ", results);
        }
    }

    public Task<List<RunRecord>> GetLatestCountryRunsAsync(string countryCode)
    {
        throw new NotImplementedException();
    }

    // --- DTOs ---
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
    public record RunDto(TimesDto Times, string Weblink, List<PlayerLinkDto> Players, string? Level);
    public record TimesDto([property: JsonPropertyName("primary_t")] double PrimaryT);
    public record PlayerLinkDto(string? Id);
    public record PlayersDto([property: JsonPropertyName("data")] List<PlayerDetailDto> Data);
    public record PlayerDetailDto(string? Id, string Rel, PlayerNamesDto Names, LocDto? Location);
    public record PlayerNamesDto(string International);
    public record LocDto(CountryDto? Country);
    public record CountryDto(string Code);
    public record UserPbItemDto(int Place, RunDto Run, SingleGameDto Game, SingleCategoryDto Category);
    public record SingleGameDto(GameDto Data);
    public record SingleCategoryDto(CategoryDto Data);
}