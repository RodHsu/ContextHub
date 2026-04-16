using Memory.Application;
using Memory.Infrastructure;
using Memory.Worker;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryApplication();
builder.Services.AddMemoryInfrastructure(builder.Configuration, "worker");
builder.Services.AddHostedService<JobWorker>();

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});

app.Run();
