using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Application.Interfaces;

// Contract for communicating with the external Speedrun.com API.
public interface ISpeedrunApi
{
    Task<List<RunRecord>> GetLatestCountryRunsAsync(string countryCode, string[] gamesToScan); // Fetches recent runs for a specific country.
    Task<(string Id, string Name)?> GetUserByNameAsync(string username); // Searches for a user ID and exact name by username.
    Task<List<RunRecord>> GetUserPersonalBestsAsync(string userId, string userName); // Retrieves all PBs for a specific user.
}