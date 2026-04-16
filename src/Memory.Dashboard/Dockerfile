FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ContextHub.slnx ./
COPY Directory.Build.props ./
COPY src/Memory.Domain/Memory.Domain.csproj src/Memory.Domain/
COPY src/Memory.Application/Memory.Application.csproj src/Memory.Application/
COPY src/Memory.Infrastructure/Memory.Infrastructure.csproj src/Memory.Infrastructure/
COPY src/Memory.Dashboard/Memory.Dashboard.csproj src/Memory.Dashboard/

RUN dotnet restore src/Memory.Dashboard/Memory.Dashboard.csproj

COPY . .
RUN dotnet publish src/Memory.Dashboard/Memory.Dashboard.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8088
ENTRYPOINT ["dotnet", "Memory.Dashboard.dll"]
