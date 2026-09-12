# One image for every topology. Which modules run is decided at startup, so there is nothing
# topology-specific to build and nothing to rebuild when the topology changes.
#
# Built from the repository root, because the sample consumes Modulith by project reference.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project files alone, so a code change does not invalidate the restore layer.
COPY --parents Directory.Build.props Directory.Packages.props global.json ./
COPY --parents src/**/*.csproj src/Directory.Build.props ./
COPY --parents samples/Directory.Build.* samples/03-modular-data/**/*.csproj samples/03-modular-data/Directory.Build.props ./
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore samples/03-modular-data/Host/Host.csproj

COPY --parents src samples/03-modular-data ./
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish samples/03-modular-data/Host/Host.csproj -c Release --no-restore -o /app

# Not PublishAot or PublishTrimmed: modules are loaded by name at runtime, so the trimmer cannot
# see that they are used. This is the cost of the approach, and it is worth stating out loud.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Host.dll"]
