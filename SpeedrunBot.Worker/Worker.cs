using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Application.UseCases;
using System.Diagnostics;

namespace SpeedrunBot.Worker;

public class Worker(IServiceProvider serviceProvider, ILogger<Worker> logger) : BackgroundService
{
    private const string StateFile = "data/scan_state.txt";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("🚀 SERVICIO DE SPEEDRUN BOT INICIADO (Modo Adaptativo Inteligente)");

        using PeriodicTimer scanTimer = new(TimeSpan.FromMinutes(10));

        await RunScanningCycleAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested && await scanTimer.WaitForNextTickAsync(stoppingToken))
        {
            await RunScanningCycleAsync(stoppingToken);
        }
    }

    private async Task RunScanningCycleAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("🔍 Iniciando ciclo de escaneo masivo: {time}", DateTimeOffset.Now);

        Stopwatch cronometro = new Stopwatch();
        cronometro.Start();

        try
        {
            using var scope = serviceProvider.CreateScope();
            var gameRepo = scope.ServiceProvider.GetRequiredService<IGameRepository>();
            var checker = scope.ServiceProvider.GetRequiredService<CheckForNewRecords>();

            var gamesToScan = await gameRepo.GetTrackedGamesAsync();

            if (gamesToScan.Count == 0) return;

            int startIndex = 0;
            if (File.Exists(StateFile) && int.TryParse(await File.ReadAllTextAsync(StateFile), out int savedIndex))
            {
                startIndex = savedIndex >= gamesToScan.Count ? 0 : savedIndex;
            }

            if (startIndex > 0)
            {
                logger.LogInformation("💾 Recuperando Save State... Retomando escaneo desde el ID #{index}", startIndex);
            }

            int chunkSize = 50;

            // --- VARIABLES DE LA CAJA DE CAMBIOS ADAPTATIVA ---
            int maxConcurrentTasks = 5;
            int currentDelayMs = 2000;
            int successStreak = 0; // Contador de racha para volver a acelerar
            object syncLock = new object();
            DateTime lastThrottleTime = DateTime.MinValue;

            for (int i = startIndex; i < gamesToScan.Count; i += chunkSize)
            {
                if (stoppingToken.IsCancellationRequested) break;

                var currentChunk = gamesToScan.Skip(i).Take(chunkSize).ToArray();
                int currentEnd = i + currentChunk.Length;

                logger.LogInformation("📡 Progreso: [{current}/{total}] IDs escaneados. Tiempo: {time}",
                    currentEnd, gamesToScan.Count, cronometro.Elapsed.ToString(@"hh\:mm\:ss"));

                using var semaphore = new SemaphoreSlim(maxConcurrentTasks);

                var tasks = currentChunk.Select(async gameId =>
                {
                    await semaphore.WaitAsync(stoppingToken);

                    try
                    {
                        bool procesadoConExito = false;
                        int intentos = 0;
                        int maxIntentos = 3;

                        while (!procesadoConExito && intentos < maxIntentos)
                        {
                            if (stoppingToken.IsCancellationRequested) break;

                            try
                            {
                                logger.LogInformation("➡️ Analizando juego ID: {id} (Intento {i}/{max})...", gameId, intentos + 1, maxIntentos);
                                await checker.ExecuteAsync("cr", new[] { gameId });

                                await Task.Delay(currentDelayMs, stoppingToken);

                                // ✅ ÉXITO: Incrementamos racha y evaluamos subir de marcha
                                lock (syncLock)
                                {
                                    successStreak++;
                                    if (successStreak >= 20)
                                    {
                                        bool huboCambio = false;
                                        // Intentamos volver a los valores originales gradualmente
                                        if (maxConcurrentTasks < 5) { maxConcurrentTasks++; huboCambio = true; }
                                        if (currentDelayMs > 2000) { currentDelayMs -= 500; huboCambio = true; }

                                        if (huboCambio)
                                        {
                                            logger.LogInformation("🚀 OPTIMIZACIÓN: Racha de éxito detectada. Subiendo a {hilos} hilos y {pausa}ms de pausa.", maxConcurrentTasks, currentDelayMs);
                                        }
                                        successStreak = 0; // Reiniciamos contador tras el ajuste
                                    }
                                }

                                procesadoConExito = true;
                            }
                            catch (TaskCanceledException)
                            {
                                logger.LogWarning("⚠️ Timeout en juego ID: {id}. Saltando...", gameId);
                                break;
                            }
                            catch (Exception ex)
                            {
                                if (ex.Message.Contains("420") || ex.Message.Contains("429"))
                                {
                                    intentos++;
                                    lock (syncLock)
                                    {
                                        successStreak = 0; // Se rompe la racha por error
                                        if ((DateTime.Now - lastThrottleTime).TotalSeconds > 60)
                                        {
                                            lastThrottleTime = DateTime.Now;
                                            if (maxConcurrentTasks > 1) maxConcurrentTasks -= 2;
                                            if (maxConcurrentTasks < 1) maxConcurrentTasks = 1;
                                            currentDelayMs += 1000;

                                            logger.LogWarning("⚙️ AUTO-AJUSTE: Reduciendo a {hilos} hilo(s) y {pausa}ms de pausa.", maxConcurrentTasks, currentDelayMs);
                                        }
                                    }

                                    logger.LogWarning("⏳ Límite de API alcanzado en juego {id}. Enfriando 60s antes del reintento...", gameId);
                                    await Task.Delay(60000, stoppingToken);
                                }
                                else
                                {
                                    logger.LogWarning("⚠️ Error HTTP inesperado en juego {id}: {msg}", gameId, ex.Message);
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

                if (nextIndex < gamesToScan.Count)
                {
                    await Task.Delay(currentDelayMs, stoppingToken);
                }
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                cronometro.Stop();
                await File.WriteAllTextAsync(StateFile, "0", stoppingToken);
                logger.LogInformation("✅ Ciclo completado al 100%. Tiempo total: {time}", cronometro.Elapsed.ToString(@"hh\:mm\:ss"));
            }
        }
        catch (Exception ex)
        {
            cronometro.Stop();
            logger.LogError(ex, "❌ Error crítico.");
        }
    }
}