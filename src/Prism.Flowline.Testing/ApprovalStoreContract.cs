using Prism.Flowline.Abstractions;
using Prism.Flowline.Core;

namespace Prism.Flowline.Testing;

/// <summary>What every <see cref="IApprovalStore"/> must do, whatever it is built on.</summary>
/// <remarks>
/// An approval is decided by people who do not coordinate with each other, so its rules are about
/// decisions arriving together: one vote per decider, no vote once the approval has left Pending,
/// exactly one decision that closes it, and no vote lost while it is still open.
/// </remarks>
public sealed class ApprovalStoreContract
{
    private readonly Func<StoreContractContext, ValueTask<IApprovalStore>> _create;

    public ApprovalStoreContract(Func<StoreContractContext, ValueTask<IApprovalStore>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
        Cases = ContractCases.Discover(this);
    }

    /// <summary>Every case, by name, for a runner that would rather enumerate than list.</summary>
    public IReadOnlyList<ContractCase> Cases { get; }

    public Task Create_and_get_round_trip() => Run(nameof(Create_and_get_round_trip), async (ctx, store) =>
    {
        ApprovalRequest written = Request(ctx) with
        {
            Superstep = 4,
            CheckpointId = ctx.NewId("cp"),
            Reason = "AmountAboveThreshold",
            ProposedInputJson = """{"amount":30000}""",
            Assignees = ["group:finance", "user:alice"],
            RequiredApprovers = 2,
            AllowModification = true,
            RequireSegregationOfDuties = true,
            InitiatorId = "user:bob",
            OnExpiry = ExpiryAction.AutoApprove
        };

        await CreateAsync(ctx, store, written);
        ApprovalRequest read = await GetAsync(store, written.ApprovalId);

        Expect.Equal(written.InstanceId, read.InstanceId, "the instance reads back as written");
        Expect.Equal(written.TenantId, read.TenantId, "the tenant reads back as written");
        Expect.Equal(written.ExecutorId, read.ExecutorId, "the executor reads back as written");
        Expect.Equal(written.Superstep, read.Superstep, "the superstep reads back as written");
        Expect.Equal(written.CheckpointId, read.CheckpointId, "the checkpoint reads back as written");
        Expect.Equal(written.Reason, read.Reason, "the reason reads back as written");
        Expect.Json(written.ProposedInputJson, read.ProposedInputJson, "the proposed input reads back as written");
        Expect.Sequence(written.Assignees, read.Assignees, "the assignees read back as written");
        Expect.Equal(written.RequiredApprovers, read.RequiredApprovers, "the quorum reads back as written");
        Expect.Equal(written.AllowModification, read.AllowModification, "AllowModification reads back as written");
        Expect.Equal(written.RequireSegregationOfDuties, read.RequireSegregationOfDuties, "segregation of duties reads back as written");
        Expect.Equal(written.InitiatorId, read.InitiatorId, "the initiator reads back as written");
        Expect.Equal(written.State, read.State, "the state reads back as written");
        Expect.Equal(written.CreatedAt, read.CreatedAt, "CreatedAt reads back as written");
        Expect.Equal(written.ExpiresAt, read.ExpiresAt, "ExpiresAt reads back as written");
        Expect.Equal(written.OnExpiry, read.OnExpiry, "the expiry action reads back as written");
    });

    public Task Duplicate_ids_are_rejected() => Run(nameof(Duplicate_ids_are_rejected), async (ctx, store) =>
    {
        ApprovalRequest first = Request(ctx);
        await CreateAsync(ctx, store, first);

        await Expect.ThrowsAsync(
            () => store.CreateAsync(first with { ExecutorId = "other" }, default).AsTask(),
            "creating an approval whose id already exists is refused");

        Expect.Equal(first.ExecutorId, (await GetAsync(store, first.ApprovalId)).ExecutorId,
            "a refused duplicate leaves the original untouched");
    });

