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

public class DiscordBotService : IHostedService, IDiscordNotifier
{
    private readonly DiscordSocketClient _client;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly string _botToken;
    private readonly ulong _adminChannelId; // Private alerts channel
    private readonly string _syncedRunnersPath = "data/synced_runners.txt";

    public DiscordBotService(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _botToken = _configuration["DiscordSettings:BotToken"] ?? throw new Exception("BotToken not found.");

        // Default to 0 if variable is not set in Railway
        _adminChannelId = ulong.TryParse(_configuration["DiscordSettings:AdminChannelId"], out var id) ? id : 0;

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

    private Task Client_Ready()
    {
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
                    .AddOption(new SlashCommandOptionBuilder().WithName("lista").WithDescription("Select the list view mode.").WithType(ApplicationCommandOptionType.String).AddChoice("Full List", "all").AddChoice("Unsynced Runners", "unsynced"));

                // NEW: /game Command Structure
                var gameCommand = new SlashCommandBuilder().WithName("game").WithDescription("Manage and view monitored games.")
                    .AddOption(new SlashCommandOptionBuilder().WithName("add").WithDescription("Add a game to watchlist (DataTakers).").WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("id_src", ApplicationCommandOptionType.String, "Speedrun.com Abbreviation", isRequired: true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("delete").WithDescription("Remove a game from watchlist (DataTakers).").WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("id_src", ApplicationCommandOptionType.String, "Speedrun.com Abbreviation to delete", isRequired: true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("list").WithDescription("Export full list of tracked games (.txt).").WithType(ApplicationCommandOptionType.SubCommand))
                    .AddOption(new SlashCommandOptionBuilder().WithName("recent").WithDescription("Top 15 games with most recent CR runs.").WithType(ApplicationCommandOptionType.SubCommand))
                    .AddOption(new SlashCommandOptionBuilder().WithName("most_played").WithDescription("Top games by number of CR runners.").WithType(ApplicationCommandOptionType.SubCommand));

                var helpCommand = new SlashCommandBuilder().WithName("help").WithDescription("Display the user guide.");
                var devCommand = new SlashCommandBuilder().WithName("dev").WithDescription("Información sobre el desarrollador.");

                var commands = new ApplicationCommandProperties[]
                {
                    setupCommand.Build(), updateCommand.Build(), registerCommand.Build(), rankCommand.Build(),
                    nrCommand.Build(), playerCommand.Build(), playersCommand.Build(), gameCommand.Build(),
                    helpCommand.Build(), devCommand.Build()
                };

                await _client.BulkOverwriteGlobalApplicationCommandsAsync(commands);
                await UpdateAllGuildPlayerCounts();
            }
            catch (Exception ex) { Console.WriteLine($"❌ CRITICAL REGISTRATION ERROR: {ex.Message}"); }
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

        // Setup & Update Logic
        if (command.CommandName == "setup" || command.CommandName == "update")
        {
            if (!isAdmin) { await command.FollowupAsync("🚫 Permisos insuficientes."); return; }
            var config = gConfig ?? new GuildConfig { GuildId = guildId };
            if (db.Entry(config).State == Microsoft.EntityFrameworkCore.EntityState.Detached) db.GuildConfigs.Add(config);

            if (command.Data.Options.FirstOrDefault(x => x.Name == "new_records_announcement")?.Value is IChannel chA) config.AnnounceChannelId = chA.Id;
            if (command.Data.Options.FirstOrDefault(x => x.Name == "rankings")?.Value is IChannel chR) config.RankingsChannelId = chR.Id;
            if (command.Data.Options.FirstOrDefault(x => x.Name == "registro")?.Value is IChannel chReg) config.RegisterChannelId = chReg.Id;
            if (command.Data.Options.FirstOrDefault(x => x.Name == "player_count")?.Value is IChannel chC) config.PlayersChannelId = chC.Id;
            if (command.Data.Options.FirstOrDefault(x => x.Name == "rol_data_helpers")?.Value is IRole rDH) config.DataMakerRoleId = rDH.Id;

            // Assign NR Ping Role
            if (command.Data.Options.FirstOrDefault(x => x.Name == "rol_notificaciones_nr")?.Value is IRole rNR) config.NrPingRoleId = rNR.Id;

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
                await command.FollowupAsync("✅ **Configuración actualizada.**");
            }

            await UpdateAllGuildPlayerCounts();
            return;
        }

        if (command.CommandName == "dev")
        {
            await command.FollowupAsync("*Hola! Mi nombre es realxones o jdsolano02, desarrollador del bot.*\n\n*Apoya el bot:* https://streamelements.com/realxones/tip \nGithub: https://github.com/jdsolano02/speedrun-discord-bot");
            return;
        }

        if (gConfig == null) { await command.FollowupAsync("⚠️ El bot no está configurado. Un admin debe usar `/setup`."); return; }

        // NEW: Game Command Logic
        if (command.CommandName == "game")
        {
            var gameRepo = scope.ServiceProvider.GetRequiredService<IGameRepository>();
            var subCommand = command.Data.Options.First();

            switch (subCommand.Name)
            {
                case "add":
                    if (!isDataHelper) { await command.FollowupAsync("🚫 Solo DataTakers/Admins."); return; }
                    var idAdd = subCommand.Options.First().Value.ToString()!;
                    await gameRepo.AddGameAsync(idAdd);
                    await command.FollowupAsync($"✅ Juego `{idAdd}` añadido a la cola de escaneo.");
                    break;

                case "delete":
                    if (!isDataHelper) { await command.FollowupAsync("🚫 Solo DataTakers/Admins."); return; }
                    var idDel = subCommand.Options.First().Value.ToString()!;
                    await gameRepo.DeleteGameAsync(idDel);
                    await command.FollowupAsync($"🗑️ Juego `{idDel}` eliminado de la base de datos.");
                    break;

                case "list":
                    var trackedIds = await gameRepo.GetTrackedGamesAsync();
                    var allRunsForNames = await repo.GetRankingAsync("", "");

                    var gamesList = trackedIds.Select(id => {
                        var run = allRunsForNames.FirstOrDefault(r => r.GameId == id);
                        return run != null ? $"{run.GameFullName} ({id})" : id;
                    }).OrderBy(g => g).ToList();

                    var content = string.Join("\r\n", gamesList);
                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(content)))
                    {
                        await command.FollowupWithFileAsync(stream, "juegos_cr.txt", $"📄 Lista de los **{gamesList.Count}** juegos monitoreados.");
                    }
                    break;

                case "recent":
                    var allForRecent = await repo.GetRankingAsync("", "");
                    var recentGames = allForRecent
                        .Where(r => r.DateSubmitted.HasValue)
                        .OrderByDescending(r => r.DateSubmitted)
                        .Select(r => r.GameFullName)
                        .Distinct()
                        .Take(15);

                    var embedR = new EmbedBuilder().WithTitle("🕒 Juegos con Actividad Reciente").WithColor(Color.Green)
                        .WithDescription(recentGames.Any() ? string.Join("\n", recentGames.Select((g, i) => $"{i + 1}. **{g}**")) : "No hay datos de fechas aún.");
                    await command.FollowupAsync(embed: embedR.Build());
                    break;

                case "most_played":
                    var allForTop = await repo.GetRankingAsync("", "");
                    var topGames = allForTop.GroupBy(r => r.GameFullName)
                        .Select(g => new { Name = g.Key, Players = g.Select(r => r.RunnerId).Distinct().Count() })
                        .OrderByDescending(x => x.Players)
                        .Take(20);

                    var embedTop = new EmbedBuilder().WithTitle("🔥 Juegos Más Jugados").WithColor(Color.Orange)
                        .WithDescription(string.Join("\n", topGames.Select((g, i) => $"{i + 1}. **{g.Name}** ({g.Players} runners)")));
                    await command.FollowupAsync(embed: embedTop.Build());
                    break;
            }
            return;
        }

