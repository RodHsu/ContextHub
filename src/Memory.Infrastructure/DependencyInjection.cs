using Memory.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using StackExchange.Redis;

namespace Memory.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddMemoryInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        services.Configure<MemoryOptions>(configuration.GetSection(MemoryOptions.SectionName));
        services.AddOptions<ContextHubOptions>()
            .Bind(configuration.GetSection(ContextHubOptions.SectionName))
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.InstanceId))
                {
                    options.InstanceId =
                        configuration["ContextHub:InstanceId"]
                        ?? configuration["Dashboard:InstanceId"]
                        ?? ProjectContext.DefaultProjectId;
                }

                options.InstanceId = options.InstanceId.Trim();
            });
        services.Configure<EmbeddingOptions>(configuration.GetSection(EmbeddingOptions.SectionName));
        services.PostConfigure<EmbeddingOptions>(EmbeddingProfileResolver.ApplyResolvedDefaults);
        services.Configure<DatabaseLoggingOptions>(configuration.GetSection(DatabaseLoggingOptions.SectionName));
        services.PostConfigure<DatabaseLoggingOptions>(options => options.ServiceName = serviceName);
        services.AddOptions<DockerRuntimeOptions>()
            .Configure(options =>
            {
                options.ComposeProject = configuration["Dashboard:ComposeProject"]?.Trim() is { Length: > 0 } composeProject
                    ? composeProject
                    : "contexthub";
                options.DockerEndpoint = configuration["Dashboard:DockerEndpoint"]?.Trim() is { Length: > 0 } dockerEndpoint
                    ? dockerEndpoint
                    : "unix:///var/run/docker.sock";
            });

        var postgresConnectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
        var redisConnectionString = configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.");

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(postgresConnectionString);
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();
        services.AddSingleton(dataSource);

        services.AddDbContextFactory<MemoryDbContext>((sp, options) =>
        {
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.UseVector());
        });

        services.AddDbContext<MemoryDbContext>((sp, options) =>
        {
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.UseVector());
        });

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<MemoryDbContext>());
        services.AddScoped<IHybridSearchStore, NpgsqlSearchStore>();
        services.AddScoped<IVectorStore, NpgsqlSearchStore>();
        services.AddScoped<IStorageExplorerStore, NpgsqlStorageExplorerStore>();

        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString));
        services.AddSingleton<ICacheVersionStore, RedisCacheVersionStore>();
        services.AddSingleton<IDashboardSnapshotStore, RedisDashboardSnapshotStore>();
        services.AddSingleton<DockerRuntimeMetricsService>();
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<DatabaseLogQueue>();
        services.AddSingleton<ILoggerProvider, DatabaseLoggerProvider>();
        services.AddHostedService<DatabaseMigrationHostedService>();
        services.AddHostedService<DatabaseLogWriterService>();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            return EmbeddingProfileResolver.Resolve(options);
        });
        services.AddSingleton<RequestTrafficMetricsCollector>();
        services.AddSingleton<IRequestTrafficSnapshotAccessor>(sp => sp.GetRequiredService<RequestTrafficMetricsCollector>());
        services.AddTransient<RequestTrafficDelegatingHandler>();
        services.AddSingleton<IResolvedEmbeddingProfileAccessor, ResolvedEmbeddingProfileAccessor>();
        services.AddSingleton<IRuntimeConfigurationAccessor, RuntimeConfigurationAccessor>();
        services.AddScoped<IServiceHealthAccessor, ServiceHealthAccessor>();
        services.AddHttpClient(HttpEmbeddingProviderClient.Name, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
            }

            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        })
        .AddHttpMessageHandler<RequestTrafficDelegatingHandler>();

        services.AddSingleton<IEmbeddingProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            if (options.Provider.Equals("Http", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpEmbeddingProvider(
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<IOptions<EmbeddingOptions>>(),
                    sp.GetRequiredService<ILogger<HttpEmbeddingProvider>>());
            }

            if (options.Provider.Equals("Onnx", StringComparison.OrdinalIgnoreCase))
            {
                return new LocalOnnxEmbeddingProvider(sp.GetRequiredService<IOptions<EmbeddingOptions>>());
            }

            return new DeterministicEmbeddingProvider(sp.GetRequiredService<IOptions<EmbeddingOptions>>());
        });

        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
            .AddCheck<RedisHealthCheck>("redis", tags: ["ready"])
            .AddCheck<EmbeddingProviderHealthCheck>("embeddings", tags: ["ready"]);

        return services;
    }
}