    public Task Get_of_an_unknown_id_is_null() => Run(nameof(Get_of_an_unknown_id_is_null), async (ctx, store)
        => Expect.Null(await store.GetAsync(ctx.NewId("apr"), default), "an unknown approval reads as null"));

    public Task List_for_instance_is_ordered_by_creation() => Run(nameof(List_for_instance_is_ordered_by_creation), async (ctx, store) =>
    {
        string instance = ctx.NewId("inst");
        DateTimeOffset now = ctx.Clock.GetUtcNow();

        ApprovalRequest later = Request(ctx, instanceId: instance) with { CreatedAt = now.AddMinutes(2) };
        ApprovalRequest earlier = Request(ctx, instanceId: instance) with { CreatedAt = now.AddMinutes(1) };
        await CreateAsync(ctx, store, later);
        await CreateAsync(ctx, store, earlier);
        await CreateAsync(ctx, store, Request(ctx, instanceId: ctx.NewId("inst")));

        IReadOnlyList<ApprovalRequest> list = await store.ListForInstanceAsync(instance, default);

        Expect.Sequence([earlier.ApprovalId, later.ApprovalId], list.Select(a => a.ApprovalId),
            "an instance's approvals list oldest first, and only that instance's");
    });

    public Task Pending_query_surfaces_soonest_expiry_first() => Run(nameof(Pending_query_surfaces_soonest_expiry_first), async (ctx, store) =>
    {
        DateTimeOffset now = ctx.Clock.GetUtcNow();
        ApprovalRequest late = Request(ctx, expiresAt: now.AddHours(8));
        ApprovalRequest soon = Request(ctx, expiresAt: now.AddHours(1));
        await CreateAsync(ctx, store, late);
        await CreateAsync(ctx, store, soon);

        IReadOnlyList<ApprovalRequest> pending = await store.QueryPendingAsync(ctx.Tenant, null, 10, default);

        Expect.Sequence([soon.ApprovalId, late.ApprovalId], pending.Select(a => a.ApprovalId),
            "the pending queue surfaces what is about to time out first");
    });

    public Task Pending_query_filters_by_tenant_and_assignee() => Run(nameof(Pending_query_filters_by_tenant_and_assignee), async (ctx, store) =>
    {
        string other = ctx.NewTenant();
        ApprovalRequest finance = Request(ctx, assignees: ["group:finance"]);
        ApprovalRequest ops = Request(ctx, assignees: ["group:ops"]);
        ApprovalRequest anyone = Request(ctx);
        await CreateAsync(ctx, store, finance);
        await CreateAsync(ctx, store, Request(ctx, tenant: other, assignees: ["group:finance"]));
        await CreateAsync(ctx, store, ops);
        await CreateAsync(ctx, store, anyone);

        Expect.Equal(3, (await store.QueryPendingAsync(ctx.Tenant, null, 10, default)).Count,
            "a tenant filter returns that tenant's pending approvals only");

        string[] forFinance = [.. (await store.QueryPendingAsync(ctx.Tenant, ["group:finance"], 10, default)).Select(a => a.ApprovalId).Order()];
        Expect.Sequence(new[] { finance.ApprovalId, anyone.ApprovalId }.Order(), forFinance,
            "an assignee filter returns approvals assigned to them, and approvals assigned to nobody");
    });

    public Task Decided_approvals_leave_the_pending_queue() => Run(nameof(Decided_approvals_leave_the_pending_queue), async (ctx, store) =>
    {
        ApprovalRequest approval = Request(ctx);
        await CreateAsync(ctx, store, approval);

        await store.TrySetStateAsync(approval.ApprovalId, ApprovalState.Approved, default);

        Expect.Equal(0, (await store.QueryPendingAsync(ctx.Tenant, null, 10, default)).Count,
            "an approval that is no longer Pending leaves the pending queue");
    });

