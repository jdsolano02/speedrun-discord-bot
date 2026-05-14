using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Application.UseCases;
using SpeedrunBot.Infrastructure.Api;
using SpeedrunBot.Infrastructure.Discord;
using SpeedrunBot.Infrastructure.Persistence;
using SpeedrunBot.Worker;
using Microsoft.EntityFrameworkCore;
using System.IO;

var builder = Host.CreateApplicationBuilder(args);

// --- SERVICES REGISTRATION ---

// Configure HttpClient for Speedrun.com API
builder.Services.AddHttpClient<ISpeedrunApi, SpeedrunApiClient>(client =>
{
    client.BaseAddress = new Uri("https://www.speedrun.com/api/v1/");
    // Increased timeout from 15 to 30 seconds to prevent cancellations on heavy leaderboards
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Register SQLite Persistence
builder.Services.AddDbContext<SpeedrunContext>();
builder.Services.AddScoped<IRunRepository, SqliteRunRepository>();
builder.Services.AddScoped<IGameRepository, SqliteGameRepository>();

// Register Application Use Cases
builder.Services.AddScoped<CheckForNewRecords>();
builder.Services.AddScoped<RegisterUser>();

// Register Discord Bot Services
builder.Services.AddSingleton<DiscordBotService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DiscordBotService>());
builder.Services.AddSingleton<IDiscordNotifier>(provider => provider.GetRequiredService<DiscordBotService>());

// Register Background Scan Worker
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

// --- PERSISTENT DATA BOOTSTRAP ---

using (var scope = host.Services.CreateScope())
{
    var currentDir = Directory.GetCurrentDirectory();
    var persistentFolder = Path.Combine(currentDir, "data");

    // Ensure the persistent volume directory exists
    if (!Directory.Exists(persistentFolder)) Directory.CreateDirectory(persistentFolder);

    // Sync required data files from root (Git upload) to the persistent volume
    var filesToSync = new[] { "speedrundb.sqlite", "scan_state.txt", "synced_runners.txt" };

    foreach (var fileName in filesToSync)
    {
        var rootPath = Path.Combine(currentDir, fileName);
        var persistentPath = Path.Combine(persistentFolder, fileName);

        if (File.Exists(rootPath))
        {
            File.Copy(rootPath, persistentPath, true);
            File.Delete(rootPath);
        }
    }

    var logger = host.Services.GetRequiredService<ILogger<Program>>();

    // Initialize and Migrate Database
    var db = scope.ServiceProvider.GetRequiredService<SpeedrunContext>();
    db.Database.EnsureCreated();

    // --- SCHEMA PATCH: INJECT NEW COLUMNS WITHOUT LOSING DATA ---
    try
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Runs ADD COLUMN DateSubmitted TEXT;");
        logger.LogInformation("✨ [Schema Update] 'DateSubmitted' column added to Runs.");
    }
    catch { /* Column already exists, safe to ignore */ }

    try
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE GuildConfigs ADD COLUMN NrPingRoleId INTEGER NOT NULL DEFAULT 0;");
        logger.LogInformation("✨ [Schema Update] 'NrPingRoleId' column added to GuildConfigs.");
    }
    catch { /* Column already exists, safe to ignore */ }

    try
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Runs ADD COLUMN TotalGlobalRunners INTEGER NOT NULL DEFAULT 0;");
        logger.LogInformation("✨ [Schema Update] 'TotalGlobalRunners' column added to Runs.");
    }
    catch { /* Column already exists, safe to ignore */ }

    try
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Runs ADD COLUMN VariablesString TEXT NOT NULL DEFAULT '';");
        logger.LogInformation("✨ [Schema Update] 'VariablesString' column added to Runs.");
    }
    catch { /* Column already exists, safe to ignore */ }

    // NEW: Safely add CategoryId to track leaderboard endpoints
    try
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Runs ADD COLUMN CategoryId TEXT NOT NULL DEFAULT '';");
        logger.LogInformation("✨ [Schema Update] 'CategoryId' column added to Runs.");
    }
    catch { /* Column already exists, safe to ignore */ }

    // --- DATABASE CLEANUP: DEDUPLICATION ---
    try
    {
        string dedupeSql = @"
            DELETE FROM Runs 
            WHERE Id NOT IN (
                SELECT Id 
                FROM (
                    SELECT Id, MAX(length(CategoryName)) 
                    FROM Runs 
                    GROUP BY RunnerId, RunLink
                )
            );";

        int rowsAffected = db.Database.ExecuteSqlRaw(dedupeSql);
        if (rowsAffected > 0)
        {
            logger.LogInformation("✨ [Cleanup] Removed {Count} duplicate run records.", rowsAffected);
        }
        else
        {
            logger.LogInformation("✨ [Cleanup] Database checked. No duplicates found.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ [Cleanup] Failed to execute deduplication: {ex.Message}");
    }
}

// Start the application
host.Run();