using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Application.UseCases;
using SpeedrunBot.Domain.Entities;
using SpeedrunBot.Infrastructure.Api;
using SpeedrunBot.Infrastructure.Discord;
using SpeedrunBot.Infrastructure.Persistence;
using SpeedrunBot.Worker;
using System.Text.Json;
using System.IO;

var builder = Host.CreateApplicationBuilder(args);

// --- INFRAESTRUCTURA Y DATOS ---
builder.Services.AddHttpClient<ISpeedrunApi, SpeedrunApiClient>(client =>
{
    client.BaseAddress = new Uri("https://www.speedrun.com/api/v1/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddDbContext<SpeedrunContext>();
builder.Services.AddScoped<IRunRepository, SqliteRunRepository>();
builder.Services.AddScoped<IGameRepository, SqliteGameRepository>();

// --- CASOS DE USO ---
builder.Services.AddScoped<CheckForNewRecords>();
builder.Services.AddScoped<RegisterUser>();

// --- DISCORD SERVICE ---
builder.Services.AddSingleton<DiscordBotService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DiscordBotService>());
builder.Services.AddSingleton<IDiscordNotifier>(provider => provider.GetRequiredService<DiscordBotService>());

// --- BACKGROUND WORKER (EL ESCÁNER) ---
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

// ====================================================================
// 🛡️ SCRIPT DE MUDANZA CON AUDITORÍA TOTAL (REFORZADO)
// ====================================================================
using (var scope = host.Services.CreateScope())
{
    var currentDir = Directory.GetCurrentDirectory();
    var persistentFolder = Path.Combine(currentDir, "data");

    // 1. Asegurar carpeta de destino
    Directory.CreateDirectory(persistentFolder);

    Console.WriteLine($"🧪 [DEBUG] Directorio de ejecución: {currentDir}");

    // 2. Listar archivos en raíz para auditoría
    try
    {
        var allFiles = Directory.GetFiles(currentDir);
        Console.WriteLine($"🧪 [DEBUG] Archivos detectados en raíz: {string.Join(", ", allFiles.Select(Path.GetFileName))}");
    }
    catch (Exception ex) { Console.WriteLine($"❌ Error auditando carpeta: {ex.Message}"); }

    // 3. Mapeo de mudanza
    var filesToMove = new Dictionary<string, string>
    {
        { "speedrundb.sqlite", Path.Combine(persistentFolder, "speedrundb.sqlite") },
        { "scan_state.txt", Path.Combine(persistentFolder, "scan_state.txt") },
        { "synced_runners.txt", Path.Combine(persistentFolder, "synced_runners.txt") }
    };

    foreach (var file in filesToMove)
    {
        if (File.Exists(file.Key))
        {
            Console.WriteLine($"🚚 [MUDANZA] ¡Encontrado! Moviendo {file.Key} -> {file.Value}...");
            try
            {
                // Usamos Copy con 'true' para sobreescribir la DB vacía que Railway creó
                File.Copy(file.Key, file.Value, true);
                File.Delete(file.Key); // Limpiamos la raíz después de copiar
                Console.WriteLine($"✅ [MUDANZA] {file.Key} mudado y sobreescrito con éxito.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [MUDANZA] Error crítico moviendo {file.Key}: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"⚠️ [MUDANZA] El archivo {file.Key} no está en la raíz. Saltando...");
        }
    }

    // 4. Inicializar DB en su ruta final
    var db = scope.ServiceProvider.GetRequiredService<SpeedrunContext>();
    db.Database.EnsureCreated();

    // --- MIGRACIÓN ANTIGUA (JSON A SQLITE) ---
    if (File.Exists("runs_database.json"))
    {
        Console.WriteLine("📦 Migrando runs_database.json a SQLite...");
        try
        {
            var json = File.ReadAllText("runs_database.json");
            var runs = JsonSerializer.Deserialize<List<RunRecord>>(json);
            if (runs != null && runs.Any())
            {
                db.Runs.AddRange(runs);
                db.SaveChanges();
            }
            File.Move("runs_database.json", "runs_database_old.json");
            Console.WriteLine($"✅ Migración exitosa: {runs?.Count ?? 0} runs movidas.");
        }
        catch (Exception ex) { Console.WriteLine($"❌ Error migrando runs: {ex.Message}"); }
    }

    if (File.Exists("tracked_games.json"))
    {
        Console.WriteLine("📦 Migrando tracked_games.json a SQLite...");
        try
        {
            var json = File.ReadAllText("tracked_games.json");
            var games = JsonSerializer.Deserialize<List<string>>(json);
            if (games != null && games.Any())
            {
                foreach (var g in games.Distinct())
                {
                    if (!db.TrackedGames.Any(x => x.GameId == g))
                        db.TrackedGames.Add(new TrackedGame { GameId = g });
                }
                db.SaveChanges();
            }
            File.Move("tracked_games.json", "tracked_games_old.json");
            Console.WriteLine($"✅ Migración exitosa.");
        }
        catch (Exception ex) { Console.WriteLine($"❌ Error migrando juegos: {ex.Message}"); }
    }
}
// ====================================================================

host.Run();