    public Task Claim_expired_returns_only_due_pending_approvals() => Run(nameof(Claim_expired_returns_only_due_pending_approvals), async (ctx, store) =>
    {
        DateTimeOffset now = ctx.Clock.GetUtcNow();
        ApprovalRequest due = Request(ctx, expiresAt: now.AddMinutes(5));
        ApprovalRequest later = Request(ctx, expiresAt: now.AddHours(5));
        ApprovalRequest decided = Request(ctx, state: ApprovalState.Approved, expiresAt: now.AddMinutes(1));
        await CreateAsync(ctx, store, due);
        await CreateAsync(ctx, store, later);
        await CreateAsync(ctx, store, decided);

        string[] claimed = [.. (await store.ClaimExpiredAsync(now.AddMinutes(10), 100, default)).Select(a => a.ApprovalId)];

        Expect.Contains(claimed, due.ApprovalId, "a pending approval past its expiry is claimed");
        Expect.DoesNotContain(claimed, later.ApprovalId, "an approval not yet expired is not claimed");
        Expect.DoesNotContain(claimed, decided.ApprovalId, "an approval already decided is not claimed, however old");
    });

    public Task One_vote_per_decider_is_enforced_at_the_store() => Run(nameof(One_vote_per_decider_is_enforced_at_the_store), async (ctx, store) =>
    {
        ApprovalRequest approval = Request(ctx) with { RequiredApprovers = 2 };
        await CreateAsync(ctx, store, approval);
        ApprovalDecision vote = Vote(ctx, approval, "alice");

        Expect.That(await store.TryRecordDecisionAsync(approval.ApprovalId, vote, null, default),
            "a decider's first vote is recorded");
        Expect.That(!await store.TryRecordDecisionAsync(approval.ApprovalId, vote, null, default),
            "a decider's second vote on the same approval is refused");

        Expect.Equal(1, (await store.GetDecisionsAsync(approval.ApprovalId, default)).Count,
            "only the first vote is kept");
    });

    public Task Decisions_cannot_be_recorded_once_the_state_has_moved_on() =>
        Run(nameof(Decisions_cannot_be_recorded_once_the_state_has_moved_on), async (ctx, store) =>
        {
            ApprovalRequest approval = Request(ctx, state: ApprovalState.Approved);
            await CreateAsync(ctx, store, approval);

            bool recorded = await store.TryRecordDecisionAsync(
                approval.ApprovalId, Vote(ctx, approval, "late", ApprovalOutcomeKind.Reject), ApprovalState.Rejected, default);

            Expect.That(!recorded, "a decision on an approval that has left Pending is refused");
            Expect.Equal(ApprovalState.Approved, (await GetAsync(store, approval.ApprovalId)).State,
                "a refused decision does not change the state");
            Expect.Equal(0, (await store.GetDecisionsAsync(approval.ApprovalId, default)).Count,
                "a refused decision is not recorded");
        });

    /// <remarks>
    /// Two approvers press the button at once. The store is the only place that can decide which of
    /// them closed the approval; a read-modify-write of the whole row lets both through.
    /// </remarks>
    public Task Concurrent_decisions_on_one_approval_let_exactly_one_close_it() =>
        Run(nameof(Concurrent_decisions_on_one_approval_let_exactly_one_close_it), async (ctx, store) =>
        {
            ApprovalRequest approval = Request(ctx);
            await CreateAsync(ctx, store, approval);

            Contention.Outcome<bool>[] outcomes = await Contention.RaceAsync(20, i => store.TryRecordDecisionAsync(
                approval.ApprovalId, Vote(ctx, approval, $"user{i}"), ApprovalState.Approved, default).AsTask());

            Contention.NoneThrew(outcomes, "a decision that loses the race is told false, not thrown at");
            Expect.Equal(1, outcomes.Count(o => o.Result), "exactly one of several closing decisions wins");

            Expect.Equal(ApprovalState.Approved, (await GetAsync(store, approval.ApprovalId)).State,
                "the winning decision's state is written");
            Expect.Equal(1, (await store.GetDecisionsAsync(approval.ApprovalId, default)).Count,
                "only the winning decision is recorded");
        });

