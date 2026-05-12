# 1. Etapa de Construcción (Usa el SDK pesado para compilar)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copiamos todo el código de tu PC al contenedor
COPY . ./

# Compilamos y publicamos el proyecto Worker y sus dependencias
RUN dotnet publish SpeedrunBot.Worker/SpeedrunBot.Worker.csproj -c Release -o /out

# 2. Etapa de Producción (Usa el Runtime ligero para ahorrar memoria RAM)
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

# Traemos solo los archivos compilados de la etapa anterior
COPY --from=build /out .

# Le decimos a Docker cómo arrancar el bot
ENTRYPOINT ["dotnet", "SpeedrunBot.Worker.dll"]