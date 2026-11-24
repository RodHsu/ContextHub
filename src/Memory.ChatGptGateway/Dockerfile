FROM mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:aac50ceef10391e78a899e3d2ccd00bbafa2dcb56ccf4586cbccf00edb84fc21 AS base
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
EXPOSE 8083

FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d AS build
ARG APP_VERSION=1.1.0
ARG BUILD_TIMESTAMP_UTC=
WORKDIR /src

COPY ContextHub.slnx ./
COPY Directory.Build.props ./
COPY src/Memory.Domain/Memory.Domain.csproj src/Memory.Domain/
COPY src/Memory.Application/Memory.Application.csproj src/Memory.Application/
COPY src/Memory.Infrastructure/Memory.Infrastructure.csproj src/Memory.Infrastructure/
COPY src/Memory.McpTransport/Memory.McpTransport.csproj src/Memory.McpTransport/
COPY src/Memory.ChatGptGateway/Memory.ChatGptGateway.csproj src/Memory.ChatGptGateway/

RUN --mount=type=cache,id=contexthub-nuget-dotnet10-v1,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Memory.ChatGptGateway/Memory.ChatGptGateway.csproj --disable-parallel

COPY . .
RUN --mount=type=cache,id=contexthub-nuget-dotnet10-v1,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Memory.ChatGptGateway/Memory.ChatGptGateway.csproj -c Release -o /app/publish --no-restore \
    /p:Version=$APP_VERSION \
    /p:FileVersion=$APP_VERSION \
    /p:InformationalVersion=$APP_VERSION \
    /p:BuildTimestampUtc=$BUILD_TIMESTAMP_UTC

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Memory.ChatGptGateway.dll"]