        // Remaining Commands (Register, Ranking, NR, Player, Players, Help)
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

            var profileUrl = $"https://www.speedrun.com/users/{pRuns[0].RunnerName.Replace(" ", "_")}";
            var embed = new EmbedBuilder()
                .WithTitle($"👤 Perfil: {pRuns[0].RunnerName}").WithUrl(profileUrl)
                .WithDescription($"[🔗 Ver perfil en Speedrun.com]({profileUrl})\n\nTotal de runs registradas: **{pRuns.Count}**")
                .WithColor(Color.Purple).WithThumbnailUrl(pRuns[0].GameThumbnail);

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
            else if (mode == "all") embed.WithDescription(string.Join(", ", allInDb));
            else embed.WithDescription($"Actualmente hay **{allInDb.Count}** runners registrados y **{allInDb.Count(n => !synced.Contains(n))}** pendientes.");

            await command.FollowupAsync(embed: embed.Build());
        }
        else if (command.CommandName == "help")
        {
            var embed = new EmbedBuilder().WithTitle("📖 Guía de Speedrun Bot").WithColor(Color.Blue)
                .AddField("🚀 `/register usuario [nombre]`", "Registra un corredor e importa sus PBs.")
                .AddField("🎮 `/game [opción]`", "Gestión de juegos, tops y más recientes.")
                .AddField("🏆 `/ranking [juego] [categoría]`", "Muestra el top nacional.")
                .AddField("🥇 `/nr`", "Lista todos los Récords Nacionales.")
                .AddField("👤 `/player [nombre]`", "Muestra el perfil de un corredor.")
                .AddField("👥 `/players`", "Directorio de runners.")
                .AddField("⚙️ `/setup`", "Configuración inicial (Admins).")
                .WithFooter("Pura vida speedrunning 🇨🇷");
            await command.FollowupAsync(embed: embed.Build());
        }
    }

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

    public async Task SendNewRecordNotificationAsync(RunRecord newRecord, double? prev, bool isNr, int rank)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpeedrunContext>();
        var embed = new EmbedBuilder().WithTitle(isNr ? "🏆 ¡NUEVO RÉCORD NACIONAL! 🏆" : "🚨 ¡NUEVO PERSONAL BEST! 🚨")
            .WithColor(isNr ? Color.Gold : Color.Green).WithThumbnailUrl(newRecord.GameThumbnail)
            .WithDescription($"**Runner:** {newRecord.RunnerName}\n**Juego:** {newRecord.GameFullName}\n**Categoría:** {newRecord.CategoryName}\n**Tiempo:** {FormatTime(newRecord.TimeInSeconds)}\n\n[Ver Validación]({newRecord.RunLink})")
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

    // System Alert implementation
    public async Task SendSystemAlertAsync(string message)
    {
        if (_adminChannelId == 0) return;

        if (await _client.GetChannelAsync(_adminChannelId) is IMessageChannel channel)
        {
            await channel.SendMessageAsync($"🛠️ **SYSTEM ALERT:** {message}");
        }
    }

    private string FormatTime(double s)
    {
        TimeSpan t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }
}