namespace SpeedrunBot.Domain.Entities;

public class GuildConfig
{
    public ulong GuildId { get; set; } // Primary Key (ID del server de Discord)
    public ulong AnnounceChannelId { get; set; }
    public ulong PlayersChannelId { get; set; }
    public ulong RankingsChannelId { get; set; }
    public ulong RegisterChannelId { get; set; }
    public ulong AdminRoleId { get; set; }
    public ulong DataMakerRoleId { get; set; }
}