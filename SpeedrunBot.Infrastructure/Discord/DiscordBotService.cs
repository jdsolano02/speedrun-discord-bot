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

namespace SpeedrunBot.Infrastructure.Discord;

public class DiscordBotService : IHostedService, IDiscordNotifier
{
    private readonly DiscordSocketClient _client;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly string _botToken;
    private readonly string _syncedRunnersPath = "synced_runners.txt";

    public DiscordBotService(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;

        _botToken = _configuration["DiscordSettings:BotToken"] ?? throw new Exception("BotToken no encontrado");

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

    private async Task Client_Ready()
    {
        var setupCommand = new SlashCommandBuilder()
            .WithName("setup")
            .WithDescription("Configura canales y roles para este servidor (Solo Admins).")
            .AddOption("anuncios", ApplicationCommandOptionType.Channel, "Donde caen los récords.", true)
            .AddOption("rankings", ApplicationCommandOptionType.Channel, "Donde usar /ranking y /player.", true)
            .AddOption("registro", ApplicationCommandOptionType.Channel, "Donde usar /register.", true)
            .AddOption("censo", ApplicationCommandOptionType.Channel, "Donde se muestra el conteo de runners.", true)
            .AddOption("rol_admin", ApplicationCommandOptionType.Role, "Rol con poder total sobre el bot.", true)
            .AddOption("rol_datamaker", ApplicationCommandOptionType.Role, "Rol para ayudar a registrar runners.", true)
            .WithDefaultMemberPermissions(GuildPermission.Administrator);

        var registerCommand = new SlashCommandBuilder()
            .WithName("register")
            .WithDescription("Gestión de base de datos de corredores")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("usuario")
                .WithDescription("Registra un runner nuevo.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("nombre", ApplicationCommandOptionType.String, "Nombre en Speedrun.com", true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("pending")
                .WithDescription("Sincroniza runners encontrados que faltan (DataMaker+).")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("all")
                .WithDescription("Resincronización masiva de PBs (Solo Admin).")
                .WithType(ApplicationCommandOptionType.SubCommand));

        var rankCommand = new SlashCommandBuilder().WithName("ranking").WithDescription("Muestra el top nacional").AddOption("juego", ApplicationCommandOptionType.String, "Busca el juego...", true, true).AddOption("categoria", ApplicationCommandOptionType.String, "Categoría o 'All'...", true, true);
        var nrCommand = new SlashCommandBuilder().WithName("nr").WithDescription("Muestra la lista completa de Récords Nacionales actuales.");
        var playerCommand = new SlashCommandBuilder().WithName("player").WithDescription("Ver el perfil y rankings de un jugador").AddOption("usuario", ApplicationCommandOptionType.String, "Nombre del jugador", true, true);
        var playersCommand = new SlashCommandBuilder().WithName("players").WithDescription("Muestra la cantidad total de runners ticos registrados.").AddOption("lista", ApplicationCommandOptionType.String, "Escribe 'all' para ver la lista de nombres.", false);
        var trackCommand = new SlashCommandBuilder().WithName("track").WithDescription("Agrega un juego a la vigilancia (Admin)").AddOption("juego", ApplicationCommandOptionType.String, "Abreviación en Speedrun.com", isRequired: true);
        var helpCommand = new SlashCommandBuilder().WithName("help").WithDescription("Guía de uso del bot.");

        try
        {
            // comandos globales para que funcione en MÚLTIPLES
            await _client.CreateGlobalApplicationCommandAsync(setupCommand.Build());
            await _client.CreateGlobalApplicationCommandAsync(registerCommand.Build());
            await _client.CreateGlobalApplicationCommandAsync(rankCommand.Build());
            await _client.CreateGlobalApplicationCommandAsync(nrCommand.Build());
            await _client.CreateGlobalApplicationCommandAsync(playerCommand.Build());
            await _client.CreateGlobalApplicationCommandAsync(playersCommand.Build());
            await _client.CreateGlobalApplicationCommandAsync(trackCommand.Build());
            await _client.CreateGlobalApplicationCommandAsync(helpCommand.Build());

            Console.WriteLine("✅ Comandos de Discord (Globales / Multi-Server) actualizados.");
            await UpdateAllGuildPlayerCounts();
        }
        catch (Exception ex) { Console.WriteLine($"❌ Error registrando comandos: {ex.Message}"); }
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

            if (guildId == 0)
            {
                await command.FollowupAsync("❌ Este comando solo se puede usar dentro de un servidor.");
                return;
            }

            // 1. LÓGICA DE SETUP
            if (command.CommandName == "setup")
            {
                var config = await db.GuildConfigs.FindAsync(guildId) ?? new GuildConfig { GuildId = guildId };
                if (db.Entry(config).State == Microsoft.EntityFrameworkCore.EntityState.Detached) db.GuildConfigs.Add(config);

                var optAnuncios = command.Data.Options.FirstOrDefault(x => x.Name == "anuncios")?.Value;
                var optRankings = command.Data.Options.FirstOrDefault(x => x.Name == "rankings")?.Value;
                var optRegistro = command.Data.Options.FirstOrDefault(x => x.Name == "registro")?.Value;
                var optCenso = command.Data.Options.FirstOrDefault(x => x.Name == "censo")?.Value;
                var optAdmin = command.Data.Options.FirstOrDefault(x => x.Name == "rol_admin")?.Value;
                var optDM = command.Data.Options.FirstOrDefault(x => x.Name == "rol_datamaker")?.Value;

                if (optAnuncios is IChannel chA) config.AnnounceChannelId = chA.Id;
                if (optRankings is IChannel chR) config.RankingsChannelId = chR.Id;
                if (optRegistro is IChannel chReg) config.RegisterChannelId = chReg.Id;
                if (optCenso is IChannel chC) config.PlayersChannelId = chC.Id;
                if (optAdmin is IRole rA) config.AdminRoleId = rA.Id;
                if (optDM is IRole rDM) config.DataMakerRoleId = rDM.Id;

                await db.SaveChangesAsync();
                await command.FollowupAsync("✅ **Servidor configurado correctamente.** El bot está listo para usarse en los canales asignados.");
                await UpdateAllGuildPlayerCounts();
                return;
            }

            // 2. VERIFICACIÓN DE CONFIGURACIÓN Y PERMISOS
            var gConfig = await db.GuildConfigs.FindAsync(guildId);
            if (gConfig == null)
            {
                await command.FollowupAsync("⚠️ El bot no ha sido configurado en este servidor. Un administrador debe usar `/setup` primero.");
                return;
            }

            var gUser = command.User as SocketGuildUser;
            bool isAdmin = IsAdmin(gUser!, gConfig);
            bool isDM = IsDataMaker(gUser!, gConfig);

            // 3. FILTROS DE CANAL
            if ((command.CommandName == "ranking" || command.CommandName == "player") && command.ChannelId != gConfig.RankingsChannelId)
            {
                await command.FollowupAsync($"❌ Usa este comando en el canal de rankings: <#{gConfig.RankingsChannelId}>"); return;
            }
            if (command.CommandName == "register" && command.ChannelId != gConfig.RegisterChannelId)
            {
                await command.FollowupAsync($"❌ Usa este comando en el canal de registro: <#{gConfig.RegisterChannelId}>"); return;
            }

            // 4. LÓGICA DE COMANDOS RESTANTES
            if (command.CommandName == "help")
            {
                var embed = new EmbedBuilder().WithTitle("📖 Guía Speedrun Bot").WithColor(Color.Blue)
                    .AddField("🚀 `/register usuario [nombre]`", "Registra un corredor.")
                    .AddField("🏆 `/ranking [juego] [categoría]`", "Top nacional.")
                    .AddField("🥇 `/nr`", "Récords Nacionales.")
                    .AddField("👤 `/player [usuario]`", "Perfil de un jugador.")
                    .AddField("⚙️ `/setup`", "Configura canales (Admins).")
                    .WithFooter("Pura vida speedrunning 🇨🇷");
                await command.FollowupAsync(embed: embed.Build());
            }
            else if (command.CommandName == "register")
            {
                var subCommand = command.Data.Options.FirstOrDefault();
                if (subCommand == null) return;

                if (subCommand.Name == "usuario")
                {
                    var target = subCommand.Options.First(x => x.Name == "nombre").Value.ToString()!;
                    var all = await repo.GetRankingAsync("", "");
                    if (all.Any(r => r.RunnerName.Equals(target, StringComparison.OrdinalIgnoreCase)))
                    {
                        await command.FollowupAsync($"✅ **{target}** ya está registrado."); return;
                    }
                    var res = await scope.ServiceProvider.GetRequiredService<RegisterUser>().ExecuteAsync(target);
                    await File.AppendAllLinesAsync(_syncedRunnersPath, new[] { target });
                    await command.FollowupAsync(res);
                    await UpdateAllGuildPlayerCounts();
                }
                else if (subCommand.Name == "pending")
                {
                    if (!isDM) { await command.FollowupAsync("🚫 Se requiere rol de **DataMaker** o **Admin**."); return; }
                    var allRuns = await repo.GetRankingAsync("", "");
                    var allRunners = allRuns.Select(r => r.RunnerName).Distinct().ToList();
                    var synced = File.Exists(_syncedRunnersPath) ? new HashSet<string>(await File.ReadAllLinesAsync(_syncedRunnersPath), StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
                    var missing = allRunners.Where(n => !synced.Contains(n)).ToList();

                    if (!missing.Any()) { await command.FollowupAsync("✅ No hay perfiles pendientes por sincronizar."); return; }
                    await command.FollowupAsync($"🔍 Sincronizando **{missing.Count}** perfiles nuevos de fondo...");

                    _ = Task.Run(async () => {
                        using var bg = _serviceProvider.CreateScope();
                        var reg = bg.ServiceProvider.GetRequiredService<RegisterUser>();
                        foreach (var m in missing) { try { await reg.ExecuteAsync(m); await File.AppendAllLinesAsync(_syncedRunnersPath, new[] { m }); await Task.Delay(2000); } catch { } }
                        await UpdateAllGuildPlayerCounts();
                    });
                }
                else if (subCommand.Name == "all")
                {
                    if (!isAdmin) { await command.FollowupAsync("🚫 Solo los administradores pueden hacer sincronización total."); return; }
                    var allRunners = (await repo.GetRankingAsync("", "")).Select(r => r.RunnerName).Distinct().ToList();
                    if (!allRunners.Any()) { await command.FollowupAsync("⚠️ No hay runners registrados todavía."); return; }

                    await command.FollowupAsync($"🚀 Iniciando resincronización total para **{allRunners.Count}** runners...");
                    _ = Task.Run(async () => {
                        using var bg = _serviceProvider.CreateScope();
                        var reg = bg.ServiceProvider.GetRequiredService<RegisterUser>();
                        foreach (var runner in allRunners) { try { await reg.ExecuteAsync(runner); await Task.Delay(2000); } catch { } }
                    });
                }
            }
            else if (command.CommandName == "ranking")
            {
                var juego = command.Data.Options.First(x => x.Name == "juego").Value?.ToString() ?? "";
                var cat = command.Data.Options.First(x => x.Name == "categoria").Value?.ToString() ?? "";

                if (cat == "ALL_CATEGORIES")
                {
                    var runs = await repo.GetRankingAsync(juego, "");
                    if (!runs.Any()) { await command.FollowupAsync("Sin registros."); return; }
                    var embed = new EmbedBuilder().WithTitle($"📚 {juego}").WithColor(Color.DarkBlue).WithThumbnailUrl(runs[0].GameThumbnail);
                    foreach (var group in runs.GroupBy(r => r.CategoryName))
                    {
                        var best = group.OrderBy(r => r.TimeInSeconds).First();
                        embed.AddField(group.Key, $"🥇 **{best.RunnerName}**: {FormatTime(best.TimeInSeconds)} (🌍 #{best.WorldRank})");
                    }
                    await command.FollowupAsync(embed: embed.Build());
                }
                else
                {
                    var ranking = await repo.GetRankingAsync(juego, cat);
                    if (!ranking.Any()) { await command.FollowupAsync("Sin registros."); return; }
                    var embed = new EmbedBuilder().WithTitle($"🏆 Ranking: {juego}").WithDescription($"**Cat:** {cat}").WithColor(Color.Blue);
                    int pos = 1;
                    for (int i = 0; i < ranking.Count && i < 10; i++)
                    {
                        if (i > 0 && ranking[i].TimeInSeconds > ranking[i - 1].TimeInSeconds) pos = i + 1;
                        embed.AddField($"{pos switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"#{pos}" }} {ranking[i].RunnerName}",
                            $"**Tiempo:** {FormatTime(ranking[i].TimeInSeconds)} | 🌍 #{ranking[i].WorldRank}");
                    }
                    await command.FollowupAsync(embed: embed.Build());
                }
            }
            else if (command.CommandName == "nr")
            {
                var allRuns = await repo.GetRankingAsync("", "");
                if (!allRuns.Any()) { await command.FollowupAsync("No hay récords registrados todavía."); return; }

                var nrs = allRuns.GroupBy(r => new { r.GameFullName, r.CategoryName })
                                 .Select(g => g.OrderBy(r => r.TimeInSeconds).First())
                                 .OrderBy(r => r.GameFullName).ThenBy(r => r.CategoryName).ToList();

                var sb = new StringBuilder().AppendLine("🏆 LISTA OFICIAL DE RÉCORDS NACIONALES 🏆\n");
                string currentGame = "";
                foreach (var nr in nrs)
                {
                    if (nr.GameFullName != currentGame)
                    {
                        sb.AppendLine($"\n🎮 {nr.GameFullName}");
                        currentGame = nr.GameFullName;
                    }
                    sb.AppendLine($"  - {nr.CategoryName}: {nr.RunnerName} ({FormatTime(nr.TimeInSeconds)}) [🌍 #{nr.WorldRank}]");
                }

                string text = sb.ToString();
                if (text.Length > 4000)
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
                    await command.FollowupWithFileAsync(stream, "Records_Nacionales.txt", text: "✅ Documento completo:");
                }
                else await command.FollowupAsync($"```text\n{text}\n```");
            }
            else if (command.CommandName == "player")
            {
                var user = command.Data.Options.First(x => x.Name == "usuario").Value?.ToString() ?? "";
                var all = await repo.GetRankingAsync("", "");
                var pRuns = all.Where(r => r.RunnerName.Equals(user, StringComparison.OrdinalIgnoreCase)).ToList();

                if (!pRuns.Any()) { await command.FollowupAsync("Jugador no encontrado."); return; }
                string profileUrl = $"https://www.speedrun.com/users/{pRuns[0].RunnerName}";

                var embed = new EmbedBuilder().WithTitle($"👤 Perfil: {pRuns[0].RunnerName}").WithUrl(profileUrl).WithColor(Color.Purple).WithThumbnailUrl(pRuns[0].GameThumbnail);
                foreach (var run in pRuns.Take(20))
                {
                    var natRank = all.Where(r => r.GameFullName == run.GameFullName && r.CategoryName == run.CategoryName)
                                     .OrderBy(r => r.TimeInSeconds).ToList().FindIndex(r => r.RunnerId == run.RunnerId) + 1;
                    embed.AddField(run.GameFullName, $"**{run.CategoryName}**: {FormatTime(run.TimeInSeconds)}\n🏆 Rank Nacional: #{natRank} 🇨🇷 | 🌍 Global: #{run.WorldRank}");
                }
                await command.FollowupAsync(embed: embed.Build());
            }
            else if (command.CommandName == "players")
            {
                var listaOption = command.Data.Options.FirstOrDefault(x => x.Name == "lista")?.Value?.ToString() ?? "";
                var allRuns = await repo.GetRankingAsync("", "");
                var uniquePlayers = allRuns.Select(r => r.RunnerName).Distinct().OrderBy(n => n).ToList();

                if (listaOption.ToLower() == "all")
                {
                    var sb = new StringBuilder().AppendLine("👥 LISTA DE RUNNERS REGISTRADOS\n");
                    foreach (var name in uniquePlayers) sb.AppendLine($"- {name}");
                    string text = sb.ToString();
                    if (text.Length > 4000)
                    {
                        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
                        await command.FollowupWithFileAsync(stream, "Lista_Runners.txt", text: "✅ Lista completa:");
                    }
                    else await command.FollowupAsync($"```text\n{text}\n```");
                }
                else
                {
                    await command.FollowupAsync($"👥 Actualmente hay **{uniquePlayers.Count}** runners registrados. Revisa el canal de censo o usa `/players all`.");
                }
            }
            else if (command.CommandName == "track")
            {
                if (!isAdmin) { await command.FollowupAsync("🚫 Solo los administradores pueden usar este comando."); return; }

                var abbreviation = command.Data.Options.First(x => x.Name == "juego").Value?.ToString() ?? "";
                var gameRepo = scope.ServiceProvider.GetRequiredService<IGameRepository>();

                using var httpClient = new HttpClient();
                var response = await httpClient.GetAsync($"https://www.speedrun.com/api/v1/games/{abbreviation}");
                if (!response.IsSuccessStatusCode) { await command.FollowupAsync($"❌ No encontré el juego `{abbreviation}`."); return; }

                var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var data = jsonDoc.RootElement.GetProperty("data");
                var gameId = data.GetProperty("id").GetString()!;
                var gameName = data.GetProperty("names").GetProperty("international").GetString();

                var currentGames = await gameRepo.GetTrackedGamesAsync();
                if (currentGames.Contains(gameId)) { await command.FollowupAsync($"⚠️ El juego **{gameName}** ya está vigilado."); return; }

                await gameRepo.AddGameAsync(gameId);
                await command.FollowupAsync($"✅ ¡Listo! **{gameName}** ha sido agregado a la vigilancia.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRITICAL ERROR] Falló comando {command.CommandName}: {ex}");
            try
            {
                await command.FollowupAsync($"❌ Ocurrió un error interno. Revisa la consola del bot.");
            }
            catch { }
        }
    }

    private bool IsAdmin(SocketGuildUser user, GuildConfig config) =>
        user.Guild.OwnerId == user.Id || user.GuildPermissions.Administrator || (config.AdminRoleId > 0 && user.Roles.Any(r => r.Id == config.AdminRoleId));

    private bool IsDataMaker(SocketGuildUser user, GuildConfig config) =>
        IsAdmin(user, config) || user.GuildPermissions.ManageMessages || (config.DataMakerRoleId > 0 && user.Roles.Any(r => r.Id == config.DataMakerRoleId));

    private async Task UpdateAllGuildPlayerCounts()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IRunRepository>();
            var db = scope.ServiceProvider.GetRequiredService<SpeedrunContext>();
            var count = (await repo.GetRankingAsync("", "")).Select(r => r.RunnerName).Distinct().Count();

            var configs = db.GuildConfigs.AsEnumerable().Where(c => c.PlayersChannelId > 0).ToList();

            var embed = new EmbedBuilder().WithTitle("📊 Censo Oficial de Speedrunners CR").WithDescription($"Actualmente la base de datos cuenta con:\n\n**{count} Corredores Verificados**").WithColor(Color.Green).WithThumbnailUrl("https://www.speedrun.com/images/flags/cr.png").WithFooter($"Última actualización: {DateTime.Now:HH:mm:ss}").Build();
            foreach (var c in configs)
            {
                if (await _client.GetChannelAsync(c.PlayersChannelId) is ITextChannel ch)
                {
                    var msgs = await ch.GetMessagesAsync(15).FlattenAsync();
                    var botMsg = msgs.FirstOrDefault(m => m.Author.Id == _client.CurrentUser.Id);
                    if (botMsg is IUserMessage msg) await msg.ModifyAsync(x => x.Embed = embed);
                    else await ch.SendMessageAsync(embed: embed);
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"⚠️ Error actualizando censo: {ex.Message}"); }
    }

