# Multi-stage build for 33pol gateway (Phase 1+).
# Switch final stage to mcr.microsoft.com/dotnet/aspnet when the host uses Microsoft.NET.Sdk.Web.
FROM mcr.microsoft.com/dotnet/sdk:10.0.100-preview.1.26101.3 AS build
WORKDIR /src

COPY ["33pol.csproj", "./"]
RUN dotnet restore "33pol.csproj"

COPY . .
RUN dotnet publish "33pol.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0.100-preview.1.26101.3 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "33pol.dll"]
