using System.Diagnostics;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using DistributedCrm.Application.Abstractions;
using DistributedCrm.Application.UseCases.ReturnRequests;
using DistributedCrm.Infrastructure.Authentication;
using DistributedCrm.Infrastructure.Caching;
using DistributedCrm.Infrastructure.ExternalApis;
using DistributedCrm.Infrastructure.HealthChecks;
using DistributedCrm.Infrastructure.Messaging;
using DistributedCrm.Infrastructure.Persistence;
using DistributedCrm.Worker;
using DistributedCrm.Worker.Configuration;
using DistributedCrm.Worker.Telemetry;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

// A web host only so Container Apps can probe it: its probes are HTTP or TCP, never a command in the container.
// The health endpoints are the only HTTP surface; the real work is the hosted Worker below.
var builder = WebApplication.CreateBuilder(args);

// Read up front so a missing value stops startup with the variable's name, instead of failing on a message.
var postgresConnectionString = builder.Configuration.GetRequiredConnectionString("DistributedCrm");
var redisConnectionString = builder.Configuration.GetRequiredConnectionString("Redis");
var serviceBusConnectionString = builder.Configuration.GetRequiredConnectionString("ServiceBus");

// Used only for a dependency whose configuration has no password or key, which means Azure. There it finds the app's
// managed identity (AZURE_TOKEN_CREDENTIALS narrows the chain to it); on a laptop it would find `az login`.
var credential = new DefaultAzureCredential();
builder.Services.AddSingleton<TokenCredential>(credential);
var redisOptions = await EntraAuthentication.CreateRedisOptionsAsync(redisConnectionString, credential);

builder.AddTelemetry(credential);

builder.Services.AddDbContext<WorkerDbContext>(options =>
    options.UseNpgsql(postgresConnectionString, npgsqlOptions => npgsqlOptions
            .EnableRetryOnFailure(maxRetryCount: 5)
            // Trace SQL only inside an existing span (a message being processed); anything else would be a one-span
            // trace. Batches are filtered too because EF Core's SaveChanges sends one. Unlike in be, physical opens
            // stay traced: nothing here polls, so a reopen only happens inside a message's span, where it explains
            // a slow first query.
            .ConfigureDataSource(dataSource => dataSource
                .UseEntraTokenWhenPasswordless(credential)
                .ConfigureTracing(tracing => tracing
                    .ConfigureCommandFilter(_ => Activity.Current is not null)
                    .ConfigureBatchFilter(_ => Activity.Current is not null))))
        .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IReturnRequestRepository, ReturnRequestRepository>();
builder.Services.AddScoped<IApprovalApiClient, SimulatedApprovalApiClient>();
builder.Services.AddScoped<IReturnRequestCacheInvalidator, RedisReturnRequestCacheInvalidator>();
builder.Services.AddScoped<ProcessReturnRequestHandler>();

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    redisOptions.AbortOnConnectFail = false; // don't throw at startup if Redis isn't ready yet - keep retrying in the background
    // Without a connection, throw at once instead of queueing the command until the 5 s timeout.
    redisOptions.BacklogPolicy = BacklogPolicy.FailFast;
    return ConnectionMultiplexer.Connect(redisOptions);
});

builder.Services.Configure<SimulatedApprovalApiOptions>(
    builder.Configuration.GetSection(SimulatedApprovalApiOptions.SectionName));

// The client connects lazily and retries on its own, so there's no startup retry loop to hand-roll.
builder.Services.AddAzureClients(clients =>
{
    if (EntraAuthentication.IsServiceBusConnectionString(serviceBusConnectionString))
    {
        clients.AddServiceBusClient(serviceBusConnectionString);
    }
    else
    {
        clients.AddServiceBusClientWithNamespace(serviceBusConnectionString);
    }

    clients.UseCredential(credential);
});

builder.Services.AddSingleton<ReturnRequestConsumer>();
builder.Services.AddHostedService<Worker>();

// Singleton: one receiver link for the app's lifetime instead of a new one per probe.
builder.Services.AddSingleton(sp => new ServiceBusQueueHealthCheck(
    sp.GetRequiredService<ServiceBusClient>(),
    ReturnRequestConsumer.QueueName));

// Dependency checks run only on /health/ready, never on /health/live.
const string readyTag = "ready";
builder.Services.AddHealthChecks()
    .AddDbContextCheck<WorkerDbContext>("postgres", tags: [readyTag])
    .AddCheck<RedisHealthCheck>("redis", tags: [readyTag])
    .AddCheck<ServiceBusQueueHealthCheck>("servicebus", tags: [readyTag]);

// A hung dependency fails the probe instead of hanging it. Set here because AddDbContextCheck takes no timeout.
builder.Services.PostConfigure<HealthCheckServiceOptions>(options =>
{
    foreach (var registration in options.Registrations)
    {
        registration.Timeout = TimeSpan.FromSeconds(5);
    }
});

var app = builder.Build();

// No auth and no forwarded headers: the worker has no ingress, only the platform's probes reach it.
// The response is only "Healthy"/"Unhealthy"; failure details go to the log.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains(readyTag) });

app.Run();
