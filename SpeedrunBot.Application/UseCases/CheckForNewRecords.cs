using SpeedrunBot.Application.Interfaces;

namespace SpeedrunBot.Application.UseCases;

// Orchestrates the scanning process to identify and notify about new personal and national records.
public class CheckForNewRecords(
    ISpeedrunApi speedrunApi,
    IRunRepository repository,
    IDiscordNotifier discordNotifier)
{
    // Fetches latest runs for a country and processes improvements or new entries.
    public async Task ExecuteAsync(string countryCode, string[] gamesToScan)
    {
        // 1. Fetch latest verified runs from the Speedrun.com API
        var latestRuns = await speedrunApi.GetLatestCountryRunsAsync(countryCode, gamesToScan);

        foreach (var run in latestRuns)
        {
            // NEW: BULLETPROOF SHIELD - Check if we already notified about this exact run link.
            // If the link is already in our DB, skip it completely to avoid spam loops.
            var gameRuns = await repository.GetRankingAsync(run.GameFullName, "ALL_CATEGORIES");
            if (gameRuns.Any(r => r.RunLink == run.RunLink))
            {
                continue;
            }

            // Check current local data to compare times
            var previousPb = await repository.GetPersonalBestAsync(run.RunnerId, run.GameFullName, run.CategoryName);
            var currentNr = await repository.GetCountryBestAsync(run.GameFullName, run.CategoryName);

            // 2. Determine if this run is a new entry or an improvement over the existing PB
            if (previousPb == null || run.TimeInSeconds < previousPb.TimeInSeconds)
            {
                // Check if this achievement also sets a new National Record
                bool isNationalRecord = currentNr == null || run.TimeInSeconds < currentNr.TimeInSeconds;

                // 3. Save to database first to ensure accurate ranking calculations
                await repository.SavePersonalBestAsync(run);

                // 4. Calculate the runner's position in the national leaderboard
                int nationalRank = await repository.GetNationalRankAsync(run.GameFullName, run.CategoryName, run.TimeInSeconds);

                // 5. Trigger the Discord notification logic
                await discordNotifier.SendNewRecordNotificationAsync(run, previousPb?.TimeInSeconds, isNationalRecord, nationalRank);
            }
        }
    }
}