using Prism.Flowline.Abstractions;
using Prism.Flowline.Core;

namespace Prism.Flowline.Testing;

/// <summary>What every <see cref="IInstanceStore"/> must do, whatever it is built on.</summary>
/// <remarks>
/// <para>
/// The instance store is where replicas meet, so most of its rules are about contention: a
/// transition is a compare-and-set, a terminal state stays terminal against a late writer, a claim
/// hands each instance to exactly one replica, and only the lease's owner may renew or release it.
/// The in-memory store keeps these with a lock; a database has to keep them with its own
/// concurrency control, which is what the contention cases exercise.
/// </para>
/// <para>
/// Build the store over <see cref="StoreContractContext.Clock"/>: <c>CreatedAt</c>,
/// <c>UpdatedAt</c> and renewed leases are read from it, and the cases move it rather than sleep.
/// </para>
/// </remarks>
public sealed class InstanceStoreContract
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);
    private const string Workflow = "contract-workflow";

    private readonly Func<StoreContractContext, ValueTask<IInstanceStore>> _create;

    public InstanceStoreContract(Func<StoreContractContext, ValueTask<IInstanceStore>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
        Cases = ContractCases.Discover(this);
    }

    /// <summary>Every case, by name, for a runner that would rather enumerate than list.</summary>
    public IReadOnlyList<ContractCase> Cases { get; }

    // ── Create and read ─────────────────────────────────────────────────────────────────────────

    public Task Created_instances_start_pending() => Run(nameof(Created_instances_start_pending), async (ctx, store) =>
    {
        CreateInstanceRequest request = Request(ctx) with
        {
            ContextJson = """{"order":42,"lines":[1,2]}""",
            CorrelationId = ctx.NewId("corr"),
            IdempotencyKey = ctx.NewId("key"),
            RerunOfInstanceId = ctx.NewId("inst"),
            MaxAttempts = 7
        };

        WorkflowInstance created = await CreateAsync(ctx, store, request);

        Expect.Equal(InstanceStatus.Pending, created.Status, "a created instance starts Pending");
        Expect.Equal(1L, created.Version, "a created instance starts at version one");
        Expect.Equal(ctx.Clock.GetUtcNow(), created.CreatedAt, "CreatedAt comes from the store's clock");
        Expect.Null(created.LeaseOwner, "a created instance is leased to nobody");

        WorkflowInstance read = await GetAsync(store, request.InstanceId);
        Expect.Equal(request.TenantId, read.TenantId, "the tenant reads back as written");
        Expect.Equal(request.WorkflowName, read.WorkflowName, "the workflow name reads back as written");
        Expect.Equal(request.WorkflowVersion, read.WorkflowVersion, "the workflow version reads back as written");
        Expect.Json(request.ContextJson, read.ContextJson, "the context reads back as written");
        Expect.Equal(request.CorrelationId, read.CorrelationId, "the correlation id reads back as written");
        Expect.Equal(request.IdempotencyKey, read.IdempotencyKey, "the idempotency key reads back as written");
        Expect.Equal(request.RerunOfInstanceId, read.RerunOfInstanceId, "the rerun link reads back as written");
        Expect.Equal(request.MaxAttempts, read.MaxAttempts, "the attempt budget reads back as written");
        Expect.Equal(InstanceStatus.Pending, read.Status, "a created instance is stored Pending");
        Expect.Equal(1L, read.Version, "a created instance is stored at version one");
        Expect.Equal(created.CreatedAt, read.CreatedAt, "CreatedAt reads back as written");
        Expect.Equal<DateTimeOffset?>(null, read.LeaseExpiresAt, "a created instance is stored with no lease");
    });

    public Task Duplicate_instance_ids_are_rejected() => Run(nameof(Duplicate_instance_ids_are_rejected), async (ctx, store) =>
    {
        CreateInstanceRequest first = Request(ctx);
        await CreateAsync(ctx, store, first);

        await Expect.ThrowsAsync(
            () => store.CreateAsync(first with { TenantId = ctx.NewTenant() }, default).AsTask(),
            "creating an instance whose id already exists is refused");

        Expect.Equal(first.TenantId, (await GetAsync(store, first.InstanceId)).TenantId,
            "a refused duplicate leaves the original untouched");
    });

    public Task Get_returns_null_for_an_unknown_id() => Run(nameof(Get_returns_null_for_an_unknown_id), async (ctx, store)
        => Expect.Null(await store.GetAsync(ctx.NewId("inst"), default), "an unknown instance reads as null"));

    public Task Idempotency_lookup_is_scoped_per_tenant() => Run(nameof(Idempotency_lookup_is_scoped_per_tenant), async (ctx, store) =>
    {
        string key = ctx.NewId("key");
        string other = ctx.NewTenant();

        WorkflowInstance a = await CreateAsync(ctx, store, Request(ctx) with { IdempotencyKey = key });
        WorkflowInstance b = await CreateAsync(ctx, store, Request(ctx, tenant: other) with { IdempotencyKey = key });

        Expect.Equal(a.InstanceId, (await store.FindByIdempotencyKeyAsync(ctx.Tenant, key, default))?.InstanceId,
            "an idempotency key finds its own tenant's instance");
        Expect.Equal(b.InstanceId, (await store.FindByIdempotencyKeyAsync(other, key, default))?.InstanceId,
            "the same key in another tenant finds that tenant's instance");
        Expect.Null(await store.FindByIdempotencyKeyAsync(ctx.NewTenant(), key, default),
            "a key is not visible to a tenant that never used it");
    });

    /// <remarks>
    /// Concurrent starts carrying the same key are what the key exists for. A store that checks
    /// for the key and then inserts, rather than letting a unique constraint decide, admits two.
    /// </remarks>
    public Task A_duplicate_idempotency_key_for_one_tenant_is_refused_under_contention() =>
        Run(nameof(A_duplicate_idempotency_key_for_one_tenant_is_refused_under_contention), async (ctx, store) =>
        {
            string key = ctx.NewId("key");
            CreateInstanceRequest[] requests = [.. Enumerable.Range(0, 12).Select(_ => Request(ctx) with { IdempotencyKey = key })];
            foreach (CreateInstanceRequest request in requests)
            {
                Retire(ctx, store, request.InstanceId);
            }

            Contention.Outcome<WorkflowInstance>[] outcomes = await Contention.RaceAsync(
                requests.Length, i => store.CreateAsync(requests[i], default).AsTask());

            Contention.Outcome<WorkflowInstance>[] created = [.. outcomes.Where(o => o.Error is null)];
            Expect.Equal(1, created.Length, "exactly one create per tenant and idempotency key succeeds, however many race");

            WorkflowInstance found = Expect.NotNull(
                await store.FindByIdempotencyKeyAsync(ctx.Tenant, key, default), "the key finds the instance that won");
            Expect.Equal(created[0].Result!.InstanceId, found.InstanceId, "the key finds the instance whose create succeeded");

            foreach (Contention.Outcome<WorkflowInstance> refused in outcomes.Where(o => o.Error is not null))
            {
                Expect.Null(await store.GetAsync(requests[refused.Contender].InstanceId, default),
                    "a refused create leaves no instance behind");
            }

            // The constraint is per tenant: another tenant is free to use the same key.
            await CreateAsync(ctx, store, Request(ctx, tenant: ctx.NewTenant()) with { IdempotencyKey = key });
        });

    /// <remarks>A unique index over a nullable key column that forgets to exclude nulls refuses the second.</remarks>
    public Task Instances_without_an_idempotency_key_never_collide() =>
        Run(nameof(Instances_without_an_idempotency_key_never_collide), async (ctx, store) =>
        {
            WorkflowInstance a = await CreateAsync(ctx, store, Request(ctx));
            WorkflowInstance b = await CreateAsync(ctx, store, Request(ctx));

            await GetAsync(store, a.InstanceId);
            await GetAsync(store, b.InstanceId);
        });

    // ── Transitions and updates ─────────────────────────────────────────────────────────────────

    public Task Transition_succeeds_from_the_expected_state() => Run(nameof(Transition_succeeds_from_the_expected_state), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
        DateTimeOffset started = ctx.Clock.GetUtcNow();

        bool moved = await store.TryTransitionAsync(
            instance.InstanceId, InstanceStatus.Pending, InstanceStatus.Running, m => m.StartedAt = started, default);

        Expect.That(moved, "a transition from the instance's current state succeeds");

        WorkflowInstance read = await GetAsync(store, instance.InstanceId);
        Expect.Equal(InstanceStatus.Running, read.Status, "a successful transition writes the new state");
        Expect.Equal<DateTimeOffset?>(started, read.StartedAt, "a transition's mutation is written with it");
        Expect.Equal(2L, read.Version, "a transition bumps the version");
    });

    public Task Transition_fails_from_an_unexpected_state() => Run(nameof(Transition_fails_from_an_unexpected_state), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));

        bool moved = await store.TryTransitionAsync(
            instance.InstanceId, InstanceStatus.Running, InstanceStatus.Completed, m => m.ResultJson = "{}", default);

        Expect.That(!moved, "a transition from a state the instance is not in is refused");

        WorkflowInstance read = await GetAsync(store, instance.InstanceId);
        Expect.Equal(InstanceStatus.Pending, read.Status, "a refused transition leaves the state alone");
        Expect.Null(read.ResultJson, "a refused transition applies none of its mutation");
        Expect.Equal(1L, read.Version, "a refused transition does not bump the version");
    });

    public Task Transition_never_leaves_a_terminal_state() => Run(nameof(Transition_never_leaves_a_terminal_state), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
        await store.UpdateAsync(instance.InstanceId, m => m.Status = InstanceStatus.Completed, default);

        bool moved = await store.TryTransitionAsync(
            instance.InstanceId, InstanceStatus.Completed, InstanceStatus.Running, null, default);

        Expect.That(!moved, "terminal states are final: a transition out of one is refused");
        Expect.Equal(InstanceStatus.Completed, (await GetAsync(store, instance.InstanceId)).Status,
            "a terminal instance keeps its state");
    });

    /// <remarks>
    /// A stale runner finishing after an operator's cancel is the case this guards: two writers
    /// leave the same state, and the store must let one of them win outright rather than both
    /// writing, or the last one in.
    /// </remarks>
    public Task Concurrent_transitions_from_one_state_let_exactly_one_win() =>
        Run(nameof(Concurrent_transitions_from_one_state_let_exactly_one_win), async (ctx, store) =>
        {
            WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
            await store.TryTransitionAsync(instance.InstanceId, InstanceStatus.Pending, InstanceStatus.Running, null, default);

            InstanceStatus[] targets =
                [InstanceStatus.Completed, InstanceStatus.Failed, InstanceStatus.Cancelled, InstanceStatus.DeadStopped];

            Contention.Outcome<bool>[] outcomes = await Contention.RaceAsync(16, i => store.TryTransitionAsync(
                instance.InstanceId, InstanceStatus.Running, targets[i % targets.Length],
                m => m.TerminalReason = $"writer-{i}", default).AsTask());

            Contention.NoneThrew(outcomes, "a writer that loses a transition is told false, not thrown at");

            Contention.Outcome<bool>[] won = [.. outcomes.Where(o => o.Result)];
            Expect.Equal(1, won.Length, "exactly one of several transitions from the same state wins");

            WorkflowInstance read = await GetAsync(store, instance.InstanceId);
            Expect.Equal(targets[won[0].Contender % targets.Length], read.Status, "the state written is the winner's");
            Expect.Equal($"writer-{won[0].Contender}", read.TerminalReason,
                "the mutation written is the winner's alone, not a mixture of writers");
        });

    public Task Update_will_not_resurrect_a_terminal_instance() => Run(nameof(Update_will_not_resurrect_a_terminal_instance), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
        await store.UpdateAsync(instance.InstanceId, m => m.Status = InstanceStatus.DeadStopped, default);

        bool updated = await store.UpdateAsync(instance.InstanceId, m => m.Status = InstanceStatus.Running, default);

        Expect.That(!updated, "an update cannot move a terminal instance to another state");
        Expect.Equal(InstanceStatus.DeadStopped, (await GetAsync(store, instance.InstanceId)).Status,
            "the terminal state survives the attempt");
    });

    public Task Update_of_a_terminal_instance_to_the_same_status_is_permitted() =>
        Run(nameof(Update_of_a_terminal_instance_to_the_same_status_is_permitted), async (ctx, store) =>
        {
            WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
            await store.UpdateAsync(instance.InstanceId, m => m.Status = InstanceStatus.Completed, default);

            bool updated = await store.UpdateAsync(instance.InstanceId, m =>
            {
                m.Status = InstanceStatus.Completed;
                m.ResultJson = """{"total":10}""";
            }, default);

            Expect.That(updated, "bookkeeping on a terminal instance that keeps its state is allowed");
            Expect.Json("""{"total":10}""", (await GetAsync(store, instance.InstanceId)).ResultJson,
                "the bookkeeping is written");
        });

    public Task A_deliberate_revive_may_leave_a_terminal_state() => Run(nameof(A_deliberate_revive_may_leave_a_terminal_state), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
        await store.UpdateAsync(instance.InstanceId, m => m.Status = InstanceStatus.Failed, default);

        bool revived = await store.UpdateAsync(instance.InstanceId, m =>
        {
            m.Status = InstanceStatus.Dispatchable;
            m.AllowTerminalTransition = true;
        }, default);

        Expect.That(revived, "an update that sets AllowTerminalTransition may move an instance out of a terminal state");
        Expect.Equal(InstanceStatus.Dispatchable, (await GetAsync(store, instance.InstanceId)).Status,
            "the revived state is written");
    });

    public Task Update_bumps_the_version_and_timestamp() => Run(nameof(Update_bumps_the_version_and_timestamp), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
        ctx.Clock.Advance(TimeSpan.FromMinutes(1));

        await store.UpdateAsync(instance.InstanceId, m => m.Status = InstanceStatus.Running, default);

        WorkflowInstance read = await GetAsync(store, instance.InstanceId);
        Expect.Equal(2L, read.Version, "every write bumps the version");
        Expect.Equal(ctx.Clock.GetUtcNow(), read.UpdatedAt, "UpdatedAt is the store clock's time of the write");
        Expect.Equal(instance.CreatedAt, read.CreatedAt, "an update leaves CreatedAt alone");
    });

    public Task Update_of_an_unknown_instance_returns_false() => Run(nameof(Update_of_an_unknown_instance_returns_false), async (ctx, store)
        => Expect.That(!await store.UpdateAsync(ctx.NewId("inst"), m => m.AttemptCount = 1, default),
            "updating an instance that does not exist reports false"));

    public Task Clear_flags_null_out_lease_and_retry_fields() => Run(nameof(Clear_flags_null_out_lease_and_retry_fields), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
        DateTimeOffset now = ctx.Clock.GetUtcNow();

        await store.UpdateAsync(instance.InstanceId, m =>
        {
            m.LeaseOwner = ctx.NewId("replica");
            m.LeaseExpiresAt = now.AddMinutes(1);
            m.NextRetryAt = now.AddMinutes(5);
            m.CompletedAt = now;
        }, default);

        WorkflowInstance set = await GetAsync(store, instance.InstanceId);
        Expect.That(set.LeaseOwner is not null && set.LeaseExpiresAt is not null && set.NextRetryAt is not null && set.CompletedAt is not null,
            "lease, retry and completion fields are written when set");

        await store.UpdateAsync(instance.InstanceId, m =>
        {
            m.ClearLease = true;
            m.ClearNextRetryAt = true;
            m.ClearCompletedAt = true;
        }, default);

        WorkflowInstance cleared = await GetAsync(store, instance.InstanceId);
        Expect.Null(cleared.LeaseOwner, "ClearLease removes the lease owner");
        Expect.Equal<DateTimeOffset?>(null, cleared.LeaseExpiresAt, "ClearLease removes the lease expiry");
        Expect.Equal<DateTimeOffset?>(null, cleared.NextRetryAt, "ClearNextRetryAt removes the retry time");
        Expect.Equal<DateTimeOffset?>(null, cleared.CompletedAt, "ClearCompletedAt removes the completion time");
    });

    // ── Claims and leases ───────────────────────────────────────────────────────────────────────

    public Task Claim_leases_a_pending_instance_and_marks_it_running() =>
        Run(nameof(Claim_leases_a_pending_instance_and_marks_it_running), async (ctx, store) =>
        {
            WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
            string replica = ctx.NewId("replica");
            DateTimeOffset now = ctx.Clock.GetUtcNow();

            IReadOnlyList<WorkflowInstance> claimed = await store.ClaimAsync(replica, 10, Lease, now, default);

            WorkflowInstance mine = Expect.NotNull(
                claimed.FirstOrDefault(i => i.InstanceId == instance.InstanceId), "a pending instance is claimable");
            Expect.Equal(InstanceStatus.Running, mine.Status, "a claimed pending instance is Running");
            Expect.Equal(replica, mine.LeaseOwner, "a claim leases the instance to the replica that claimed it");
            Expect.Equal<DateTimeOffset?>(now + Lease, mine.LeaseExpiresAt, "the lease runs for the requested duration from the claim's now");
            Expect.Equal<DateTimeOffset?>(now, mine.StartedAt, "a first claim records when the instance started");

            WorkflowInstance stored = await GetAsync(store, instance.InstanceId);
            Expect.Equal(replica, stored.LeaseOwner, "the lease is stored, not only returned");
            Expect.Equal(InstanceStatus.Running, stored.Status, "the claimed state is stored, not only returned");
        });

    public Task A_leased_instance_is_not_claimed_again() => Run(nameof(A_leased_instance_is_not_claimed_again), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
        await store.ClaimAsync(ctx.NewId("replica"), 10, Lease, ctx.Clock.GetUtcNow(), default);

        IReadOnlyList<WorkflowInstance> second = await store.ClaimAsync(ctx.NewId("replica"), 10, Lease, ctx.Clock.GetUtcNow(), default);

        Expect.DoesNotContain(second.Select(i => i.InstanceId), instance.InstanceId,
            "an instance whose lease is held is not claimed by another replica");
    });

    public Task An_expired_lease_is_reclaimed_as_an_orphan() => Run(nameof(An_expired_lease_is_reclaimed_as_an_orphan), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
        await store.ClaimAsync(ctx.NewId("replica"), 10, Lease, ctx.Clock.GetUtcNow(), default);

        ctx.Clock.Advance(TimeSpan.FromSeconds(90));
        string rescuer = ctx.NewId("replica");

        IReadOnlyList<WorkflowInstance> recovered = await store.ClaimAsync(rescuer, 10, Lease, ctx.Clock.GetUtcNow(), default);

        WorkflowInstance mine = Expect.NotNull(recovered.FirstOrDefault(i => i.InstanceId == instance.InstanceId),
            "an instance whose lease expired is claimable again");
        Expect.Equal(rescuer, mine.LeaseOwner, "the orphan is leased to the replica that recovered it");
    });

    public Task Retry_scheduled_instances_are_claimed_only_once_due() => Run(nameof(Retry_scheduled_instances_are_claimed_only_once_due), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
        await store.UpdateAsync(instance.InstanceId, m =>
        {
            m.Status = InstanceStatus.RetryScheduled;
            m.NextRetryAt = ctx.Clock.GetUtcNow().AddMinutes(5);
        }, default);

        Expect.DoesNotContain(
            (await store.ClaimAsync(ctx.NewId("replica"), 10, Lease, ctx.Clock.GetUtcNow(), default)).Select(i => i.InstanceId),
            instance.InstanceId, "a retry is not claimed before it is due");

        ctx.Clock.Advance(TimeSpan.FromMinutes(6));

        IReadOnlyList<WorkflowInstance> claimed = await store.ClaimAsync(ctx.NewId("replica"), 10, Lease, ctx.Clock.GetUtcNow(), default);

        WorkflowInstance mine = Expect.NotNull(claimed.FirstOrDefault(i => i.InstanceId == instance.InstanceId),
            "a retry is claimed once it is due");
        Expect.Equal(InstanceStatus.Running, mine.Status, "a claimed retry is Running");
        Expect.Equal<DateTimeOffset?>(null, mine.NextRetryAt, "a claimed retry no longer carries its retry time");
    });

    public Task Dispatchable_instances_are_claimed_immediately() => Run(nameof(Dispatchable_instances_are_claimed_immediately), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
        await store.UpdateAsync(instance.InstanceId, m =>
        {
            m.Status = InstanceStatus.Dispatchable;
            m.ClearLease = true;
        }, default);

        IReadOnlyList<WorkflowInstance> claimed = await store.ClaimAsync(ctx.NewId("replica"), 10, Lease, ctx.Clock.GetUtcNow(), default);

        WorkflowInstance mine = Expect.NotNull(claimed.FirstOrDefault(i => i.InstanceId == instance.InstanceId),
            "a dispatchable instance is claimable at once");
        Expect.Equal(InstanceStatus.Running, mine.Status, "a claimed dispatchable instance is Running");
    });

    public Task Terminal_instances_are_never_claimed() => Run(nameof(Terminal_instances_are_never_claimed), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
        await store.UpdateAsync(instance.InstanceId, m => m.Status = InstanceStatus.Completed, default);

        Expect.DoesNotContain(
            (await store.ClaimAsync(ctx.NewId("replica"), 10, Lease, ctx.Clock.GetUtcNow(), default)).Select(i => i.InstanceId),
            instance.InstanceId, "a terminal instance is never claimed");
    });

    public Task Claim_respects_the_batch_maximum() => Run(nameof(Claim_respects_the_batch_maximum), async (ctx, store) =>
    {
        for (int i = 0; i < 20; i++)
        {
            await CreateAsync(ctx, store, Request(ctx));
        }

        IReadOnlyList<WorkflowInstance> claimed = await store.ClaimAsync(ctx.NewId("replica"), 5, Lease, ctx.Clock.GetUtcNow(), default);

        Expect.Equal(5, claimed.Count, "a claim takes up to its maximum and no more");
    });

    public Task Claim_with_no_capacity_returns_nothing() => Run(nameof(Claim_with_no_capacity_returns_nothing), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));

        IReadOnlyList<WorkflowInstance> claimed = await store.ClaimAsync(ctx.NewId("replica"), 0, Lease, ctx.Clock.GetUtcNow(), default);

        Expect.Equal(0, claimed.Count, "a claim with no capacity takes nothing");
        Expect.Null((await GetAsync(store, instance.InstanceId)).LeaseOwner, "a claim with no capacity leases nothing");
    });

    /// <remarks>
    /// Replicas race for the same rows. A claim that reads candidates and then writes them without
    /// a lock or a conditional update hands one instance to two replicas, or throws at the loser;
    /// a claim that skips rows another claimer holds, and lets the loser come back for more, does
    /// neither.
    /// </remarks>
    public Task Claim_hands_each_instance_to_one_replica_under_contention() =>
        Run(nameof(Claim_hands_each_instance_to_one_replica_under_contention), async (ctx, store) =>
        {
            const int total = 150;
            const int replicas = 8;

            var mine = new List<string>(total);
            for (int i = 0; i < total; i++)
            {
                mine.Add((await CreateAsync(ctx, store, Request(ctx))).InstanceId);
            }

            DateTimeOffset now = ctx.Clock.GetUtcNow();
            string[] replicaIds = [.. Enumerable.Range(0, replicas).Select(_ => ctx.NewId("replica"))];

            Contention.Outcome<List<string>>[] outcomes = await Contention.RaceAsync(replicas, async r =>
            {
                var taken = new List<string>();
                for (int round = 0; round < 1_000; round++)
                {
                    IReadOnlyList<WorkflowInstance> batch = await store.ClaimAsync(replicaIds[r], 10, Lease, now, default);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    taken.AddRange(batch.Select(i => i.InstanceId));
                }

                return taken;
            });

            Contention.NoneThrew(outcomes, "a replica that loses the race for an instance claims fewer, rather than failing");

            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Contention.Outcome<List<string>> outcome in outcomes)
            {
                foreach (string id in outcome.Result!)
                {
                    if (!owners.TryAdd(id, replicaIds[outcome.Contender]))
                    {
                        throw new StoreContractException(
                            "no instance is leased to two replicas at once",
                            $"'{id}' was claimed by {owners[id]} and by {replicaIds[outcome.Contender]}");
                    }
                }
            }

            string[] stranded = [.. mine.Where(id => !owners.ContainsKey(id))];
            Expect.That(stranded.Length == 0, "contention delays a claim but never strands an instance",
                $"{stranded.Length} of {total} instances were never claimed");

            foreach (string id in mine)
            {
                Expect.Equal(owners[id], (await GetAsync(store, id)).LeaseOwner,
                    "the stored lease names the replica the instance was handed to");
            }
        });

    public Task Lease_renewal_extends_only_for_the_current_owner() => Run(nameof(Lease_renewal_extends_only_for_the_current_owner), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
        string owner = ctx.NewId("replica");
        await store.ClaimAsync(owner, 10, Lease, ctx.Clock.GetUtcNow(), default);

        ctx.Clock.Advance(TimeSpan.FromSeconds(20));

        Expect.That(await store.RenewLeaseAsync(instance.InstanceId, owner, Lease, default),
            "the lease owner can renew its lease");
        Expect.That(!await store.RenewLeaseAsync(instance.InstanceId, ctx.NewId("replica"), Lease, default),
            "a replica that does not hold the lease cannot renew it");

        WorkflowInstance read = await GetAsync(store, instance.InstanceId);
        Expect.Equal(owner, read.LeaseOwner, "a refused renewal leaves the lease with its owner");
        Expect.Equal<DateTimeOffset?>(ctx.Clock.GetUtcNow() + Lease, read.LeaseExpiresAt,
            "a renewal runs the lease from the store clock's now");
    });

    public Task Renewing_an_unknown_instance_fails() => Run(nameof(Renewing_an_unknown_instance_fails), async (ctx, store)
        => Expect.That(!await store.RenewLeaseAsync(ctx.NewId("inst"), ctx.NewId("replica"), Lease, default),
            "renewing a lease on an instance that does not exist reports false"));

    public Task Release_clears_the_lease_for_the_owner_only() => Run(nameof(Release_clears_the_lease_for_the_owner_only), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
        string owner = ctx.NewId("replica");
        await store.ClaimAsync(owner, 10, Lease, ctx.Clock.GetUtcNow(), default);

        await store.ReleaseLeaseAsync(instance.InstanceId, ctx.NewId("replica"), default);
        Expect.Equal(owner, (await GetAsync(store, instance.InstanceId)).LeaseOwner,
            "a replica that does not hold the lease cannot release it");

        await store.ReleaseLeaseAsync(instance.InstanceId, owner, default);
        WorkflowInstance released = await GetAsync(store, instance.InstanceId);
        Expect.Null(released.LeaseOwner, "the owner's release clears the lease owner");
        Expect.Equal<DateTimeOffset?>(null, released.LeaseExpiresAt, "the owner's release clears the lease expiry");
    });

    /// <remarks>Explicit release turns a lease-length orphan recovery into an instant handoff on a planned restart.</remarks>
    public Task Released_instances_are_immediately_reclaimable() => Run(nameof(Released_instances_are_immediately_reclaimable), async (ctx, store) =>
    {
        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx));
        string owner = ctx.NewId("replica");
        await store.ClaimAsync(owner, 10, Lease, ctx.Clock.GetUtcNow(), default);
        await store.ReleaseLeaseAsync(instance.InstanceId, owner, default);

        IReadOnlyList<WorkflowInstance> claimed = await store.ClaimAsync(ctx.NewId("replica"), 10, Lease, ctx.Clock.GetUtcNow(), default);

        Expect.Contains(claimed.Select(i => i.InstanceId), instance.InstanceId,
            "a released instance is claimable straight away, without waiting out the lease");
    });

    // ── Queries ─────────────────────────────────────────────────────────────────────────────────

    public Task Query_filters_by_status_workflow_tenant_and_correlation() =>
        Run(nameof(Query_filters_by_status_workflow_tenant_and_correlation), async (ctx, store) =>
        {
            string t1 = ctx.Tenant;
            string t2 = ctx.NewTenant();
            string alpha = ctx.NewId("alpha");
            string beta = ctx.NewId("beta");
            string correlation = ctx.NewId("corr");

            await CreateAsync(ctx, store, Request(ctx, t1, alpha) with { CorrelationId = correlation });
            WorkflowInstance b = await CreateAsync(ctx, store, Request(ctx, t1, beta));
            await CreateAsync(ctx, store, Request(ctx, t2, alpha));
            await store.UpdateAsync(b.InstanceId, m => m.Status = InstanceStatus.Completed, default);

            Expect.Equal(2, (await store.QueryAsync(new InstanceQuery { TenantId = t1 }, default)).Total,
                "a tenant filter returns that tenant's instances");
            Expect.Equal(2, (await store.QueryAsync(new InstanceQuery { WorkflowName = alpha }, default)).Total,
                "a workflow filter returns that workflow's instances across tenants");
            Expect.Equal(1, (await store.QueryAsync(new InstanceQuery { CorrelationId = correlation }, default)).Total,
                "a correlation filter returns the instances carrying that correlation id");
            Expect.Equal(1, (await store.QueryAsync(
                    new InstanceQuery { TenantId = t1, Statuses = [InstanceStatus.Completed] }, default)).Total,
                "a status filter returns the instances in those states");
            Expect.Equal(1, (await store.QueryAsync(new InstanceQuery { TenantId = t2, WorkflowName = alpha }, default)).Total,
                "filters combine");
        });

    public Task Query_filters_by_creation_window() => Run(nameof(Query_filters_by_creation_window), async (ctx, store) =>
    {
        WorkflowInstance old = await CreateAsync(ctx, store, Request(ctx));
        ctx.Clock.Advance(TimeSpan.FromHours(2));
        DateTimeOffset boundary = ctx.Clock.GetUtcNow();
        WorkflowInstance recent = await CreateAsync(ctx, store, Request(ctx));

        Expect.Sequence([recent.InstanceId],
            (await store.QueryAsync(new InstanceQuery { TenantId = ctx.Tenant, CreatedAfter = boundary }, default)).Items.Select(i => i.InstanceId),
            "CreatedAfter keeps instances created at or after it");
        Expect.Sequence([old.InstanceId],
            (await store.QueryAsync(new InstanceQuery { TenantId = ctx.Tenant, CreatedBefore = boundary.AddSeconds(-1) }, default)).Items.Select(i => i.InstanceId),
            "CreatedBefore keeps instances created at or before it");
    });

    public Task Query_paginates_newest_first() => Run(nameof(Query_paginates_newest_first), async (ctx, store) =>
    {
        var ids = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            ids.Add((await CreateAsync(ctx, store, Request(ctx))).InstanceId);
            ctx.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        Page<WorkflowInstance> first = await store.QueryAsync(new InstanceQuery { TenantId = ctx.Tenant, Limit = 2, Offset = 0 }, default);
        Page<WorkflowInstance> second = await store.QueryAsync(new InstanceQuery { TenantId = ctx.Tenant, Limit = 2, Offset = 2 }, default);

        Expect.Equal(5, first.Total, "Total counts every match, not the page");
        Expect.Sequence([ids[4], ids[3]], first.Items.Select(i => i.InstanceId), "the first page holds the newest instances");
        Expect.Sequence([ids[2], ids[1]], second.Items.Select(i => i.InstanceId), "Offset skips the pages before it");
    });

    // ── Payloads ────────────────────────────────────────────────────────────────────────────────

    public Task Large_and_non_ASCII_payloads_round_trip() => Run(nameof(Large_and_non_ASCII_payloads_round_trip), async (ctx, store) =>
    {
        string context = Samples.LargeJson("context");
        string result = Samples.LargeJson("result");

        WorkflowInstance instance = await CreateAsync(ctx, store, Request(ctx) with { ContextJson = context });
        await store.UpdateAsync(instance.InstanceId, m =>
        {
            m.ResultJson = result;
            m.TerminalReason = Samples.NonAscii;
        }, default);

        WorkflowInstance read = await GetAsync(store, instance.InstanceId);
        Expect.Json(context, read.ContextJson, "a large, non-ASCII context reads back unchanged");
        Expect.Json(result, read.ResultJson, "a large, non-ASCII result reads back unchanged");
        Expect.Equal(Samples.NonAscii, read.TerminalReason, "non-ASCII text reads back unchanged");
    });

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────────

    private Task Run(string name, Func<StoreContractContext, IInstanceStore, Task> body)
        => ContractRunner.RunAsync(name, _create, body);

    private static CreateInstanceRequest Request(StoreContractContext ctx, string? tenant = null, string? workflow = null) => new()
    {
        InstanceId = ctx.NewId("inst"),
        TenantId = tenant ?? ctx.Tenant,
        WorkflowName = workflow ?? Workflow,
        WorkflowVersion = "1.0.0"
    };

    private static async Task<WorkflowInstance> CreateAsync(StoreContractContext ctx, IInstanceStore store, CreateInstanceRequest request)
    {
        Retire(ctx, store, request.InstanceId);
        return await store.CreateAsync(request, default);
    }

    private static async Task<WorkflowInstance> GetAsync(IInstanceStore store, string instanceId)
        => Expect.NotNull(await store.GetAsync(instanceId, default), $"instance '{instanceId}' can be read back");

    /// <summary>
    /// Leaves nothing claimable behind. Claims read the whole store, so an instance a case leaves
    /// Pending would be claimed by the next case that shares the database.
    /// </summary>
    private static void Retire(StoreContractContext ctx, IInstanceStore store, string instanceId)
        => ctx.OnDispose(async () => await store.UpdateAsync(instanceId, m =>
        {
            m.Status = InstanceStatus.Cancelled;
            m.ClearLease = true;
            m.ClearNextRetryAt = true;
        }, default));
}
