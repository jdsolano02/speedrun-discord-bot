using SpeedrunBot.Application.Interfaces;
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
            var allRuns = await runRepository.GetRankingAsync("", "");
            bool alreadyExists = allRuns.Any(r => r.RunnerId == user.Value.Id);

            // 3. Fetch all personal bests for this runner from the API.
            var pbs = await api.GetUserPersonalBestsAsync(user.Value.Id, user.Value.Name);
            if (!pbs.Any()) return $"⚠️ **{user.Value.Name}** has no verified runs to register.";

            // 4. Save or update each run in the local repository.
            // FIX: Using the correct interface method name 'SavePersonalBestAsync'
            foreach (var run in pbs)
            {
                await runRepository.SavePersonalBestAsync(run);
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