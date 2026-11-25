using Prism.Flowline.Abstractions;
using Prism.Flowline.Core;

namespace Prism.Flowline.Testing;

/// <summary>What every <see cref="IDomainEventSubscriptionStore"/> must do, whatever it is built on.</summary>
/// <remarks>
/// <para>
/// Matching is <see cref="DomainSubscriptionMatch"/>'s, so every store agrees on what a subscription
/// means; what a store adds is durability and one compare-and-set. <see cref="IDomainEventSubscriptionStore.TryDeliverAsync"/>
/// is the exactly-once boundary for resuming a parked instance: transports deliver at least once
/// and replicas race, so only one delivery to a wait may win.
/// </para>
/// <para>
/// Every topic a case uses starts with a segment of its own, so matches never reach another case's
/// subscriptions in a shared database.
/// </para>
/// </remarks>
public sealed class DomainEventSubscriptionStoreContract
{
    private readonly Func<StoreContractContext, ValueTask<IDomainEventSubscriptionStore>> _create;

    public DomainEventSubscriptionStoreContract(Func<StoreContractContext, ValueTask<IDomainEventSubscriptionStore>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
        Cases = ContractCases.Discover(this);
    }

    /// <summary>Every case, by name, for a runner that would rather enumerate than list.</summary>
    public IReadOnlyList<ContractCase> Cases { get; }

    public Task Matches_on_topic_tenant_and_correlation() => Run(nameof(Matches_on_topic_tenant_and_correlation), async (ctx, store) =>
    {
        string root = Topic(ctx);
        DomainEventSubscription registered = await RegisterAsync(ctx, store,
            Wait(ctx, $"{root}.#", correlationKey: "ORD-1") with { TenantId = ctx.Tenant });

        Expect.Sequence([registered.SubscriptionId],
            (await store.MatchAsync(Message($"{root}.placed", "ORD-1", ctx.Tenant), default)).Select(s => s.SubscriptionId),
            "a message on the topic, for the tenant, with the correlation key, matches");
        Expect.Equal(0, (await store.MatchAsync(Message($"{root}.placed", "ORD-2", ctx.Tenant), default)).Count,
            "a different correlation key does not match");
        Expect.Equal(0, (await store.MatchAsync(Message($"{root}.placed", "ORD-1", ctx.NewTenant()), default)).Count,
            "another tenant's message does not match: a cross-tenant delivery is a security defect");
        Expect.Equal(0, (await store.MatchAsync(Message($"{Topic(ctx)}.placed", "ORD-1", ctx.Tenant), default)).Count,
            "a message on another topic does not match");
    });

    public Task A_subscription_without_a_tenant_sees_every_tenant() =>
        Run(nameof(A_subscription_without_a_tenant_sees_every_tenant), async (ctx, store) =>
        {
            string root = Topic(ctx);
            await RegisterAsync(ctx, store, Wait(ctx, $"{root}.#"));

            Expect.Equal(1, (await store.MatchAsync(Message($"{root}.placed", tenantId: ctx.Tenant), default)).Count,
                "a subscription with no tenant is a host-level listener and matches any tenant");
        });

    public Task Scope_narrows_matching() => Run(nameof(Scope_narrows_matching), async (ctx, store) =>
    {
        string root = Topic(ctx);
        await RegisterAsync(ctx, store, Wait(ctx, $"{root}.#") with { Scope = DeliveryScope.Distributed });

        Expect.Equal(0, (await store.MatchAsync(Message($"{root}.placed"), default)).Count,
            "a subscription for Distributed messages does not match a Local one");
        Expect.Equal(1, (await store.MatchAsync(Message($"{root}.placed") with { Scope = DeliveryScope.Distributed }, default)).Count,
            "a subscription for Distributed messages matches a Distributed one");
    });

