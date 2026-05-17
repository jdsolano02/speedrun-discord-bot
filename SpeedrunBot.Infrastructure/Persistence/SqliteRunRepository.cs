using Microsoft.EntityFrameworkCore;
using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Infrastructure.Persistence;

// SQLite implementation for storing and retrieving speedrun records.
public class SqliteRunRepository(SpeedrunContext context) : IRunRepository
{
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

    public async Task<List<string>> GetRecentlyActiveGamesAsync(int limit = 25)
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