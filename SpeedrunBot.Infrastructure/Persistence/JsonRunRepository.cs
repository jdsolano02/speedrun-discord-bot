using System.Text.Json;
using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Infrastructure.Persistence;

public class JsonRunRepository : IRunRepository
{
    private readonly string _filePath = "runs_database.json";
    private List<RunRecord> _runs = new();
    private static readonly SemaphoreSlim _fileLock = new(1, 1);

    public JsonRunRepository()
    {
        if (File.Exists(_filePath))
        {
            var json = File.ReadAllText(_filePath);
            _runs = JsonSerializer.Deserialize<List<RunRecord>>(json) ?? new();
        }
    }

    public Task<RunRecord?> GetPersonalBestAsync(string runnerId, string gameFullName, string categoryName)
    {
        return Task.FromResult(_runs.FirstOrDefault(r =>
            r.RunnerId == runnerId && r.GameFullName == gameFullName && r.CategoryName == categoryName));
    }

    public Task<RunRecord?> GetCountryBestAsync(string gameFullName, string categoryName)
    {
        return Task.FromResult(_runs
            .Where(r => r.GameFullName == gameFullName && r.CategoryName == categoryName)
            .OrderBy(r => r.TimeInSeconds)
            .FirstOrDefault());
    }

    public Task<int> GetNationalRankAsync(string gameFullName, string categoryName, double timeInSeconds)
    {
        var orderedRuns = _runs
            .Where(r => r.GameFullName == gameFullName && r.CategoryName == categoryName)
            .OrderBy(r => r.TimeInSeconds)
            .ToList();

        int index = orderedRuns.FindIndex(r => r.TimeInSeconds == timeInSeconds);
        return Task.FromResult(index >= 0 ? index + 1 : 1);
    }

    public async Task SavePersonalBestAsync(RunRecord run)
    {
        await _fileLock.WaitAsync();
        try
        {
            _runs.RemoveAll(r => r.RunnerId == run.RunnerId && r.GameFullName == run.GameFullName && r.CategoryName == run.CategoryName);
            _runs.Add(run);
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(_runs, jsonOptions));
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public Task<List<RunRecord>> GetRankingAsync(string gameFullName, string categoryName)
    {
        // Si no hay filtros, devolvemos todo (Útil para Autocomplete y /top)
        if (string.IsNullOrWhiteSpace(gameFullName) && string.IsNullOrWhiteSpace(categoryName))
        {
            return Task.FromResult(_runs.OrderBy(r => r.TimeInSeconds).ToList());
        }

        var ranking = _runs
            .Where(r => (string.IsNullOrEmpty(gameFullName) || r.GameFullName.Equals(gameFullName, StringComparison.OrdinalIgnoreCase)) &&
                        (string.IsNullOrEmpty(categoryName) || r.CategoryName.Equals(categoryName, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r.TimeInSeconds)
            .ToList();

        return Task.FromResult(ranking);
    }
}