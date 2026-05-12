namespace SpeedrunBot.Domain.Entities;

// Stores the ID of a game that the bot should actively monitor for new records.

public class TrackedGame
{
    public string GameId { get; set; } = string.Empty; // The Speedrun.com unique ID of the game (Primary Key).
}