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
}using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Application.UseCases;

// Use Case to handle the registration and data synchronization of a runner.
public class RegisterUser(ISpeedrunApi api, IRunRepository runRepository)
{
    public async Task<string> ExecuteAsync(string username)
    {
        try
        {
            // 1. Fetch user data from Speedrun.com API to get the official ID and Name.
            var user = await api.GetUserByNameAsync(username);
            if (user == null) return $"❌ Runner `{username}` not found on Speedrun.com.";

            // 2. Check if the runner already exists in our local database.
            // We use the unique RunnerId to verify existence.
            var allRuns = await runRepository.GetRankingAsync("", "");
            bool alreadyExists = allRuns.Any(r => r.RunnerId == user.Value.Id);

            // 3. Fetch all personal bests for this runner from the API.
            var pbs = await api.GetUserPersonalBestsAsync(user.Value.Id, user.Value.Name);
            if (!pbs.Any()) return $"⚠️ **{user.Value.Name}** has no verified runs to register.";

            // 4. Save or update each run in the local repository.
            foreach (var run in pbs)
            {
                await runRepository.AddOrUpdateRunAsync(run);
            }

            // 5. Return a conditional message based on whether it was a new registration or an update.
            if (alreadyExists)
            {
                return $"🔄 **{user.Value.Name}** was already registered. Profile and PBs have been updated.";
            }

            return $"🚀 **{user.Value.Name}** registered successfully! **{pbs.Count}** PBs imported.";
        }
        catch (Exception ex)
        {
            return $"❌ Error registering runner: {ex.Message}";
        }
    }
}