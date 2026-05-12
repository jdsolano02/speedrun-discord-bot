using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Application.Interfaces;

// Defines the methods to send notifications to Discord channels.
public interface IDiscordNotifier
{
    // Sends a formatted notification about a new speedrun achievement.
    Task SendNewRecordNotificationAsync(RunRecord newRecord, double? previousTimeInSeconds, bool isNationalRecord, int nationalRank);
}