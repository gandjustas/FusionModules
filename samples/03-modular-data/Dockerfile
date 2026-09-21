# syntax=docker/dockerfile:1
#
# The directive is load-bearing: COPY --parents below needs Dockerfile frontend 1.20, and without
# it BuildKit uses whatever frontend is built into the daemon. On an older one the build fails on
# a flag rather than on anything to do with this sample.

# One image for every topology. Which modules run is decided at startup, so there is nothing
# topology-specific to build and nothing to rebuild when the topology changes.
#
# Built from the repository root, because the sample consumes Modulith by project reference.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The sample multi-targets so that net8.0 is exercised by its tests; the image needs one runtime
# and names it. Without -f, publish on a multi-targeted project has nowhere to put the output.

# Restore against the project files alone, so a code change does not invalidate the restore layer.
COPY --parents Directory.Build.props Directory.Packages.props global.json ./
COPY --parents src/**/*.csproj src/Directory.Build.props ./
COPY --parents samples/Directory.Build.* samples/03-modular-data/**/*.csproj samples/03-modular-data/Directory.Build.props ./
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore samples/03-modular-data/Host/Host.csproj

COPY --parents src samples/03-modular-data ./
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish samples/03-modular-data/Host/Host.csproj -c Release --no-restore -f net10.0 -o /app

# Not PublishAot or PublishTrimmed: modules are loaded by name at runtime, so the trimmer cannot
# see that they are used. This is the cost of the approach, and it is worth stating out loud.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Host.dll"]
