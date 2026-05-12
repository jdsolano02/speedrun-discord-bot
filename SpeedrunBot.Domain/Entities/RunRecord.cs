namespace SpeedrunBot.Domain.Entities;

// Represents a specific speedrun achievement stored in the local database.

public record RunRecord
{
    public int Id { get; init; } // Internal unique identifier for the database.
    public string RunnerId { get; init; } = string.Empty; // The Speedrun.com unique ID for the runner.
    public string RunnerName { get; init; } = string.Empty; // Display name of the speedrunner.
    public string GameId { get; init; } = string.Empty; // The Speedrun.com unique ID for the game.
    public string GameFullName { get; init; } = string.Empty; // Full display title of the game.
    public string CategoryName { get; init; } = string.Empty; // The specific category of the run (e.g., Any%, 100%).
    public double TimeInSeconds { get; init; } // Total time of the run converted to seconds.
    public string RunLink { get; init; } = string.Empty; // Direct URL to the run on Speedrun.com.
    public string GameThumbnail { get; init; } = string.Empty; // URL to the game's cover art or icon.
    public int WorldRank { get; init; } // Position of the run in the global leaderboard.
}