# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Stage 1 — build & publish the whole solution, output the Daleel.Web app.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution + every project file first so `dotnet restore` is cached
# independently of source changes (only re-runs when a .csproj changes).
COPY Daleel.sln ./
COPY src/Daleel.Agent/Daleel.Agent.csproj                 src/Daleel.Agent/
COPY src/Daleel.Apify/Daleel.Apify.csproj                 src/Daleel.Apify/
COPY src/Daleel.Cli/Daleel.Cli.csproj                     src/Daleel.Cli/
COPY src/Daleel.Core/Daleel.Core.csproj                   src/Daleel.Core/
COPY src/Daleel.Pipeline/Daleel.Pipeline.csproj           src/Daleel.Pipeline/
COPY src/Daleel.Search/Daleel.Search.csproj               src/Daleel.Search/
COPY src/Daleel.Web/Daleel.Web.csproj                     src/Daleel.Web/
COPY src/Daleel.Web.Client/Daleel.Web.Client.csproj       src/Daleel.Web.Client/
COPY tests/Daleel.Agent.Tests/Daleel.Agent.Tests.csproj       tests/Daleel.Agent.Tests/
COPY tests/Daleel.Core.Tests/Daleel.Core.Tests.csproj         tests/Daleel.Core.Tests/
COPY tests/Daleel.E2E.Tests/Daleel.E2E.Tests.csproj           tests/Daleel.E2E.Tests/
COPY tests/Daleel.Pipeline.Tests/Daleel.Pipeline.Tests.csproj tests/Daleel.Pipeline.Tests/
COPY tests/Daleel.Search.Tests/Daleel.Search.Tests.csproj     tests/Daleel.Search.Tests/
COPY tests/Daleel.Web.Tests/Daleel.Web.Tests.csproj           tests/Daleel.Web.Tests/

RUN dotnet restore Daleel.sln

# Copy the rest of the source and build the entire solution.
COPY . .
RUN dotnet build Daleel.sln -c Release --no-restore

# Publish only the web entry point (Daleel.Web pulls in the WASM client + agents).
RUN dotnet publish src/Daleel.Web/Daleel.Web.csproj \
    -c Release --no-build -o /app/publish

# ---------------------------------------------------------------------------
# Stage 2 — slim ASP.NET runtime, non-root, listening on :8080.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# curl is used by the container HEALTHCHECK below.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Run as a dedicated non-root user.
RUN groupadd --system --gid 1001 daleel \
    && useradd --system --uid 1001 --gid daleel --no-create-home daleel

COPY --from=build --chown=daleel:daleel /app/publish ./

# Pre-create the SQLite data directory owned by the app user. WORKDIR creates
# /app as root, and COPY --chown only affects copied files — not the dir itself —
# so the non-root user can't create subdirs under /app at runtime. Creating
# /app/data here also seeds the named volume mounted at this path with the right
# ownership on first init, so the DB survives redeploys.
RUN mkdir -p /app/data && chown daleel:daleel /app/data

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080
USER daleel

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Daleel.Web.dll"]
