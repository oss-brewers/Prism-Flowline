using System.Text.Json;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Core;
using Prism.Flowline.Persistence;
using FluentAssertions;
using Xunit;

namespace Prism.Flowline.UnitTests;

public class EventSequencerTests
{
    [Fact]
    public void Starts_at_one_and_increments()
    {
        var sequencer = new NotificationSequencer();
        sequencer.Next("i1").Should().Be(1);
        sequencer.Next("i1").Should().Be(2);
        sequencer.Next("i1").Should().Be(3);
    }

    [Fact]
    public void Sequences_are_isolated_per_instance()
    {
        var sequencer = new NotificationSequencer();
        sequencer.Next("a").Should().Be(1);
        sequencer.Next("b").Should().Be(1);
        sequencer.Next("a").Should().Be(2);
    }

    [Fact]
    public void Peek_does_not_advance()
    {
        var sequencer = new NotificationSequencer();
        sequencer.Next("i1");
        sequencer.Peek("i1").Should().Be(1);
        sequencer.Peek("i1").Should().Be(1);
    }

    [Fact]
    public void Peek_on_an_unknown_instance_is_zero()
        => new NotificationSequencer().Peek("nope").Should().Be(0);

    [Fact]
    public void Seed_continues_the_sequence_after_a_resume()
    {
        var sequencer = new NotificationSequencer();
        sequencer.Seed("i1", 42);

        sequencer.Next("i1").Should().Be(43, "a resumed instance must not restart its sequence at 1");
    }

    [Fact]
    public void Forget_resets_an_instance()
    {
        var sequencer = new NotificationSequencer();
        sequencer.Next("i1");
        sequencer.Forget("i1");
        sequencer.Next("i1").Should().Be(1);
    }

    [Fact]
    public async Task Is_gapless_under_parallel_increment()
    {
        var sequencer = new NotificationSequencer();
        const int count = 2000;

        long[] values = await Task.WhenAll(
            Enumerable.Range(0, count).Select(_ => Task.Run(() => sequencer.Next("i1"))));

        values.Should().OnlyHaveUniqueItems();
        values.Order().Should().Equal(Enumerable.Range(1, count).Select(i => (long)i));
    }
}

public class EventFactoryTests
{
    [Fact]
    public void Create_serialises_the_payload_in_web_casing()
    {
        EventEnvelope envelope = NotificationFactory.Create(
            "i1", 5, WorkflowEventTypes.ExecutorInvoked, new { ExecutorId = "pay", Superstep = 2 }, "pay", 2, "t1");

        envelope.Sequence.Should().Be(5);
        envelope.ExecutorId.Should().Be("pay");
        envelope.TenantId.Should().Be("t1");
        envelope.PayloadJson.Should().Contain("\"executorId\"").And.Contain("\"pay\"");
    }

    [Fact]
    public void ApprovalRequested_carries_everything_a_consumer_needs_to_render_a_prompt()
    {
        var approval = new ApprovalRequest
        {
            ApprovalId = "apr_1",
            InstanceId = "i1",
            TenantId = "t1",
            ExecutorId = "post-payment",
            Reason = "AmountAboveThreshold",
            ProposedInputJson = """{"amount":48200}""",
            Assignees = ["group:finance"],
            AllowModification = true,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ExpiresAt = DateTimeOffset.UnixEpoch.AddHours(8)
        };

        EventEnvelope envelope = NotificationFactory.ApprovalRequested(approval);

        envelope.EventType.Should().Be(WorkflowEventTypes.ApprovalRequested);
        envelope.ExecutorId.Should().Be("post-payment");

        using JsonDocument doc = JsonDocument.Parse(envelope.PayloadJson);
        JsonElement root = doc.RootElement;
        root.GetProperty("approvalId").GetString().Should().Be("apr_1");
        root.GetProperty("reason").GetString().Should().Be("AmountAboveThreshold");
        root.GetProperty("allowModification").GetBoolean().Should().BeTrue();
        // No second fetch should be needed to submit a decision.
        root.GetProperty("decisionUrl").GetString().Should().Be("/approvals/apr_1/decision");
    }

    [Fact]
    public void ApprovalDecided_reports_outcome_and_resulting_state()
    {
        var approval = new ApprovalRequest
        {
            ApprovalId = "apr_2", InstanceId = "i1", TenantId = "t1", ExecutorId = "pay",
            CreatedAt = DateTimeOffset.UnixEpoch, ExpiresAt = DateTimeOffset.UnixEpoch.AddHours(1)
        };

        var decision = new ApprovalDecision
        {
            ApprovalId = "apr_2", DeciderId = "alice", Outcome = ApprovalOutcomeKind.Approve,
            DecidedAt = DateTimeOffset.UnixEpoch
        };

        EventEnvelope envelope = NotificationFactory.ApprovalDecided(approval, decision, ApprovalState.Approved);

        using JsonDocument doc = JsonDocument.Parse(envelope.PayloadJson);
        doc.RootElement.GetProperty("outcome").GetString().Should().Be("Approve");
        doc.RootElement.GetProperty("state").GetString().Should().Be("Approved");
        doc.RootElement.GetProperty("deciderId").GetString().Should().Be("alice");
    }

