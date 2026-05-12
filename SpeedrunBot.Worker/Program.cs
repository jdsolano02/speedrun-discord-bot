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
// 🛡️ SCRIPT DE MIGRACIÓN AL VOLUMEN PERSISTENTE
// ====================================================================
using (var scope = host.Services.CreateScope())
{
    var persistentFolder = Path.Combine(Directory.GetCurrentDirectory(), "data");

    // Asegurar que la carpeta 'data' exista en el servidor de Railway
    Directory.CreateDirectory(persistentFolder);

    // Mapeo de archivos: Origen (Raíz) -> Destino (Volumen)
    var filesToMove = new Dictionary<string, string>
    {
        { "speedrundb.sqlite", Path.Combine(persistentFolder, "speedrundb.sqlite") },
        { "scan_state.txt", Path.Combine(persistentFolder, "scan_state.txt") },
        { "synced_runners.txt", Path.Combine(persistentFolder, "synced_runners.txt") }
    };

    foreach (var file in filesToMove)
    {
        // Si el archivo está en la raíz (llegó por GitHub) 
        // Y NO existe aún en el volumen estable... lo mudamos
        if (File.Exists(file.Key))
        {
            Console.WriteLine($"🚚 [MUDANZA] Moviendo {file.Key} al volumen persistente...");
            try
            {
                File.Move(file.Key, file.Value);
                Console.WriteLine($"✅ [MUDANZA] {file.Key} mudado con éxito.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [MUDANZA] Error moviendo {file.Key}: {ex.Message}");
            }
        }
    }

    var db = scope.ServiceProvider.GetRequiredService<SpeedrunContext>();
    db.Database.EnsureCreated();

    // --- MIGRACIÓN ANTIGUA (JSON A SQLITE) ---
    // Mantenemos esto por si acaso quedara algún rastro de archivos .json
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