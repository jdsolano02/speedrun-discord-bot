namespace SpeedrunBot.Domain.Entities;

public record RunRecord
{
    public int Id { get; init; } // Primary Key para SQLite
    public string RunnerId { get; init; } = string.Empty;
    public string RunnerName { get; init; } = string.Empty;
    public string GameId { get; init; } = string.Empty;
    public string GameFullName { get; init; } = string.Empty;
    public string CategoryName { get; init; } = string.Empty;
    public double TimeInSeconds { get; init; }
    public string RunLink { get; init; } = string.Empty;
    public string GameThumbnail { get; init; } = string.Empty;
    public int WorldRank { get; init; }
}