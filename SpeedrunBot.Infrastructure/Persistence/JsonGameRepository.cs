using System.Text.Json;
using SpeedrunBot.Application.Interfaces;

namespace SpeedrunBot.Infrastructure.Persistence;

public class JsonGameRepository : IGameRepository
{
    private readonly string _filePath = "tracked_games.json";
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public async Task<List<string>> GetTrackedGamesAsync()
    {
        if (!File.Exists(_filePath)) return new List<string>();

        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public async Task AddGameAsync(string gameId)
    {
        await _lock.WaitAsync();
        try
        {
            var games = await GetTrackedGamesAsync();
            if (!games.Contains(gameId))
            {
                games.Add(gameId);
                var json = JsonSerializer.Serialize(games, _options);
                await File.WriteAllTextAsync(_filePath, json);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}