using Prism.Flowline.Abstractions;
using Prism.Flowline.Core;

namespace Prism.Flowline.Testing;

/// <summary>What every <see cref="IGatePolicyStore"/> must do, whatever it is built on.</summary>
/// <remarks>
/// A policy is written at one scope — an instance, a tenant, or host-wide when the tenant is null —
/// and found by falling through those scopes in that order. Listing never merges them: each scope
/// reports only what was written at it, so a tenant's view cannot be mistaken for the host's.
/// </remarks>
public sealed class GatePolicyStoreContract
{
    private const string Version = "1.0.0";

    private readonly Func<StoreContractContext, ValueTask<IGatePolicyStore>> _create;

    public GatePolicyStoreContract(Func<StoreContractContext, ValueTask<IGatePolicyStore>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
        Cases = ContractCases.Discover(this);
    }

    /// <summary>Every case, by name, for a runner that would rather enumerate than list.</summary>
    public IReadOnlyList<ContractCase> Cases { get; }

    public Task A_policy_applies_only_to_its_workflow_version_and_executor() =>
        Run(nameof(A_policy_applies_only_to_its_workflow_version_and_executor), async (ctx, store) =>
        {
            string workflow = ctx.NewId("wf");
            await store.SetAsync(null, workflow, Version, "pay", Gate("policy"), default);

            Expect.Equal("policy", (await store.FindAsync(null, workflow, Version, "pay", null, default))?.Reason,
                "a policy is found at the scope it was written");
            Expect.Null(await store.FindAsync(null, workflow, "2.0.0", "pay", null, default),
                "a policy belongs to the version it was written against");
            Expect.Null(await store.FindAsync(null, workflow, Version, "other", null, default),
                "a policy belongs to the executor it was written for");
            Expect.Null(await store.FindAsync(null, ctx.NewId("wf"), Version, "pay", null, default),
                "a policy belongs to the workflow it was written for");
        });

    public Task A_tenant_without_its_own_policy_falls_back_to_the_host_wide_one() =>
        Run(nameof(A_tenant_without_its_own_policy_falls_back_to_the_host_wide_one), async (ctx, store) =>
        {
            string workflow = ctx.NewId("wf");
            await store.SetAsync(null, workflow, Version, "pay", Gate("host"), default);

            Expect.Equal("host", (await store.FindAsync(ctx.Tenant, workflow, Version, "pay", null, default))?.Reason,
                "a tenant with no policy of its own inherits the host-wide one");
        });

    public Task A_tenants_own_policy_wins_over_the_host_wide_one() =>
        Run(nameof(A_tenants_own_policy_wins_over_the_host_wide_one), async (ctx, store) =>
        {
            string workflow = ctx.NewId("wf");
            await store.SetAsync(null, workflow, Version, "pay", Gate("host"), default);
            await store.SetAsync(ctx.Tenant, workflow, Version, "pay", Gate("tenant", ExecutionMode.Autonomous), default);

            ApprovalGate? found = await store.FindAsync(ctx.Tenant, workflow, Version, "pay", null, default);

            Expect.Equal("tenant", found?.Reason, "the more specific scope wins");
            Expect.Equal(ExecutionMode.Autonomous, found?.Mode, "the tenant's policy is returned whole");
        });

    public Task Another_tenant_is_unaffected_by_a_tenant_specific_policy() =>
        Run(nameof(Another_tenant_is_unaffected_by_a_tenant_specific_policy), async (ctx, store) =>
        {
            string workflow = ctx.NewId("wf");
            await store.SetAsync(ctx.Tenant, workflow, Version, "pay", Gate("tenant"), default);

            Expect.Null(await store.FindAsync(ctx.NewTenant(), workflow, Version, "pay", null, default),
                "one tenant's configuration is not another's");
            Expect.Null(await store.FindAsync(null, workflow, Version, "pay", null, default),
                "a tenant's configuration is not the host's");
        });

    public Task Resolution_runs_instance_then_tenant_then_host_wide() =>
        Run(nameof(Resolution_runs_instance_then_tenant_then_host_wide), async (ctx, store) =>
        {
            string workflow = ctx.NewId("wf");
            string instance = ctx.NewId("inst");
            await store.SetAsync(null, workflow, Version, "pay", Gate("host"), default);
            await store.SetAsync(ctx.Tenant, workflow, Version, "pay", Gate("tenant"), default);
            await store.SetInstanceOverrideAsync(instance, "pay", Gate("instance"), default);

            Expect.Equal("instance", (await store.FindAsync(ctx.Tenant, workflow, Version, "pay", instance, default))?.Reason,
                "an instance override outranks every policy scope");
            Expect.Equal("tenant", (await store.FindAsync(ctx.Tenant, workflow, Version, "pay", ctx.NewId("inst"), default))?.Reason,
                "an override applies to its own instance only");
            Expect.Equal("host", (await store.FindAsync(ctx.NewTenant(), workflow, Version, "pay", null, default))?.Reason,
                "with no override and no tenant policy, the host-wide policy stands");
            Expect.Null(await store.FindAsync(ctx.NewTenant(), workflow, Version, "other", null, default),
                "with nothing at any scope, nothing is found and the definition's gate stands");
        });