    /// <remarks>
    /// Sixteen deliveries of one message to one wait, as a redelivering transport and several
    /// replicas produce. An update that does not condition on the wait still being open lets more
    /// than one through, and the instance resumes twice.
    /// </remarks>
    public Task Only_one_caller_wins_a_delivery() => Run(nameof(Only_one_caller_wins_a_delivery), async (ctx, store) =>
    {
        DomainEventSubscription subscription = await RegisterAsync(ctx, store, Wait(ctx, $"{Topic(ctx)}.#"));
        DomainEventMessage[] messages = [.. Enumerable.Range(0, 16).Select(i => Message($"x.{i}") with { PayloadJson = $$"""{"n":{{i}}}""" })];

        Contention.Outcome<bool>[] outcomes = await Contention.RaceAsync(messages.Length,
            i => store.TryDeliverAsync(subscription.SubscriptionId, messages[i], default).AsTask());

        Contention.NoneThrew(outcomes, "a delivery that loses the race is told false, not thrown at");
        Contention.Outcome<bool>[] won = [.. outcomes.Where(o => o.Result)];
        Expect.Equal(1, won.Length, "exactly one delivery to a wait wins");

        DomainEventSubscription stored = Expect.NotNull(
            await store.FindWaitAsync(subscription.InstanceId!, subscription.ExecutorId!, default), "the wait can be found");
        Expect.Equal(messages[won[0].Contender].MessageId, stored.DeliveredMessageId, "the wait records the winning message");
        Expect.Json(messages[won[0].Contender].PayloadJson, stored.DeliveredPayloadJson, "the wait records the winning payload");
    });

    public Task A_delivered_wait_stops_matching() => Run(nameof(A_delivered_wait_stops_matching), async (ctx, store) =>
    {
        string root = Topic(ctx);
        DomainEventSubscription subscription = await RegisterAsync(ctx, store, Wait(ctx, $"{root}.#"));

        await store.TryDeliverAsync(subscription.SubscriptionId, Message($"{root}.placed"), default);

        Expect.Equal(0, (await store.MatchAsync(Message($"{root}.placed"), default)).Count,
            "a wait that has been delivered to matches nothing more: a parked instance resumes once");
    });

    /// <remarks>A resumed instance replays its executor, which registers the wait again; it must find its payload.</remarks>
    public Task Re_registering_the_same_wait_preserves_a_recorded_delivery() =>
        Run(nameof(Re_registering_the_same_wait_preserves_a_recorded_delivery), async (ctx, store) =>
        {
            string root = Topic(ctx);
            DomainEventSubscription wait = Wait(ctx, $"{root}.#");
            DomainEventSubscription first = await RegisterAsync(ctx, store, wait);
            await store.TryDeliverAsync(first.SubscriptionId, Message($"{root}.placed"), default);

            DomainEventSubscription second = await RegisterAsync(ctx, store, wait with { SubscriptionId = ctx.NewId("sub") });

            Expect.Equal(first.SubscriptionId, second.SubscriptionId, "registering the same wait again returns the existing one");
            Expect.That(second.DeliveredPayloadJson is not null, "the existing wait keeps the delivery recorded against it");
        });

    public Task Find_wait_returns_the_wait_for_one_executor() => Run(nameof(Find_wait_returns_the_wait_for_one_executor), async (ctx, store) =>
    {
        string instance = ctx.NewId("inst");
        DomainEventSubscription a = await RegisterAsync(ctx, store, Wait(ctx, $"{Topic(ctx)}.#", instance, "a"));

        Expect.Equal(a.SubscriptionId, (await store.FindWaitAsync(instance, "a", default))?.SubscriptionId,
            "a wait is found by its instance and executor");
        Expect.Null(await store.FindWaitAsync(instance, "b", default), "another executor of the instance has no wait");
    });

    public Task Claims_expired_waits_once() => Run(nameof(Claims_expired_waits_once), async (ctx, store) =>
    {
        DateTimeOffset now = ctx.Clock.GetUtcNow();
        DomainEventSubscription expired = await RegisterAsync(ctx, store, Wait(ctx, $"{Topic(ctx)}.#", expiresAt: now.AddMinutes(-1)));

        IReadOnlyList<DomainEventSubscription> first = await store.ClaimExpiredAsync(now, 100, default);
        DomainEventSubscription claimed = Expect.NotNull(first.FirstOrDefault(s => s.SubscriptionId == expired.SubscriptionId),
            "a wait past its expiry is claimed");
        Expect.That(claimed.Expired, "a claimed wait is marked expired");

        Expect.DoesNotContain((await store.ClaimExpiredAsync(now, 100, default)).Select(s => s.SubscriptionId),
            expired.SubscriptionId, "a second sweeper does not claim the same wait and expire the instance twice");
    });

    public Task Does_not_claim_a_wait_that_has_not_expired() => Run(nameof(Does_not_claim_a_wait_that_has_not_expired), async (ctx, store) =>
    {
        DateTimeOffset now = ctx.Clock.GetUtcNow();
        DomainEventSubscription open = await RegisterAsync(ctx, store, Wait(ctx, $"{Topic(ctx)}.#", expiresAt: now.AddMinutes(5)));

        Expect.DoesNotContain((await store.ClaimExpiredAsync(now, 100, default)).Select(s => s.SubscriptionId),
            open.SubscriptionId, "a wait whose window is still open is not claimed");
    });

