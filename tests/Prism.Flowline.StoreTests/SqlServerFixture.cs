using Prism.Flowline.Stores.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Xunit;

namespace Prism.Flowline.StoreTests;

/// <summary>
/// Owns one SQL Server container and one database for the whole assembly.
/// </summary>
/// <remarks>
/// <para>
/// Every contract shares the database, the way a deployment's stores share theirs. The contract
/// cases scope what they write by tenant and id and retire what an instance claim or an expiry
/// sweep could still find, so they do not see each other's rows — which is what the package README
/// promises a provider, and this suite is where that promise is kept.
/// </para>
/// <para>
/// The schema is made by the host's own <see cref="PrismDatabaseInitializer"/>, over the
/// <see cref="IDbContextFactory{TContext}"/> that <see cref="SqlServerServiceCollectionExtensions.AddSqlServerStores"/>
/// registers, so the suite runs the stores on exactly the model a host does.
/// </para>
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    /// <summary>Kept in step with the image ci.yml and the release workflows pre-pull.</summary>
    public const string Image = "mcr.microsoft.com/mssql/server:2022-latest";

    private MsSqlContainer? _container;
    private ServiceProvider? _services;

    public IDbContextFactory<PrismDbContext> Contexts { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!DockerProbe.ShouldStart)
        {
            return;
        }

        _container = new MsSqlBuilder(Image).Build();
        await _container.StartAsync();

        // A database of its own rather than master, so EnsureCreated has something to create.
        var connection = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "PrismStoreContract"
        };

        _services = new ServiceCollection()
            .AddSqlServerStores(connection.ConnectionString)
            .BuildServiceProvider();

        Contexts = _services.GetRequiredService<IDbContextFactory<PrismDbContext>>();
        await new PrismDatabaseInitializer(Contexts).StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>
/// One collection, so the contracts run one class at a time against the shared database: a
/// contract's cases are not written to run concurrently with each other over one store.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}
