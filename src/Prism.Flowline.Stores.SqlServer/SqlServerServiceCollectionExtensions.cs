using System.Text.Json;
using Prism.Flowline.Api;
using Prism.Flowline.Core;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Prism.Flowline.Stores.SqlServer;

/// <summary>
/// Registers the SQL Server implementations of the framework's store contracts.
/// </summary>
/// <remarks>
/// The framework library (<c>Prism.Flowline</c>) ships in-memory defaults so it runs standalone for
/// local development and tests. Production storage is infrastructure-specific, so it lives in an
/// adapter beside the framework rather than inside it, and a host swaps it in at startup.
/// </remarks>
public static class SqlServerServiceCollectionExtensions
{
    public static IServiceCollection AddSqlServerStores(
        this IServiceCollection services,
        string connectionString,
        bool ensureDatabaseCreated = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Microsoft.Data.SqlClient will not open a connection in invariant globalization mode, and
        // says so only when the first query runs — in a host, on the first request that touches an
        // instance. Saying it here turns that into a failure at startup that names the fix.
        if (AppContext.TryGetSwitch("System.Globalization.Invariant", out bool invariant) && invariant)
        {
            throw new InvalidOperationException(
                "The SQL Server stores need culture data, and this process runs in invariant globalization mode, " +
                "which Microsoft.Data.SqlClient does not support. Set <InvariantGlobalization>false</InvariantGlobalization> " +
                "in the host's project file.");
        }

        services.AddDbContextFactory<PrismDbContext>(builder => builder.UseSqlServer(connectionString));

        // Displace the library's in-memory defaults. RemoveAll before Add so a re-registration is
        // idempotent and the last caller wins deterministically.
        Replace<IInstanceStore, SqlServerInstanceStore>(services);
        Replace<IEventStore, SqlServerEventStore>(services);
        Replace<ILogStore, SqlServerLogStore>(services);
        Replace<IApprovalStore, SqlServerApprovalStore>(services);
        Replace<IGatePolicyStore, SqlServerGatePolicyStore>(services);
        Replace<IAuditStore, SqlServerAuditStore>(services);
        Replace<IBlobStore, SqlServerBlobStore>(services);
        Replace<ICheckpointStore<JsonElement>, SqlServerCheckpointStore>(services);
        Replace<ICheckpointDescriber, SqlServerCheckpointDescriber>(services);

        if (ensureDatabaseCreated)
        {
            services.AddHostedService<PrismDatabaseInitializer>();
        }

        return services;
    }

    private static void Replace<TService, TImplementation>(IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        services.RemoveAll<TService>();
        services.AddSingleton<TService, TImplementation>();
    }
}

/// <summary>Creates the schema on startup. Intended for dev and test environments only.</summary>
public sealed class PrismDatabaseInitializer(IDbContextFactory<PrismDbContext> factory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
