using Prism.Flowline.Messaging.RabbitMQ;
using Prism.Flowline.Cache.Redis;
using Prism.Flowline.Api;
using Prism.Flowline.Service.ControlPlane;
using Prism.Flowline.Stores.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Prism.Flowline.Service;

public sealed class PrismServiceOptions
{
    /// <summary>When set, SQL Server replaces the framework's in-memory stores.</summary>
    public string? SqlServerConnectionString { get; set; }

    /// <summary>
    /// When set, Redis becomes the SSE backplane, so a subscriber can connect to any replica rather
    /// than only the one running the instance. Cache only — it carries no domain messages.
    /// </summary>
    public string? RedisConnectionString { get; set; }

    public bool EnsureDatabaseCreated { get; set; }

    public int RedisMaxStreamLength { get; set; } = 10_000;

    /// <summary>
    /// When set, RabbitMQ carries domain events between services. Unset leaves the framework's
    /// in-process broker in place, which is correct for a single-service deployment.
    /// </summary>
    public string? RabbitMqConnectionString { get; set; }
}

/// <summary>
/// Composition root for the host: turns on the Prism.Flowline framework, then substitutes the concrete
/// infrastructure this deployment uses.
/// </summary>
/// <remarks>
/// Everything reusable — API endpoints, SSE, dispatcher, approvals, control plane — comes from the
/// <c>Prism.Flowline</c> library, and the environment-specific implementations (SQL Server, Redis,
/// RabbitMQ) from their adapter projects. This project contributes the wiring that selects them.
/// With no connection string configured
/// the host runs entirely on the library's in-memory defaults, which is what keeps local dev and the
/// integration tests dependency-free.
/// </remarks>
public static class PrismServiceCollectionExtensions
{
    public static WorkflowHostBuilder AddPrism(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<PrismServiceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new PrismServiceOptions
        {
            SqlServerConnectionString = configuration["Prism:SqlServer:ConnectionString"]
                ?? configuration.GetConnectionString("Prism"),
            RedisConnectionString = configuration["Prism:Redis:ConnectionString"],
            EnsureDatabaseCreated = configuration.GetValue("Prism:SqlServer:EnsureDatabaseCreated", false),
            RedisMaxStreamLength = configuration.GetValue("Prism:Redis:MaxStreamLength", 10_000),
            RabbitMqConnectionString = configuration["Prism:RabbitMq:ConnectionString"]
        };
        configure?.Invoke(options);

        // 1. The framework, with its in-memory defaults.
        WorkflowHostBuilder host = services
            .AddWorkflowHost(configuration)
            .AddBuiltInMiddleware()
            .AddBackgroundServices();

        // The control plane is part of this deployable, not the framework.
        services.AddControlPlane();

        // 2. Concrete infrastructure, when configured, displacing those defaults.
        if (!string.IsNullOrWhiteSpace(options.SqlServerConnectionString))
        {
            services.AddSqlServerStores(options.SqlServerConnectionString, options.EnsureDatabaseCreated);
        }

        // Cache: the SSE backplane, so a subscriber can reach any replica. Unset leaves the
        // framework's in-memory bus, which is correct for a single replica.
        if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            services.AddRedisCache(options.RedisConnectionString, options.RedisMaxStreamLength);
        }

        // Messaging: domain events between services. Unset leaves the in-process broker, which is
        // correct for a single service. The two are independent — a deployment may want either,
        // both, or neither.
        if (!string.IsNullOrWhiteSpace(options.RabbitMqConnectionString))
        {
            services.AddRabbitMqMessaging(options.RabbitMqConnectionString);
        }

        return host;
    }
}
