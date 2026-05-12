using SpeedrunBot.Application.Interfaces;

namespace SpeedrunBot.Application.UseCases;

// Handles the manual registration of a new runner and the import of their history.
public class RegisterUser(
    ISpeedrunApi speedrunApi,
    IRunRepository repository,
    IGameRepository gameRepository)
{
    // Validates a runner, imports their personal bests, and updates the game watchlist.
    public async Task<string> ExecuteAsync(string username)
    {
        // 1. Verify if the user exists on Speedrun.com
        var user = await speedrunApi.GetUserByNameAsync(username);
        if (user == null) return $"❌ User `{username}` was not found on Speedrun.com.";

        // 2. Retrieve all full-game personal bests for the runner
        var personalBests = await speedrunApi.GetUserPersonalBestsAsync(user.Value.Id, user.Value.Name);

        if (personalBests.Count == 0)
            return $"⚠️ Runner **{user.Value.Name}** does not have any full-game records.";

        // 3. Import each run and ensure the game is added to the scanner's watchlist
        foreach (var run in personalBests)
        {
            await repository.SavePersonalBestAsync(run); // Save historical record
            await gameRepository.AddGameAsync(run.GameId); // Add game to automatic scanning
        }

        return $"✅ **{user.Value.Name}** registered! {personalBests.Count} runs imported and watchlist updated.";
    }
}