    public async Task SendNewRecordNotificationAsync(RunRecord newRecord, double? prev, bool isNr, int rank)
    {
        if (_client.ConnectionState != ConnectionState.Connected) await Task.Delay(5000);
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpeedrunContext>();

        var configs = db.GuildConfigs.AsEnumerable().Where(c => c.AnnounceChannelId > 0).ToList();

        var embed = new EmbedBuilder().WithTitle(isNr ? "🏆 ¡NUEVO RÉCORD NACIONAL! 🏆" : "🚨 ¡NUEVO PERSONAL BEST! 🚨")
            .WithColor(isNr ? Color.Gold : Color.Green).WithThumbnailUrl(newRecord.GameThumbnail)
            .WithDescription($"**Jugador:** {newRecord.RunnerName}\n**Juego:** {newRecord.GameFullName}\n" +
                             $"**Tiempo:** {FormatTime(newRecord.TimeInSeconds)}\n\n[Ver validación]({newRecord.RunLink})")
            .WithFooter($"Rank Nacional: #{rank} 🇨🇷 | 🌍 Global: #{newRecord.WorldRank}").Build();

        foreach (var c in configs)
            if (await _client.GetChannelAsync(c.AnnounceChannelId) is IMessageChannel ch)
                await ch.SendMessageAsync(text: isNr ? "@everyone" : "", embed: embed);

        await UpdateAllGuildPlayerCounts();
    }

