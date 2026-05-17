using Discord;
using SpeedrunBot.Infrastructure.Discord.Utils;
using System.Text;

namespace SpeedrunBot.Infrastructure.Discord.Commands;

public static class PlayerCommands
{
    public static async Task HandleAsync(BotCommandContext ctx)
    {
        if (ctx.Command.CommandName == "top")
        {
            var subCommand = ctx.Command.Data.Options.First();
            var allRuns = await ctx.Repo.GetRankingAsync("", "");

            if (subCommand.Name == "runs")
            {
                var validRuns = allRuns
                    .Where(r => r.TotalGlobalRunners > 0 && r.WorldRank > 0 && FormattingUtils.IsEligibleForPrestige(r.GameFullName, r.CategoryName))
                    .Select(r => new {
                        Run = r,
                        Weight = (double)r.WorldRank / r.TotalGlobalRunners,
                        // NEW: Clamp ensures the prestige is strictly between 0 and 100.
                        Prestige = Math.Clamp((1.0 - ((double)r.WorldRank / r.TotalGlobalRunners)) * 100.0, 0, 100)
                    })
                    .OrderBy(x => x.Weight)
                    .Take(20)
                    .ToList();

                if (!validRuns.Any()) { await ctx.Command.FollowupAsync("Aún no hay suficientes datos globales recolectados."); return; }

                var embed = new EmbedBuilder()
                    .WithTitle("🌟 Top 20 Mejores Runs de Costa Rica")
                    .WithColor(Color.Magenta)
                    .WithDescription("Calculado mediante percentil global de los Main Leaderboards.\n\n");

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
                await ctx.Command.FollowupAsync(embed: embed.Build());
            }
            else if (subCommand.Name == "players")
            {
                var playersScore = allRuns
                    .Where(r => r.TotalGlobalRunners > 0 && r.WorldRank > 0 && FormattingUtils.IsEligibleForPrestige(r.GameFullName, r.CategoryName))
                    .GroupBy(r => r.RunnerName)
                    .Select(g => {
                        // NEW: Clamp each run's score before summing
                        double totalPrestige = g.Sum(r => Math.Clamp((1.0 - ((double)r.WorldRank / r.TotalGlobalRunners)) * 100.0, 0, 100));
                        return new { RunnerName = g.Key, TotalPrestige = totalPrestige, RunCount = g.Count() };
                    })
                    .OrderByDescending(x => x.TotalPrestige)
                    .Take(20)
                    .ToList();

                if (!playersScore.Any()) { await ctx.Command.FollowupAsync("Aún no hay suficientes datos globales recolectados."); return; }

                var embed = new EmbedBuilder()
                    .WithTitle("🎖️ Top 20 Jugadores por Prestigio Total")
                    .WithColor(Color.Gold)
                    .WithDescription("Calculado sumando el prestigio de los Main Leaderboards del jugador.\n\n");

                var sb = new StringBuilder();
                int rankIndex = 1;
                foreach (var p in playersScore)
                {
                    string medal = rankIndex switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"**{rankIndex}.**" };
                    sb.AppendLine($"{medal} **{p.RunnerName}** - `{p.TotalPrestige:F0} pts` *(en {p.RunCount} main runs)*");
                    rankIndex++;
                }

                embed.WithDescription(embed.Description + sb.ToString());
                await ctx.Command.FollowupAsync(embed: embed.Build());
            }
        }
        else if (ctx.Command.CommandName == "player")
        {
            if (ctx.Command.ChannelId != ctx.GuildConfig.RankingsChannelId) { await ctx.Command.FollowupAsync($"❌ Usa <#{ctx.GuildConfig.RankingsChannelId}>."); return; }

            var user = ctx.Command.Data.Options.First().Value.ToString()!;
            var all = await ctx.Repo.GetRankingAsync("", "");
            var pRuns = all.Where(r => r.RunnerName.Equals(user, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!pRuns.Any()) { await ctx.Command.FollowupAsync("Corredor no encontrado."); return; }

            var allPlayersPrestige = all
                .Where(r => r.TotalGlobalRunners > 0 && r.WorldRank > 0 && FormattingUtils.IsEligibleForPrestige(r.GameFullName, r.CategoryName))
                .GroupBy(r => r.RunnerName)
                .Select(g => new {
                    RunnerName = g.Key,
                    // NEW: Clamp each run's score before summing for total player ranking
                    TotalPrestige = g.Sum(r => Math.Clamp((1.0 - ((double)r.WorldRank / r.TotalGlobalRunners)) * 100.0, 0, 100))
                })
                .OrderByDescending(x => x.TotalPrestige)
                .ToList();

            var targetPlayer = allPlayersPrestige.FirstOrDefault(p => p.RunnerName.Equals(pRuns[0].RunnerName, StringComparison.OrdinalIgnoreCase));
            double totalPrestigeScore = targetPlayer?.TotalPrestige ?? 0;
            int playerNationalRank = targetPlayer != null ? allPlayersPrestige.IndexOf(targetPlayer) + 1 : 0;
            string playerRankText = playerNationalRank > 0 ? $"\n🎖️ **Runner top `#{playerNationalRank}` del país**" : "";

            var profileUrl = $"https://www.speedrun.com/users/{pRuns[0].RunnerName.Replace(" ", "_")}";
            var embed = new EmbedBuilder()
                .WithTitle($"👤 Perfil: {pRuns[0].RunnerName}").WithUrl(profileUrl)
                .WithDescription($"[🔗 Ver perfil en Speedrun.com]({profileUrl})\n\n🎮 Total de Runs: **{pRuns.Count}**\n✨ **Prestigio Total:** `{totalPrestigeScore:F0} pts`{playerRankText}")
                .WithColor(Color.Purple).WithThumbnailUrl(pRuns[0].GameThumbnail);

            var allRunsRanked = all
                .Where(r => r.TotalGlobalRunners > 0 && r.WorldRank > 0 && FormattingUtils.IsEligibleForPrestige(r.GameFullName, r.CategoryName))
                .Select(r => new {
                    RunLink = r.RunLink,
                    // NEW: Clamp
                    Prestige = Math.Clamp((1.0 - ((double)r.WorldRank / r.TotalGlobalRunners)) * 100.0, 0, 100)
                })
                .OrderByDescending(x => x.Prestige)
                .ToList();

            foreach (var run in pRuns.Take(15))
            {
                var natRank = all.Where(r => r.GameFullName == run.GameFullName && r.CategoryName == run.CategoryName)
                                 .OrderBy(r => r.TimeInSeconds).ToList().FindIndex(r => r.RunnerId == run.RunnerId) + 1;

                string weightDisplay = "";
                if (run.TotalGlobalRunners > 0 && run.WorldRank > 0)
                {
                    if (FormattingUtils.IsEligibleForPrestige(run.GameFullName, run.CategoryName))
                    {
                        // Safely calculate weight and clamp percentage
                        double rawWeight = (double)run.WorldRank / run.TotalGlobalRunners;
                        double runWeight = Math.Clamp(rawWeight, 0, 1);
                        double runPrestige = Math.Clamp((1.0 - runWeight) * 100.0, 0, 100);

                        int runCountryRank = allRunsRanked.FindIndex(x => x.RunLink == run.RunLink) + 1;
                        string runCountryRankText = runCountryRank > 0 ? $"\n🏅 Run top `#{runCountryRank}` del país" : "";

                        weightDisplay = $"\n⚖️ Prestigio: `{runPrestige:F2} pts` (Top {runWeight * 100:F1}%){runCountryRankText}";
                    }
                    else
                    {
                        weightDisplay = $"\n⚖️ Prestigio: `0.00 pts` *(Categoría For Fun / Extensión)*";
                    }
                }
                else
                {
                    if (!FormattingUtils.IsEligibleForPrestige(run.GameFullName, run.CategoryName))
                    {
                        weightDisplay = $"\n⚖️ Prestigio: `0.00 pts` *(Categoría For Fun / Extensión)*";
                    }
                }

                embed.AddField(run.GameFullName, $"**{run.CategoryName}**: {FormattingUtils.FormatTime(run.TimeInSeconds)}\n🇨🇷 Rank CR: #{natRank} | 🌍 Global: #{run.WorldRank} de {run.TotalGlobalRunners}{weightDisplay}");
            }
            await ctx.Command.FollowupAsync(embed: embed.Build());
        }
        else if (ctx.Command.CommandName == "players")
        {
            var mode = ctx.Command.Data.Options.FirstOrDefault(x => x.Name == "lista")?.Value?.ToString();
            var juegoFilter = ctx.Command.Data.Options.FirstOrDefault(x => x.Name == "juego")?.Value?.ToString();

            var allRuns = await ctx.Repo.GetRankingAsync(juegoFilter ?? "", "");
            var targetRunners = allRuns.Select(r => r.RunnerName).Distinct().OrderBy(n => n).ToList();

            if (!targetRunners.Any())
            {
                await ctx.Command.FollowupAsync(string.IsNullOrEmpty(juegoFilter) ? "No se encontraron corredores." : $"No hay corredores registrados para `{juegoFilter}`.");
                return;
            }

            var synced = File.Exists(ctx.SyncedRunnersPath) ? new HashSet<string>(await File.ReadAllLinesAsync(ctx.SyncedRunnersPath), StringComparer.OrdinalIgnoreCase) : new HashSet<string>();

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
                targetRunners = missing;
            }
            else if (mode == "all")
            {
                description = string.Join(", ", targetRunners);
            }
            else
            {
                description = $"Actualmente hay **{targetRunners.Count}** runners registrados y **{targetRunners.Count(n => !synced.Contains(n))}** pendientes.";
            }

            if (description.Length > 4000)
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(description));
                await ctx.Command.FollowupWithFileAsync(stream, "runners.txt", $"📄 **{title}**\n*(Lista demasiado larga para Discord, enviada como archivo adjunto. Total: **{targetRunners.Count}**)*");
            }
            else
            {
                var embed = new EmbedBuilder()
                    .WithTitle(title)
                    .WithColor(Color.Blue)
                    .WithDescription(description)
                    .WithFooter($"Total: {targetRunners.Count}");

                await ctx.Command.FollowupAsync(embed: embed.Build());
            }
        }
    }
}