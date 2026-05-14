using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Application.UseCases;
using SpeedrunBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace SpeedrunBot.Worker;

// Main background service that handles the periodic scanning of speedruns.
public class Worker(IServiceProvider serviceProvider, ILogger<Worker> logger) : BackgroundService
{
    private const string StateFile = "data/scan_state.txt";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("🚀 SPEEDRUN BOT SERVICE STARTED (Intelligent Adaptive Mode)");

        // Run the Prestige Engine Backfill to calculate competitive weights for new/missing runs.
        await BackfillMissingLeaderboardSizesAsync(stoppingToken);

        using PeriodicTimer scanTimer = new(TimeSpan.FromMinutes(10));

        await RunScanningCycleAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested && await scanTimer.WaitForNextTickAsync(stoppingToken))
        {
            await RunScanningCycleAsync(stoppingToken);
        }
    }

    // Directly targets unique leaderboards with TotalGlobalRunners <= 0 to catch fresh runs AND revive old failed ones.
    private async Task BackfillMissingLeaderboardSizesAsync(CancellationToken stoppingToken)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpeedrunContext>();
        var notifier = scope.ServiceProvider.GetRequiredService<IDiscordNotifier>();

        // CHANGED: From == 0 to <= 0. This forces the engine to recalculate all the -1s that survived the deduplication!
        var pendingBoards = await db.Runs
            .Where(r => r.TotalGlobalRunners <= 0)
            .Select(r => new { r.GameId, r.CategoryId, r.VariablesString })
            .Distinct()
            .ToListAsync(stoppingToken);

        if (pendingBoards.Count == 0) return;

        logger.LogInformation("🔍 [PRESTIGE ENGINE] Found {count} unique leaderboards to backfill (including revives).", pendingBoards.Count);
        await notifier.SendSystemAlertAsync($"🔍 **Prestige Engine:** Calculating competitive weights for {pendingBoards.Count} leaderboards...");

        using var httpClient = new HttpClient { BaseAddress = new Uri("https://www.speedrun.com/api/v1/") };

        int updatedCount = 0;

        for (int i = 0; i < pendingBoards.Count; i++)
        {
            var board = pendingBoards[i];
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                // 1. Fetch category variables to identify WHICH ones are actual subcategories
                var varsResponse = await httpClient.GetAsync($"categories/{board.CategoryId}/variables", stoppingToken);

                if ((int)varsResponse.StatusCode == 420 || (int)varsResponse.StatusCode == 429)
                {
                    logger.LogWarning("⚠️ API Rate limit hit (Variables). Pausing for 60 seconds...");
                    await Task.Delay(60000, stoppingToken);
                    i--; // Step back to retry this exact same board
                    continue;
                }

                if (varsResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    var obsoleteRuns = await db.Runs.Where(r => r.GameId == board.GameId && r.CategoryId == board.CategoryId && r.VariablesString == board.VariablesString).ToListAsync(stoppingToken);
                    obsoleteRuns.ForEach(r => r.TotalGlobalRunners = -1);
                    await db.SaveChangesAsync(stoppingToken);
                    continue;
                }

                varsResponse.EnsureSuccessStatusCode();
                var varsJson = await varsResponse.Content.ReadAsStringAsync(stoppingToken);
                using var varsDoc = System.Text.Json.JsonDocument.Parse(varsJson);

                var subcategoryIds = new HashSet<string>();
                foreach (var v in varsDoc.RootElement.GetProperty("data").EnumerateArray())
                {
                    if (v.GetProperty("is-subcategory").GetBoolean())
                    {
                        subcategoryIds.Add(v.GetProperty("id").GetString()!);
                    }
                }

                // 2. Clean the VariablesString to ONLY include real subcategories
                string cleanVariables = "";
                if (!string.IsNullOrEmpty(board.VariablesString))
                {
                    var validPairs = board.VariablesString.Split('&')
                        .Where(pair =>
                        {
                            var parts = pair.Split('=');
                            if (parts.Length != 2) return false;
                            var varId = parts[0].Replace("var-", "");
                            return subcategoryIds.Contains(varId);
                        }).ToList();

                    cleanVariables = string.Join("&", validPairs);
                }

                // 3. Construct clean API URL
                string query = string.IsNullOrEmpty(cleanVariables) ? "" : $"?{cleanVariables}";
                string url = $"leaderboards/{board.GameId}/category/{board.CategoryId}{query}";

                var response = await httpClient.GetAsync(url, stoppingToken);

                if ((int)response.StatusCode == 420 || (int)response.StatusCode == 429)
                {
                    logger.LogWarning("⚠️ API Rate limit hit (Leaderboard). Pausing for 60 seconds...");
                    await Task.Delay(60000, stoppingToken);
                    i--; // Step back to retry
                    continue;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    var obsoleteRuns = await db.Runs.Where(r => r.GameId == board.GameId && r.CategoryId == board.CategoryId && r.VariablesString == board.VariablesString).ToListAsync(stoppingToken);
                    obsoleteRuns.ForEach(r => r.TotalGlobalRunners = -1);
                    await db.SaveChangesAsync(stoppingToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(stoppingToken);
                using var doc = System.Text.Json.JsonDocument.Parse(json);

                var runsArray = doc.RootElement.GetProperty("data").GetProperty("runs");
                int totalRunners = runsArray.GetArrayLength();

                // 4. Batch update and sync
                var runsToUpdate = await db.Runs.Where(r => r.GameId == board.GameId && r.CategoryId == board.CategoryId && r.VariablesString == board.VariablesString).ToListAsync(stoppingToken);

                foreach (var run in runsToUpdate)
                {
                    run.TotalGlobalRunners = totalRunners > 0 ? totalRunners : -1;
                    run.VariablesString = cleanVariables;
                }

                await db.SaveChangesAsync(stoppingToken);
                updatedCount++;

                await Task.Delay(1500, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // Graceful shutdown
            }
            catch (Exception ex)
            {
                logger.LogWarning("⚠️ Failed to backfill leaderboard {gameId}: {message}", board.GameId, ex.Message);
            }
        }

        logger.LogInformation("✅ [PRESTIGE ENGINE] Completed. {count} leaderboards weighted.", updatedCount);
        await notifier.SendSystemAlertAsync($"✅ **Prestige Engine Completed:** {updatedCount} leaderboards mathematically weighted.");
    }

    private async Task RunScanningCycleAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("🔍 Starting massive scan cycle: {time}", DateTimeOffset.Now);

        Stopwatch stopwatch = new Stopwatch();
        stopwatch.Start();

        try
        {
            using var scope = serviceProvider.CreateScope();
            var gameRepo = scope.ServiceProvider.GetRequiredService<IGameRepository>();
            var checker = scope.ServiceProvider.GetRequiredService<CheckForNewRecords>();
            var notifier = scope.ServiceProvider.GetRequiredService<IDiscordNotifier>();

            var gamesToScan = await gameRepo.GetTrackedGamesAsync();
            if (gamesToScan.Count == 0) return;

            int startIndex = 0;
            if (File.Exists(StateFile) && int.TryParse(await File.ReadAllTextAsync(StateFile), out int savedIndex))
            {
                startIndex = savedIndex >= gamesToScan.Count ? 0 : savedIndex;
            }

            int chunkSize = 50;

            int maxConcurrentTasks = 5;
            int currentDelayMs = 2000;
            int successStreak = 0;
            object syncLock = new object();
            DateTime lastThrottleTime = DateTime.MinValue;

            for (int i = startIndex; i < gamesToScan.Count; i += chunkSize)
            {
                if (stoppingToken.IsCancellationRequested) break;

                var currentChunk = gamesToScan.Skip(i).Take(chunkSize).ToArray();
                int currentEnd = i + currentChunk.Length;

                logger.LogInformation("📡 Progress: [{current}/{total}] IDs scanned.", currentEnd, gamesToScan.Count);

                using var semaphore = new SemaphoreSlim(maxConcurrentTasks);

                var tasks = currentChunk.Select(async gameId =>
                {
                    await semaphore.WaitAsync(stoppingToken);
                    try
                    {
                        bool isProcessed = false;
                        int attempts = 0;
                        int maxAttempts = 3;

                        while (!isProcessed && attempts < maxAttempts)
                        {
                            if (stoppingToken.IsCancellationRequested) break;

                            try
                            {
                                await checker.ExecuteAsync("cr", new[] { gameId });
                                await Task.Delay(currentDelayMs, stoppingToken);

                                bool notifyUpshift = false;
                                lock (syncLock)
                                {
                                    successStreak++;
                                    if (successStreak >= 20)
                                    {
                                        bool changed = false;
                                        if (maxConcurrentTasks < 5) { maxConcurrentTasks++; changed = true; }
                                        if (currentDelayMs > 2000) { currentDelayMs -= 500; changed = true; }

                                        if (changed) notifyUpshift = true;
                                        successStreak = 0;
                                    }
                                }

                                if (notifyUpshift)
                                {
                                    logger.LogInformation("🚀 OPTIMIZATION: Increasing to {threads} threads and {delay}ms delay.", maxConcurrentTasks, currentDelayMs);
                                    await notifier.SendSystemAlertAsync($"🚀 **Gearbox Upshift:** {maxConcurrentTasks} threads | {currentDelayMs}ms delay.");
                                }

                                isProcessed = true;
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                if (ex.Message.Contains("420") || ex.Message.Contains("429"))
                                {
                                    attempts++;
                                    bool notifyDownshift = false;

                                    lock (syncLock)
                                    {
                                        successStreak = 0;
                                        if ((DateTime.Now - lastThrottleTime).TotalSeconds > 60)
                                        {
                                            lastThrottleTime = DateTime.Now;
                                            if (maxConcurrentTasks > 1) maxConcurrentTasks -= 2;
                                            if (maxConcurrentTasks < 1) maxConcurrentTasks = 1;
                                            currentDelayMs += 1000;
                                            notifyDownshift = true;
                                        }
                                    }

                                    if (notifyDownshift)
                                    {
                                        logger.LogWarning("⚙️ AUTO-ADJUST: Throttling down to {threads} thread(s) and {delay}ms delay.", maxConcurrentTasks, currentDelayMs);
                                        await notifier.SendSystemAlertAsync($"⚠️ **Gearbox Downshift (Rate Limit):** {maxConcurrentTasks} threads | {currentDelayMs}ms delay.");
                                    }

                                    await Task.Delay(60000, stoppingToken);
                                }
                                else
                                {
                                    break;
                                }
                            }
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);

                int nextIndex = i + chunkSize;
                await File.WriteAllTextAsync(StateFile, nextIndex.ToString(), stoppingToken);
                await notifier.SendSystemAlertAsync($"📦 **Chunk Completed:** [{currentEnd}/{gamesToScan.Count}] scanned.");

                if (nextIndex < gamesToScan.Count) await Task.Delay(currentDelayMs, stoppingToken);
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                await File.WriteAllTextAsync(StateFile, "0", stoppingToken);
                logger.LogInformation("✅ Cycle completed 100%.");
                await notifier.SendSystemAlertAsync($"🏁 **Scan Cycle 100% Completed.** Time: {stopwatch.Elapsed.ToString(@"hh\:mm\:ss")}");
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("🛑 Scan cycle gracefully cancelled due to app shutdown.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "❌ Critical error during scan cycle.");
        }
    }
}