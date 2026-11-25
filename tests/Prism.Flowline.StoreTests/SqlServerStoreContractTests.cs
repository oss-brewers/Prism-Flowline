using System.Text.Json;
using Prism.Flowline.Core;
using Prism.Flowline.Stores.SqlServer;
using Prism.Flowline.Testing;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Xunit;

namespace Prism.Flowline.StoreTests;

// Every store contract in Prism.Flowline.Testing that has a SQL Server implementation, run on a
// Testcontainers SQL Server. Written by following the package README's "Writing your provider's test
// class" and nothing else, which is the check that a third party could do the same.
//
// Not here: the audit-record store (the host's durable one is SQLite, in Prism.Flowline.Service)
// and the domain-event subscription store (no durable implementation exists yet). Both contracts
// run against the in-memory stores in Prism.Flowline.UnitTests.

[Collection(SqlServerCollection.Name)]
public class SqlServerInstanceStoreContractTests(SqlServerFixture database)
{
    private readonly InstanceStoreContract _contract = new(context =>
        ValueTask.FromResult<IInstanceStore>(new SqlServerInstanceStore(database.Contexts, context.Clock)));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<InstanceStoreContract>();

    [RequiresDockerTheory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => _contract.Cases.Named(@case.Value).Run();
}

[Collection(SqlServerCollection.Name)]
public class SqlServerEventStoreContractTests(SqlServerFixture database)
{
    private readonly EventStoreContract _contract = new(_ =>
        ValueTask.FromResult<IEventStore>(new SqlServerEventStore(database.Contexts)));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<EventStoreContract>();

    [RequiresDockerTheory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => _contract.Cases.Named(@case.Value).Run();
}

[Collection(SqlServerCollection.Name)]
public class SqlServerApprovalStoreContractTests(SqlServerFixture database)
{
    private readonly ApprovalStoreContract _contract = new(_ =>
        ValueTask.FromResult<IApprovalStore>(new SqlServerApprovalStore(database.Contexts)));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<ApprovalStoreContract>();

    [RequiresDockerTheory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => _contract.Cases.Named(@case.Value).Run();
}

[Collection(SqlServerCollection.Name)]
public class SqlServerGatePolicyStoreContractTests(SqlServerFixture database)
{
    private readonly GatePolicyStoreContract _contract = new(_ =>
        ValueTask.FromResult<IGatePolicyStore>(new SqlServerGatePolicyStore(database.Contexts)));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<GatePolicyStoreContract>();

    [RequiresDockerTheory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => _contract.Cases.Named(@case.Value).Run();
}

[Collection(SqlServerCollection.Name)]
public class SqlServerLogStoreContractTests(SqlServerFixture database)
{
    private readonly LogStoreContract _contract = new(_ =>
        ValueTask.FromResult<ILogStore>(new SqlServerLogStore(database.Contexts)));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<LogStoreContract>();

    [RequiresDockerTheory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => _contract.Cases.Named(@case.Value).Run();
}

[Collection(SqlServerCollection.Name)]
public class SqlServerAuditStoreContractTests(SqlServerFixture database)
{
    private readonly AuditStoreContract _contract = new(_ =>
        ValueTask.FromResult<IAuditStore>(new SqlServerAuditStore(database.Contexts)));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<AuditStoreContract>();

    [RequiresDockerTheory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => _contract.Cases.Named(@case.Value).Run();
}

[Collection(SqlServerCollection.Name)]
public class SqlServerBlobStoreContractTests(SqlServerFixture database)
{
    private readonly BlobStoreContract _contract = new(_ =>
        ValueTask.FromResult<IBlobStore>(new SqlServerBlobStore(database.Contexts)));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<BlobStoreContract>();

    [RequiresDockerTheory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => _contract.Cases.Named(@case.Value).Run();
}

[Collection(SqlServerCollection.Name)]
public class SqlServerCheckpointStoreContractTests(SqlServerFixture database)
{
    private readonly CheckpointStoreContract _contract = new(_ =>
        ValueTask.FromResult<ICheckpointStore<JsonElement>>(new SqlServerCheckpointStore(database.Contexts)));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<CheckpointStoreContract>();

    [RequiresDockerTheory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => _contract.Cases.Named(@case.Value).Run();
}
