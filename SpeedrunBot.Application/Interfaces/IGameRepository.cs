namespace SpeedrunBot.Application.Interfaces;

public interface IGameRepository
{
    Task<List<string>> GetTrackedGamesAsync();
    Task AddGameAsync(string gameId);
}