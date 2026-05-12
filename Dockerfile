# STAGE 1: Build & Publish
# Use the heavy SDK image to compile the source code
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copy the entire solution to the container
COPY . ./

# Restore dependencies and publish the Worker project to the /out folder
RUN dotnet publish SpeedrunBot.Worker/SpeedrunBot.Worker.csproj -c Release -o /out

# STAGE 2: Final Runtime
# Use the lightweight runtime image for production (saves RAM and disk space)
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

# Copy only the compiled binaries from the build stage
COPY --from=build /out .

# Command to launch the background service
ENTRYPOINT ["dotnet", "SpeedrunBot.Worker.dll"]