    /// <remarks>
    /// A quorum of three votes at once, none of which closes the approval on its own. Each is a
    /// different decider on a Pending approval, so each must be recorded; reporting false for one
    /// tells that approver their vote was refused when it was only unlucky.
    /// </remarks>
    public Task Concurrent_votes_on_a_pending_approval_are_all_recorded() =>
        Run(nameof(Concurrent_votes_on_a_pending_approval_are_all_recorded), async (ctx, store) =>
        {
            const int voters = 6;
            ApprovalRequest approval = Request(ctx) with { RequiredApprovers = voters + 1 };
            await CreateAsync(ctx, store, approval);

            Contention.Outcome<bool>[] outcomes = await Contention.RaceAsync(voters, i => store.TryRecordDecisionAsync(
                approval.ApprovalId, Vote(ctx, approval, $"user{i}"), null, default).AsTask());

            Contention.NoneThrew(outcomes, "a vote that arrives alongside others is recorded, not thrown at");
            Expect.Equal(voters, outcomes.Count(o => o.Result),
                "every vote by a different decider on a Pending approval is recorded");

            string[] deciders = [.. (await store.GetDecisionsAsync(approval.ApprovalId, default)).Select(d => d.DeciderId).Order()];
            Expect.Sequence(Enumerable.Range(0, voters).Select(i => $"user{i}").Order(), deciders,
                "no vote is lost when votes arrive together");
        });

    public Task Setting_state_back_to_pending_marks_it_escalated() => Run(nameof(Setting_state_back_to_pending_marks_it_escalated), async (ctx, store) =>
    {
        ApprovalRequest approval = Request(ctx);
        await CreateAsync(ctx, store, approval);

        Expect.That(await store.TrySetStateAsync(approval.ApprovalId, ApprovalState.Pending, default),
            "setting the state of an existing approval succeeds");

        Expect.That((await GetAsync(store, approval.ApprovalId)).EscalatedOnce,
            "returning an approval to Pending records that it was escalated");
    });

    public Task Cancelling_an_instance_cancels_only_its_pending_approvals() =>
        Run(nameof(Cancelling_an_instance_cancels_only_its_pending_approvals), async (ctx, store) =>
        {
            string instance = ctx.NewId("inst");
            ApprovalRequest pending = Request(ctx, instanceId: instance);
            ApprovalRequest approved = Request(ctx, instanceId: instance, state: ApprovalState.Approved);
            ApprovalRequest elsewhere = Request(ctx, instanceId: ctx.NewId("inst"));
            await CreateAsync(ctx, store, pending);
            await CreateAsync(ctx, store, approved);
            await CreateAsync(ctx, store, elsewhere);

            await store.CancelForInstanceAsync(instance, default);

            Expect.Equal(ApprovalState.Cancelled, (await GetAsync(store, pending.ApprovalId)).State,
                "cancelling an instance cancels its pending approvals");
            Expect.Equal(ApprovalState.Approved, (await GetAsync(store, approved.ApprovalId)).State,
                "cancelling an instance leaves its decided approvals as they were decided");
            Expect.Equal(ApprovalState.Pending, (await GetAsync(store, elsewhere.ApprovalId)).State,
                "cancelling an instance leaves other instances' approvals alone");
        });

    public Task Missing_approvals_report_false_or_empty() => Run(nameof(Missing_approvals_report_false_or_empty), async (ctx, store) =>
    {
        string missing = ctx.NewId("apr");

        Expect.That(!await store.TrySetStateAsync(missing, ApprovalState.Approved, default),
            "setting the state of an unknown approval reports false");
        Expect.That(!await store.TryRecordDecisionAsync(missing, new ApprovalDecision
            {
                ApprovalId = missing, DeciderId = "alice", Outcome = ApprovalOutcomeKind.Approve, DecidedAt = ctx.Clock.GetUtcNow()
            }, ApprovalState.Approved, default),
            "deciding an unknown approval reports false");
        Expect.Equal(0, (await store.GetDecisionsAsync(missing, default)).Count, "an unknown approval has no decisions");
        Expect.Equal(0, (await store.ListForInstanceAsync(ctx.NewId("inst"), default)).Count,
            "an instance with no approvals lists none");
    });

