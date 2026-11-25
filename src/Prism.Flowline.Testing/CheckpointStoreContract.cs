using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Prism.Flowline.Testing;

/// <summary>
/// What every <see cref="ICheckpointStore{TStoreObject}"/> of <see cref="JsonElement"/> must do for
/// the runner, whatever it is built on.
/// </summary>
/// <remarks>
/// The interface is the Agent Framework's; the session id is the instance id. The framework's
/// <c>CheckpointManager</c> takes the last checkpoint of the index as the resume point, so the
/// index is oldest-first and its order is total even when two checkpoints share a commit time.
/// </remarks>
public sealed class CheckpointStoreContract
{
    private readonly Func<StoreContractContext, ValueTask<ICheckpointStore<JsonElement>>> _create;

    public CheckpointStoreContract(Func<StoreContractContext, ValueTask<ICheckpointStore<JsonElement>>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
        Cases = ContractCases.Discover(this);
    }

    /// <summary>Every case, by name, for a runner that would rather enumerate than list.</summary>
    public IReadOnlyList<ContractCase> Cases { get; }

    public Task A_checkpoint_round_trips() => Run(nameof(A_checkpoint_round_trips), async (ctx, store) =>
    {
        string session = ctx.NewId("session");
        JsonElement written = Payload("hello");

        CheckpointInfo info = await store.CreateCheckpointAsync(session, written);
        JsonElement read = await store.RetrieveCheckpointAsync(session, info);

        Expect.Equal(session, info.SessionId, "a checkpoint belongs to the session it was created in");
        Expect.That(Expect.JsonEquivalent(written, read), "a checkpoint reads back as it was written", read.GetRawText());
    });

    /// <remarks>The framework resumes from the last element of the index, so this ordering is the contract.</remarks>
    public Task Index_is_oldest_first() => Run(nameof(Index_is_oldest_first), async (ctx, store) =>
    {
        string session = ctx.NewId("session");
        var created = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            created.Add((await store.CreateCheckpointAsync(session, Payload($"v{i}"))).CheckpointId);
            ctx.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        Expect.Sequence(created, (await store.RetrieveIndexAsync(session)).Select(c => c.CheckpointId),
            "the index lists checkpoints oldest first, so its last element is the newest");
    });

    /// <remarks>
    /// Checkpoints within one superstep can share a commit time at the store's precision. Ordering by
    /// time alone makes the resume point a coin toss; a tiebreak on the (monotonic) id keeps it total.
    /// </remarks>
    public Task Ordering_stays_total_when_commit_timestamps_collide() =>
        Run(nameof(Ordering_stays_total_when_commit_timestamps_collide), async (ctx, store) =>
        {
            string session = ctx.NewId("session");
            var created = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                created.Add((await store.CreateCheckpointAsync(session, Payload($"v{i}"))).CheckpointId);
            }

            Expect.Sequence(created, (await store.RetrieveIndexAsync(session)).Select(c => c.CheckpointId),
                "checkpoints created within one instant still list in the order they were created");
        });

    public Task Repeated_index_reads_are_stable() => Run(nameof(Repeated_index_reads_are_stable), async (ctx, store) =>
    {
        string session = ctx.NewId("session");
        for (int i = 0; i < 8; i++)
        {
            await store.CreateCheckpointAsync(session, Payload($"v{i}"));
        }

        string[] first = [.. (await store.RetrieveIndexAsync(session)).Select(c => c.CheckpointId)];
        string[] second = [.. (await store.RetrieveIndexAsync(session)).Select(c => c.CheckpointId)];

        Expect.Sequence(first, second, "reading the index twice gives the same order twice");
    });

    public Task Parent_filtering_returns_only_children() => Run(nameof(Parent_filtering_returns_only_children), async (ctx, store) =>
    {
        string session = ctx.NewId("session");
        CheckpointInfo root = await store.CreateCheckpointAsync(session, Payload("root"));
        CheckpointInfo child = await store.CreateCheckpointAsync(session, Payload("child"), root);
        await store.CreateCheckpointAsync(session, Payload("grandchild"), child);
        await store.CreateCheckpointAsync(session, Payload("unrelated"));

        Expect.Sequence([child.CheckpointId], (await store.RetrieveIndexAsync(session, root)).Select(c => c.CheckpointId),
            "an index filtered by parent lists that parent's children only");
    });

    public Task Sessions_are_isolated() => Run(nameof(Sessions_are_isolated), async (ctx, store) =>
    {
        string first = ctx.NewId("session");
        string second = ctx.NewId("session");
        CheckpointInfo a = await store.CreateCheckpointAsync(first, Payload("a"));
        await store.CreateCheckpointAsync(second, Payload("b"));

        Expect.Sequence([a.CheckpointId], (await store.RetrieveIndexAsync(first)).Select(c => c.CheckpointId),
            "a session's index lists its own checkpoints only");
    });

    public Task Unknown_session_yields_an_empty_index() => Run(nameof(Unknown_session_yields_an_empty_index), async (ctx, store)
        => Expect.Equal(0, (await store.RetrieveIndexAsync(ctx.NewId("session"))).Count(),
            "a session with no checkpoints has an empty index, not an error"));

    public Task Retrieving_a_missing_checkpoint_throws() => Run(nameof(Retrieving_a_missing_checkpoint_throws), async (ctx, store) =>
    {
        string session = ctx.NewId("session");
        await store.CreateCheckpointAsync(session, Payload());

        await Expect.ThrowsAsync<KeyNotFoundException>(
            () => store.RetrieveCheckpointAsync(session, new CheckpointInfo(session, ctx.NewId("missing"))).AsTask(),
            "retrieving a checkpoint that does not exist throws KeyNotFoundException");
    });

    public Task Concurrent_creates_are_all_retained() => Run(nameof(Concurrent_creates_are_all_retained), async (ctx, store) =>
    {
        string session = ctx.NewId("session");

        Contention.Outcome<CheckpointInfo>[] outcomes = await Contention.RaceAsync(
            50, i => store.CreateCheckpointAsync(session, Payload($"v{i}")).AsTask());

        Contention.NoneThrew(outcomes, "concurrent checkpoints of one session all succeed");
        Expect.Sequence(outcomes.Select(o => o.Result!.CheckpointId).Order(),
            (await store.RetrieveIndexAsync(session)).Select(c => c.CheckpointId).Order(),
            "every checkpoint created concurrently is in the index");
    });

    public Task Large_and_non_ASCII_payloads_round_trip() => Run(nameof(Large_and_non_ASCII_payloads_round_trip), async (ctx, store) =>
    {
        string session = ctx.NewId("session");
        JsonElement written = JsonDocument.Parse(Samples.LargeJson("checkpoint")).RootElement.Clone();

        CheckpointInfo info = await store.CreateCheckpointAsync(session, written);
        JsonElement read = await store.RetrieveCheckpointAsync(session, info);

        Expect.That(Expect.JsonEquivalent(written, read), "a large, non-ASCII checkpoint reads back unchanged",
            $"{read.GetRawText().Length} chars came back for {written.GetRawText().Length} written");
    });

    private Task Run(string name, Func<StoreContractContext, ICheckpointStore<JsonElement>, Task> body)
        => ContractRunner.RunAsync(name, _create, body);

    private static JsonElement Payload(string value = "state")
        => JsonDocument.Parse(JsonSerializer.Serialize(new { value, text = Samples.NonAscii })).RootElement.Clone();
}
