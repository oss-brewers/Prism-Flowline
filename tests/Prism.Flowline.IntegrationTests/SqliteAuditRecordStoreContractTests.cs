using Prism.Flowline.Abstractions;
using Prism.Flowline.Service.Infrastructure.Auditing;
using Prism.Flowline.Testing;
using Prism.Flowline.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Prism.Flowline.IntegrationTests;

/// <summary>
/// The audit-record contract against the host's durable audit-record store. It has no SQL Server
/// counterpart — the host keeps audit records in SQLite, with migrations, here in the Service — so
/// this is where the contract meets a database rather than in Prism.Flowline.StoreTests.
/// </summary>
public class SqliteAuditRecordStoreContractTests(SqliteAuditDatabase database) : IClassFixture<SqliteAuditDatabase>
{
    private readonly AuditRecordStoreContract _contract = new(_ =>
        ValueTask.FromResult<IAuditRecordStore>(new SqliteAuditRecordStore(database.Contexts)));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<AuditRecordStoreContract>();

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => _contract.Cases.Named(@case.Value).Run();
}

/// <summary>
/// One SQLite file for the class, migrated the way the host migrates it at startup. SQLite needs no
/// container, so these run everywhere.
/// </summary>
public sealed class SqliteAuditDatabase : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"prism-audit-contract-{Guid.NewGuid():N}.db");
    private ServiceProvider? _services;

    public IDbContextFactory<AuditRecordDbContext> Contexts { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _services = new ServiceCollection()
            .AddDbContextFactory<AuditRecordDbContext>(options => options.UseSqlite($"Data Source={_path};Pooling=False"))
            .BuildServiceProvider();

        Contexts = _services.GetRequiredService<IDbContextFactory<AuditRecordDbContext>>();
        await new AuditRecordDatabaseInitializer(Contexts).StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        File.Delete(_path);
    }
}