    public Task Removes_every_wait_belonging_to_an_instance() => Run(nameof(Removes_every_wait_belonging_to_an_instance), async (ctx, store) =>
    {
        string root = Topic(ctx);
        string finished = ctx.NewId("inst");
        string running = ctx.NewId("inst");
        await RegisterAsync(ctx, store, Wait(ctx, $"{root}.#", finished, "a"));
        await RegisterAsync(ctx, store, Wait(ctx, $"{root}.#", finished, "b"));
        await RegisterAsync(ctx, store, Wait(ctx, $"{root}.#", running, "a"));

        await store.RemoveForInstanceAsync(finished, default);

        Expect.Equal(0, (await store.QueryAsync(new DomainSubscriptionQuery { InstanceId = finished }, default)).Count,
            "a terminated instance's waits are all removed");
        Expect.Equal(1, (await store.QueryAsync(new DomainSubscriptionQuery { InstanceId = running }, default)).Count,
            "another instance's waits are left alone");
    });

    public Task A_wait_must_name_its_instance_and_executor() => Run(nameof(A_wait_must_name_its_instance_and_executor), async (ctx, store)
        => await Expect.ThrowsAsync<ArgumentException>(() => store.RegisterAsync(new DomainEventSubscription
        {
            SubscriptionId = ctx.NewId("sub"),
            Kind = DomainSubscriptionKind.Wait,
            TopicFilter = $"{Topic(ctx)}.#"
        }, default).AsTask(), "a wait that names no instance and executor is refused, since nothing could ever resume"));

    public Task Large_and_non_ASCII_payloads_round_trip() => Run(nameof(Large_and_non_ASCII_payloads_round_trip), async (ctx, store) =>
    {
        string root = Topic(ctx);
        DomainEventSubscription subscription = await RegisterAsync(ctx, store,
            Wait(ctx, $"{root}.#", correlationKey: "Bestellung-ü-注文"));
        DomainEventMessage message = Message($"{root}.placed", "Bestellung-ü-注文") with { PayloadJson = Samples.LargeJson("delivered") };

        Expect.That(await store.TryDeliverAsync(subscription.SubscriptionId, message, default), "the delivery is recorded");

        DomainEventSubscription stored = Expect.NotNull(
            await store.FindWaitAsync(subscription.InstanceId!, subscription.ExecutorId!, default), "the wait can be found");
        Expect.Equal(subscription.CorrelationKey, stored.CorrelationKey, "a non-ASCII correlation key reads back unchanged");
        Expect.Json(message.PayloadJson, stored.DeliveredPayloadJson, "a large, non-ASCII payload reads back unchanged");
    });

    private Task Run(string name, Func<StoreContractContext, IDomainEventSubscriptionStore, Task> body)
        => ContractRunner.RunAsync(name, _create, body);

    /// <summary>A first topic segment no other case uses.</summary>
    private static string Topic(StoreContractContext ctx) => ctx.NewId("topic");

    private static DomainEventSubscription Wait(
        StoreContractContext ctx, string topicFilter, string? instanceId = null, string executorId = "wait",
        string? correlationKey = null, DateTimeOffset? expiresAt = null) => new()
    {
        SubscriptionId = ctx.NewId("sub"),
        Kind = DomainSubscriptionKind.Wait,
        TopicFilter = topicFilter,
        InstanceId = instanceId ?? ctx.NewId("inst"),
        ExecutorId = executorId,
        CorrelationKey = correlationKey,
        ExpiresAt = expiresAt,
        CreatedAt = ctx.Clock.GetUtcNow()
    };

    private static DomainEventMessage Message(string topic, string? correlationKey = null, string? tenantId = null) => new()
    {
        MessageId = $"msg-{Guid.NewGuid():N}",
        Topic = topic,
        PayloadJson = """{"orderId":"ORD-1"}""",
        CorrelationKey = correlationKey,
        TenantId = tenantId
    };

    private static async Task<DomainEventSubscription> RegisterAsync(
        StoreContractContext ctx, IDomainEventSubscriptionStore store, DomainEventSubscription subscription)
    {
        // An expiry sweep reads every wait in the store; one this case leaves open would be claimed
        // by the next case that shares the database.
        if (subscription.InstanceId is { } instanceId)
        {
            ctx.OnDispose(async () => await store.RemoveForInstanceAsync(instanceId, default));
        }

        return await store.RegisterAsync(subscription, default);
    }
}
