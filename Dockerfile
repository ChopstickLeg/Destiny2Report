# syntax=docker/dockerfile:1

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS restore
WORKDIR /src

COPY Destiny2Report.sln ./
COPY Destiny2Report.API/Destiny2Report.API.csproj Destiny2Report.API/
COPY Destiny2Report.BungieClient/Destiny2Report.BungieClient.csproj Destiny2Report.BungieClient/
COPY Destiny2Report.Tests/Destiny2Report.Tests.csproj Destiny2Report.Tests/
RUN dotnet restore Destiny2Report.API/Destiny2Report.API.csproj

FROM restore AS publish
COPY . .
RUN dotnet publish Destiny2Report.API/Destiny2Report.API.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    -p:UseAppHost=false \
    -p:SkipGenerateOpenApiClient=true

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

EXPOSE 8080
COPY --from=publish /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Destiny2Report.API.dll"]
