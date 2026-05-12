using Microsoft.EntityFrameworkCore;
using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Infrastructure.Persistence;

public class SqliteGameRepository : IGameRepository
{
    private readonly SpeedrunContext _context;

    public SqliteGameRepository(SpeedrunContext context)
    {
        _context = context;
        _context.Database.EnsureCreated();
    }

    public async Task<List<string>> GetTrackedGamesAsync()
    {
        return await _context.TrackedGames.Select(g => g.GameId).ToListAsync();
    }

    public async Task AddGameAsync(string gameId)
    {
        if (!await _context.TrackedGames.AnyAsync(g => g.GameId == gameId))
        {
            await _context.TrackedGames.AddAsync(new TrackedGame { GameId = gameId });
            await _context.SaveChangesAsync();
        }
    }
}