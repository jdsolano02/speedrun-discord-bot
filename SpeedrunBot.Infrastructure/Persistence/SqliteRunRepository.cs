using Microsoft.EntityFrameworkCore;
using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Infrastructure.Persistence;

// SQLite implementation for storing and retrieving speedrun records.
public class SqliteRunRepository(SpeedrunContext context) : IRunRepository
{
    // NEW: Blacklist to filter out joke/meme categories that skew the prestige rankings.
    private readonly string[] _bannedKeywords = { "Meme", "Break Dirt" };

    public async Task<RunRecord?> GetPersonalBestAsync(string runnerId, string gameFullName, string categoryName)
    {
        return await context.Runs
            .Where(r => r.RunnerId == runnerId && r.GameFullName == gameFullName && r.CategoryName == categoryName)
            .OrderBy(r => r.TimeInSeconds)
            .FirstOrDefaultAsync();
    }

    public async Task<RunRecord?> GetCountryBestAsync(string gameFullName, string categoryName)
    {
        return await context.Runs
            .Where(r => r.GameFullName == gameFullName && r.CategoryName == categoryName)
            .OrderBy(r => r.TimeInSeconds)
            .FirstOrDefaultAsync();
    }

    public async Task<int> GetNationalRankAsync(string gameFullName, string categoryName, double timeInSeconds)
    {
        var runs = await context.Runs
            .Where(r => r.GameFullName == gameFullName && r.CategoryName == categoryName)
            .OrderBy(r => r.TimeInSeconds)
            .ToListAsync();

        int index = runs.FindIndex(r => r.TimeInSeconds == timeInSeconds);
        return index >= 0 ? index + 1 : 1;
    }

    public async Task SavePersonalBestAsync(RunRecord run)
    {
        if (_bannedKeywords.Any(keyword =>
            (run.CategoryName != null && run.CategoryName.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
            (run.GameFullName != null && run.GameFullName.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
        {
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
            existing.CategoryId = run.CategoryId;
            existing.VariablesString = run.VariablesString;

            if (existing.TotalGlobalRunners == -1)
            {
                existing.TotalGlobalRunners = 0;
            }

            context.Runs.Update(existing);
        }
        else
        {
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

    // UPDATED: Read-Side Shield ensuring garbage never escapes the database
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
        }

        // Fetch data to memory first to bypass SQLite translation limits on complex string filters
        var results = await query.ToListAsync();

        // 🛡️ THE SHIELD: Filter out any runs that bypassed the write-filter
        results = results.Where(r => !_bannedKeywords.Any(keyword =>
            (r.CategoryName != null && r.CategoryName.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
            (r.GameFullName != null && r.GameFullName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))).ToList();

        if (!isAllCategories)
        {
            return results.OrderBy(r => r.TimeInSeconds).ToList();
        }
        else
        {
            return results
                .OrderBy(r => r.CategoryName)
                .ThenBy(r => r.TimeInSeconds)
                .ToList();
        }
    }

    public async Task<List<(string GameName, int RunnerCount)>> GetMostPlayedGamesAsync(int limit = 20)
    {
        var games = await context.Runs.ToListAsync();

        return games
            .Where(r => !_bannedKeywords.Any(keyword =>
                (r.CategoryName != null && r.CategoryName.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                (r.GameFullName != null && r.GameFullName.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
            .GroupBy(r => r.GameFullName)
            .Select(g => new
            {
                GameName = g.Key,
                RunnerCount = g.Select(r => r.RunnerId).Distinct().Count()
            })
            .OrderByDescending(x => x.RunnerCount)
            .Take(limit)
            .Select(x => ValueTuple.Create(x.GameName, x.RunnerCount))
            .ToList();
    }

    public async Task<List<string>> GetRecentlyActiveGamesAsync(int limit = 25)
    {
        var games = await context.Runs.Where(r => r.DateSubmitted != null).ToListAsync();

        return games
            .Where(r => !_bannedKeywords.Any(keyword =>
                (r.CategoryName != null && r.CategoryName.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                (r.GameFullName != null && r.GameFullName.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
            .GroupBy(r => r.GameFullName)
            .Select(g => new
            {
                GameName = g.Key,
                LatestRunDate = g.Max(r => r.DateSubmitted)
            })
            .OrderByDescending(x => x.LatestRunDate)
            .Select(x => x.GameName)
            .Take(limit)
            .ToList();
    }
}