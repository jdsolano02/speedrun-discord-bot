using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Domain.Entities;
using SpeedrunBot.Infrastructure.Persistence;
using SpeedrunBot.Infrastructure.Discord.Commands;
using SpeedrunBot.Infrastructure.Discord.Utils;

namespace SpeedrunBot.Infrastructure.Discord;

public class DiscordBotService : IHostedService, IDiscordNotifier
{
    private readonly DiscordSocketClient _client;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly string _botToken;
    private readonly ulong _adminChannelId;
    private readonly string _syncedRunnersPath = "data/synced_runners.txt";

    public DiscordBotService(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _botToken = _configuration["DiscordSettings:BotToken"] ?? throw new Exception("BotToken not found.");
        _adminChannelId = ulong.TryParse(_configuration["DiscordSettings:AdminChannelId"], out var id) ? id : 0;

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.GuildMembers,
            AlwaysDownloadUsers = true
        });

        // NEW: capture every internal Discord.Net log (errors, warnings, gateway reconnects,
        // rate limits, dispatch failures). Without this, unhandled exceptions inside event
        // handlers are swallowed silently by the library.
        _client.Log += Client_Log;

        _client.Ready += Client_Ready;
        _client.Disconnected += Client_Disconnected;
        _client.SlashCommandExecuted += SlashCommandHandler;
        _client.AutocompleteExecuted += AutocompleteHandler;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _client.LoginAsync(TokenType.Bot, _botToken);
        await _client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await _client.StopAsync();

    private Task Client_Log(LogMessage msg)
    {
        Console.WriteLine($"[Discord.Net] {msg.Severity} | {msg.Source} | {msg.Message} {msg.Exception}");
        return Task.CompletedTask;
    }

    private Task Client_Disconnected(Exception ex)
    {
        Console.WriteLine($"⚠️ [Discord.Net] Client disconnected: {ex.Message}");
        return Task.CompletedTask;
    }

    private Task Client_Ready()
    {
        Console.WriteLine($"✅ [Discord.Net] Ready. Logged in as {_client.CurrentUser?.Username}#{_client.CurrentUser?.Discriminator} ({_client.CurrentUser?.Id}).");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000);

                var setupCommand = new SlashCommandBuilder().WithName("setup").WithDescription("Configure bot channels and roles for this server.")
                    .AddOption("new_records_announcement", ApplicationCommandOptionType.Channel, "Channel for record notifications.", isRequired: true)
                    .AddOption("rankings", ApplicationCommandOptionType.Channel, "Channel for ranking and player commands.", isRequired: true)
                    .AddOption("registro", ApplicationCommandOptionType.Channel, "Channel for the register command.", isRequired: true)
                    .AddOption("player_count", ApplicationCommandOptionType.Channel, "Channel for the census embed.", isRequired: true)
                    .AddOption("rol_data_helpers", ApplicationCommandOptionType.Role, "Role allowed to manage runner data.", isRequired: true)
                    .AddOption("rol_notificaciones_nr", ApplicationCommandOptionType.Role, "Role to ping for National Records.", isRequired: false)
                    .WithDefaultMemberPermissions(GuildPermission.Administrator);

                var updateCommand = new SlashCommandBuilder().WithName("update").WithDescription("Update specific bot channels or roles.")
                    .AddOption("new_records_announcement", ApplicationCommandOptionType.Channel, "Channel for record notifications.", isRequired: false)
                    .AddOption("rankings", ApplicationCommandOptionType.Channel, "Channel for ranking and player commands.", isRequired: false)
                    .AddOption("registro", ApplicationCommandOptionType.Channel, "Channel for the register command.", isRequired: false)
                    .AddOption("player_count", ApplicationCommandOptionType.Channel, "Channel for the census embed.", isRequired: false)
                    .AddOption("rol_data_helpers", ApplicationCommandOptionType.Role, "Role allowed to manage runner data.", isRequired: false)
                    .AddOption("rol_notificaciones_nr", ApplicationCommandOptionType.Role, "Role to ping for National Records.", isRequired: false)
                    .WithDefaultMemberPermissions(GuildPermission.Administrator);

                var registerCommand = new SlashCommandBuilder().WithName("register").WithDescription("Manage the speedrunner database.")
                    .AddOption(new SlashCommandOptionBuilder().WithName("usuario").WithDescription("Register a new runner by their Speedrun.com name.").WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("nombre", ApplicationCommandOptionType.String, "Speedrun.com username.", isRequired: true, isAutocomplete: true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("pending").WithDescription("Sync discovered runners not yet in the system (Data Helpers).").WithType(ApplicationCommandOptionType.SubCommand))
                    .AddOption(new SlashCommandOptionBuilder().WithName("all").WithDescription("Force a full resync of all registered runners (Admin only).").WithType(ApplicationCommandOptionType.SubCommand));

                var rankCommand = new SlashCommandBuilder().WithName("ranking").WithDescription("Show national rankings.")
                    .AddOption("juego", ApplicationCommandOptionType.String, "Game title", isRequired: true, isAutocomplete: true)
                    .AddOption("categoria", ApplicationCommandOptionType.String, "Category or 'ALL_CATEGORIES'", isRequired: true, isAutocomplete: true);

                var nrCommand = new SlashCommandBuilder().WithName("nr").WithDescription("Show all current National Records.");

                var playerCommand = new SlashCommandBuilder().WithName("player").WithDescription("View a runner's profile.")
                    .AddOption("usuario", ApplicationCommandOptionType.String, "Runner name", isRequired: true, isAutocomplete: true);

                var playersCommand = new SlashCommandBuilder().WithName("players").WithDescription("Show registered runners list.")
                    .AddOption(new SlashCommandOptionBuilder().WithName("lista").WithDescription("Select the list view mode.").WithType(ApplicationCommandOptionType.String).AddChoice("Full List", "all").AddChoice("Unsynced Runners", "unsynced"))
                    .AddOption("juego", ApplicationCommandOptionType.String, "Filter by specific game", isRequired: false, isAutocomplete: true);

                var gameCommand = new SlashCommandBuilder().WithName("game").WithDescription("Manage and view monitored games.")
                    .AddOption(new SlashCommandOptionBuilder().WithName("add").WithDescription("Add a game to watchlist (DataTakers).").WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("id_src", ApplicationCommandOptionType.String, "Speedrun.com Abbreviation", isRequired: true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("delete").WithDescription("Remove a game from watchlist (DataTakers).").WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("id_src", ApplicationCommandOptionType.String, "Speedrun.com Abbreviation to delete", isRequired: true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("list").WithDescription("Export full list of tracked games (.txt).").WithType(ApplicationCommandOptionType.SubCommand))
                    .AddOption(new SlashCommandOptionBuilder().WithName("recent").WithDescription("Top 25 games with most recent CR runs.").WithType(ApplicationCommandOptionType.SubCommand))
                    .AddOption(new SlashCommandOptionBuilder().WithName("most_played").WithDescription("Top games by number of CR runners.").WithType(ApplicationCommandOptionType.SubCommand));

                var topCommand = new SlashCommandBuilder().WithName("top").WithDescription("Competitive leaderboards based on prestige.")
                    .AddOption(new SlashCommandOptionBuilder().WithName("runs").WithDescription("Top 20 best individual runs by competitive weight.").WithType(ApplicationCommandOptionType.SubCommand))
                    .AddOption(new SlashCommandOptionBuilder().WithName("players").WithDescription("Top 20 players by total accumulated prestige score.").WithType(ApplicationCommandOptionType.SubCommand));

                var helpCommand = new SlashCommandBuilder().WithName("help").WithDescription("Display the user guide.");

                var devCommand = new SlashCommandBuilder().WithName("dev").WithDescription("Información sobre el desarrollador.");

                var exportSocialsCommand = new SlashCommandBuilder().WithName("export_socials").WithDescription("Exporta un CSV con las redes sociales de todos los runners (Admins).")
                    .WithDefaultMemberPermissions(GuildPermission.Administrator);

                var commands = new ApplicationCommandProperties[]
                {
                    setupCommand.Build(), updateCommand.Build(), registerCommand.Build(), rankCommand.Build(),
                    nrCommand.Build(), playerCommand.Build(), playersCommand.Build(), gameCommand.Build(),
                    topCommand.Build(), helpCommand.Build(), devCommand.Build(), exportSocialsCommand.Build()
                };

                await _client.BulkOverwriteGlobalApplicationCommandsAsync(commands);
                Console.WriteLine($"✅ [Discord.Net] {commands.Length} slash commands registered globally.");

                await UpdateAllGuildPlayerCounts();
            }
            catch (Exception ex) { Console.WriteLine($"❌ CRITICAL REGISTRATION ERROR: {ex.Message}\n{ex}"); }
        });

        return Task.CompletedTask;
    }

    private async Task SlashCommandHandler(SocketSlashCommand command)
    {
        // Deferring is time-sensitive and can fail on its own (expired interaction token,
        // double-ack, transient network blip talking to Discord's API). It used to sit
        // outside the try/catch below, so a failure here threw an unhandled exception that
        // Discord.Net silently swallowed — the command showed as "used" in Discord but the
        // bot never responded, and nothing was logged. Now it's isolated and logged.
        try
        {
            await command.DeferAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ DeferAsync falló para /{command.CommandName}: {ex.Message}\n{ex}");
            return; // el token de interacción ya no sirve, no hay nada más que hacer
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IRunRepository>();
            var db = scope.ServiceProvider.GetRequiredService<SpeedrunContext>();

            ulong guildId = command.GuildId ?? 0;
            if (guildId == 0) { await command.FollowupAsync("❌ This command is for servers only."); return; }

            var gConfig = await db.GuildConfigs.FindAsync(guildId);
            var gUser = command.User as SocketGuildUser;
            bool isAdmin = gUser!.GuildPermissions.Administrator || gUser.Guild.OwnerId == gUser.Id;
            bool isDataHelper = isAdmin || (gConfig != null && gConfig.DataMakerRoleId > 0 && gUser.Roles.Any(r => r.Id == gConfig.DataMakerRoleId));

            var ctx = new BotCommandContext(command, _serviceProvider, db, repo, gConfig!, isAdmin, isDataHelper, _client, _syncedRunnersPath, UpdateAllGuildPlayerCounts);

            switch (command.CommandName)
            {
                case "setup":
                case "update":
                case "register":
                case "export_socials":
                    await AdminCommands.HandleAsync(ctx);
                    break;

                case "ranking":
                case "nr":
                case "game":
                    await RankingCommands.HandleAsync(ctx);
                    break;

                case "player":
                case "players":
                case "top":
                    await PlayerCommands.HandleAsync(ctx);
                    break;

                case "help":
                    var embed = new EmbedBuilder().WithTitle("📖 Guía de Speedrun Bot").WithColor(Color.Blue)
                        .AddField("🚀 `/register usuario [nombre]`", "Registra un corredor e importa sus PBs.")
                        .AddField("🎮 `/game [opción]`", "Gestión de juegos, tops y más recientes.")
                        .AddField("🏆 `/ranking [juego] [categoría]`", "Muestra el top nacional.")
                        .AddField("🌟 `/top [runs/players]`", "Muestra leaderboards competitivos basados en puntos de prestigio.")
                        .AddField("🥇 `/nr`", "Lista todos los Récords Nacionales.")
                        .AddField("👤 `/player [nombre]`", "Muestra el perfil de un corredor con su prestigio.")
                        .AddField("👥 `/players`", "Directorio general o filtrado por juego.")
                        .AddField("⚙️ `/setup`", "Configuración inicial (Admins).")
                        .WithFooter("Pura vida speedrunning 🇨🇷");
                    await command.FollowupAsync(embed: embed.Build());
                    break;

                case "dev":
                    await command.FollowupAsync("*Hola! Mi nombre es realxones o jdsolano02, desarrollador del bot.*\n\n*Apoya el bot:* https://streamelements.com/realxones/tip \nGithub: https://github.com/jdsolano02/speedrun-discord-bot");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error procesando comando {command.CommandName}: {ex.Message}\n{ex}");
            try
            {
                await command.FollowupAsync($"⚠️ Ocurrió un error interno procesando la solicitud: {ex.Message}");
            }
            catch (Exception followupEx)
            {
                // Si incluso el followup falla (p. ej. token expirado), no hay más que loguear.
                Console.WriteLine($"❌ FollowupAsync también falló para /{command.CommandName}: {followupEx.Message}");
            }
        }
    }

    private async Task AutocompleteHandler(SocketAutocompleteInteraction interaction)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IRunRepository>();
            var all = await repo.GetRankingAsync("", "");
            var currentInput = interaction.Data.Current.Value?.ToString() ?? "";

            if (interaction.Data.Current.Name == "juego")
            {
                var choices = all.Select(r => r.GameFullName).Distinct()
                    .Where(g => g.Contains(currentInput, StringComparison.OrdinalIgnoreCase))
                    .Take(25).Select(g => new AutocompleteResult(g, g));
                await interaction.RespondAsync(choices);
            }
            else if (interaction.Data.Current.Name == "categoria")
            {
                var game = interaction.Data.Options.FirstOrDefault(o => o.Name == "juego")?.Value?.ToString() ?? "";
                var list = new List<AutocompleteResult> { new AutocompleteResult("--- TODAS LAS CATEGORÍAS ---", "ALL_CATEGORIES") };
                list.AddRange(all.Where(r => r.GameFullName == game).Select(r => r.CategoryName).Distinct()
                    .Where(c => c.Contains(currentInput, StringComparison.OrdinalIgnoreCase))
                    .Take(24).Select(c => new AutocompleteResult(c, c)));
                await interaction.RespondAsync(list);
            }
            else if (interaction.Data.Current.Name == "usuario" || interaction.Data.Current.Name == "nombre")
            {
                var choices = all.Select(r => r.RunnerName).Distinct()
                    .Where(u => u.Contains(currentInput, StringComparison.OrdinalIgnoreCase))
                    .Take(25).Select(u => new AutocompleteResult(u, u));
                await interaction.RespondAsync(choices);
            }
        }
        catch (Exception ex)
        {
            // Autocomplete no tiene followup: si falla, solo logueamos para no perderlo en silencio.
            Console.WriteLine($"❌ Error en autocomplete ({interaction.Data.Current.Name}): {ex.Message}\n{ex}");
        }
    }

    private async Task UpdateAllGuildPlayerCounts()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IRunRepository>();
            var db = scope.ServiceProvider.GetRequiredService<SpeedrunContext>();

            var count = (await repo.GetRankingAsync("", "")).Select(r => r.RunnerName).Distinct().Count();
            var embed = new EmbedBuilder().WithTitle("📊 Censo Speedrunners CR").WithDescription($"Total:\n\n**{count} Corredores Verificados**")
                .WithColor(Color.Green).WithThumbnailUrl("https://www.speedrun.com/images/flags/cr.png")
                .WithFooter($"Última actualización: {DateTime.Now:HH:mm:ss}").Build();

            foreach (var c in db.GuildConfigs.AsEnumerable().Where(c => c.PlayersChannelId > 0))
            {
                if (await _client.GetChannelAsync(c.PlayersChannelId) is ITextChannel ch)
                {
                    var msgs = await ch.GetMessagesAsync(10).FlattenAsync();
                    var botMsg = msgs.FirstOrDefault(m => m.Author.Id == _client.CurrentUser.Id);
                    if (botMsg is IUserMessage msg) await msg.ModifyAsync(x => x.Embed = embed);
                    else await ch.SendMessageAsync(embed: embed);
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"⚠️ Census update error: {ex.Message}\n{ex}"); }
    }

    public async Task SendNewRecordNotificationAsync(RunRecord newRecord, double? prev, bool isNr, int rank)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpeedrunContext>();

        var embed = new EmbedBuilder().WithTitle(isNr ? "🏆 ¡NUEVO RÉCORD NACIONAL! 🏆" : "🚨 ¡NUEVO PERSONAL BEST! 🚨")
            .WithColor(isNr ? Color.Gold : Color.Green).WithThumbnailUrl(newRecord.GameThumbnail)
            .WithDescription($"**Runner:** {newRecord.RunnerName}\n**Juego:** {newRecord.GameFullName}\n**Categoría:** {newRecord.CategoryName}\n**Tiempo:** {FormattingUtils.FormatTime(newRecord.TimeInSeconds)}\n\n[Ver Validación]({newRecord.RunLink})")
            .WithFooter($"Rank Nacional: #{rank} 🇨🇷 | Global: #{newRecord.WorldRank}").Build();

        foreach (var c in db.GuildConfigs.AsEnumerable().Where(c => c.AnnounceChannelId > 0))
        {
            if (await _client.GetChannelAsync(c.AnnounceChannelId) is IMessageChannel ch)
            {
                string pingText = "";
                if (isNr)
                {
                    pingText = c.NrPingRoleId > 0 ? $"<@&{c.NrPingRoleId}>" : "@everyone";
                }
                await ch.SendMessageAsync(text: pingText, embed: embed);
            }
        }

        await UpdateAllGuildPlayerCounts();
    }

    public async Task SendSystemAlertAsync(string message)
    {
        if (_adminChannelId == 0) return;
        if (await _client.GetChannelAsync(_adminChannelId) is IMessageChannel channel)
        {
            await channel.SendMessageAsync($"🛠️ **SYSTEM ALERT:** {message}");
        }
    }
}