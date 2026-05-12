using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Application.Interfaces;

public interface IDiscordNotifier
{
    Task SendNewRecordNotificationAsync(RunRecord newRecord, double? previousTimeInSeconds, bool isNationalRecord, int nationalRank);
}