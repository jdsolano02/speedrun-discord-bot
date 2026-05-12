using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Application.UseCases;
using SpeedrunBot.Domain.Entities;
using SpeedrunBot.Infrastructure.Persistence;
using System.Text.Json;
using System.Text;
using System.Linq;

namespace SpeedrunBot.Infrastructure.Discord;

// Main service to handle Discord interactions, slash commands, and notifications.
public class DiscordBotService : IHostedService, IDiscordNotifier
{
    private readonly DiscordSocketClient _client;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly string _botToken;
    private readonly string _syncedRunnersPath = "data/synced_runners.txt";

    public DiscordBotService(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _botToken = _configuration["DiscordSettings:BotToken"] ?? throw new Exception("BotToken not found.");

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.GuildMembers,
            AlwaysDownloadUsers = true
        });

        _client.Ready += Client_Ready;
        _client.SlashCommandExecuted += SlashCommandHandler;
        _client.AutocompleteExecuted += AutocompleteHandler;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _client.LoginAsync(TokenType.Bot, _botToken);
        await _client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await _client.StopAsync();

    // Registers and updates all global slash commands.
    private Task Client_Ready()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                Console.WriteLine("DEBUG [1/5]: Discord Ready Event Fired. Waiting 3 seconds...");
                await Task.Delay(3000);

                Console.WriteLine("DEBUG [2/5]: Building command definitions...");

                // 1. SETUP: Configuration (All fields REQUIRED)
                var setupCommand = new SlashCommandBuilder().WithName("setup").WithDescription("Configure bot channels and roles for this server.")
                    .AddOption("new_records_announcement", ApplicationCommandOptionType.Channel, "Channel for record notifications.", isRequired: true)
                    .AddOption("rankings", ApplicationCommandOptionType.Channel, "Channel for ranking and player commands.", isRequired: true)
                    .AddOption("registro", ApplicationCommandOptionType.Channel, "Channel for the register command.", isRequired: true)
                    .AddOption("player_count", ApplicationCommandOptionType.Channel, "Channel for the census embed.", isRequired: true)
                    .AddOption("rol_data_helpers", ApplicationCommandOptionType.Role, "Role allowed to manage runner data.", isRequired: true)
                    .WithDefaultMemberPermissions(GuildPermission.Administrator);

                // 1.5 UPDATE: Update configuration (All fields OPTIONAL)
                var updateCommand = new SlashCommandBuilder().WithName("update").WithDescription("Update specific bot channels or roles.")
                    .AddOption("new_records_announcement", ApplicationCommandOptionType.Channel, "Channel for record notifications.", isRequired: false)
                    .AddOption("rankings", ApplicationCommandOptionType.Channel, "Channel for ranking and player commands.", isRequired: false)
                    .AddOption("registro", ApplicationCommandOptionType.Channel, "Channel for the register command.", isRequired: false)
                    .AddOption("player_count", ApplicationCommandOptionType.Channel, "Channel for the census embed.", isRequired: false)
                    .AddOption("rol_data_helpers", ApplicationCommandOptionType.Role, "Role allowed to manage runner data.", isRequired: false)
                    .WithDefaultMemberPermissions(GuildPermission.Administrator);

                // 2. REGISTER: Runner management subcommands.
                var registerCommand = new SlashCommandBuilder().WithName("register").WithDescription("Manage the speedrunner database.")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("usuario")
                        .WithDescription("Register a new runner by their Speedrun.com name.")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("nombre", ApplicationCommandOptionType.String, "Speedrun.com username.", isRequired: true, isAutocomplete: true) // <--- ESTO FALTABA
                    )
                    .AddOption(new SlashCommandOptionBuilder().WithName("pending").WithDescription("Sync discovered runners not yet in the system (Data Helpers).").WithType(ApplicationCommandOptionType.SubCommand))
                    .AddOption(new SlashCommandOptionBuilder().WithName("all").WithDescription("Force a full resync of all registered runners (Admin only).").WithType(ApplicationCommandOptionType.SubCommand));

                // 3. PUBLIC, UTILITY & DEV COMMANDS
                var rankCommand = new SlashCommandBuilder().WithName("ranking").WithDescription("Show national rankings.")
                    .AddOption("juego", ApplicationCommandOptionType.String, "Game title", isRequired: true, isAutocomplete: true)
                    .AddOption("categoria", ApplicationCommandOptionType.String, "Category or 'ALL_CATEGORIES'", isRequired: true, isAutocomplete: true);

                var nrCommand = new SlashCommandBuilder().WithName("nr").WithDescription("Show all current National Records.");

                var playerCommand = new SlashCommandBuilder().WithName("player").WithDescription("View a runner's profile.")
                    .AddOption("usuario", ApplicationCommandOptionType.String, "Runner name", isRequired: true, isAutocomplete: true);

                var playersCommand = new SlashCommandBuilder().WithName("players").WithDescription("Show registered runners list.")
                    .AddOption(new SlashCommandOptionBuilder().WithName("lista").WithDescription("Select the list view mode.").WithType(ApplicationCommandOptionType.String).AddChoice("Full List", "all").AddChoice("Unsynced Runners", "unsynced"));

                var trackCommand = new SlashCommandBuilder().WithName("track").WithDescription("Add a game to the scanning watchlist (Admin).")
                    .AddOption("juego", ApplicationCommandOptionType.String, "Speedrun.com abbreviation", isRequired: true);

                var helpCommand = new SlashCommandBuilder().WithName("help").WithDescription("Display the user guide.");
                var devCommand = new SlashCommandBuilder().WithName("dev").WithDescription("Información sobre el desarrollador y apoyo al proyecto.");

                Console.WriteLine("DEBUG [3/5]: Packaging commands into array...");
                var commands = new ApplicationCommandProperties[]
                {
                    setupCommand.Build(),
                    updateCommand.Build(),
                    registerCommand.Build(),
                    rankCommand.Build(),
                    nrCommand.Build(),
                    playerCommand.Build(),
                    playersCommand.Build(),
                    trackCommand.Build(),
                    helpCommand.Build(),
                    devCommand.Build()
                };

                Console.WriteLine($"DEBUG [4/5]: Sending {commands.Length} commands to Discord API...");
                await _client.BulkOverwriteGlobalApplicationCommandsAsync(commands);

                Console.WriteLine("✅ DEBUG [5/5]: Global commands successfully initialized!");

                await UpdateAllGuildPlayerCounts();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ CRITICAL REGISTRATION ERROR: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        });

        return Task.CompletedTask;
    }

    private async Task SlashCommandHandler(SocketSlashCommand command)
    {
        await command.DeferAsync();
        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRunRepository>();
        var db = scope.ServiceProvider.GetRequiredService<SpeedrunContext>();
        ulong guildId = command.GuildId ?? 0;

        if (guildId == 0) { await command.FollowupAsync("❌ This command is for servers only."); return; }

        var gConfig = await db.GuildConfigs.FindAsync(guildId);
        var gUser = command.User as SocketGuildUser;

        bool isAdmin = gUser!.GuildPermissions.Administrator || gUser.Guild.OwnerId == gUser.Id;
        bool isDataHelper = isAdmin || (gConfig != null && gConfig.DataMakerRoleId > 0 && gUser.Roles.Any(r => r.Id == gConfig.DataMakerRoleId));

        // --- COMMAND LOGIC ---

        if (command.CommandName == "setup" || command.CommandName == "update")
        {
            if (!isAdmin) { await command.FollowupAsync("🚫 Permisos insuficientes (Admin/Owner required)."); return; }
            var config = gConfig ?? new GuildConfig { GuildId = guildId };
            if (db.Entry(config).State == Microsoft.EntityFrameworkCore.EntityState.Detached) db.GuildConfigs.Add(config);

            if (command.Data.Options.FirstOrDefault(x => x.Name == "new_records_announcement")?.Value is IChannel chA) config.AnnounceChannelId = chA.Id;
            if (command.Data.Options.FirstOrDefault(x => x.Name == "rankings")?.Value is IChannel chR) config.RankingsChannelId = chR.Id;
            if (command.Data.Options.FirstOrDefault(x => x.Name == "registro")?.Value is IChannel chReg) config.RegisterChannelId = chReg.Id;
            if (command.Data.Options.FirstOrDefault(x => x.Name == "player_count")?.Value is IChannel chC) config.PlayersChannelId = chC.Id;
            if (command.Data.Options.FirstOrDefault(x => x.Name == "rol_data_helpers")?.Value is IRole rDH) config.DataMakerRoleId = rDH.Id;

            await db.SaveChangesAsync();

            if (command.CommandName == "setup")
            {
                var welcomeMsg = "✅ **Servidor configurado correctamente.**\n\n" +
                                 "*Hola! Mi nombre es realxones o jdsolano02, gracias por incluir mi bot en tu Discord.*\n\n" +
                                 "*Si quieres apoyar a mantener corriendo el bot de manera gratuita para toda la comunidad, considera dejar tu propina aquí:* https://streamelements.com/realxones/tip \n" +
                                 "Puedes revisar la documentación del bot aquí: https://github.com/jdsolano02/speedrun-discord-bot \n" +
                                 "También revisa mis redes sociales: https://linktr.ee/Xones \n" +
                                 "**¡Muchas gracias por tu apoyo!**";
                await command.FollowupAsync(welcomeMsg);
            }
            else
            {
                await command.FollowupAsync("✅ **Configuración del servidor actualizada correctamente.**");
            }

            await UpdateAllGuildPlayerCounts();
            return;
        }

        if (command.CommandName == "dev")
        {
            var devInfo = "*Hola! Mi nombre es realxones o jdsolano02, desarrollador del bot.*\n\n" +
                          "*Si quieres apoyar a mantener corriendo el bot de manera gratuita para toda la comunidad, considera dejar tu propina aquí:* https://streamelements.com/realxones/tip \n" +
                          "Puedes revisar la documentación del bot aquí: https://github.com/jdsolano02/speedrun-discord-bot \n" +
                          "También revisa mis redes sociales: https://linktr.ee/Xones \n" +
                          "**¡Muchas gracias por tu apoyo!**";

            await command.FollowupAsync(devInfo);
            return;
        }

        if (gConfig == null) { await command.FollowupAsync("⚠️ El bot no está configurado. Un admin debe usar `/setup`."); return; }

        if (command.CommandName == "register")
        {
            if (command.ChannelId != gConfig.RegisterChannelId) { await command.FollowupAsync($"❌ Usa este comando en <#{gConfig.RegisterChannelId}>"); return; }
            var sub = command.Data.Options.First();

            if (sub.Name == "usuario")
            {
                var target = sub.Options.First().Value.ToString()!;
                var res = await scope.ServiceProvider.GetRequiredService<RegisterUser>().ExecuteAsync(target);
                await File.AppendAllLinesAsync(_syncedRunnersPath, new[] { target });
                await command.FollowupAsync(res);
                await UpdateAllGuildPlayerCounts();
            }
            else if (sub.Name == "pending")
            {
                if (!isDataHelper) { await command.FollowupAsync("🚫 Rol de **Data Helper** o **Admin** requerido."); return; }
                var allRunners = (await repo.GetRankingAsync("", "")).Select(r => r.RunnerName).Distinct().ToList();
                var synced = File.Exists(_syncedRunnersPath) ? new HashSet<string>(await File.ReadAllLinesAsync(_syncedRunnersPath), StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
                var missing = allRunners.Where(n => !synced.Contains(n)).ToList();

                if (!missing.Any()) { await command.FollowupAsync("✅ No hay runners pendientes."); return; }
                await command.FollowupAsync($"🔍 Sincronización de **{missing.Count}** runners iniciada en segundo plano...");

                _ = Task.Run(async () => {
                    using var bg = _serviceProvider.CreateScope();
                    var reg = bg.ServiceProvider.GetRequiredService<RegisterUser>();
                    foreach (var m in missing) { try { await reg.ExecuteAsync(m); await File.AppendAllLinesAsync(_syncedRunnersPath, new[] { m }); await Task.Delay(2000); } catch { } }
                    if (await _client.GetChannelAsync(gConfig.RegisterChannelId) is IMessageChannel ch)
                        await ch.SendMessageAsync($"🔔 **Sincronización finalizada:** {missing.Count} runners importados.");
                    await UpdateAllGuildPlayerCounts();
                });
            }
            else if (sub.Name == "all")
            {
                if (!isAdmin) { await command.FollowupAsync("🚫 Solo para Administradores."); return; }
                var allRunners = (await repo.GetRankingAsync("", "")).Select(r => r.RunnerName).Distinct().ToList();
                await command.FollowupAsync($"🚀 Iniciando actualización masiva de **{allRunners.Count}** perfiles...");
                _ = Task.Run(async () => {
                    using var bg = _serviceProvider.CreateScope();
                    var reg = bg.ServiceProvider.GetRequiredService<RegisterUser>();
                    foreach (var r in allRunners) { try { await reg.ExecuteAsync(r); await Task.Delay(2000); } catch { } }
                    if (await _client.GetChannelAsync(gConfig.RegisterChannelId) is IMessageChannel ch)
                        await ch.SendMessageAsync($"🔔 **Actualización masiva completada.** Datos de PBs actualizados.");
                });
            }
        }
        else if (command.CommandName == "ranking")
        {
            if (command.ChannelId != gConfig.RankingsChannelId) { await command.FollowupAsync($"❌ Usa <#{gConfig.RankingsChannelId}> para rankings."); return; }
            var juego = command.Data.Options.First(x => x.Name == "juego").Value?.ToString() ?? "";
            var cat = command.Data.Options.First(x => x.Name == "categoria").Value?.ToString() ?? "";
            var runs = await repo.GetRankingAsync(juego, cat == "ALL_CATEGORIES" ? "" : cat);
            if (!runs.Any()) { await command.FollowupAsync("Sin registros."); return; }

            var embed = new EmbedBuilder().WithTitle(cat == "ALL_CATEGORIES" ? $"📚 {juego} (Resumen)" : $"🏆 Ranking: {juego}").WithColor(Color.Blue).WithThumbnailUrl(runs[0].GameThumbnail);
            if (cat == "ALL_CATEGORIES")
            {
                foreach (var group in runs.GroupBy(r => r.CategoryName))
                {
                    var best = group.OrderBy(r => r.TimeInSeconds).First();
                    embed.AddField(group.Key, $"🥇 **{best.RunnerName}**: {FormatTime(best.TimeInSeconds)} (🌍 #{best.WorldRank})");
                }
            }
            else
            {
                int pos = 1;
                for (int i = 0; i < runs.Count && i < 10; i++)
                {
                    if (i > 0 && runs[i].TimeInSeconds > runs[i - 1].TimeInSeconds) pos = i + 1;
                    embed.AddField($"{pos switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"#{pos}" }} {runs[i].RunnerName}", $"**Tiempo:** {FormatTime(runs[i].TimeInSeconds)} | 🌍 #{runs[i].WorldRank}");
                }
            }
            await command.FollowupAsync(embed: embed.Build());
        }
        else if (command.CommandName == "nr")
        {
            var allRuns = await repo.GetRankingAsync("", "");
            var nrs = allRuns.GroupBy(r => new { r.GameFullName, r.CategoryName }).Select(g => g.OrderBy(r => r.TimeInSeconds).First()).OrderBy(r => r.GameFullName).ToList();
            var sb = new StringBuilder().AppendLine("🏆 RÉCORDS NACIONALES OFICIALES 🏆\n");
            foreach (var nr in nrs) sb.AppendLine($"- {nr.GameFullName} ({nr.CategoryName}): {nr.RunnerName} [{FormatTime(nr.TimeInSeconds)}]");
            await command.FollowupAsync($"```text\n{sb}```");
        }
        else if (command.CommandName == "player")
        {
            if (command.ChannelId != gConfig.RankingsChannelId) { await command.FollowupAsync($"❌ Usa <#{gConfig.RankingsChannelId}>."); return; }
            var user = command.Data.Options.First().Value.ToString()!;
            var all = await repo.GetRankingAsync("", "");
            var pRuns = all.Where(r => r.RunnerName.Equals(user, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!pRuns.Any()) { await command.FollowupAsync("Corredor no encontrado."); return; }

            // SRC profile URL (Slugs usually use underscores for spaces)
            var profileUrl = $"https://www.speedrun.com/users/{pRuns[0].RunnerName.Replace(" ", "_")}";

            var embed = new EmbedBuilder()
                .WithTitle($"👤 Perfil: {pRuns[0].RunnerName}")
                .WithUrl(profileUrl) // Title is now a clickable link
                .WithDescription($"[🔗 Ver perfil en Speedrun.com]({profileUrl})\n\nTotal de runs registradas: **{pRuns.Count}**")
                .WithColor(Color.Purple)
                .WithThumbnailUrl(pRuns[0].GameThumbnail);

            foreach (var run in pRuns.Take(15))
            {
                var natRank = all.Where(r => r.GameFullName == run.GameFullName && r.CategoryName == run.CategoryName)
                                 .OrderBy(r => r.TimeInSeconds).ToList().FindIndex(r => r.RunnerId == run.RunnerId) + 1;
                embed.AddField(run.GameFullName, $"**{run.CategoryName}**: {FormatTime(run.TimeInSeconds)}\n🇨🇷 Rank Nacional: #{natRank} | 🌍 Global: #{run.WorldRank}");
            }
            await command.FollowupAsync(embed: embed.Build());
        }
        else if (command.CommandName == "players")
        {
            var mode = command.Data.Options.FirstOrDefault()?.Value?.ToString();
            var allInDb = (await repo.GetRankingAsync("", "")).Select(r => r.RunnerName).Distinct().OrderBy(n => n).ToList();
            var synced = File.Exists(_syncedRunnersPath) ? new HashSet<string>(await File.ReadAllLinesAsync(_syncedRunnersPath), StringComparer.OrdinalIgnoreCase) : new HashSet<string>();

            var embed = new EmbedBuilder().WithTitle("👥 Directorio de Speedrunners").WithColor(Color.Blue).WithFooter($"Total en DB: {allInDb.Count}");

            if (mode == "unsynced")
            {
                var missing = allInDb.Where(n => !synced.Contains(n)).ToList();
                embed.WithDescription(missing.Any() ? string.Join(", ", missing) : "Todos los perfiles están sincronizados.");
                embed.WithTitle("⚠️ Runners Pendientes de Registro Completo");
            }
            else if (mode == "all")
            {
                embed.WithDescription(string.Join(", ", allInDb));
            }
            else
            {
                embed.WithDescription($"Actualmente hay **{allInDb.Count}** runners registrados y **{allInDb.Count(n => !synced.Contains(n))}** pendientes.");
            }
            await command.FollowupAsync(embed: embed.Build());
        }
        else if (command.CommandName == "track")
        {
            if (!isAdmin) { await command.FollowupAsync("🚫 Solo para administradores."); return; }
            var abbr = command.Data.Options.First().Value.ToString()!;
            await scope.ServiceProvider.GetRequiredService<IGameRepository>().AddGameAsync(abbr);
            await command.FollowupAsync($"✅ Juego `{abbr}` agregado a la lista de vigilancia.");
        }
        else if (command.CommandName == "help")
        {
            var embed = new EmbedBuilder().WithTitle("📖 Guía de Speedrun Bot").WithColor(Color.Blue)
                .AddField("🚀 `/register usuario [nombre]`", "Registra un corredor e importa sus PBs.")
                .AddField("🏆 `/ranking [juego] [categoría]`", "Muestra el top nacional.")
                .AddField("🥇 `/nr`", "Lista todos los Récords Nacionales.")
                .AddField("👤 `/player [nombre]`", "Muestra el perfil y logros de un corredor.")
                .AddField("👥 `/players [all/unsynced]`", "Lista de runners registrados o pendientes.")
                .AddField("⚙️ `/setup`", "Configuración inicial (Solo Admins).")
                .AddField("🔧 `/update`", "Actualizar configuración (Solo Admins).")
                .AddField("💻 `/dev`", "Información del desarrollador y apoyo.")
                .WithFooter("Pura vida speedrunning 🇨🇷");
            await command.FollowupAsync(embed: embed.Build());
        }
    }

    // Handles Discord's dynamic suggestions based on database content.
    private async Task AutocompleteHandler(SocketAutocompleteInteraction interaction)
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

    // Updates the persistent census embed with latest player counts.
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
        catch (Exception ex) { Console.WriteLine($"⚠️ Census update error: {ex.Message}"); }
    }

    // Sends detailed notifications for PBs and National Records.
    public async Task SendNewRecordNotificationAsync(RunRecord newRecord, double? prev, bool isNr, int rank)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpeedrunContext>();
        var embed = new EmbedBuilder().WithTitle(isNr ? "🏆 ¡NUEVO RÉCORD NACIONAL! 🏆" : "🚨 ¡NUEVO PERSONAL BEST! 🚨")
            .WithColor(isNr ? Color.Gold : Color.Green).WithThumbnailUrl(newRecord.GameThumbnail)
            .WithDescription($"**Runner:** {newRecord.RunnerName}\n**Juego:** {newRecord.GameFullName}\n**Categoría:** {newRecord.CategoryName}\n**Tiempo:** {FormatTime(newRecord.TimeInSeconds)}\n\n[Ver Validación]({newRecord.RunLink})")
            .WithFooter($"Rank Nacional: #{rank} 🇨🇷 | Global: #{newRecord.WorldRank}").Build();

        foreach (var c in db.GuildConfigs.AsEnumerable().Where(c => c.AnnounceChannelId > 0))
            if (await _client.GetChannelAsync(c.AnnounceChannelId) is IMessageChannel ch)
                await ch.SendMessageAsync(text: isNr ? "@everyone" : "", embed: embed);

        await UpdateAllGuildPlayerCounts();
    }

    private string FormatTime(double s)
    {
        TimeSpan t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }
}