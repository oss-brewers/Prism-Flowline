using Prism.Flowline.Abstractions;
using Prism.Flowline.Core;

namespace Prism.Flowline.Testing;

/// <summary>What every <see cref="IEventStore"/> must do, whatever it is built on.</summary>
/// <remarks>
/// The writer assigns each event its sequence; the store keeps every one it is given and reads them
/// back in sequence order with nothing missing, because the SSE <c>id:</c> is that sequence and a
/// reconnecting client resumes from it. A gap a reader can see is an event that client never gets.
/// </remarks>
public sealed class EventStoreContract
{
    private readonly Func<StoreContractContext, ValueTask<IEventStore>> _create;

    public EventStoreContract(Func<StoreContractContext, ValueTask<IEventStore>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
        Cases = ContractCases.Discover(this);
    }

    /// <summary>Every case, by name, for a runner that would rather enumerate than list.</summary>
    public IReadOnlyList<ContractCase> Cases { get; }

    public Task Query_returns_events_in_ascending_sequence() => Run(nameof(Query_returns_events_in_ascending_sequence), async (ctx, store) =>
    {
        string instance = ctx.NewId("inst");
        await store.AppendBatchAsync([Event(ctx, instance, 3, "c"), Event(ctx, instance, 1, "a"), Event(ctx, instance, 2, "b")], default);

        Page<EventEnvelope> page = await store.QueryAsync(new EventQuery(instance), default);

        Expect.Sequence([1L, 2L, 3L], page.Items.Select(e => e.Sequence), "events come back in ascending sequence, whatever order they were appended in");
    });

    public Task Query_honours_from_exclusive() => Run(nameof(Query_honours_from_exclusive), async (ctx, store) =>
    {
        string instance = await SeedAsync(ctx, store, 5);

        Page<EventEnvelope> page = await store.QueryAsync(new EventQuery(instance, FromExclusive: 2), default);

        Expect.Sequence([3L, 4L, 5L], page.Items.Select(e => e.Sequence), "FromExclusive starts after the given sequence");
    });

    public Task Query_honours_to_inclusive() => Run(nameof(Query_honours_to_inclusive), async (ctx, store) =>
    {
        string instance = await SeedAsync(ctx, store, 5);

        Page<EventEnvelope> page = await store.QueryAsync(new EventQuery(instance, ToInclusive: 3), default);

        Expect.Sequence([1L, 2L, 3L], page.Items.Select(e => e.Sequence), "ToInclusive stops at the given sequence");
    });

    public Task Query_filters_by_type() => Run(nameof(Query_filters_by_type), async (ctx, store) =>
    {
        string instance = ctx.NewId("inst");
        await store.AppendBatchAsync(
        [
            Event(ctx, instance, 1, WorkflowEventTypes.ExecutorInvoked),
            Event(ctx, instance, 2, WorkflowEventTypes.ApprovalRequested),
            Event(ctx, instance, 3, WorkflowEventTypes.ExecutorCompleted),
            Event(ctx, instance, 4, WorkflowEventTypes.ApprovalRequested)
        ], default);

        Page<EventEnvelope> page = await store.QueryAsync(
            new EventQuery(instance, Types: [WorkflowEventTypes.ApprovalRequested]), default);

        Expect.Sequence([2L, 4L], page.Items.Select(e => e.Sequence), "a type filter keeps only events of those types");
        Expect.Equal(2, page.Total, "Total counts the filtered events");
    });

    public Task Query_paginates_and_reports_a_cursor() => Run(nameof(Query_paginates_and_reports_a_cursor), async (ctx, store) =>
    {
        string instance = await SeedAsync(ctx, store, 10);

        Page<EventEnvelope> page = await store.QueryAsync(new EventQuery(instance, Limit: 4), default);

        Expect.Sequence([1L, 2L, 3L, 4L], page.Items.Select(e => e.Sequence), "a page holds the first Limit events");
        Expect.Equal(10, page.Total, "Total counts every matching event, not the page");
        Expect.Equal("4", page.NextCursor, "a page with more after it names the last sequence it holds as the cursor");

        Page<EventEnvelope> next = await store.QueryAsync(new EventQuery(instance, FromExclusive: long.Parse(page.NextCursor!), Limit: 4), default);
        Expect.Sequence([5L, 6L, 7L, 8L], next.Items.Select(e => e.Sequence), "the cursor resumes exactly where the page ended");
    });

    public Task Last_page_has_no_cursor() => Run(nameof(Last_page_has_no_cursor), async (ctx, store) =>
    {
        string instance = await SeedAsync(ctx, store, 3);

        Page<EventEnvelope> page = await store.QueryAsync(new EventQuery(instance, Limit: 10), default);

        Expect.Null(page.NextCursor, "a page with nothing after it carries no cursor");
    });

    public Task Read_streams_from_a_sequence() => Run(nameof(Read_streams_from_a_sequence), async (ctx, store) =>
    {
        string instance = await SeedAsync(ctx, store, 5);

        Expect.Sequence([4L, 5L], await ReadAllAsync(store, instance, 3), "a read streams every event after the given sequence, in order");
    });

    public Task MaxSequence_reports_the_highest_stored_value() => Run(nameof(MaxSequence_reports_the_highest_stored_value), async (ctx, store) =>
    {
        string instance = await SeedAsync(ctx, store, 7);

        Expect.Equal(7L, await store.MaxSequenceAsync(instance, default), "MaxSequence is the highest sequence stored");
    });

