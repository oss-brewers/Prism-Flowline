using Prism.Flowline.Abstractions;

namespace Prism.Flowline.Testing;

/// <summary>What every <see cref="IAuditRecordStore"/> must do, whatever it is built on.</summary>
/// <remarks>
/// A workflow's audit record: one root per instance, upserted as the run opens and closes, and
/// entries keyed by (section kind, key) so a retried executor corrects its entry rather than adding
/// a contradictory one. The store knows nothing about what the sections mean.
/// </remarks>
public sealed class AuditRecordStoreContract
{
    private readonly Func<StoreContractContext, ValueTask<IAuditRecordStore>> _create;

    public AuditRecordStoreContract(Func<StoreContractContext, ValueTask<IAuditRecordStore>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
        Cases = ContractCases.Discover(this);
    }

    /// <summary>Every case, by name, for a runner that would rather enumerate than list.</summary>
    public IReadOnlyList<ContractCase> Cases { get; }

    public Task Upserting_a_root_creates_then_replaces_it() => Run(nameof(Upserting_a_root_creates_then_replaces_it), async (ctx, store) =>
    {
        AuditRecordRoot opened = Root(ctx, ctx.NewId("inst"), ctx.NewId("wf")) with { AttributesJson = """{"channel":"web"}""" };
        await store.UpsertRootAsync(opened, default);

        AuditRecordDocument first = await GetAsync(store, opened.InstanceId);
        Expect.Equal(opened.RootKind, first.Root.RootKind, "the root kind reads back as written");
        Expect.Equal(opened.RootKey, first.Root.RootKey, "the root key reads back as written");
        Expect.Equal(opened.WorkflowName, first.Root.WorkflowName, "the workflow name reads back as written");
        Expect.Equal(opened.WorkflowVersion, first.Root.WorkflowVersion, "the workflow version reads back as written");
        Expect.Equal(AuditRecordStatus.Open, first.Root.Status, "the status reads back as written");
        Expect.Json(opened.AttributesJson, first.Root.AttributesJson, "the attributes read back as written");
        Expect.Equal(opened.OpenedUtc, first.Root.OpenedUtc, "the opening time reads back as written");
        Expect.Equal(0, first.Entries.Count, "a root with nothing recorded against it has no entries");

        DateTimeOffset closed = ctx.Clock.GetUtcNow().AddMinutes(3);
        await store.UpsertRootAsync(opened with { Status = AuditRecordStatus.Completed, ClosedUtc = closed }, default);

        AuditRecordDocument second = await GetAsync(store, opened.InstanceId);
        Expect.Equal(AuditRecordStatus.Completed, second.Root.Status, "a second upsert replaces the status");
        Expect.Equal<DateTimeOffset?>(closed, second.Root.ClosedUtc, "a second upsert writes the closing time");
        Expect.Equal(1, (await store.ListAsync(opened.WorkflowName, null, null, 10, default)).Count,
            "an upserted root is one root, not two");
    });

    public Task Get_returns_null_for_an_unknown_instance() => Run(nameof(Get_returns_null_for_an_unknown_instance), async (ctx, store)
        => Expect.Null(await store.GetAsync(ctx.NewId("inst"), default), "an instance with no audit record reads as null"));

    public Task Appending_the_same_section_and_key_replaces_the_entry() =>
        Run(nameof(Appending_the_same_section_and_key_replaces_the_entry), async (ctx, store) =>
        {
            AuditRecordRoot root = await OpenAsync(ctx, store);

            await store.AppendAsync(Entry(ctx, root, "step", "pick", """{"attempt":1}""", 1), default);
            await store.AppendAsync(Entry(ctx, root, "step", "pick", """{"attempt":2}""", 2), default);

            AuditRecordDocument document = await GetAsync(store, root.InstanceId);
            Expect.Equal(1, document.Entries.Count, "a retried step corrects its entry rather than adding a second one");
            Expect.Json("""{"attempt":2}""", document.Entries[0].PayloadJson, "the replacing entry's payload is the one kept");
        });

    public Task Sections_and_keys_distinguish_entries() => Run(nameof(Sections_and_keys_distinguish_entries), async (ctx, store) =>
    {
        AuditRecordRoot root = await OpenAsync(ctx, store);

        await store.AppendAsync(Entry(ctx, root, "step", "pick", "{}", 1), default);
        await store.AppendAsync(Entry(ctx, root, "step", "pack", "{}", 2), default);
        await store.AppendAsync(Entry(ctx, root, "check", "pick", "{}", 3), default);
        await store.AppendAsync(Entry(ctx, root, "submission", null, "{}", 4), default);

        Expect.Equal(4, (await GetAsync(store, root.InstanceId)).Entries.Count,
            "a different section or a different key is a different entry");
    });

    public Task Entries_read_back_in_sequence_order() => Run(nameof(Entries_read_back_in_sequence_order), async (ctx, store) =>
    {
        AuditRecordRoot root = await OpenAsync(ctx, store);

        await store.AppendAsync(Entry(ctx, root, "step", "c", "{}", 3), default);
        await store.AppendAsync(Entry(ctx, root, "step", "a", "{}", 1), default);
        await store.AppendAsync(Entry(ctx, root, "step", "b", "{}", 2), default);

        Expect.Sequence(["a", "b", "c"], (await GetAsync(store, root.InstanceId)).Entries.Select(e => e.Key),
            "entries read back in sequence order, whatever order they arrived in");
    });

