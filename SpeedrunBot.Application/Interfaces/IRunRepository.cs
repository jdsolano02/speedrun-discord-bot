using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Application.Interfaces;

// Manages speedrun data storage and retrieval in the local database.
public interface IRunRepository
{
    // Gets a specific runner's PB.
    Task<RunRecord?> GetPersonalBestAsync(string runnerId, string gameFullName, string categoryName);

    // Gets the top record for a category in the country.
    Task<RunRecord?> GetCountryBestAsync(string gameFullName, string categoryName);

    // Calculates the rank position.
    Task<int> GetNationalRankAsync(string gameFullName, string categoryName, double timeInSeconds);

    // Returns the full leaderboard for a category.
    Task<List<RunRecord>> GetRankingAsync(string gameFullName, string categoryName);

    // Saves or updates a run in the database.
    Task SavePersonalBestAsync(RunRecord run);

    // NEW: Retrieves a list of game names ordered by their most recent run submissions.
    Task<List<string>> GetRecentlyActiveGamesAsync(int limit = 15);
}