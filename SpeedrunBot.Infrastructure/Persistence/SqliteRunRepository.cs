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

    // NEW LOGIC: Saves or Updates a PB using the RunLink as a unique identifier.
    // This prevents duplicates when category names vary slightly between API endpoints.
    public async Task SavePersonalBestAsync(RunRecord run)
    {
        // 1. Search for an existing record by the unique RunLink
        var existing = await context.Runs.FirstOrDefaultAsync(r => r.RunLink == run.RunLink);

        if (existing != null)
        {
            // 2. If it exists, we decide whether to update it or leave it.
            // We prioritize the most detailed CategoryName (the longest string).
            if (run.CategoryName.Length >= existing.CategoryName.Length)
            {
                // We remove the old one to ensure the new one (with potentially more data) is saved.
                context.Runs.Remove(existing);
            }
            else
            {
                // If the incoming record has a shorter (less detailed) name, we keep the existing one.
                return;
            }
        }

        // 3. Add the new/updated record.
        await context.Runs.AddAsync(run);
        await context.SaveChangesAsync();
    }

    // Returns the leaderboard for a game/category. If filters are empty, returns all records.
    public async Task<List<RunRecord>> GetRankingAsync(string gameFullName, string categoryName)
    {
        var query = context.Runs.AsQueryable();

        if (!string.IsNullOrWhiteSpace(gameFullName))
            query = query.Where(r => r.GameFullName == gameFullName);

        if (!string.IsNullOrWhiteSpace(categoryName) && categoryName != "ALL_CATEGORIES")
            query = query.Where(r => r.CategoryName == categoryName);

        return await query.OrderBy(r => r.TimeInSeconds).ToListAsync();
    }
}