    private async Task AutocompleteHandler(SocketAutocompleteInteraction interaction)
    {
        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRunRepository>();
        var all = await repo.GetRankingAsync("", "");

        if (interaction.Data.Current.Name == "juego")
        {
            var choices = all.Select(r => r.GameFullName).Distinct().Where(g => g.Contains(interaction.Data.Current.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase)).Take(25).Select(g => new AutocompleteResult(g, g));
            await interaction.RespondAsync(choices);
        }
        else if (interaction.Data.Current.Name == "categoria")
        {
            var game = interaction.Data.Options.FirstOrDefault(o => o.Name == "juego")?.Value?.ToString() ?? "";
            var list = new List<AutocompleteResult> { new AutocompleteResult("--- Todas ---", "ALL_CATEGORIES") };
            list.AddRange(all.Where(r => r.GameFullName == game).Select(r => r.CategoryName).Distinct().Where(c => c.Contains(interaction.Data.Current.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase)).Take(24).Select(c => new AutocompleteResult(c, c)));
            await interaction.RespondAsync(list);
        }
        else if (interaction.Data.Current.Name == "usuario" || interaction.Data.Current.Name == "nombre")
        {
            var choices = all.Select(r => r.RunnerName).Distinct().Where(u => u.Contains(interaction.Data.Current.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase)).Take(25).Select(u => new AutocompleteResult(u, u));
            await interaction.RespondAsync(choices);
        }
    }

    private string FormatTime(double s)
    {
        TimeSpan t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }
}