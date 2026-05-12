using SpeedrunBot.Application.Interfaces;

namespace SpeedrunBot.Application.UseCases;

public class RegisterUser(
    ISpeedrunApi speedrunApi,
    IRunRepository repository,
    IGameRepository gameRepository)
{
    public async Task<string> ExecuteAsync(string username)
    {
        var user = await speedrunApi.GetUserByNameAsync(username);
        if (user == null) return $"❌ No se encontró el usuario `{username}` en Speedrun.com.";

        var personalBests = await speedrunApi.GetUserPersonalBestsAsync(user.Value.Id, user.Value.Name);

        if (personalBests.Count == 0) return $"⚠️ El usuario **{user.Value.Name}** no tiene récords de juego completo.";

        foreach (var run in personalBests)
        {
            // Guarda el récord
            await repository.SavePersonalBestAsync(run);

            // Agrega el ID del juego a la lista de vigilancia dinámica
            await gameRepository.AddGameAsync(run.GameId);
        }

        return $"✅ ¡**{user.Value.Name}** registrado! Se importaron {personalBests.Count} récords y se actualizaron los juegos a vigilar automáticamente.";
    }
}