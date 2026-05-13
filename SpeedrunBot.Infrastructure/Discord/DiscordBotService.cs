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

                // NEW: Added the optional "juego" parameter to the players command
                var playersCommand = new SlashCommandBuilder().WithName("players").WithDescription("Show registered runners list.")
                    .AddOption(new SlashCommandOptionBuilder().WithName("lista").WithDescription("Select the list view mode.").WithType(ApplicationCommandOptionType.String).AddChoice("Full List", "all").AddChoice("Unsynced Runners", "unsynced"))
                    .AddOption("juego", ApplicationCommandOptionType.String, "Filter by specific game", isRequired: false, isAutocomplete: true);

                var gameCommand = new SlashCommandBuilder().WithName("game").WithDescription("Manage and view monitored games.")
                    .AddOption(new SlashCommandOptionBuilder().WithName("add").WithDescription("Add a game to watchlist (DataTakers).").WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("id_src", ApplicationCommandOptionType.String, "Speedrun.com Abbreviation", isRequired: true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("delete").WithDescription("Remove a game from watchlist (DataTakers).").WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("id_src", ApplicationCommandOptionType.String, "Speedrun.com Abbreviation to delete", isRequired: true))
                    .AddOption(new SlashCommandOptionBuilder().WithName("list").WithDescription("Export full list of tracked games (.txt).").WithType(ApplicationCommandOptionType.SubCommand))
                    .AddOption(new SlashCommandOptionBuilder().WithName("recent").WithDescription("Top 15 games with most recent CR runs.").WithType(ApplicationCommandOptionType.SubCommand))
                    .AddOption(new SlashCommandOptionBuilder().WithName("most_played").WithDescription("Top games by number of CR runners.").WithType(ApplicationCommandOptionType.SubCommand));

                var topCommand = new SlashCommandBuilder().WithName("top").WithDescription("Competitive leaderboards based on prestige.")
                    .AddOption(new SlashCommandOptionBuilder().WithName("runs").WithDescription("Top 20 best individual runs by competitive weight.").WithType(ApplicationCommandOptionType.SubCommand))
                    .AddOption(new SlashCommandOptionBuilder().WithName("players").WithDescription("Top 20 players by total accumulated prestige score.").WithType(ApplicationCommandOptionType.SubCommand));

                var helpCommand = new SlashCommandBuilder().WithName("help").WithDescription("Display the user guide.");
                var devCommand = new SlashCommandBuilder().WithName("dev").WithDescription("Información sobre el desarrollador.");

                var commands = new ApplicationCommandProperties[]
                {
                    setupCommand.Build(), updateCommand.Build(), registerCommand.Build(), rankCommand.Build(),
                    nrCommand.Build(), playerCommand.Build(), playersCommand.Build(), gameCommand.Build(),
                    topCommand.Build(), helpCommand.Build(), devCommand.Build()
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

            if (command.CommandName == "game")
            {
                var gameRepo = scope.ServiceProvider.GetRequiredService<IGameRepository>();
                var subCommand = command.Data.Options.First();

                switch (subCommand.Name)
                {
                    case "add":
                        if (!isDataHelper) { await command.FollowupAsync("🚫 Solo DataTakers/Admins."); return; }
                        var idAdd = subCommand.Options.First().Value.ToString()!;

                        var currentlyTracked = await gameRepo.GetTrackedGamesAsync();
                        if (currentlyTracked.Any(id => id.Equals(idAdd, StringComparison.OrdinalIgnoreCase)))
                        {
                            await command.FollowupAsync($"⚠️ El juego `{idAdd}` ya se encuentra en la lista de escaneo.");
                            break;
                        }

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
                        var recentGames = await repo.GetRecentlyActiveGamesAsync(15);
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

                bool isAllCategories = string.IsNullOrWhiteSpace(cat) || cat.Equals("ALL_CATEGORIES", StringComparison.OrdinalIgnoreCase);
                var runs = await repo.GetRankingAsync(juego, isAllCategories ? "" : cat);

                if (!runs.Any()) { await command.FollowupAsync("Sin registros."); return; }

                var embed = new EmbedBuilder().WithTitle(isAllCategories ? $"📚 {juego} (Resumen General)" : $"🏆 Ranking: {juego}").WithColor(Color.Blue).WithThumbnailUrl(runs[0].GameThumbnail);

                if (isAllCategories)
                {
                    var groupedCategories = runs.GroupBy(r => r.CategoryName).ToList();

                    foreach (var group in groupedCategories.Take(25))
                    {
                        var categoryRuns = group.ToList();
                        var sb = new StringBuilder();
                        int pos = 1;

                        for (int i = 0; i < categoryRuns.Count && i < 10; i++)
                        {
                            if (i > 0 && categoryRuns[i].TimeInSeconds > categoryRuns[i - 1].TimeInSeconds) pos = i + 1;
                            string medal = pos switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"#{pos}" };
                            sb.AppendLine($"{medal} **{categoryRuns[i].RunnerName}**: {FormatTime(categoryRuns[i].TimeInSeconds)}");
                        }

                        string fieldContent = sb.ToString();
                        if (string.IsNullOrWhiteSpace(fieldContent)) fieldContent = "Sin registros.";

                        embed.AddField(group.Key.Length > 256 ? group.Key.Substring(0, 253) + "..." : group.Key, fieldContent);
                    }

                    if (groupedCategories.Count > 25)
                    {
                        embed.WithFooter($"Mostrando 25 de {groupedCategories.Count} categorías por límites visuales de Discord.");
                    }
                }
                else
                {
                    int pos = 1;
                    for (int i = 0; i < runs.Count && i < 15; i++)
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

                string ticks = new string('`', 3);
                await command.FollowupAsync($"{ticks}text\n{sb}{ticks}");
            }
            else if (command.CommandName == "top")
            {
                var subCommand = command.Data.Options.First();
                var allRuns = await repo.GetRankingAsync("", "");

                if (subCommand.Name == "runs")
                {
                    var validRuns = allRuns
                        .Where(r => r.TotalGlobalRunners > 0 && r.WorldRank > 0)
                        .Select(r => new {
                            Run = r,
                            Weight = (double)r.WorldRank / r.TotalGlobalRunners,
                            Prestige = (1.0 - ((double)r.WorldRank / r.TotalGlobalRunners)) * 100.0
                        })
                        .OrderBy(x => x.Weight)
                        .Take(20)
                        .ToList();

                    if (!validRuns.Any()) { await command.FollowupAsync("Aún no hay suficientes datos globales recolectados."); return; }

                    var embed = new EmbedBuilder()
                        .WithTitle("🌟 Top 20 Mejores Runs de Costa Rica")
                        .WithColor(Color.Magenta)
                        .WithDescription("Calculado mediante percentil global (`Rank Global / Total Runners`).\n\n");

                    var sb = new StringBuilder();
                    int rankIndex = 1;
                    foreach (var item in validRuns)
                    {
                        var natRank = allRuns.Where(r => r.GameFullName == item.Run.GameFullName && r.CategoryName == item.Run.CategoryName)
                                             .OrderBy(r => r.TimeInSeconds).ToList().FindIndex(r => r.RunnerId == item.Run.RunnerId) + 1;

                        sb.AppendLine($"**{rankIndex}. {item.Run.RunnerName}** - {item.Run.GameFullName} ({item.Run.CategoryName})");
                        sb.AppendLine($"└ 🇨🇷 #{natRank} | 🌍 #{item.Run.WorldRank} | ✨ Prestigio: `{item.Prestige:F2} pts`\n");
                        rankIndex++;
                    }

                    embed.WithDescription(embed.Description + sb.ToString());
                    await command.FollowupAsync(embed: embed.Build());
                }
                else if (subCommand.Name == "players")
                {
                    var playersScore = allRuns
                        .Where(r => r.TotalGlobalRunners > 0 && r.WorldRank > 0)
                        .GroupBy(r => r.RunnerName)
                        .Select(g => {
                            double totalPrestige = g.Sum(r => (1.0 - ((double)r.WorldRank / r.TotalGlobalRunners)) * 100.0);
                            return new { RunnerName = g.Key, TotalPrestige = totalPrestige, RunCount = g.Count() };
                        })
                        .OrderByDescending(x => x.TotalPrestige)
                        .Take(20)
                        .ToList();

                    if (!playersScore.Any()) { await command.FollowupAsync("Aún no hay suficientes datos globales recolectados."); return; }

                    var embed = new EmbedBuilder()
                        .WithTitle("🎖️ Top 20 Jugadores por Prestigio Total")
                        .WithColor(Color.Gold)
                        .WithDescription("Calculado sumando el prestigio de *todas* las runs del jugador.\n\n");

                    var sb = new StringBuilder();
                    int rankIndex = 1;
                    foreach (var p in playersScore)
                    {
                        string medal = rankIndex switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"**{rankIndex}.**" };
                        sb.AppendLine($"{medal} **{p.RunnerName}** - `{p.TotalPrestige:F0} pts` *(en {p.RunCount} runs)*");
                        rankIndex++;
                    }

                    embed.WithDescription(embed.Description + sb.ToString());
                    await command.FollowupAsync(embed: embed.Build());
                }
            }
            else if (command.CommandName == "player")
            {
                if (command.ChannelId != gConfig.RankingsChannelId) { await command.FollowupAsync($"❌ Usa <#{gConfig.RankingsChannelId}>."); return; }
                var user = command.Data.Options.First().Value.ToString()!;
                var all = await repo.GetRankingAsync("", "");
                var pRuns = all.Where(r => r.RunnerName.Equals(user, StringComparison.OrdinalIgnoreCase)).ToList();
                if (!pRuns.Any()) { await command.FollowupAsync("Corredor no encontrado."); return; }

                double totalPrestigeScore = 0;
                foreach (var r in pRuns)
                {
                    if (r.TotalGlobalRunners > 0 && r.WorldRank > 0)
                    {
                        double weight = (double)r.WorldRank / r.TotalGlobalRunners;
                        double points = (1.0 - weight) * 100.0;
                        if (points > 0) totalPrestigeScore += points;
                    }
                }

                var profileUrl = $"https://www.speedrun.com/users/{pRuns[0].RunnerName.Replace(" ", "_")}";
                var embed = new EmbedBuilder()
                    .WithTitle($"👤 Perfil: {pRuns[0].RunnerName}").WithUrl(profileUrl)
                    .WithDescription($"[🔗 Ver perfil en Speedrun.com]({profileUrl})\n\n🎮 Total de Runs: **{pRuns.Count}**\n✨ **Prestigio Total:** `{totalPrestigeScore:F0} pts`")
                    .WithColor(Color.Purple).WithThumbnailUrl(pRuns[0].GameThumbnail);

                foreach (var run in pRuns.Take(15))
                {
                    var natRank = all.Where(r => r.GameFullName == run.GameFullName && r.CategoryName == run.CategoryName)
                                     .OrderBy(r => r.TimeInSeconds).ToList().FindIndex(r => r.RunnerId == run.RunnerId) + 1;

                    string weightDisplay = "";
                    if (run.TotalGlobalRunners > 0 && run.WorldRank > 0)
                    {
                        double runWeight = (double)run.WorldRank / run.TotalGlobalRunners;
                        double runPrestige = (1.0 - runWeight) * 100.0;
                        weightDisplay = $"\n⚖️ Prestigio: `{runPrestige:F2} pts` (Top {runWeight * 100:F1}%)";
                    }

                    embed.AddField(run.GameFullName, $"**{run.CategoryName}**: {FormatTime(run.TimeInSeconds)}\n🇨🇷 Rank CR: #{natRank} | 🌍 Global: #{run.WorldRank} de {run.TotalGlobalRunners}{weightDisplay}");
                }
                await command.FollowupAsync(embed: embed.Build());
            }
            else if (command.CommandName == "players")
            {
                // NEW: Logic for filtering by specific game and implementing safe Discord limits handling
                var mode = command.Data.Options.FirstOrDefault(x => x.Name == "lista")?.Value?.ToString();
                var juegoFilter = command.Data.Options.FirstOrDefault(x => x.Name == "juego")?.Value?.ToString();

                var allRuns = await repo.GetRankingAsync(juegoFilter ?? "", "");
                var targetRunners = allRuns.Select(r => r.RunnerName).Distinct().OrderBy(n => n).ToList();

                if (!targetRunners.Any())
                {
                    await command.FollowupAsync(string.IsNullOrEmpty(juegoFilter) ? "No se encontraron corredores." : $"No hay corredores registrados para `{juegoFilter}`.");
                    return;
                }

                var synced = File.Exists(_syncedRunnersPath) ? new HashSet<string>(await File.ReadAllLinesAsync(_syncedRunnersPath), StringComparer.OrdinalIgnoreCase) : new HashSet<string>();

                string title = "👥 Directorio de Speedrunners";
                string description = "";

                if (!string.IsNullOrEmpty(juegoFilter))
                {
                    title = $"🎮 Runners de: {juegoFilter}";
                    description = string.Join(", ", targetRunners);
                }
                else if (mode == "unsynced")
                {
                    var missing = targetRunners.Where(n => !synced.Contains(n)).ToList();
                    description = missing.Any() ? string.Join(", ", missing) : "Todos los perfiles están sincronizados.";
                    title = "⚠️ Runners Pendientes de Registro Completo";
                    targetRunners = missing; // Update for accurate count in footer
                }
                else if (mode == "all")
                {
                    description = string.Join(", ", targetRunners);
                }
                else
                {
                    description = $"Actualmente hay **{targetRunners.Count}** runners registrados y **{targetRunners.Count(n => !synced.Contains(n))}** pendientes.";
                }

                // If string exceeds Embed Description Limit (4096 characters), send as plain text file to prevent crash
                if (description.Length > 4000)
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(description));
                    await command.FollowupWithFileAsync(stream, "runners.txt", $"📄 **{title}**\n*(Lista demasiado larga para Discord, enviada como archivo adjunto. Total: **{targetRunners.Count}**)*");
                }
                else
                {
                    var embed = new EmbedBuilder()
                        .WithTitle(title)
                        .WithColor(Color.Blue)
                        .WithDescription(description)
                        .WithFooter($"Total: {targetRunners.Count}");

                    await command.FollowupAsync(embed: embed.Build());
                }
            }
            else if (command.CommandName == "help")
            {
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
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error procesando comando {command.CommandName}: {ex.Message}");
            await command.FollowupAsync($"⚠️ Ocurrió un error interno procesando la solicitud: {ex.Message}");
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

    // Accurate Millisecond Formatting (00:00:00.000)
    private string FormatTime(double s)
    {
        TimeSpan t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ?
            $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}" :
            $"{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
    }
}