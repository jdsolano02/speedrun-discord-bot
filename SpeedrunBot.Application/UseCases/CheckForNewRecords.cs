using SpeedrunBot.Application.Interfaces;

namespace SpeedrunBot.Application.UseCases;

public class CheckForNewRecords(
    ISpeedrunApi speedrunApi,
    IRunRepository repository,
    IDiscordNotifier discordNotifier)
{
    public async Task ExecuteAsync(string countryCode, string[] gamesToScan)
    {
        var latestRuns = await speedrunApi.GetLatestCountryRunsAsync(countryCode, gamesToScan);

        foreach (var run in latestRuns)
        {

            var previousPb = await repository.GetPersonalBestAsync(run.RunnerId, run.GameFullName, run.CategoryName);
            var currentNr = await repository.GetCountryBestAsync(run.GameFullName, run.CategoryName);

            if (previousPb == null || run.TimeInSeconds < previousPb.TimeInSeconds)
            {
                bool isNationalRecord = currentNr == null || run.TimeInSeconds < currentNr.TimeInSeconds;

                // 1. Guardamos primero para que el cálculo de ranking incluya este nuevo tiempo
                await repository.SavePersonalBestAsync(run);

                // 2. Calculamos el rango nacional (¿Qué top de Costa Rica es?)
                int nationalRank = await repository.GetNationalRankAsync(run.GameFullName, run.CategoryName, run.TimeInSeconds);

                // 3. Enviamos a Discord
                await discordNotifier.SendNewRecordNotificationAsync(run, previousPb?.TimeInSeconds, isNationalRecord, nationalRank);
            }
        }
    }
}