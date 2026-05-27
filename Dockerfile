# Multi-stage build for 33pol gateway (Phase 1+).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props .editorconfig 33pol.sln ./
COPY config/ config/
COPY src/ src/
RUN dotnet restore src/33pol.App/33pol.App.csproj
RUN dotnet publish src/33pol.App/33pol.App.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# curl for container health checks (Compose / orchestrators)
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "33pol.App.dll"]
