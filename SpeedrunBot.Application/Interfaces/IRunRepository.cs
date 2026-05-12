using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Application.Interfaces;

public interface IRunRepository
{
    Task<RunRecord?> GetPersonalBestAsync(string runnerId, string gameFullName, string categoryName);
    Task<RunRecord?> GetCountryBestAsync(string gameFullName, string categoryName);
    Task<int> GetNationalRankAsync(string gameFullName, string categoryName, double timeInSeconds);
    Task<List<RunRecord>> GetRankingAsync(string gameFullName, string categoryName);
    Task SavePersonalBestAsync(RunRecord run);
}