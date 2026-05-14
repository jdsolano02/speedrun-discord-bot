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

    // UPDATED: Saves or Updates a PB using replacement logic to bypass 'init' property restrictions.
    public async Task SavePersonalBestAsync(RunRecord run)
    {
        var existing = await context.Runs.FirstOrDefaultAsync(r => r.RunLink == run.RunLink);

        if (existing != null)
        {
            // If the new record has a better (longer) name, we replace the whole entity
            if (run.CategoryName.Length > existing.CategoryName.Length)
            {
                context.Runs.Remove(existing);
                await context.Runs.AddAsync(run);
            }
            else
            {
                // If we keep the old one, we still MUST update its metadata and reset weight
                existing.CategoryId = run.CategoryId;
                existing.VariablesString = run.VariablesString;

                if (existing.TotalGlobalRunners == -1)
                {
                    existing.TotalGlobalRunners = 0;
                }

                context.Runs.Update(existing);
            }
        }
        else
        {
            await context.Runs.AddAsync(run);
        }

        await context.SaveChangesAsync();
    }

    // Returns the leaderboard for a game/category.
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

    // Counts unique runners per game for /game most_played.
    public async Task<List<(string GameName, int RunnerCount)>> GetMostPlayedGamesAsync(int limit = 20)
    {
        return await context.Runs
            .GroupBy(r => r.GameFullName)
            .Select(g => new
            {
                GameName = g.Key,
                RunnerCount = g.Select(r => r.RunnerId).Distinct().Count()
            })
            .OrderByDescending(x => x.RunnerCount)
            .Take(limit)
            .Select(x => ValueTuple.Create(x.GameName, x.RunnerCount))
            .ToListAsync();
    }

    // Fixes /game recent using official submission dates.
    public async Task<List<string>> GetRecentlyActiveGamesAsync(int limit = 15)
    {
        return await context.Runs
            .Where(r => r.DateSubmitted != null)
            .GroupBy(r => r.GameFullName)
            .Select(g => new
            {
                GameName = g.Key,
                LatestRunDate = g.Max(r => r.DateSubmitted)
            })
            .OrderByDescending(x => x.LatestRunDate)
            .Select(x => x.GameName)
            .Take(limit)
            .ToListAsync();
    }
}