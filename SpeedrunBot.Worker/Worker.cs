using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Application.UseCases;
using System.Diagnostics;

namespace SpeedrunBot.Worker;

// Main background service that handles the periodic scanning of speedruns.
public class Worker(IServiceProvider serviceProvider, ILogger<Worker> logger) : BackgroundService
{
    private const string StateFile = "data/scan_state.txt"; // File to persist scanning progress.

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("🚀 SPEEDRUN BOT SERVICE STARTED (Intelligent Adaptive Mode)");

        using PeriodicTimer scanTimer = new(TimeSpan.FromMinutes(10));

        // Immediate first run on startup
        await RunScanningCycleAsync(stoppingToken);

        // Periodic loop
        while (!stoppingToken.IsCancellationRequested && await scanTimer.WaitForNextTickAsync(stoppingToken))
        {
            await RunScanningCycleAsync(stoppingToken);
        }
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

            var gamesToScan = await gameRepo.GetTrackedGamesAsync();
            if (gamesToScan.Count == 0) return;

            // Restore last saved index to resume progress if the bot restarted
            int startIndex = 0;
            if (File.Exists(StateFile) && int.TryParse(await File.ReadAllTextAsync(StateFile), out int savedIndex))
            {
                startIndex = savedIndex >= gamesToScan.Count ? 0 : savedIndex;
            }

            if (startIndex > 0)
            {
                logger.LogInformation("💾 State recovered. Resuming scan from ID #{index}", startIndex);
            }

            int chunkSize = 50;

            // --- ADAPTIVE GEARBOX VARIABLES (Throttling logic) ---
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

                logger.LogInformation("📡 Progress: [{current}/{total}] IDs scanned. Elapsed: {time}",
                    currentEnd, gamesToScan.Count, stopwatch.Elapsed.ToString(@"hh\:mm\:ss"));

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
                                logger.LogInformation("➡️ Analyzing game: {id} (Attempt {i}/{max})...", gameId, attempts + 1, maxAttempts);
                                await checker.ExecuteAsync("cr", new[] { gameId });

                                await Task.Delay(currentDelayMs, stoppingToken);

                                // SUCCESS: Increase streak and evaluate if we can speed up
                                lock (syncLock)
                                {
                                    successStreak++;
                                    if (successStreak >= 20)
                                    {
                                        bool changed = false;
                                        if (maxConcurrentTasks < 5) { maxConcurrentTasks++; changed = true; }
                                        if (currentDelayMs > 2000) { currentDelayMs -= 500; changed = true; }

                                        if (changed)
                                        {
                                            logger.LogInformation("🚀 OPTIMIZATION: Success streak detected. Increasing to {threads} threads and {delay}ms delay.", maxConcurrentTasks, currentDelayMs);
                                        }
                                        successStreak = 0;
                                    }
                                }
                                isProcessed = true;
                            }
                            catch (TaskCanceledException)
                            {
                                logger.LogWarning("⚠️ Timeout on game {id}. Skipping...", gameId);
                                break;
                            }
                            catch (Exception ex)
                            {
                                // API Rate Limit detection (429 or unofficial 420)
                                if (ex.Message.Contains("420") || ex.Message.Contains("429"))
                                {
                                    attempts++;
                                    lock (syncLock)
                                    {
                                        successStreak = 0; // Reset streak on error
                                        if ((DateTime.Now - lastThrottleTime).TotalSeconds > 60)
                                        {
                                            lastThrottleTime = DateTime.Now;
                                            if (maxConcurrentTasks > 1) maxConcurrentTasks -= 2;
                                            if (maxConcurrentTasks < 1) maxConcurrentTasks = 1;
                                            currentDelayMs += 1000;

                                            logger.LogWarning("⚙️ AUTO-ADJUST: Throttling down to {threads} thread(s) and {delay}ms delay.", maxConcurrentTasks, currentDelayMs);
                                        }
                                    }

                                    logger.LogWarning("⏳ Rate limit hit on {id}. Cooling down 60s...", gameId);
                                    await Task.Delay(60000, stoppingToken);
                                }
                                else
                                {
                                    logger.LogWarning("⚠️ Unexpected error on game {id}: {msg}", gameId, ex.Message);
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

                // Save progress after each chunk
                int nextIndex = i + chunkSize;
                await File.WriteAllTextAsync(StateFile, nextIndex.ToString(), stoppingToken);

                if (nextIndex < gamesToScan.Count)
                {
                    await Task.Delay(currentDelayMs, stoppingToken);
                }
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                await File.WriteAllTextAsync(StateFile, "0", stoppingToken); // Reset state on completion
                logger.LogInformation("✅ Cycle completed 100%. Total time: {time}", stopwatch.Elapsed.ToString(@"hh\:mm\:ss"));
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "❌ Critical error during scan cycle.");
        }
    }
}