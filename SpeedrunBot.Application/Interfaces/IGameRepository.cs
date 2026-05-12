namespace SpeedrunBot.Application.Interfaces;

// Handles database operations related to the list of monitored games.
public interface IGameRepository
{
    Task<List<string>> GetTrackedGamesAsync(); // Retrieves all game IDs currently being watched.
    Task AddGameAsync(string gameId); // Adds a new game ID to the tracking list.
}