    public Task Roots_are_listed_per_workflow_and_filtered_by_status_and_key() =>
        Run(nameof(Roots_are_listed_per_workflow_and_filtered_by_status_and_key), async (ctx, store) =>
        {
            string workflow = ctx.NewId("wf");
            await store.UpsertRootAsync(Root(ctx, ctx.NewId("inst"), workflow, "ORDER-1") with { Status = AuditRecordStatus.Completed }, default);
            await store.UpsertRootAsync(Root(ctx, ctx.NewId("inst"), workflow, "ORDER-2"), default);
            await store.UpsertRootAsync(Root(ctx, ctx.NewId("inst"), ctx.NewId("wf"), "ORDER-1"), default);

            Expect.Equal(2, (await store.ListAsync(workflow, null, null, 10, default)).Count,
                "a workflow's list holds that workflow's roots only");
            Expect.Sequence(["ORDER-2"], (await store.ListAsync(workflow, null, AuditRecordStatus.Open, 10, default)).Select(r => r.RootKey),
                "a status filter keeps roots in that status");
            Expect.Sequence(["ORDER-1"], (await store.ListAsync(workflow, "ORDER-1", null, 10, default)).Select(r => r.RootKey),
                "a root-key filter keeps roots with that key");
        });

    public Task Roots_are_listed_newest_first_up_to_the_limit() =>
        Run(nameof(Roots_are_listed_newest_first_up_to_the_limit), async (ctx, store) =>
        {
            string workflow = ctx.NewId("wf");
            for (int i = 0; i < 4; i++)
            {
                await store.UpsertRootAsync(Root(ctx, ctx.NewId("inst"), workflow, $"ORDER-{i}"), default);
                ctx.Clock.Advance(TimeSpan.FromMinutes(1));
            }

            Expect.Sequence(["ORDER-3", "ORDER-2", "ORDER-1", "ORDER-0"],
                (await store.ListAsync(workflow, null, null, 10, default)).Select(r => r.RootKey),
                "roots list newest first");
            Expect.Sequence(["ORDER-3", "ORDER-2"],
                (await store.ListAsync(workflow, null, null, 2, default)).Select(r => r.RootKey),
                "a limit keeps the newest roots");
        });

    public Task Large_and_non_ASCII_payloads_round_trip() => Run(nameof(Large_and_non_ASCII_payloads_round_trip), async (ctx, store) =>
    {
        AuditRecordRoot root = Root(ctx, ctx.NewId("inst"), ctx.NewId("wf"), "ORDER-ü-日本") with
        {
            AttributesJson = Samples.LargeJson("attributes")
        };
        await store.UpsertRootAsync(root, default);

        AuditRecordEntry written = Entry(ctx, root, "step", Samples.NonAscii[..20], Samples.LargeJson("entry"), 1);
        await store.AppendAsync(written, default);

        AuditRecordDocument document = await GetAsync(store, root.InstanceId);
        Expect.Equal(root.RootKey, document.Root.RootKey, "a non-ASCII root key reads back unchanged");
        Expect.Json(root.AttributesJson, document.Root.AttributesJson, "large, non-ASCII attributes read back unchanged");

        AuditRecordEntry read = Expect.NotNull(document.Entries.FirstOrDefault(), "the entry can be read back");
        Expect.Equal(written.Id, read.Id, "the entry id reads back as written");
        Expect.Equal(written.Key, read.Key, "a non-ASCII entry key reads back unchanged");
        Expect.Json(written.PayloadJson, read.PayloadJson, "a large, non-ASCII payload reads back unchanged");
        Expect.Equal(written.RecordedUtc, read.RecordedUtc, "the recording time reads back as written");
    });

    private Task Run(string name, Func<StoreContractContext, IAuditRecordStore, Task> body)
        => ContractRunner.RunAsync(name, _create, body);

    private static AuditRecordRoot Root(StoreContractContext ctx, string instanceId, string workflow, string rootKey = "ORDER-1") => new()
    {
        InstanceId = instanceId,
        WorkflowName = workflow,
        WorkflowVersion = "1.0.0",
        RootKind = "order",
        RootKey = rootKey,
        Status = AuditRecordStatus.Open,
        OpenedUtc = ctx.Clock.GetUtcNow()
    };

    private static AuditRecordEntry Entry(
        StoreContractContext ctx, AuditRecordRoot root, string kind, string? key, string payload, int sequence) => new()
    {
        Id = Guid.NewGuid(),
        InstanceId = root.InstanceId,
        SectionKind = kind,
        Key = key,
        PayloadJson = payload,
        Sequence = sequence,
        RecordedUtc = ctx.Clock.GetUtcNow()
    };

    private static async Task<AuditRecordRoot> OpenAsync(StoreContractContext ctx, IAuditRecordStore store)
    {
        AuditRecordRoot root = Root(ctx, ctx.NewId("inst"), ctx.NewId("wf"));
        await store.UpsertRootAsync(root, default);
        return root;
    }

    private static async Task<AuditRecordDocument> GetAsync(IAuditRecordStore store, string instanceId)
        => Expect.NotNull(await store.GetAsync(instanceId, default), $"the audit record of '{instanceId}' can be read back");
}
