FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
EXPOSE 8083

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG APP_VERSION=1.1.0
ARG BUILD_TIMESTAMP_UTC=
WORKDIR /src
COPY . .
RUN dotnet publish src/Memory.ChatGptGateway/Memory.ChatGptGateway.csproj -c Release -o /app/publish \
    /p:Version=$APP_VERSION \
    /p:FileVersion=$APP_VERSION \
    /p:InformationalVersion=$APP_VERSION \
    /p:BuildTimestampUtc=$BUILD_TIMESTAMP_UTC

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Memory.ChatGptGateway.dll"]
