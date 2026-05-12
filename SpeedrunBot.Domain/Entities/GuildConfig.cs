namespace SpeedrunBot.Domain.Entities;

// Represents the configuration settings for a specific Discord server(Guild).

public class GuildConfig
{
    public ulong GuildId { get; set; } // Primary Key (Discord Server's ID)
    public ulong AnnounceChannelId { get; set; } // Channel ID where new records and pb's are posted.
    public ulong PlayersChannelId { get; set; } // Channel ID used to display the list of verified players.
    public ulong RankingsChannelId { get; set; } // Channel ID dedicated to showing the local speedrun rankings.
    public ulong RegisterChannelId { get; set; } // Channel ID where users can use commands to register themselves.
    public ulong AdminRoleId { get; set; } // Role ID allowed to manage bot settings (Admins).
    public ulong DataMakerRoleId { get; set; } // Role ID allowed to manually trigger data updates or modifications.
    public ulong NrPingRoleId { get; set; } // Role Id for pinging users when a new national record is set.
}