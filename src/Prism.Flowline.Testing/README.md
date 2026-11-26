# Prism.Flowline.Testing

The rules every [Prism.Flowline](https://github.com/oss-brewers/Prism-Flowline) store must keep,
written once and runnable against any implementation.

The store interfaces state their semantics in remarks — optimistic transitions, terminal states
that stay terminal, an atomic claim, gap-free event sequences, one winning delivery. This package
turns each of those sentences into a case you can run against your own store. If your store passes
every case of a contract, the runtime cannot tell it from the in-memory one.

The package depends on `OssBrewers.Prism.Flowline` and nothing else: no test framework, no assertion library.
Each case is a plain method returning a `Task`, so it runs under xunit 2, xunit 3 on
Microsoft.Testing.Platform, NUnit, MSTest, or a console loop.

## The contracts

| Store interface | Contract | Notes |
| --- | --- | --- |
| `IInstanceStore` | `InstanceStoreContract` | Build over `context.Clock`. Contention cases for claims, transitions and idempotency keys |
| `IEventStore` | `EventStoreContract` | Concurrent appends of distinct sequences must all land, in order |
| `IApprovalStore` | `ApprovalStoreContract` | Concurrent decisions: one closes it; concurrent votes: none is lost |
| `IGatePolicyStore` | `GatePolicyStoreContract` | Resolution order instance → tenant → host-wide (`null` tenant) |
| `ILogStore` | `LogStoreContract` | Entries share sequences; read oldest first |
| `IAuditStore` | `AuditStoreContract` | Newest first; filter before the limit |
| `IBlobStore` | `BlobStoreContract` | A missing blob is a `KeyNotFoundException` |
| `IAuditRecordStore` | `AuditRecordStoreContract` | One entry per (instance, section kind, key) |
| `IDomainEventSubscriptionStore` | `DomainEventSubscriptionStoreContract` | `TryDeliverAsync` is a compare-and-set |
| `ICheckpointStore<JsonElement>` | `CheckpointStoreContract` | Index oldest first, total order under timestamp collision; a missing checkpoint is a `KeyNotFoundException` |
| `ICacheStore` | `CacheStoreContract` | Expiry cases wait in real time: a server keeps its own clock |

Every contract has the same shape:

```csharp
public sealed class InstanceStoreContract(Func<StoreContractContext, ValueTask<IInstanceStore>> create)
{
    public Task Created_instances_start_pending();
    public Task Claim_hands_each_instance_to_one_replica_under_contention();
    // … one public method per case

    public IReadOnlyList<ContractCase> Cases { get; }   // every case, by name, ready to run
}

public sealed record ContractCase(string Name, Func<Task> Run);
```

## Writing your provider's test class

Four steps. The repository's own SQL Server suite, `tests/Prism.Flowline.StoreTests`, was written by
following them and is a complete worked example.

### 1. Reference the package from your test project

```xml
<PackageReference Include="OssBrewers.Prism.Flowline.Testing" Version="1.0.0" />
```

Your test project keeps its own test framework and its own fixtures (a Testcontainers database, a
connection string from the environment). The package brings neither.

### 2. Write the factory

A contract takes a factory, `Func<StoreContractContext, ValueTask<TStore>>`, and calls it **once per
case** with a fresh context. Build your store inside it.

```csharp
var contract = new InstanceStoreContract(context =>
    ValueTask.FromResult<IInstanceStore>(new PostgresInstanceStore(dataSource, context.Clock)));
```

The context gives the factory three things:

- **`context.Clock`** — a `TimeProvider` that stands still until the case moves it. If your store
  reads the time (`CreatedAt`, `UpdatedAt`, a renewed lease's expiry), build it over this clock.
  Cases about leases, retries and expiry advance it instead of sleeping, and compare what your store
  wrote against it, so a store that reads `DateTimeOffset.UtcNow` directly fails them.
- **Names nobody else uses** — `context.Tenant`, `context.NewTenant()` and `context.NewId(prefix)`.
  The cases take every tenant, id, workflow name and topic from these, which is what lets your
  whole suite **share one database**: no case reads another's rows. Where a store operation reads
  the whole store regardless — an instance claim, an approval or wait expiry sweep — the case
  retires what it created when it ends (cancels the instances and approvals, removes the waits), so
  a later case finds nothing of it left to claim.
- **`context.OnDispose(Func<ValueTask>)`** — teardown that runs when the case ends, pass or fail.
  Use it if your factory builds something per case (a schema, a connection) that should go away.

Sharing one database is the expected arrangement. Two things to keep: run **one contract's cases
one at a time** against a shared database (most frameworks do this within a test class by default),
and create the schema once, before the first case, in your own fixture.

### 3. Enumerate the cases

Give each case its own test so a failure is reported by name. Case names are stable, and
`ContractCase.NamesOf<TContract>()` lists them without building a store — useful where the
framework wants its data from a static member.

**xunit 2 or xunit 3:**

```csharp
using Prism.Flowline.Core;
using Prism.Flowline.Testing;
using Xunit;

public sealed class PostgresInstanceStoreContractTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private readonly InstanceStoreContract _contract = new(context =>
        ValueTask.FromResult<IInstanceStore>(new PostgresInstanceStore(database.DataSource, context.Clock)));

    public static TheoryData<string> Cases => [.. ContractCase.NamesOf<InstanceStoreContract>()];

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(string @case) => _contract.Cases.Named(@case).Run();
}
```

Each row is its own test, named for its case. One wrinkle: xunit cuts a string argument to 50
characters when it names a row, and most case names are longer, so rows read
`Keeps_the_contract(case: "Claim_hands_each_instance_to_one_replica_unde"···)`. If that matters to
you, pass the name in a small type that overrides `ToString` and implements `IXunitSerializable`
(in `Xunit.Abstractions` on xunit 2, `Xunit.Sdk` on xunit 3), which xunit shows whole:

```csharp
public sealed class CaseName : IXunitSerializable
{
    [Obsolete("For the xunit serializer only.")] public CaseName() { }
    private CaseName(string value) => Value = value;

    public string Value { get; private set; } = "";

    public static TheoryData<CaseName> Of<TContract>() where TContract : class
        => [.. ContractCase.NamesOf<TContract>().Select(name => new CaseName(name))];

    public void Deserialize(IXunitSerializationInfo info) => Value = info.GetValue<string>(nameof(Value));
    public void Serialize(IXunitSerializationInfo info) => info.AddValue(nameof(Value), Value);
    public override string ToString() => Value;
}

// …then, in the test class:
public static TheoryData<CaseName> Cases => CaseName.Of<InstanceStoreContract>();

[Theory]
[MemberData(nameof(Cases))]
public Task Keeps_the_contract(CaseName @case) => _contract.Cases.Named(@case.Value).Run();
```

The row then reads `Keeps_the_contract(case: Claim_hands_each_instance_to_one_replica_under_contention)`.

**NUnit:** `[TestCaseSource(nameof(Cases))]` over `ContractCase.NamesOf<InstanceStoreContract>()`.
**MSTest:** `[DynamicData]` the same way. **Anything else:** `foreach (ContractCase c in contract.Cases) await c.Run();`.

Prefer one method per case instead of a theory? Every case is also a public method:
`[Fact] public Task Claims() => _contract.Claim_hands_each_instance_to_one_replica_under_contention();`.

### 4. Decide how your suite skips

The package has no opinion. If your store needs a database that a developer machine may not have,
skip the class there with your framework's skip mechanism — and make CI refuse to skip (this
repository's suites read `PRISM_REQUIRE_DOCKER`), or a runner that lost its database publishes a green
build that tested nothing.

## Reading a failure

A case that finds a broken rule throws `StoreContractException`. Its message names the case, the
rule in words, and what was seen instead:

```
Concurrent_transitions_from_one_state_let_exactly_one_win: exactly one of several transitions from
the same state wins. Expected 1, got 3
```

A store that throws where a case expected an answer fails the same way, with the store's exception
as the inner exception: `…: the store threw DbUpdateConcurrencyException where the case expected an
answer`. Exceptions from your factory are not wrapped — they are yours, not a broken rule.

## What the cases allow

- **JSON is compared as JSON.** A store may normalise whitespace, key order and number spelling (a
  `jsonb` column does all three) but not values. Plain text — reasons, messages, comments — is
  compared exactly, and the payload cases fill it with non-ASCII text from several scripts and both
  Unicode planes.
- **The exception type for a refused duplicate is yours.** Creating an instance or approval whose
  id (or idempotency key) already exists must throw; what it throws is up to the store. Missing
  blobs and checkpoints are the exception: those throw `KeyNotFoundException`.
- **Argument validation is yours too.** No case passes `null` where the interface does not allow it.
