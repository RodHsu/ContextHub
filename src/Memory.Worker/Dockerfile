FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ContextHub.slnx ./
COPY Directory.Build.props ./
COPY src/Memory.Domain/Memory.Domain.csproj src/Memory.Domain/
COPY src/Memory.Application/Memory.Application.csproj src/Memory.Application/
COPY src/Memory.Infrastructure/Memory.Infrastructure.csproj src/Memory.Infrastructure/
COPY src/Memory.Worker/Memory.Worker.csproj src/Memory.Worker/

RUN dotnet restore src/Memory.Worker/Memory.Worker.csproj

COPY . .
RUN dotnet publish src/Memory.Worker/Memory.Worker.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Memory.Worker.dll"]
