# syntax=docker/dockerfile:1
#
# Build context is the repo root. Two targets (same layout as altered-bga-api):
#   docker build -f build/Dockerfile -t deck-api .
#   docker build -f build/Dockerfile --target migrations -t deck-api-migrations .
#
# `migrations` is a one-shot EF Core migrations bundle:
#   docker run --rm deck-api-migrations --connection "Host=postgres;Database=deckapi;..."
# The app image can also run the legacy import:
#   docker run --rm -e ConnectionStrings__decks=... -e UniquesSearch__BaseUrl=... deck-api import-legacy --source "Host=..."

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS restore
WORKDIR /src
COPY src/DeckApi/DeckApi.csproj src/DeckApi/
RUN dotnet restore src/DeckApi/DeckApi.csproj
COPY src/ src/

FROM restore AS publish
RUN dotnet publish src/DeckApi/DeckApi.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM restore AS migrations-build
ENV PATH="${PATH}:/root/.dotnet/tools"
RUN dotnet tool install --global dotnet-ef --version 10.* \
 && dotnet ef migrations bundle --project src/DeckApi/DeckApi.csproj --configuration Release --output /app/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS migrations
WORKDIR /app
COPY --from=migrations-build /app/efbundle .
ENTRYPOINT ["./efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS app
WORKDIR /app
COPY --from=publish /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "DeckApi.dll"]
