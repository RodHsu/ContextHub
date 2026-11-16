FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src
ARG APP_VERSION=1.1.0
ARG BUILD_TIMESTAMP_UTC

COPY ContextHub.slnx ./
COPY Directory.Build.props ./
COPY src/Memory.Domain/Memory.Domain.csproj src/Memory.Domain/
COPY src/Memory.Application/Memory.Application.csproj src/Memory.Application/
COPY src/Memory.Infrastructure/Memory.Infrastructure.csproj src/Memory.Infrastructure/
COPY src/Memory.Worker/Memory.Worker.csproj src/Memory.Worker/

RUN --mount=type=cache,id=contexthub-nuget-dotnet10-v1,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Memory.Worker/Memory.Worker.csproj --disable-parallel

COPY . .
RUN --mount=type=cache,id=contexthub-nuget-dotnet10-v1,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Memory.Worker/Memory.Worker.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false /p:Version=$APP_VERSION /p:InformationalVersion=$APP_VERSION /p:BuildTimestampUtc=$BUILD_TIMESTAMP_UTC

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Memory.Worker.dll"]
