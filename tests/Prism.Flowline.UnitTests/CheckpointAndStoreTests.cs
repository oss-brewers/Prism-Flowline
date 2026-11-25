using System.Text.Json;
using Prism.Flowline.Persistence;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Prism.Flowline.UnitTests;

// What the in-memory checkpoint, approval and supporting stores do beyond their contracts. The
// rules every implementation keeps — index order, parent filtering, one vote per decider, scope
// resolution and the rest — live in Prism.Flowline.Testing and run against these stores in
// Contracts/InMemoryStoreContractTests.

public class CheckpointStoreTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-07-30T10:00:00Z"));

    private static JsonElement Payload(string value = "state")
        => JsonDocument.Parse($$"""{"value":"{{value}}"}""").RootElement.Clone();

    [Fact]
    public async Task Large_payloads_are_offloaded_to_the_blob_store()
    {
        var blobs = new InMemoryBlobStore();
        var store = new OverflowCheckpointStore(blobs, inlineThresholdBytes: 128, clock: _clock);

        JsonElement large = JsonDocument.Parse($$"""{"value":"{{new string('x', 5000)}}"}""").RootElement.Clone();
        CheckpointInfo info = await store.CreateCheckpointAsync("s1", large);

        blobs.Count.Should().Be(1);
        store.Describe("s1").Single().BlobUri.Should().NotBeNull();
        store.Describe("s1").Single().Payload.Should().BeNull();

        JsonElement restored = await store.RetrieveCheckpointAsync("s1", info);
        restored.GetProperty("value").GetString().Should().HaveLength(5000);
    }

    [Fact]
    public async Task Small_payloads_stay_inline()
    {
        var blobs = new InMemoryBlobStore();
        var store = new OverflowCheckpointStore(blobs, inlineThresholdBytes: 100_000, clock: _clock);

        await store.CreateCheckpointAsync("s1", Payload());

        blobs.Count.Should().Be(0);
        store.Describe("s1").Single().Payload.Should().NotBeNull();
    }

    [Fact]
    public async Task Empty_session_ids_are_rejected()
    {
        var store = new OverflowCheckpointStore();
        Func<Task> act = async () => await store.CreateCheckpointAsync("", Payload());
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Describe_reports_size_and_commit_time_in_order()
    {
        var store = new OverflowCheckpointStore(clock: _clock);
        await store.CreateCheckpointAsync("s1", Payload("a"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await store.CreateCheckpointAsync("s1", Payload("bb"));

        IReadOnlyList<CheckpointRecord> described = store.Describe("s1");

        described.Should().HaveCount(2);
        described[0].CommittedAt.Should().BeBefore(described[1].CommittedAt);
        described.Should().OnlyContain(c => c.SizeBytes > 0);
    }

    [Fact]
    public async Task Prune_removes_only_checkpoints_older_than_the_cutoff()
    {
        var store = new OverflowCheckpointStore(clock: _clock);
        await store.CreateCheckpointAsync("s1", Payload("old"));

        _clock.Advance(TimeSpan.FromDays(10));
        DateTimeOffset cutoff = _clock.GetUtcNow();
        await store.CreateCheckpointAsync("s1", Payload("new"));

        int removed = store.Prune(cutoff);

        removed.Should().Be(1);
        store.Describe("s1").Should().ContainSingle();
    }

    [Fact]
    public async Task Pruning_every_checkpoint_drops_the_session()
    {
        var store = new OverflowCheckpointStore(clock: _clock);
        await store.CreateCheckpointAsync("s1", Payload());

        _clock.Advance(TimeSpan.FromDays(30));
        store.Prune(_clock.GetUtcNow());

        store.SessionCount.Should().Be(0);
    }
}

public class ApprovalStoreTests
{
    [Fact]
    public async Task Create_rejects_null()
        => await new InMemoryApprovalStore().Invoking(s => s.CreateAsync(null!, default).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
}

public class SupportingStoreTests
{
    [Fact]
    public async Task Log_store_rejects_null_entries()
    {
        var store = new InMemoryLogStore();
        await store.Invoking(s => s.AppendAsync(null!, default).AsTask()).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Blob_store_count_drops_when_a_blob_is_deleted()
    {
        var store = new InMemoryBlobStore();

        string uri = await store.UploadAsync("k1", [1, 2, 3, 4], default);
        store.Count.Should().Be(1);

        await store.DeleteAsync(uri, default);
        store.Count.Should().Be(0);
    }

    [Fact]
    public async Task Audit_store_rejects_null()
    {
        var store = new InMemoryAuditStore();
        await store.Invoking(s => s.WriteAsync(null!, default).AsTask()).Should().ThrowAsync<ArgumentNullException>();
    }
}