    public Task MaxSequence_of_an_unknown_instance_is_zero() => Run(nameof(MaxSequence_of_an_unknown_instance_is_zero), async (ctx, store)
        => Expect.Equal(0L, await store.MaxSequenceAsync(ctx.NewId("inst"), default),
            "an instance with no events has a MaxSequence of zero, so its first event is one"));

    public Task Instances_are_isolated() => Run(nameof(Instances_are_isolated), async (ctx, store) =>
    {
        string first = await SeedAsync(ctx, store, 3);
        string second = await SeedAsync(ctx, store, 5);

        Expect.Equal(3, (await store.QueryAsync(new EventQuery(first), default)).Total, "one instance's events are its own");
        Expect.Equal(5, (await store.QueryAsync(new EventQuery(second), default)).Total, "another instance's events are its own");
        Expect.Equal(3L, await store.MaxSequenceAsync(first, default), "MaxSequence reads one instance only");
    });

    /// <remarks>
    /// Several writers append for one instance at once, each with sequences of its own. Every one
    /// must land exactly once: a store that loses a write under contention leaves a gap a reader
    /// will step over, and one that writes twice repeats an SSE id.
    /// </remarks>
    public Task Concurrent_appends_for_one_instance_never_reuse_or_skip_a_sequence() =>
        Run(nameof(Concurrent_appends_for_one_instance_never_reuse_or_skip_a_sequence), async (ctx, store) =>
        {
            const int total = 200;
            const int writers = 8;
            string instance = ctx.NewId("inst");

            Contention.Outcome<int>[] outcomes = await Contention.RaceAsync(writers, async w =>
            {
                long[] mine = [.. Enumerable.Range(1, total).Where(s => s % writers == w).Select(s => (long)s)];
                foreach (long[] batch in mine.Chunk(1 + w % 3))
                {
                    await store.AppendBatchAsync([.. batch.Select(s => Event(ctx, instance, s, "e"))], default);
                }

                return mine.Length;
            });

            Contention.NoneThrew(outcomes, "concurrent appends of distinct sequences all succeed");

            long[] expected = [.. Enumerable.Range(1, total).Select(s => (long)s)];
            Expect.Sequence(expected, await ReadAllAsync(store, instance, 0),
                "every appended sequence reads back once, in order, with none missing");
            Expect.Equal((long)total, await store.MaxSequenceAsync(instance, default), "MaxSequence sees every append");
            Expect.Equal(total, (await store.QueryAsync(new EventQuery(instance, Limit: 1000), default)).Total,
                "a query counts every append");
        });

    public Task Large_and_non_ASCII_payloads_round_trip() => Run(nameof(Large_and_non_ASCII_payloads_round_trip), async (ctx, store) =>
    {
        string instance = ctx.NewId("inst");
        var written = new EventEnvelope
        {
            InstanceId = instance,
            Sequence = 1,
            EventType = "custom.non_ascii",
            TenantId = ctx.Tenant,
            ExecutorId = "pay",
            Superstep = 3,
            PayloadJson = Samples.LargeJson("event"),
            TraceId = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            OccurredAt = ctx.Clock.GetUtcNow(),
            WorkflowName = "orders"
        };

        await store.AppendBatchAsync([written], default);

        EventEnvelope read = Expect.NotNull(
            (await store.QueryAsync(new EventQuery(instance), default)).Items.FirstOrDefault(), "the event can be read back");

        Expect.Json(written.PayloadJson, read.PayloadJson, "a large, non-ASCII payload reads back unchanged");
        Expect.Equal(written.EventType, read.EventType, "the event type reads back as written");
        Expect.Equal(written.TenantId, read.TenantId, "the tenant reads back as written");
        Expect.Equal(written.ExecutorId, read.ExecutorId, "the executor reads back as written");
        Expect.Equal(written.Superstep, read.Superstep, "the superstep reads back as written");
        Expect.Equal(written.TraceId, read.TraceId, "the trace id reads back as written");
        Expect.Equal(written.OccurredAt, read.OccurredAt, "the time reads back as written");
        Expect.Equal(written.WorkflowName, read.WorkflowName, "the workflow name reads back as written");

        await foreach (EventEnvelope streamed in store.ReadAsync(instance, 0, default))
        {
            Expect.Json(written.PayloadJson, streamed.PayloadJson, "a streamed payload reads back unchanged too");
        }
    });

    private Task Run(string name, Func<StoreContractContext, IEventStore, Task> body)
        => ContractRunner.RunAsync(name, _create, body);

    private static EventEnvelope Event(StoreContractContext ctx, string instanceId, long sequence, string type) => new()
    {
        InstanceId = instanceId,
        Sequence = sequence,
        EventType = type,
        TenantId = ctx.Tenant,
        PayloadJson = $$"""{"sequence":{{sequence}}}""",
        OccurredAt = ctx.Clock.GetUtcNow().AddSeconds(sequence)
    };

    private static async Task<string> SeedAsync(StoreContractContext ctx, IEventStore store, int count)
    {
        string instance = ctx.NewId("inst");
        await store.AppendBatchAsync(
            [.. Enumerable.Range(1, count).Select(i => Event(ctx, instance, i, WorkflowEventTypes.ExecutorInvoked))], default);
        return instance;
    }

    private static async Task<List<long>> ReadAllAsync(IEventStore store, string instanceId, long fromExclusive)
    {
        var read = new List<long>();
        await foreach (EventEnvelope envelope in store.ReadAsync(instanceId, fromExclusive, default))
        {
            read.Add(envelope.Sequence);
        }

        return read;
    }
}
