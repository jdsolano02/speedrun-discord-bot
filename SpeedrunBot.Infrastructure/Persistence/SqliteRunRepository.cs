using Microsoft.EntityFrameworkCore;
using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Infrastructure.Persistence;

public class SqliteRunRepository : IRunRepository
{
    private readonly SpeedrunContext _context;

    public SqliteRunRepository(SpeedrunContext context)
    {
        _context = context;
        _context.Database.EnsureCreated(); // Crea el archivo DB si no existe
    }

    public async Task<RunRecord?> GetPersonalBestAsync(string runnerId, string gameFullName, string categoryName)
    {
        return await _context.Runs.FirstOrDefaultAsync(r =>
            r.RunnerId == runnerId && r.GameFullName == gameFullName && r.CategoryName == categoryName);
    }

    public async Task<RunRecord?> GetCountryBestAsync(string gameFullName, string categoryName)
    {
        return await _context.Runs
            .Where(r => r.GameFullName == gameFullName && r.CategoryName == categoryName)
            .OrderBy(r => r.TimeInSeconds)
            .FirstOrDefaultAsync();
    }

    public async Task<int> GetNationalRankAsync(string gameFullName, string categoryName, double timeInSeconds)
    {
        var runs = await _context.Runs
            .Where(r => r.GameFullName == gameFullName && r.CategoryName == categoryName)
            .OrderBy(r => r.TimeInSeconds)
            .ToListAsync();

        int index = runs.FindIndex(r => r.TimeInSeconds == timeInSeconds);
        return index >= 0 ? index + 1 : 1;
    }

    public async Task SavePersonalBestAsync(RunRecord run)
    {
        var existing = await _context.Runs.FirstOrDefaultAsync(r =>
            r.RunnerId == run.RunnerId && r.GameFullName == run.GameFullName && r.CategoryName == run.CategoryName);

        if (existing != null)
        {
            _context.Runs.Remove(existing); // Removemos el viejo
        }

        await _context.Runs.AddAsync(run); // Insertamos el nuevo
        await _context.SaveChangesAsync();
    }

    public async Task<List<RunRecord>> GetRankingAsync(string gameFullName, string categoryName)
    {
        var query = _context.Runs.AsQueryable();

        if (!string.IsNullOrWhiteSpace(gameFullName))
            query = query.Where(r => EF.Functions.Like(r.GameFullName, $"%{gameFullName}%"));

        if (!string.IsNullOrWhiteSpace(categoryName) && categoryName != "ALL_CATEGORIES")
            query = query.Where(r => EF.Functions.Like(r.CategoryName, $"%{categoryName}%"));

        return await query.OrderBy(r => r.TimeInSeconds).ToListAsync();
    }
}