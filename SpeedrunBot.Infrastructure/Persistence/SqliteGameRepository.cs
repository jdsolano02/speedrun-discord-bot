using Microsoft.EntityFrameworkCore;
using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Infrastructure.Persistence;

// SQLite implementation for managing monitored games.
public class SqliteGameRepository(SpeedrunContext context) : IGameRepository
{
    // Returns a list of all game IDs currently tracked by the bot.
    public async Task<List<string>> GetTrackedGamesAsync()
    {
        return await context.TrackedGames.Select(g => g.GameId).ToListAsync();
    }

    // Adds a new game ID to the tracking list if it doesn't exist.
    public async Task AddGameAsync(string gameId)
    {
        if (!await context.TrackedGames.AnyAsync(g => g.GameId == gameId))
        {
            await context.TrackedGames.AddAsync(new TrackedGame { GameId = gameId });
            await context.SaveChangesAsync();
        }
    }
}