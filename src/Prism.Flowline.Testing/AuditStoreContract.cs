using Prism.Flowline.Core;

namespace Prism.Flowline.Testing;

/// <summary>What every <see cref="IAuditStore"/> must do, whatever it is built on.</summary>
/// <remarks>
/// The operator audit trail: who did what, newest first, for one instance or for all of them. A
/// query for one instance returns that instance's entries however many newer entries other
/// instances have, so the filter belongs before the limit, not after it.
/// </remarks>
public sealed class AuditStoreContract
{
    private readonly Func<StoreContractContext, ValueTask<IAuditStore>> _create;

    public AuditStoreContract(Func<StoreContractContext, ValueTask<IAuditStore>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
        Cases = ContractCases.Discover(this);
    }

    /// <summary>Every case, by name, for a runner that would rather enumerate than list.</summary>
    public IReadOnlyList<ContractCase> Cases { get; }

    public Task Query_returns_newest_first_and_filters_by_instance() =>
        Run(nameof(Query_returns_newest_first_and_filters_by_instance), async (ctx, store) =>
        {
            string first = ctx.NewId("inst");
            string second = ctx.NewId("inst");
            DateTimeOffset now = ctx.Clock.GetUtcNow();

            await store.WriteAsync(Entry(ctx, "a", first, now), default);
            await store.WriteAsync(Entry(ctx, "b", first, now.AddMinutes(1)), default);
            await store.WriteAsync(Entry(ctx, "c", second, now.AddMinutes(2)), default);

            Expect.Sequence(["b", "a"], (await store.QueryAsync(first, 10, default)).Select(e => e.Action),
                "an instance's audit trail holds its own entries, newest first");

            string[] all = [.. (await store.QueryAsync(null, 1000, default))
                .Where(e => e.InstanceId == first || e.InstanceId == second)
                .Select(e => e.Action)];
            Expect.Sequence(["c", "b", "a"], all, "with no instance, every instance's entries are returned, newest first");

            Expect.Sequence(["b"], (await store.QueryAsync(first, 1, default)).Select(e => e.Action),
                "a limit keeps the newest entries");
        });

    /// <remarks>A store that takes the newest entries across the store and then filters returns too few.</remarks>
    public Task Query_by_instance_is_not_crowded_out_by_other_instances() =>
        Run(nameof(Query_by_instance_is_not_crowded_out_by_other_instances), async (ctx, store) =>
        {
            string quiet = ctx.NewId("inst");
            string busy = ctx.NewId("inst");
            DateTimeOffset now = ctx.Clock.GetUtcNow();

            for (int i = 0; i < 3; i++)
            {
                await store.WriteAsync(Entry(ctx, $"quiet-{i}", quiet, now.AddSeconds(i)), default);
            }

            for (int i = 0; i < 20; i++)
            {
                await store.WriteAsync(Entry(ctx, $"busy-{i}", busy, now.AddMinutes(1 + i)), default);
            }

            Expect.Equal(3, (await store.QueryAsync(quiet, 5, default)).Count,
                "an instance's entries are found however many newer entries other instances have");
        });

    public Task Large_and_non_ASCII_payloads_round_trip() => Run(nameof(Large_and_non_ASCII_payloads_round_trip), async (ctx, store) =>
    {
        string instance = ctx.NewId("inst");
        var written = new AuditEntry
        {
            Action = "gate.policy.set",
            InstanceId = instance,
            ApprovalId = ctx.NewId("apr"),
            ActorId = "user:zoë",
            Reason = Samples.NonAscii,
            Detail = Samples.LargeJson("detail"),
            OccurredAt = ctx.Clock.GetUtcNow()
        };

        await store.WriteAsync(written, default);
        AuditEntry read = Expect.NotNull((await store.QueryAsync(instance, 10, default)).FirstOrDefault(),
            "the entry can be read back");

        Expect.Equal(written.Action, read.Action, "the action reads back as written");
        Expect.Equal(written.ApprovalId, read.ApprovalId, "the approval reads back as written");
        Expect.Equal(written.ActorId, read.ActorId, "a non-ASCII actor reads back unchanged");
        Expect.Equal(written.Reason, read.Reason, "a non-ASCII reason reads back unchanged");
        Expect.Equal(written.Detail, read.Detail, "a large, non-ASCII detail reads back unchanged");
        Expect.Equal(written.OccurredAt, read.OccurredAt, "the time reads back as written");
    });

    private Task Run(string name, Func<StoreContractContext, IAuditStore, Task> body)
        => ContractRunner.RunAsync(name, _create, body);

    private static AuditEntry Entry(StoreContractContext ctx, string action, string instanceId, DateTimeOffset at) => new()
    {
        Action = action,
        InstanceId = instanceId,
        ActorId = "user:alice",
        OccurredAt = at
    };
}
