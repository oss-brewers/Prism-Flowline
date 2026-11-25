using System.Text.Json;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Core;
using Prism.Flowline.Notifications;
using Prism.Flowline.Persistence;
using Prism.Flowline.Testing;
using Prism.Flowline.TestSupport;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Xunit;

namespace Prism.Flowline.UnitTests.Contracts;

// Every store contract in Prism.Flowline.Testing, run against the in-memory implementations the
// library ships. This is the suite that proves each contract is satisfiable before any database is
// involved, and the one an adapter author can compare against while developing.
//
// One theory per contract, one row per case: a failure is reported by the case's name, and the
// factory builds a fresh store for every row. The tests that state what the in-memory classes do
// beyond the contract stay beside those classes, in InstanceStoreTests, CheckpointAndStoreTests and
// the rest.

public class InMemoryInstanceStoreContractTests
{
    private static readonly InstanceStoreContract Contract = new(context =>
        ValueTask.FromResult<IInstanceStore>(new InMemoryInstanceStore(context.Clock)));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<InstanceStoreContract>();

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => Contract.Cases.Named(@case.Value).Run();
}

public class InMemoryEventStoreContractTests
{
    private static readonly EventStoreContract Contract = new(_ =>
        ValueTask.FromResult<IEventStore>(new InMemoryEventStore()));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<EventStoreContract>();

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => Contract.Cases.Named(@case.Value).Run();
}

public class InMemoryApprovalStoreContractTests
{
    private static readonly ApprovalStoreContract Contract = new(_ =>
        ValueTask.FromResult<IApprovalStore>(new InMemoryApprovalStore()));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<ApprovalStoreContract>();

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => Contract.Cases.Named(@case.Value).Run();
}

public class InMemoryGatePolicyStoreContractTests
{
    private static readonly GatePolicyStoreContract Contract = new(_ =>
        ValueTask.FromResult<IGatePolicyStore>(new InMemoryGatePolicyStore()));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<GatePolicyStoreContract>();

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => Contract.Cases.Named(@case.Value).Run();
}

public class InMemoryLogStoreContractTests
{
    private static readonly LogStoreContract Contract = new(_ =>
        ValueTask.FromResult<ILogStore>(new InMemoryLogStore()));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<LogStoreContract>();

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => Contract.Cases.Named(@case.Value).Run();
}

public class InMemoryAuditStoreContractTests
{
    private static readonly AuditStoreContract Contract = new(_ =>
        ValueTask.FromResult<IAuditStore>(new InMemoryAuditStore()));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<AuditStoreContract>();

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => Contract.Cases.Named(@case.Value).Run();
}

public class InMemoryBlobStoreContractTests
{
    private static readonly BlobStoreContract Contract = new(_ =>
        ValueTask.FromResult<IBlobStore>(new InMemoryBlobStore()));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<BlobStoreContract>();

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => Contract.Cases.Named(@case.Value).Run();
}

public class InMemoryAuditRecordStoreContractTests
{
    private static readonly AuditRecordStoreContract Contract = new(_ =>
        ValueTask.FromResult<IAuditRecordStore>(new InMemoryAuditRecordStore()));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<AuditRecordStoreContract>();

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => Contract.Cases.Named(@case.Value).Run();
}

public class InMemoryDomainEventSubscriptionStoreContractTests
{
    private static readonly DomainEventSubscriptionStoreContract Contract = new(_ =>
        ValueTask.FromResult<IDomainEventSubscriptionStore>(new InMemoryDomainEventSubscriptionStore()));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<DomainEventSubscriptionStoreContract>();

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => Contract.Cases.Named(@case.Value).Run();
}

/// <summary>
/// Built with a blob store and the default inline threshold, as the host wires it, so the large
/// payload case goes through the overflow path and the small ones stay inline.
/// </summary>
public class InMemoryCheckpointStoreContractTests
{
    private static readonly CheckpointStoreContract Contract = new(context =>
        ValueTask.FromResult<ICheckpointStore<JsonElement>>(
            new OverflowCheckpointStore(new InMemoryBlobStore(), clock: context.Clock)));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<CheckpointStoreContract>();

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => Contract.Cases.Named(@case.Value).Run();
}

public class InMemoryCacheStoreContractTests
{
    private static readonly CacheStoreContract Contract = new(_ =>
        ValueTask.FromResult<ICacheStore>(new InMemoryCacheStore()));

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<CacheStoreContract>();

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => Contract.Cases.Named(@case.Value).Run();
}

