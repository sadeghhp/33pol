# Multi-stage build for 33pol gateway (Phase 1+).
# Layer order: manifests → restore (cached when only .cs changes) → full src → publish.
# Requires BuildKit (default in Docker 24+): NuGet package cache mount across builds.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG APP_VERSION=2.0.0
ARG APP_ASSEMBLY_VERSION=2.0.0
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_PACKAGES=/root/.nuget/packages

COPY Directory.Build.props Directory.Packages.props .editorconfig 33pol.sln ./
COPY config/ config/

# Restore layer: copy only project manifests so code edits do not bust NuGet cache.
COPY src/33pol.App/33pol.App.csproj src/33pol.App/
COPY src/33pol.Api/33pol.Api.csproj src/33pol.Api/
COPY src/33pol.Billing/33pol.Billing.csproj src/33pol.Billing/
COPY src/33pol.Core/33pol.Core.csproj src/33pol.Core/
COPY src/33pol.Observability/33pol.Observability.csproj src/33pol.Observability/
COPY src/33pol.OperatorConsole/33pol.OperatorConsole.csproj src/33pol.OperatorConsole/
COPY src/33pol.Persistence/33pol.Persistence.csproj src/33pol.Persistence/
COPY src/33pol.Policy/33pol.Policy.csproj src/33pol.Policy/
COPY src/33pol.Proxy/33pol.Proxy.csproj src/33pol.Proxy/
COPY src/33pol.Registry/33pol.Registry.csproj src/33pol.Registry/
COPY src/33pol.Security/33pol.Security.csproj src/33pol.Security/
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore src/33pol.App/33pol.App.csproj

COPY src/ src/
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/33pol.App/33pol.App.csproj -c Release -o /app/publish \
    /p:UseAppHost=false /p:Version=${APP_ASSEMBLY_VERSION} /p:InformationalVersion=${APP_VERSION} --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .
COPY deploy/docker/gateway-healthcheck.sh /app/healthcheck.sh
RUN chmod +x /app/healthcheck.sh

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "33pol.App.dll"]
