using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Application.UseCases;
using SpeedrunBot.Domain.Entities;
using SpeedrunBot.Infrastructure.Api;
using SpeedrunBot.Infrastructure.Discord;
using SpeedrunBot.Infrastructure.Persistence;
using SpeedrunBot.Worker;
using System.Text.Json; // Necesario para la migración

var builder = Host.CreateApplicationBuilder(args);

// --- INFRAESTRUCTURA Y DATOS ---
builder.Services.AddHttpClient<ISpeedrunApi, SpeedrunApiClient>(client =>
{
    client.BaseAddress = new Uri("https://www.speedrun.com/api/v1/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

// 1. Agregamos el contexto de Base de Datos SQLite
builder.Services.AddDbContext<SpeedrunContext>();

// 2. Cambiamos los repositorios a Sqlite y usamos AddScoped (es obligatorio para DBs)
builder.Services.AddScoped<IRunRepository, SqliteRunRepository>();
builder.Services.AddScoped<IGameRepository, SqliteGameRepository>();

// --- CASOS DE USO ---

// 3. Cambiamos CheckForNewRecords a Scoped porque ahora usa repositorios Scoped
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
// --- SCRIPT DE MIGRACIÓN ÚNICA (DE JSON A SQLITE) ---
// ====================================================================
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SpeedrunContext>();
    db.Database.EnsureCreated(); // Crea el archivo speedrundb.sqlite si no existe

    // Migrar Runs
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
            File.Move("runs_database.json", "runs_database_old.json"); // Renombramos para evitar duplicados futuros
            Console.WriteLine($"✅ Migración exitosa: {runs?.Count ?? 0} runs movidas a la DB.");
        }
        catch (Exception ex) { Console.WriteLine($"❌ Error migrando runs: {ex.Message}"); }
    }

    // Migrar Juegos en Vigilancia
    if (File.Exists("tracked_games.json"))
    {
        Console.WriteLine("📦 Migrando tracked_games.json a SQLite...");
        try
        {
            var json = File.ReadAllText("tracked_games.json");
            var games = JsonSerializer.Deserialize<List<string>>(json);
            if (games != null && games.Any())
            {
                // Se agregó .Distinct() para filtrar IDs repetidos en el JSON original
                foreach (var g in games.Distinct())
                {
                    if (!db.TrackedGames.Any(x => x.GameId == g)) // Evitar duplicados
                        db.TrackedGames.Add(new TrackedGame { GameId = g });
                }
                db.SaveChanges();
            }
            File.Move("tracked_games.json", "tracked_games_old.json");
            Console.WriteLine($"✅ Migración exitosa: {games?.Distinct().Count() ?? 0} juegos movidos a la DB.");
        }
        catch (Exception ex) { Console.WriteLine($"❌ Error migrando juegos: {ex.Message}"); }
    }
}
// ====================================================================

host.Run();