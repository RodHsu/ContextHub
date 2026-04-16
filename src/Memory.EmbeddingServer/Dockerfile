FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ContextHub.slnx ./
COPY Directory.Build.props ./
COPY src/Memory.Domain/Memory.Domain.csproj src/Memory.Domain/
COPY src/Memory.Application/Memory.Application.csproj src/Memory.Application/
COPY src/Memory.Infrastructure/Memory.Infrastructure.csproj src/Memory.Infrastructure/
COPY src/Memory.EmbeddingServer/Memory.EmbeddingServer.csproj src/Memory.EmbeddingServer/

RUN dotnet restore src/Memory.EmbeddingServer/Memory.EmbeddingServer.csproj

COPY . .
RUN dotnet publish src/Memory.EmbeddingServer/Memory.EmbeddingServer.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl libgomp1 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8081
ENTRYPOINT ["dotnet", "Memory.EmbeddingServer.dll"]
