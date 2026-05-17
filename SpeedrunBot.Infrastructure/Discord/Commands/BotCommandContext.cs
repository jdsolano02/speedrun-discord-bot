using Discord.WebSocket;
using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Domain.Entities;
using SpeedrunBot.Infrastructure.Persistence;

namespace SpeedrunBot.Infrastructure.Discord.Commands;

// Encapsulates all necessary dependencies and state for a command execution
public record BotCommandContext(
    SocketSlashCommand Command,
    IServiceProvider ScopeProvider,
    SpeedrunContext Db,
    IRunRepository Repo,
    GuildConfig GuildConfig,
    bool IsAdmin,
    bool IsDataHelper,
    DiscordSocketClient Client,
    string SyncedRunnersPath,
    Func<Task> UpdateCensusAction
);