# syntax=docker/dockerfile:1

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-alpine AS build
WORKDIR /src

COPY Destiny2Report.API/Destiny2Report.API.csproj Destiny2Report.API/
COPY Destiny2Report.BungieClient/Destiny2Report.BungieClient.csproj Destiny2Report.BungieClient/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore Destiny2Report.API/Destiny2Report.API.csproj

COPY Destiny2Report.API/ Destiny2Report.API/
COPY Destiny2Report.BungieClient/ Destiny2Report.BungieClient/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish Destiny2Report.API/Destiny2Report.API.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    -p:UseAppHost=false \
    -p:DebugSymbols=false \
    -p:DebugType=None \
    -p:SkipGenerateOpenApiClient=true

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

EXPOSE 8080
COPY --link --from=build --chown=$APP_UID:$APP_UID /app/publish .
USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 \
    CMD ["wget", "--no-verbose", "--tries=1", "--spider", "http://localhost:8080/health"]
ENTRYPOINT ["dotnet", "Destiny2Report.API.dll"]
