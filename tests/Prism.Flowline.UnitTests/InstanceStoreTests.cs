using Prism.Flowline.Abstractions;
using Prism.Flowline.Persistence;
using FluentAssertions;
using Xunit;

namespace Prism.Flowline.UnitTests;

/// <summary>
/// What the in-memory instance store does beyond the contract.
/// </summary>
/// <remarks>
/// The rules every instance store keeps — creation, idempotency, transitions, terminal finality,
/// claims, leases and queries — live in <c>Prism.Flowline.Testing.InstanceStoreContract</c> and run
/// against this store in <see cref="Contracts.InMemoryInstanceStoreContractTests"/>.
/// </remarks>
public class InstanceStoreTests
{
    private readonly InMemoryInstanceStore _store = new();

    [Fact]
    public async Task Create_and_query_reject_nulls()
    {
        await _store.Invoking(s => s.CreateAsync(null!, default).AsTask()).Should().ThrowAsync<ArgumentNullException>();
        await _store.Invoking(s => s.QueryAsync(null!, default).AsTask()).Should().ThrowAsync<ArgumentNullException>();
        await _store.Invoking(s => s.UpdateAsync("i1", null!, default).AsTask()).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task A_duplicate_idempotency_key_is_refused_with_an_invalid_operation()
    {
        await _store.CreateAsync(TestFactory.CreateRequest("a", idempotencyKey: "key-1"), default);

        await _store.Invoking(s => s.CreateAsync(TestFactory.CreateRequest("b", idempotencyKey: "key-1"), default).AsTask())
            .Should().ThrowAsync<InvalidOperationException>("the contract leaves the type to the store; this one says what it refused");

        (await _store.GetAsync("b", default)).Should().BeNull();
    }

    [Fact]
    public async Task A_refused_duplicate_id_releases_the_idempotency_key_it_reserved()
    {
        await _store.CreateAsync(TestFactory.CreateRequest("a"), default);

        // The second create reserves key-2, then fails on the id. The key must be free again, or
        // the next start with it would be refused for an instance that was never created.
        await _store.Invoking(s => s.CreateAsync(TestFactory.CreateRequest("a", idempotencyKey: "key-2"), default).AsTask())
            .Should().ThrowAsync<InvalidOperationException>();

        await _store.CreateAsync(TestFactory.CreateRequest("c", idempotencyKey: "key-2"), default);
        (await _store.FindByIdempotencyKeyAsync("t1", "key-2", default))!.InstanceId.Should().Be("c");
    }
}

public class InstanceStatusTests
{
    [Theory]
    [InlineData(InstanceStatus.Completed, true)]
    [InlineData(InstanceStatus.Failed, true)]
    [InlineData(InstanceStatus.DeadStopped, true)]
    [InlineData(InstanceStatus.Cancelled, true)]
    [InlineData(InstanceStatus.Running, false)]
    [InlineData(InstanceStatus.Pending, false)]
    [InlineData(InstanceStatus.AwaitingApproval, false)]
    [InlineData(InstanceStatus.AwaitingInput, false)]
    [InlineData(InstanceStatus.Suspended, false)]
    [InlineData(InstanceStatus.RetryScheduled, false)]
    [InlineData(InstanceStatus.Dispatchable, false)]
    public void Terminal_classification_is_exact(InstanceStatus status, bool expected)
        => status.IsTerminal().Should().Be(expected);

    [Theory]
    [InlineData(InstanceStatus.Pending, true)]
    [InlineData(InstanceStatus.RetryScheduled, true)]
    [InlineData(InstanceStatus.Dispatchable, true)]
    [InlineData(InstanceStatus.Running, false)]
    [InlineData(InstanceStatus.Completed, false)]
    public void Claimable_classification_is_exact(InstanceStatus status, bool expected)
        => status.IsClaimable().Should().Be(expected);

    [Fact]
    public void AwaitingApproval_is_distinct_from_AwaitingInput()
    {
        // Conflating them would make the approval policy un-targetable.
        InstanceStatus.AwaitingApproval.Should().NotBe(InstanceStatus.AwaitingInput);
        InstanceStatus.AwaitingApproval.IsTerminal().Should().BeFalse();
    }
}