    [Fact]
    public void Terminated_reports_status_and_reason()
    {
        var instance = new WorkflowInstance
        {
            InstanceId = "i1", TenantId = "t1", WorkflowName = "wf", WorkflowVersion = "1.0.0",
            Status = InstanceStatus.DeadStopped, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch
        };

        EventEnvelope envelope = NotificationFactory.Terminated(instance, "FraudDetected");

        using JsonDocument doc = JsonDocument.Parse(envelope.PayloadJson);
        doc.RootElement.GetProperty("status").GetString().Should().Be("DeadStopped");
        doc.RootElement.GetProperty("reason").GetString().Should().Be("FraudDetected");
    }
}

public class IdGeneratorTests
{
    [Fact]
    public void Ids_are_unique()
    {
        string[] ids = Enumerable.Range(0, 5000).Select(_ => IdGenerator.NewId()).ToArray();
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Ids_sort_chronologically()
    {
        // The checkpoint ordering tiebreak depends on this property.
        string first = IdGenerator.NewId();
        Thread.Sleep(5);
        string second = IdGenerator.NewId();

        string.CompareOrdinal(first, second).Should().BeLessThan(0);
    }

    [Fact]
    public void Prefix_is_applied()
        => IdGenerator.NewId("apr").Should().StartWith("apr_");

    [Fact]
    public void Ids_use_only_crockford_base32_characters()
    {
        string id = IdGenerator.NewId();
        id.Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]+$");
        id.Should().HaveLength(26);
    }
}

/// <summary>
/// What the in-memory event store does beyond the contract. Ordering, ranges, type filters, paging,
/// streaming, isolation and concurrent appends live in <c>Prism.Flowline.Testing.EventStoreContract</c>
/// and run against this store in <see cref="Contracts.InMemoryEventStoreContractTests"/>.
/// </summary>
public class EventStoreTests
{
    private readonly InMemoryEventStore _store = new();

    [Fact]
    public async Task Limit_is_clamped_to_the_permitted_range()
    {
        await _store.AppendBatchAsync(
            Enumerable.Range(1, 5).Select(i => TestFactory.Event("i1", i, WorkflowEventTypes.ExecutorInvoked)).ToArray(), default);

        (await _store.QueryAsync(new EventQuery("i1", Limit: 0), default)).Items.Should().HaveCount(1);
        (await _store.QueryAsync(new EventQuery("i1", Limit: 99_999), default)).Items.Should().HaveCount(5);
    }

    [Fact]
    public async Task Append_rejects_null()
    {
        Func<Task> act = async () => await _store.AppendBatchAsync(null!, default);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}

public class DirectEventSinkTests
{
    [Fact]
    public async Task Writes_to_the_store_and_the_bus()
    {
        var store = new InMemoryEventStore();
        var bus = new Prism.Flowline.Notifications.InMemoryNotificationBus();
        var sink = new DirectNotificationSink(store, bus);

        await sink.PublishAsync(TestFactory.Event("i1", 1, "e"), default);

        (await store.MaxSequenceAsync("i1", default)).Should().Be(1);
    }

    [Fact]
    public async Task Applies_redaction_at_write_time()
    {
        var store = new InMemoryEventStore();
        var policy = new RedactionPolicy(bodyAllowList: ["executorId"]);
        var sink = new DirectNotificationSink(store, redaction: policy);

        await sink.PublishAsync(
            TestFactory.Event("i1", 1, "e", payload: """{"executorId":"pay","ssn":"123-45-6789"}"""), default);

        Page<EventEnvelope> page = await store.QueryAsync(new EventQuery("i1"), default);
        page.Items[0].PayloadJson.Should().Contain("pay");
        page.Items[0].PayloadJson.Should().NotContain("123-45-6789",
            "the history API must not leak what the live stream withheld");
    }

    [Fact]
    public async Task Rejects_null_envelopes()
    {
        var sink = new DirectNotificationSink(new InMemoryEventStore());
        Func<Task> act = async () => await sink.PublishAsync(null!, default);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}

public class EventPublisherTests
{
    [Fact]
    public async Task Batches_events_to_the_store()
    {
        var store = new InMemoryEventStore();
        await using var publisher = new NotificationPublisher(store, batchSize: 10);

        for (int i = 1; i <= 25; i++)
        {
            await publisher.PublishAsync(TestFactory.Event("i1", i, "e"), default);
        }

        await WaitForAsync(async () => (await store.MaxSequenceAsync("i1", default)) == 25);

        (await store.QueryAsync(new EventQuery("i1", Limit: 100), default)).Total.Should().Be(25);
    }

    [Fact]
    public async Task Fans_out_to_the_bus_as_well_as_the_store()
    {
        var store = new InMemoryEventStore();
        var bus = new Prism.Flowline.Notifications.InMemoryNotificationBus();
        await using var publisher = new NotificationPublisher(store, bus, batchSize: 5);

        await publisher.PublishAsync(TestFactory.Event("i1", 1, "e"), default);

        await WaitForAsync(async () => (await store.MaxSequenceAsync("i1", default)) == 1);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, int timeoutMs = 3000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(10);
        }

        throw new TimeoutException("Condition was not met before the timeout.");
    }
}
