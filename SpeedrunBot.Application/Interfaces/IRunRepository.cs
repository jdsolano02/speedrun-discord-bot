using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Application.Interfaces;

// Manages speedrun data storage and retrieval in the local database.
public interface IRunRepository
{
    Task<RunRecord?> GetPersonalBestAsync(string runnerId, string gameFullName, string categoryName); // Gets a specific runner's PB.
    Task<RunRecord?> GetCountryBestAsync(string gameFullName, string categoryName); // Gets the top record for a category in the country.
    Task<int> GetNationalRankAsync(string gameFullName, string categoryName, double timeInSeconds); // Calculates the rank position.
    Task<List<RunRecord>> GetRankingAsync(string gameFullName, string categoryName); // Returns the full leaderboard for a category.
    Task SavePersonalBestAsync(RunRecord run); // Saves or updates a run in the database.
}