    public Task List_returns_one_scope_and_never_merges_another() =>
        Run(nameof(List_returns_one_scope_and_never_merges_another), async (ctx, store) =>
        {
            string workflow = ctx.NewId("wf");
            await store.SetAsync(null, workflow, Version, "pay", Gate("host"), default);
            await store.SetAsync(null, workflow, Version, "notify", Gate("host"), default);
            await store.SetAsync(ctx.Tenant, workflow, Version, "pay", Gate("tenant"), default);

            IReadOnlyDictionary<string, ApprovalGate> tenant = await store.ListAsync(ctx.Tenant, workflow, Version, default);
            IReadOnlyDictionary<string, ApprovalGate> host = await store.ListAsync(null, workflow, Version, default);

            Expect.Sequence(["pay"], tenant.Keys.Order(), "a tenant's list holds what was written for that tenant only");
            Expect.Equal("tenant", tenant["pay"].Reason, "a tenant's list carries the tenant's policy");
            Expect.Sequence(["notify", "pay"], host.Keys.Order(), "the host-wide list holds what was written host-wide only");
            Expect.Equal("host", host["pay"].Reason, "the host-wide list carries the host-wide policy");
            Expect.Equal(0, (await store.ListAsync(ctx.NewTenant(), workflow, Version, default)).Count,
                "a tenant that wrote nothing lists nothing, even with host-wide policies in place");
            Expect.Equal(0, (await store.ListAsync(ctx.Tenant, workflow, "2.0.0", default)).Count,
                "a list is for one version");
        });

    public Task Removal_falls_back_to_the_next_scope() => Run(nameof(Removal_falls_back_to_the_next_scope), async (ctx, store) =>
    {
        string workflow = ctx.NewId("wf");
        await store.SetAsync(null, workflow, Version, "pay", Gate("host"), default);
        await store.SetAsync(ctx.Tenant, workflow, Version, "pay", Gate("tenant"), default);

        Expect.That(await store.RemoveAsync(ctx.Tenant, workflow, Version, "pay", default),
            "removing a policy that exists reports true");
        Expect.That(!await store.RemoveAsync(ctx.Tenant, workflow, Version, "pay", default),
            "removing a policy that is already gone reports false");

        Expect.Equal("host", (await store.FindAsync(ctx.Tenant, workflow, Version, "pay", null, default))?.Reason,
            "with the tenant's policy removed, the host-wide one applies again");
    });

    public Task Setting_a_policy_again_replaces_it() => Run(nameof(Setting_a_policy_again_replaces_it), async (ctx, store) =>
    {
        string workflow = ctx.NewId("wf");
        await store.SetAsync(ctx.Tenant, workflow, Version, "pay", Gate("first"), default);
        await store.SetAsync(ctx.Tenant, workflow, Version, "pay", Gate("second"), default);

        Expect.Equal("second", (await store.FindAsync(ctx.Tenant, workflow, Version, "pay", null, default))?.Reason,
            "a second write at the same scope replaces the first");
        Expect.Equal(1, (await store.ListAsync(ctx.Tenant, workflow, Version, default)).Count,
            "a replaced policy is not listed twice");
    });

    public Task A_policy_round_trips_every_field() => Run(nameof(A_policy_round_trips_every_field), async (ctx, store) =>
    {
        string workflow = ctx.NewId("wf");
        var written = new ApprovalGate
        {
            Mode = ExecutionMode.RequireApproval,
            Reason = Samples.NonAscii,
            Assignees = ["group:finance", "user:zoë"],
            RequiredApprovers = 3,
            Expiry = TimeSpan.FromMinutes(95),
            OnExpiry = ExpiryAction.Escalate,
            EscalationAssignees = ["group:directors"],
            AllowModification = true,
            RequireSegregationOfDuties = true,
            Locked = true
        };

        await store.SetAsync(ctx.Tenant, workflow, Version, "pay", written, default);
        ApprovalGate read = Expect.NotNull(await store.FindAsync(ctx.Tenant, workflow, Version, "pay", null, default),
            "the policy can be read back");

        Expect.Equal(written.Mode, read.Mode, "the mode reads back as written");
        Expect.Equal(written.Reason, read.Reason, "a non-ASCII reason reads back unchanged");
        Expect.Sequence(written.Assignees, read.Assignees, "the assignees read back as written");
        Expect.Equal(written.RequiredApprovers, read.RequiredApprovers, "the quorum reads back as written");
        Expect.Equal(written.Expiry, read.Expiry, "the expiry reads back as written");
        Expect.Equal(written.OnExpiry, read.OnExpiry, "the expiry action reads back as written");
        Expect.Sequence(written.EscalationAssignees, read.EscalationAssignees, "the escalation assignees read back as written");
        Expect.Equal(written.AllowModification, read.AllowModification, "AllowModification reads back as written");
        Expect.Equal(written.RequireSegregationOfDuties, read.RequireSegregationOfDuties, "segregation of duties reads back as written");
        Expect.Equal(written.Locked, read.Locked, "Locked reads back as written");
    });

    private Task Run(string name, Func<StoreContractContext, IGatePolicyStore, Task> body)
        => ContractRunner.RunAsync(name, _create, body);

    private static ApprovalGate Gate(string reason, ExecutionMode mode = ExecutionMode.RequireApproval)
        => new() { Mode = mode, Reason = reason };
}
