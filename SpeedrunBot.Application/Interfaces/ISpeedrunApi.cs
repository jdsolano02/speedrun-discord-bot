using SpeedrunBot.Domain.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SpeedrunBot.Application.Interfaces;

public interface ISpeedrunApi
{
    Task<List<RunRecord>> GetLatestCountryRunsAsync(string countryCode, string[] gamesToScan);
    Task<(string Id, string Name)?> GetUserByNameAsync(string username);
    Task<List<RunRecord>> GetUserPersonalBestsAsync(string userId, string userName);
}