    public Task Large_and_non_ASCII_payloads_round_trip() => Run(nameof(Large_and_non_ASCII_payloads_round_trip), async (ctx, store) =>
    {
        ApprovalRequest approval = Request(ctx) with
        {
            Reason = Samples.NonAscii,
            ProposedInputJson = Samples.LargeJson("proposed"),
            Assignees = ["group:" + Samples.NonAscii],
            AllowModification = true
        };
        await CreateAsync(ctx, store, approval);

        ApprovalDecision vote = Vote(ctx, approval, "user:zoë") with
        {
            Comment = Samples.NonAscii,
            ModifiedInputJson = Samples.LargeJson("modified")
        };
        await store.TryRecordDecisionAsync(approval.ApprovalId, vote, ApprovalState.Approved, default);

        ApprovalRequest read = await GetAsync(store, approval.ApprovalId);
        Expect.Equal(Samples.NonAscii, read.Reason, "a non-ASCII reason reads back unchanged");
        Expect.Json(approval.ProposedInputJson, read.ProposedInputJson, "a large, non-ASCII proposed input reads back unchanged");
        Expect.Sequence(approval.Assignees, read.Assignees, "non-ASCII assignees read back unchanged");

        ApprovalDecision recorded = Expect.NotNull(
            (await store.GetDecisionsAsync(approval.ApprovalId, default)).FirstOrDefault(), "the decision can be read back");
        Expect.Equal(vote.DeciderId, recorded.DeciderId, "a non-ASCII decider reads back unchanged");
        Expect.Equal(vote.Comment, recorded.Comment, "a non-ASCII comment reads back unchanged");
        Expect.Json(vote.ModifiedInputJson, recorded.ModifiedInputJson, "a large, non-ASCII modified input reads back unchanged");
        Expect.Equal(vote.Outcome, recorded.Outcome, "the outcome reads back as written");
        Expect.Equal(vote.DecidedAt, recorded.DecidedAt, "the decision time reads back as written");
    });

    private Task Run(string name, Func<StoreContractContext, IApprovalStore, Task> body)
        => ContractRunner.RunAsync(name, _create, body);

    private static ApprovalRequest Request(
        StoreContractContext ctx, string? instanceId = null, ApprovalState state = ApprovalState.Pending,
        string? tenant = null, string[]? assignees = null, DateTimeOffset? expiresAt = null) => new()
    {
        ApprovalId = ctx.NewId("apr"),
        InstanceId = instanceId ?? ctx.NewId("inst"),
        TenantId = tenant ?? ctx.Tenant,
        ExecutorId = "pay",
        Assignees = assignees ?? [],
        State = state,
        CreatedAt = ctx.Clock.GetUtcNow(),
        ExpiresAt = expiresAt ?? ctx.Clock.GetUtcNow().AddHours(1)
    };

    private static ApprovalDecision Vote(
        StoreContractContext ctx, ApprovalRequest approval, string decider,
        ApprovalOutcomeKind outcome = ApprovalOutcomeKind.Approve) => new()
    {
        ApprovalId = approval.ApprovalId,
        DeciderId = decider,
        Outcome = outcome,
        DecidedAt = ctx.Clock.GetUtcNow()
    };

    private static async Task CreateAsync(StoreContractContext ctx, IApprovalStore store, ApprovalRequest request)
    {
        // An expiry sweep reads every approval in the store; one this case leaves Pending would be
        // claimed by the next case that shares the database.
        ctx.OnDispose(async () => await store.TrySetStateAsync(request.ApprovalId, ApprovalState.Cancelled, default));
        await store.CreateAsync(request, default);
    }

    private static async Task<ApprovalRequest> GetAsync(IApprovalStore store, string approvalId)
        => Expect.NotNull(await store.GetAsync(approvalId, default), $"approval '{approvalId}' can be read back");
}
