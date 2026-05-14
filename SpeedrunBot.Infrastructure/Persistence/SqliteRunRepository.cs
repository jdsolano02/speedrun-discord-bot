using Microsoft.EntityFrameworkCore;
using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Infrastructure.Persistence;

// SQLite implementation for storing and retrieving speedrun records.
public class SqliteRunRepository(SpeedrunContext context) : IRunRepository
{
    // Fetches the best time for a specific runner in a specific game and category.
    public async Task<RunRecord?> GetPersonalBestAsync(string runnerId, string gameFullName, string categoryName)
    {
        return await context.Runs.FirstOrDefaultAsync(r =>
            r.RunnerId == runnerId && r.GameFullName == gameFullName && r.CategoryName == categoryName);
    }

    // Retrieves the best time in the country for a given category.
    public async Task<RunRecord?> GetCountryBestAsync(string gameFullName, string categoryName)
    {
        return await context.Runs
            .Where(r => r.GameFullName == gameFullName && r.CategoryName == categoryName)
            .OrderBy(r => r.TimeInSeconds)
            .FirstOrDefaultAsync();
    }

    // Calculates the position of a specific time within the national leaderboard.
    public async Task<int> GetNationalRankAsync(string gameFullName, string categoryName, double timeInSeconds)
    {
        var runs = await context.Runs
            .Where(r => r.GameFullName == gameFullName && r.CategoryName == categoryName)
            .OrderBy(r => r.TimeInSeconds)
            .ToListAsync();

        int index = runs.FindIndex(r => r.TimeInSeconds == timeInSeconds);
        return index >= 0 ? index + 1 : 1;
    }

    // UPDATED: Saves or Updates a PB ensuring metadata (IDs) is never discarded.
    public async Task SavePersonalBestAsync(RunRecord run)
    {
        // 1. Search for an existing record by the unique RunLink
        var existing = await context.Runs.FirstOrDefaultAsync(r => r.RunLink == run.RunLink);

        if (existing != null)
        {
            // 2. CRITICAL: Always update Metadata IDs and variables from the API
            existing.CategoryId = run.CategoryId;
            existing.VariablesString = run.VariablesString;

            // NEW: If the existing record was marked as failed (-1), reset it to 0 for the Prestige Engine to catch it
            if (existing.TotalGlobalRunners == -1)
            {
                existing.TotalGlobalRunners = 0;
            }

            // 3. Prioritize the most detailed CategoryName (the longest string) for display
            if (run.CategoryName.Length >= existing.CategoryName.Length)
            {
                existing.CategoryName = run.CategoryName;
            }

            context.Runs.Update(existing);
        }
        else
        {
            // 4. Add the new record if it doesn't exist.
            await context.Runs.AddAsync(run);
        }

        await context.SaveChangesAsync();
    }

    // Returns the leaderboard for a game/category. If filters are empty, returns all records.
    public async Task<List<RunRecord>> GetRankingAsync(string gameFullName, string categoryName)
    {
        var query = context.Runs.AsQueryable();

        if (!string.IsNullOrWhiteSpace(gameFullName))
            query = query.Where(r => r.GameFullName == gameFullName);

        bool isAllCategories = string.IsNullOrWhiteSpace(categoryName) ||
                               categoryName.Equals("ALL_CATEGORIES", StringComparison.OrdinalIgnoreCase);

        if (!isAllCategories)
        {
            query = query.Where(r => r.CategoryName == categoryName);
            return await query.OrderBy(r => r.TimeInSeconds).ToListAsync();
        }
        else
        {
            return await query
                .OrderBy(r => r.CategoryName)
                .ThenBy(r => r.TimeInSeconds)
                .ToListAsync();
        }
    }

    // UPDATED: Counts unique runners per game instead of total run records to fix /game most_played logic.
    public async Task<List<(string GameName, int RunnerCount)>> GetMostPlayedGamesAsync(int limit = 20)
    {
        return await context.Runs
            .GroupBy(r => r.GameFullName)
            .Select(g => new
            {
                GameName = g.Key,
                // Count unique runner IDs to show how many people play the game
                RunnerCount = g.Select(r => r.RunnerId).Distinct().Count()
            })
            .OrderByDescending(x => x.RunnerCount)
            .Take(limit)
            .Select(x => ValueTuple.Create(x.GameName, x.RunnerCount))
            .ToListAsync();
    }

    // UPDATED: Fixes /game recent to strictly use the official submission date from Speedrun.com.
    public async Task<List<string>> GetRecentlyActiveGamesAsync(int limit = 15)
    {
        return await context.Runs
            .Where(r => r.DateSubmitted != null)
            .GroupBy(r => r.GameFullName)
            .Select(g => new
            {
                GameName = g.Key,
                // Get the absolute latest run date across all categories for this game
                LatestRunDate = g.Max(r => r.DateSubmitted)
            })
            .OrderByDescending(x => x.LatestRunDate)
            .Select(x => x.GameName)
            .Take(limit)
            .ToListAsync();
    }
}