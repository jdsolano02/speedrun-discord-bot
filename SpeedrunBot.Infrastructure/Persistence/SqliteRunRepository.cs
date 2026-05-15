using Microsoft.EntityFrameworkCore;
using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Infrastructure.Persistence;

// SQLite implementation for storing and retrieving speedrun records.
public class SqliteRunRepository(SpeedrunContext context) : IRunRepository
{
    // NEW: Blacklist to filter out joke/meme categories that skew the prestige rankings.
    // Add any future annoying categories or games to this array.
    private readonly string[] _bannedKeywords = { "Meme Categories", "Break Dirt", "Meme" };

    // Fetches the best time for a specific runner in a specific game and category.
    public async Task<RunRecord?> GetPersonalBestAsync(string runnerId, string gameFullName, string categoryName)
    {
        // UPDATED: Ensure we always fetch the absolute fastest time for accurate PB comparisons.
        return await context.Runs
            .Where(r => r.RunnerId == runnerId && r.GameFullName == gameFullName && r.CategoryName == categoryName)
            .OrderBy(r => r.TimeInSeconds)
            .FirstOrDefaultAsync();
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

    // UPDATED: Saves or Updates a PB avoiding Discord spam, ghost records, and meme categories.
    public async Task SavePersonalBestAsync(RunRecord run)
    {
        // NEW: Anti-Meme Filter. If the category or game contains a banned keyword, discard it completely.
        if (_bannedKeywords.Any(keyword =>
            run.CategoryName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            run.GameFullName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            // If it somehow already exists in the DB, we purge it to clean the rankings.
            var unwantedPbs = await context.Runs.Where(r =>
                r.RunnerId == run.RunnerId &&
                r.GameFullName == run.GameFullName &&
                r.CategoryName == run.CategoryName).ToListAsync();

            if (unwantedPbs.Any())
            {
                context.Runs.RemoveRange(unwantedPbs);
                await context.SaveChangesAsync();
            }

            return; // Silently reject and do not process this run any further
        }

        var existing = await context.Runs.FirstOrDefaultAsync(r => r.RunLink == run.RunLink);

        if (existing != null)
        {
            // 1. Update the metadata safely (These properties have 'set' accessors)
            existing.CategoryId = run.CategoryId;
            existing.VariablesString = run.VariablesString;

            // 2. Revive the run for the Prestige Engine
            if (existing.TotalGlobalRunners == -1)
            {
                existing.TotalGlobalRunners = 0;
            }

            // Note: We skip updating CategoryName because it is 'init-only'. Attempting to recreate
            // the entity to change the name would trigger false "New Record" alerts in Discord.

            context.Runs.Update(existing);
        }
        else
        {
            // NEW: Delete old slower PBs for this runner in this exact category to prevent ghost records.
            var oldPbs = await context.Runs.Where(r =>
                r.RunnerId == run.RunnerId &&
                r.GameFullName == run.GameFullName &&
                r.CategoryName == run.CategoryName).ToListAsync();

            if (oldPbs.Any())
            {
                context.Runs.RemoveRange(oldPbs);
            }

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

    // Counts unique runners per game instead of total run records to fix /game most_played logic.
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

    // UPDATED: Aumentamos el límite a 25 para evitar que juegos de la misma semana queden por fuera
    public async Task<List<string>> GetRecentlyActiveGamesAsync(int limit = 25)
    {
        return await context.Runs
            .Where(r => r.DateSubmitted != null)
            .GroupBy(r => r.GameFullName)
            .Select(g => new
            {
                GameName = g.Key,
                // Agarra la fecha exacta en la que el moderador de Speedrun.com aprobó la run
                LatestRunDate = g.Max(r => r.DateSubmitted)
            })
            // Ordena desde lo que se aprobó HOY hacia atrás
            .OrderByDescending(x => x.LatestRunDate)
            .Select(x => x.GameName)
            .Take(limit)
            .ToListAsync();
    }
}