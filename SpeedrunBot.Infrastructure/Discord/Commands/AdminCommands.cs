using Discord;
using Microsoft.Extensions.DependencyInjection;
using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Application.UseCases;
using SpeedrunBot.Domain.Entities;
using System.Text;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace SpeedrunBot.Infrastructure.Discord.Commands;

public static class AdminCommands
{
    public static async Task HandleAsync(BotCommandContext ctx)
    {
        if (ctx.Command.CommandName == "setup" || ctx.Command.CommandName == "update")
        {
            if (!ctx.IsAdmin) { await ctx.Command.FollowupAsync("🚫 Permisos insuficientes."); return; }
            var config = ctx.GuildConfig ?? new GuildConfig { GuildId = ctx.Command.GuildId ?? 0 };
            if (ctx.Db.Entry(config).State == Microsoft.EntityFrameworkCore.EntityState.Detached) ctx.Db.GuildConfigs.Add(config);

            if (ctx.Command.Data.Options.FirstOrDefault(x => x.Name == "new_records_announcement")?.Value is IChannel chA) config.AnnounceChannelId = chA.Id;
            if (ctx.Command.Data.Options.FirstOrDefault(x => x.Name == "rankings")?.Value is IChannel chR) config.RankingsChannelId = chR.Id;
            if (ctx.Command.Data.Options.FirstOrDefault(x => x.Name == "registro")?.Value is IChannel chReg) config.RegisterChannelId = chReg.Id;
            if (ctx.Command.Data.Options.FirstOrDefault(x => x.Name == "player_count")?.Value is IChannel chC) config.PlayersChannelId = chC.Id;
            if (ctx.Command.Data.Options.FirstOrDefault(x => x.Name == "rol_data_helpers")?.Value is IRole rDH) config.DataMakerRoleId = rDH.Id;
            if (ctx.Command.Data.Options.FirstOrDefault(x => x.Name == "rol_notificaciones_nr")?.Value is IRole rNR) config.NrPingRoleId = rNR.Id;

            await ctx.Db.SaveChangesAsync();

            if (ctx.Command.CommandName == "setup")
            {
                var welcomeMsg = "✅ **Servidor configurado correctamente.**\n\n" +
                                 "*Hola! Mi nombre es realxones o jdsolano02, gracias por incluir mi bot en tu Discord.*\n\n" +
                                 "*Si quieres apoyar a mantener corriendo el bot de manera gratuita para toda la comunidad, considera dejar tu propina aquí:* https://streamelements.com/realxones/tip \n" +
                                 "Puedes revisar la documentación del bot aquí: https://github.com/jdsolano02/speedrun-discord-bot \n" +
                                 "También revisa mis redes sociales: https://linktr.ee/Xones \n" +
                                 "**¡Muchas gracias por tu apoyo!**";
                await ctx.Command.FollowupAsync(welcomeMsg);
            }
            else
            {
                await ctx.Command.FollowupAsync("✅ **Configuración actualizada.**");
            }

            await ctx.UpdateCensusAction();
        }
        else if (ctx.Command.CommandName == "register")
        {
            if (ctx.Command.ChannelId != ctx.GuildConfig.RegisterChannelId) { await ctx.Command.FollowupAsync($"❌ Usa este comando en <#{ctx.GuildConfig.RegisterChannelId}>"); return; }
            var sub = ctx.Command.Data.Options.First();

            if (sub.Name == "usuario")
            {
                var target = sub.Options.First().Value.ToString()!;
                var res = await ctx.ScopeProvider.GetRequiredService<RegisterUser>().ExecuteAsync(target);
                await File.AppendAllLinesAsync(ctx.SyncedRunnersPath, new[] { target });
                await ctx.Command.FollowupAsync(res);
                await ctx.UpdateCensusAction();
            }
            else if (sub.Name == "pending")
            {
                if (!ctx.IsDataHelper) { await ctx.Command.FollowupAsync("🚫 Rol de **Data Helper** o **Admin** requerido."); return; }
                var allRunners = (await ctx.Repo.GetRankingAsync("", "")).Select(r => r.RunnerName).Distinct().ToList();
                var synced = File.Exists(ctx.SyncedRunnersPath) ? new HashSet<string>(await File.ReadAllLinesAsync(ctx.SyncedRunnersPath), StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
                var missing = allRunners.Where(n => !synced.Contains(n)).ToList();

                if (!missing.Any()) { await ctx.Command.FollowupAsync("✅ No hay runners pendientes."); return; }
                await ctx.Command.FollowupAsync($"🔍 Sincronización de **{missing.Count}** runners iniciada en segundo plano...");

                _ = Task.Run(async () => {
                    using var bg = ctx.ScopeProvider.CreateScope();
                    var reg = bg.ServiceProvider.GetRequiredService<RegisterUser>();
                    foreach (var m in missing) { try { await reg.ExecuteAsync(m); await File.AppendAllLinesAsync(ctx.SyncedRunnersPath, new[] { m }); await Task.Delay(2000); } catch { } }
                    if (await ctx.Client.GetChannelAsync(ctx.Command.ChannelId ?? 0) is IMessageChannel ch)
                        await ch.SendMessageAsync($"🔔 **Sincronización finalizada:** {missing.Count} runners importados.");
                    await ctx.UpdateCensusAction();
                });
            }
            else if (sub.Name == "all")
            {
                if (!ctx.IsAdmin) { await ctx.Command.FollowupAsync("🚫 Solo para Administradores."); return; }
                var allRunners = (await ctx.Repo.GetRankingAsync("", "")).Select(r => r.RunnerName).Distinct().ToList();
                await ctx.Command.FollowupAsync($"🚀 Iniciando actualización masiva de **{allRunners.Count}** perfiles...");
                _ = Task.Run(async () => {
                    using var bg = ctx.ScopeProvider.CreateScope();
                    var reg = bg.ServiceProvider.GetRequiredService<RegisterUser>();
                    foreach (var r in allRunners) { try { await reg.ExecuteAsync(r); await Task.Delay(2000); } catch { } }
                    if (await ctx.Client.GetChannelAsync(ctx.Command.ChannelId ?? 0) is IMessageChannel ch)
                        await ch.SendMessageAsync($"🔔 **Actualización masiva completada.** Datos de PBs actualizados.");
                });
            }
        }
        else if (ctx.Command.CommandName == "export_socials")
        {
            if (!ctx.IsAdmin) { await ctx.Command.FollowupAsync("🚫 Solo para Administradores."); return; }

            await ctx.Command.FollowupAsync("⏳ **Iniciando extracción de redes sociales...**\n*Esto tomará un par de minutos para no saturar la API de Speedrun.com (HTTP 429). El archivo se enviará por aquí cuando esté listo.*");

            _ = Task.Run(async () => {
                try
                {
                    // FIXED: Creating scope from Root Provider ensures it survives 
                    // even after the original command context is disposed
                    using var bg = ctx.ScopeProvider.CreateScope();
                    var repo = bg.ServiceProvider.GetRequiredService<IRunRepository>();

                    var allRuns = await repo.GetRankingAsync("", "");
                    var uniqueRunners = allRuns.Select(r => new { r.RunnerId, r.RunnerName }).Distinct().ToList();

                    var sb = new StringBuilder();
                    sb.AppendLine("RunnerName,Twitch,YouTube,Twitter,SpeedrunComProfile");

                    using var httpClient = new HttpClient { BaseAddress = new Uri("https://www.speedrun.com/api/v1/") };

                    foreach (var runner in uniqueRunners)
                    {
                        try
                        {
                            var response = await httpClient.GetAsync($"users/{runner.RunnerId}");
                            if (response.IsSuccessStatusCode)
                            {
                                var json = await response.Content.ReadAsStringAsync();
                                using var doc = JsonDocument.Parse(json);
                                var data = doc.RootElement.GetProperty("data");

                                string twitch = data.TryGetProperty("twitch", out var tw) && tw.ValueKind != JsonValueKind.Null ? tw.GetProperty("uri").GetString() ?? "" : "";
                                string youtube = data.TryGetProperty("youtube", out var yt) && yt.ValueKind != JsonValueKind.Null ? yt.GetProperty("uri").GetString() ?? "" : "";
                                string twitter = data.TryGetProperty("twitter", out var twi) && twi.ValueKind != JsonValueKind.Null ? twi.GetProperty("uri").GetString() ?? "" : "";
                                string weblink = data.TryGetProperty("weblink", out var web) && web.ValueKind != JsonValueKind.Null ? web.GetString() ?? "" : "";

                                sb.AppendLine($"{runner.RunnerName},{twitch},{youtube},{twitter},{weblink}");
                            }
                            await Task.Delay(1500);
                        }
                        catch { /* Ignore */ }
                    }

                    if (await ctx.Client.GetChannelAsync(ctx.Command.ChannelId ?? 0) is IMessageChannel ch)
                    {
                        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
                        await ch.SendFileAsync(stream, "socials_costa_rica.csv", "✅ **Extracción completada.** Aquí tenés la lista de contactos de todos los runners:");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Error exportando socials: {ex.Message}");
                }
            });
        }
    }
}