/// <summary>The package's own promises about the contracts, independent of any store.</summary>
public class StoreContractPackageTests
{
    [Fact]
    public void Every_contract_lists_its_cases_by_unique_name()
    {
        IReadOnlyList<string>[] contracts =
        [
            ContractCase.NamesOf<InstanceStoreContract>(), ContractCase.NamesOf<EventStoreContract>(),
            ContractCase.NamesOf<ApprovalStoreContract>(), ContractCase.NamesOf<GatePolicyStoreContract>(),
            ContractCase.NamesOf<LogStoreContract>(), ContractCase.NamesOf<AuditStoreContract>(),
            ContractCase.NamesOf<BlobStoreContract>(), ContractCase.NamesOf<AuditRecordStoreContract>(),
            ContractCase.NamesOf<DomainEventSubscriptionStoreContract>(), ContractCase.NamesOf<CheckpointStoreContract>(),
            ContractCase.NamesOf<CacheStoreContract>()
        ];

        foreach (IReadOnlyList<string> names in contracts)
        {
            Assert.NotEmpty(names);
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void Case_names_match_the_cases_an_instance_runs()
    {
        var contract = new InstanceStoreContract(_ => throw new InvalidOperationException("never built"));

        Assert.Equal(ContractCase.NamesOf<InstanceStoreContract>(), contract.Cases.Select(c => c.Name));
    }

    [Fact]
    public async Task A_broken_rule_names_the_case_and_the_rule()
    {
        // A store that stamps instances with its own idea of the time and forgets them at once: the
        // first rule it meets is the one reported.
        var contract = new InstanceStoreContract(_ => ValueTask.FromResult<IInstanceStore>(new ForgetfulInstanceStore()));

        StoreContractException failure = await Assert.ThrowsAsync<StoreContractException>(
            () => contract.Created_instances_start_pending());

        Assert.Equal(nameof(InstanceStoreContract.Created_instances_start_pending), failure.CaseName);
        Assert.Equal("CreatedAt comes from the store's clock", failure.Rule);
        Assert.StartsWith("Created_instances_start_pending: CreatedAt comes from the store's clock. Expected ", failure.Message);
    }

    [Fact]
    public async Task A_store_that_throws_fails_the_case_rather_than_escaping_it()
    {
        var contract = new BlobStoreContract(_ => ValueTask.FromResult<IBlobStore>(new ThrowingBlobStore()));

        StoreContractException failure = await Assert.ThrowsAsync<StoreContractException>(() => contract.A_blob_round_trips());

        Assert.IsType<TimeoutException>(failure.InnerException);
        Assert.Contains("threw TimeoutException", failure.Message);
    }

    [Fact]
    public async Task A_factory_failure_is_reported_as_itself()
    {
        var contract = new BlobStoreContract(_ => throw new InvalidOperationException("no database"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => contract.A_blob_round_trips());
    }

    [Fact]
    public void Case_names_are_resolved_exactly()
    {
        var contract = new BlobStoreContract(_ => ValueTask.FromResult<IBlobStore>(new InMemoryBlobStore()));

        Assert.Equal(nameof(BlobStoreContract.A_blob_round_trips), contract.Cases.Named("A_blob_round_trips").Name);
        Assert.Throws<ArgumentException>(() => contract.Cases.Named("a_blob_round_trips"));
    }

    private sealed class ForgetfulInstanceStore : IInstanceStore
    {
        public ValueTask<WorkflowInstance> CreateAsync(CreateInstanceRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(new WorkflowInstance
            {
                InstanceId = request.InstanceId,
                TenantId = request.TenantId,
                WorkflowName = request.WorkflowName,
                WorkflowVersion = request.WorkflowVersion,
                Status = InstanceStatus.Pending,
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
                Version = 1
            });

        public ValueTask<WorkflowInstance?> GetAsync(string instanceId, CancellationToken cancellationToken) => new((WorkflowInstance?)null);
        public ValueTask<WorkflowInstance?> FindByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken cancellationToken) => new((WorkflowInstance?)null);
        public ValueTask<Page<WorkflowInstance>> QueryAsync(InstanceQuery query, CancellationToken cancellationToken) => new(new Page<WorkflowInstance>([], 0));
        public ValueTask<bool> TryTransitionAsync(string instanceId, InstanceStatus from, InstanceStatus to, Action<InstanceMutation>? mutate, CancellationToken cancellationToken) => new(false);
        public ValueTask<bool> UpdateAsync(string instanceId, Action<InstanceMutation> mutate, CancellationToken cancellationToken) => new(false);
        public ValueTask<IReadOnlyList<WorkflowInstance>> ClaimAsync(string replicaId, int max, TimeSpan leaseDuration, DateTimeOffset now, CancellationToken cancellationToken) => new((IReadOnlyList<WorkflowInstance>)[]);
        public ValueTask<bool> RenewLeaseAsync(string instanceId, string replicaId, TimeSpan leaseDuration, CancellationToken cancellationToken) => new(false);
        public ValueTask ReleaseLeaseAsync(string instanceId, string replicaId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class ThrowingBlobStore : IBlobStore
    {
        public ValueTask<string> UploadAsync(string key, byte[] content, CancellationToken cancellationToken) => throw new TimeoutException("the wire went quiet");
        public ValueTask<byte[]> DownloadAsync(string uri, CancellationToken cancellationToken) => throw new TimeoutException("the wire went quiet");
        public ValueTask DeleteAsync(string uri, CancellationToken cancellationToken) => throw new TimeoutException("the wire went quiet");
    }
}
