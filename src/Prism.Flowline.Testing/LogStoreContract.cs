using Prism.Flowline.Abstractions;
using Prism.Flowline.Core;

namespace Prism.Flowline.Testing;

/// <summary>What every <see cref="ILogStore"/> must do, whatever it is built on.</summary>
/// <remarks>
/// A log entry's <see cref="InstanceLogEntry.Sequence"/> is the event sequence the instance had
/// reached when it was logged, not an identity: several entries share one, and the store keeps
/// them all. Entries read back oldest first, filtered by level (whatever its case) and executor.
/// </remarks>
public sealed class LogStoreContract
{
    private readonly Func<StoreContractContext, ValueTask<ILogStore>> _create;

    public LogStoreContract(Func<StoreContractContext, ValueTask<ILogStore>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
        Cases = ContractCases.Discover(this);
    }

    /// <summary>Every case, by name, for a runner that would rather enumerate than list.</summary>
    public IReadOnlyList<ContractCase> Cases { get; }

    public Task Query_filters_by_level_and_executor() => Run(nameof(Query_filters_by_level_and_executor), async (ctx, store) =>
    {
        string instance = ctx.NewId("inst");
        await store.AppendAsync(Entry(ctx, instance, "Error", "a"), default);
        await store.AppendAsync(Entry(ctx, instance, "Information", "a"), default);
        await store.AppendAsync(Entry(ctx, instance, "Error", "b"), default);

        Expect.Equal(3, (await store.QueryAsync(instance, null, null, 100, default)).Count, "with no filter, every entry is returned");
        Expect.Equal(2, (await store.QueryAsync(instance, "Error", null, 100, default)).Count, "a level filter keeps that level");
        Expect.Equal(1, (await store.QueryAsync(instance, "Error", "a", 100, default)).Count, "level and executor filters combine");
        Expect.Equal(2, (await store.QueryAsync(instance, "error", null, 100, default)).Count, "a level matches whatever its case");
    });

    public Task Instances_are_isolated() => Run(nameof(Instances_are_isolated), async (ctx, store) =>
    {
        string first = ctx.NewId("inst");
        await store.AppendAsync(Entry(ctx, first, "Error", "a"), default);
        await store.AppendAsync(Entry(ctx, ctx.NewId("inst"), "Error", "a"), default);

        Expect.Equal(1, (await store.QueryAsync(first, null, null, 100, default)).Count, "an instance's log holds its own entries only");
    });

    /// <remarks>
    /// The runner logs with the sequence it has reached and the LLM executor logs with zero, so a
    /// store keyed on (instance, sequence) refuses the second entry of every run.
    /// </remarks>
    public Task Entries_sharing_a_sequence_are_all_kept() => Run(nameof(Entries_sharing_a_sequence_are_all_kept), async (ctx, store) =>
    {
        string instance = ctx.NewId("inst");
        for (int i = 0; i < 4; i++)
        {
            await store.AppendAsync(Entry(ctx, instance, "Information", "llm", sequence: 0) with { Message = $"call {i}" }, default);
        }

        Expect.Equal(4, (await store.QueryAsync(instance, null, null, 100, default)).Count,
            "entries that share a sequence are separate entries, and every one is kept");
    });

    public Task Entries_are_returned_oldest_first_up_to_the_limit() =>
        Run(nameof(Entries_are_returned_oldest_first_up_to_the_limit), async (ctx, store) =>
        {
            string instance = ctx.NewId("inst");
            for (int i = 0; i < 5; i++)
            {
                await store.AppendAsync(Entry(ctx, instance, "Information", "a") with { Message = $"line {i}" }, default);
                ctx.Clock.Advance(TimeSpan.FromSeconds(1));
            }

            Expect.Sequence(["line 0", "line 1", "line 2", "line 3", "line 4"],
                (await store.QueryAsync(instance, null, null, 100, default)).Select(e => e.Message),
                "a log reads in the order it was written");
            Expect.Sequence(["line 0", "line 1"],
                (await store.QueryAsync(instance, null, null, 2, default)).Select(e => e.Message),
                "a limit keeps the earliest entries");
        });

    public Task Large_and_non_ASCII_payloads_round_trip() => Run(nameof(Large_and_non_ASCII_payloads_round_trip), async (ctx, store) =>
    {
        string instance = ctx.NewId("inst");
        InstanceLogEntry written = Entry(ctx, instance, "Error", "pay") with
        {
            Superstep = 7,
            Message = Samples.NonAscii,
            ExceptionType = "System.InvalidOperationException",
            StackTrace = Samples.LargeText(),
            TraceId = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
        };

        await store.AppendAsync(written, default);
        InstanceLogEntry read = Expect.NotNull(
            (await store.QueryAsync(instance, null, null, 10, default)).FirstOrDefault(), "the entry can be read back");

        Expect.Equal(written.Message, read.Message, "a non-ASCII message reads back unchanged");
        Expect.Equal(written.StackTrace, read.StackTrace, "a large, non-ASCII stack trace reads back unchanged");
        Expect.Equal(written.ExceptionType, read.ExceptionType, "the exception type reads back as written");
        Expect.Equal(written.Level, read.Level, "the level reads back as written");
        Expect.Equal(written.ExecutorId, read.ExecutorId, "the executor reads back as written");
        Expect.Equal(written.Superstep, read.Superstep, "the superstep reads back as written");
        Expect.Equal(written.Sequence, read.Sequence, "the sequence reads back as written");
        Expect.Equal(written.TraceId, read.TraceId, "the trace id reads back as written");
        Expect.Equal(written.LoggedAt, read.LoggedAt, "the time reads back as written");
    });

    private Task Run(string name, Func<StoreContractContext, ILogStore, Task> body)
        => ContractRunner.RunAsync(name, _create, body);

    private static InstanceLogEntry Entry(
        StoreContractContext ctx, string instanceId, string level, string executorId, long sequence = 1) => new()
    {
        InstanceId = instanceId,
        Sequence = sequence,
        Level = level,
        ExecutorId = executorId,
        Message = "msg",
        LoggedAt = ctx.Clock.GetUtcNow()
    };
}
