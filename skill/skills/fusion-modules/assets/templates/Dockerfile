# One image for every topology. Which modules run is decided at startup, so there is nothing
# topology-specific to build and nothing to rebuild when the topology changes.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project files alone, so a code change does not invalidate the restore layer.
COPY --parents Directory.Build.props Directory.Packages.props global.json ./
COPY --parents **/*.csproj ./
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore Host/Host.csproj

COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish Host/Host.csproj -c Release --no-restore -o /app

# Not PublishAot and not PublishTrimmed: modules are loaded by name at runtime, so the trimmer
# cannot see that they are used and removes them. This is the cost of the approach.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Host.dll"]
