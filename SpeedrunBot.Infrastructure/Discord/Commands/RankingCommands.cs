using Discord;
using Microsoft.Extensions.DependencyInjection;
using SpeedrunBot.Application.Interfaces;
using SpeedrunBot.Infrastructure.Discord.Utils;
using System.Text;

namespace SpeedrunBot.Infrastructure.Discord.Commands;

public static class RankingCommands
{
    public static async Task HandleAsync(BotCommandContext ctx)
    {
        if (ctx.Command.CommandName == "ranking")
        {
            if (ctx.Command.ChannelId != ctx.GuildConfig.RankingsChannelId) { await ctx.Command.FollowupAsync($"❌ Usa <#{ctx.GuildConfig.RankingsChannelId}> para rankings."); return; }
            var juego = ctx.Command.Data.Options.First(x => x.Name == "juego").Value?.ToString() ?? "";
            var cat = ctx.Command.Data.Options.First(x => x.Name == "categoria").Value?.ToString() ?? "";

            bool isAllCategories = string.IsNullOrWhiteSpace(cat) || cat.Equals("ALL_CATEGORIES", StringComparison.OrdinalIgnoreCase);
            var runs = await ctx.Repo.GetRankingAsync(juego, isAllCategories ? "" : cat);

            if (!runs.Any()) { await ctx.Command.FollowupAsync("Sin registros."); return; }

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
                        sb.AppendLine($"{medal} **{categoryRuns[i].RunnerName}**: {FormattingUtils.FormatTime(categoryRuns[i].TimeInSeconds)}");
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
                    embed.AddField($"{pos switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"#{pos}" }} {runs[i].RunnerName}", $"**Tiempo:** {FormattingUtils.FormatTime(runs[i].TimeInSeconds)} | 🌍 #{runs[i].WorldRank}");
                }
            }
            await ctx.Command.FollowupAsync(embed: embed.Build());
        }
        else if (ctx.Command.CommandName == "nr")
        {
            var allRuns = await ctx.Repo.GetRankingAsync("", "");
            var nrs = allRuns.GroupBy(r => new { r.GameFullName, r.CategoryName }).Select(g => g.OrderBy(r => r.TimeInSeconds).First()).OrderBy(r => r.GameFullName).ToList();
            var sb = new StringBuilder().AppendLine("🏆 RÉCORDS NACIONALES OFICIALES 🏆\n");
            foreach (var nr in nrs) sb.AppendLine($"- {nr.GameFullName} ({nr.CategoryName}): {nr.RunnerName} [{FormattingUtils.FormatTime(nr.TimeInSeconds)}]");

            string content = sb.ToString();

            if (content.Length > 1950)
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                await ctx.Command.FollowupWithFileAsync(stream, "nrs_costa_rica.txt", "🏆 **Récords Nacionales Oficiales**\n*(La lista es muy larga para mostrarla en el chat, aquí tienes el documento completo).*");
            }
            else
            {
                string ticks = new string('`', 3);
                await ctx.Command.FollowupAsync($"{ticks}text\n{content}{ticks}");
            }
        }
        else if (ctx.Command.CommandName == "game")
        {
            var gameRepo = ctx.ScopeProvider.GetRequiredService<IGameRepository>();
            var subCommand = ctx.Command.Data.Options.First();

            switch (subCommand.Name)
            {
                case "add":
                    if (!ctx.IsDataHelper) { await ctx.Command.FollowupAsync("🚫 Solo DataTakers/Admins."); return; }
                    var idAdd = subCommand.Options.First().Value.ToString()!;

                    var currentlyTracked = await gameRepo.GetTrackedGamesAsync();
                    if (currentlyTracked.Any(id => id.Equals(idAdd, StringComparison.OrdinalIgnoreCase)))
                    {
                        await ctx.Command.FollowupAsync($"⚠️ El juego `{idAdd}` ya se encuentra en la lista de escaneo.");
                        break;
                    }

                    await gameRepo.AddGameAsync(idAdd);
                    await ctx.Command.FollowupAsync($"✅ Juego `{idAdd}` añadido a la cola de escaneo.");
                    break;

                case "delete":
                    if (!ctx.IsDataHelper) { await ctx.Command.FollowupAsync("🚫 Solo DataTakers/Admins."); return; }
                    var idDel = subCommand.Options.First().Value.ToString()!;
                    await gameRepo.DeleteGameAsync(idDel);
                    await ctx.Command.FollowupAsync($"🗑️ Juego `{idDel}` eliminado de la base de datos.");
                    break;

                case "list":
                    var trackedIds = await gameRepo.GetTrackedGamesAsync();
                    var allRunsForNames = await ctx.Repo.GetRankingAsync("", "");

                    var gamesList = trackedIds.Select(id => {
                        var run = allRunsForNames.FirstOrDefault(r => r.GameId == id);
                        return run != null ? $"{run.GameFullName} ({id})" : id;
                    }).OrderBy(g => g).ToList();

                    var content = string.Join("\r\n", gamesList);
                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(content)))
                    {
                        await ctx.Command.FollowupWithFileAsync(stream, "juegos_cr.txt", $"📄 Lista de los **{gamesList.Count}** juegos monitoreados.");
                    }
                    break;

                case "recent":
                    var recentGames = await ctx.Repo.GetRecentlyActiveGamesAsync();
                    var embedR = new EmbedBuilder().WithTitle("🕒 Juegos con Actividad Reciente").WithColor(Color.Green)
                        .WithDescription(recentGames.Any() ? string.Join("\n", recentGames.Select((g, i) => $"{i + 1}. **{g}**")) : "No hay datos de fechas aún.");
                    await ctx.Command.FollowupAsync(embed: embedR.Build());
                    break;

                case "most_played":
                    var allForTop = await ctx.Repo.GetRankingAsync("", "");
                    var topGames = allForTop.GroupBy(r => r.GameFullName)
                        .Select(g => new { Name = g.Key, Players = g.Select(r => r.RunnerId).Distinct().Count() })
                        .OrderByDescending(x => x.Players)
                        .Take(20);

                    var embedTop = new EmbedBuilder().WithTitle("🔥 Juegos Más Jugados").WithColor(Color.Orange)
                        .WithDescription(string.Join("\n", topGames.Select((g, i) => $"{i + 1}. **{g.Name}** ({g.Players} runners)")));
                    await ctx.Command.FollowupAsync(embed: embedTop.Build());
                    break;
            }
        }
    }
}