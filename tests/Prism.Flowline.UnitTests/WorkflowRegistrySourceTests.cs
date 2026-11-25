using System.Collections.Concurrent;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Core;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace Prism.Flowline.UnitTests;

/// <summary>
/// The registry asking <see cref="IWorkflowDefinitionSource"/>s for what it was not composed with:
/// resolution order, what is held and for whom, single flight, and the checks a sourced definition
/// must pass before it is served.
/// </summary>
public class WorkflowRegistrySourceTests
{
    private static WorkflowRegistry Registry(
        IEnumerable<IWorkflowDefinition>? registered = null, int capacity = 1024, params IWorkflowDefinitionSource[] sources)
        => new(registered ?? [], sources, new RegistryOptions { ResolvedCapacity = capacity });

    [Fact]
    public async Task A_registered_definition_wins_over_a_source()
    {
        var registered = new ConfigurableWorkflow("orders", "1.0.0");
        var source = ScriptedSource.Always(new ConfigurableWorkflow("orders", "1.0.0"));
        WorkflowRegistry registry = Registry([registered], sources: source);

        (await registry.ResolveAsync("orders", "1.0.0", "t1"))!.Definition.Should().BeSameAs(registered);
        (await registry.ResolveAsync("orders", null, "t1"))!.Definition.Should().BeSameAs(registered);

        source.Calls.Should().Be(0, "a lookup a start-up definition answers never reaches a source");
    }

    [Fact]
    public async Task A_source_is_asked_when_nothing_is_registered()
    {
        var canvas = new ConfigurableWorkflow("canvas", "1.0.0");
        var source = ScriptedSource.Always(canvas);
        WorkflowRegistry registry = Registry(sources: source);

        WorkflowDescriptor? found = await registry.ResolveAsync("canvas", "1.0.0", "t1");

        found!.Definition.Should().BeSameAs(canvas);
        found.TenantId.Should().BeNull();
        source.Lookups.Should().Equal(new WorkflowLookup("canvas", "1.0.0", "t1"));
    }

    [Fact]
    public async Task Sources_are_asked_in_registration_order_and_the_first_answer_wins()
    {
        var first = new ConfigurableWorkflow("canvas", "1.0.0");
        var silent = ScriptedSource.Never();
        var answering = ScriptedSource.Always(first);
        var unreached = ScriptedSource.Always(new ConfigurableWorkflow("canvas", "1.0.0"));
        WorkflowRegistry registry = Registry(sources: [silent, answering, unreached]);

        (await registry.ResolveAsync("canvas", "1.0.0", "t1"))!.Definition.Should().BeSameAs(first);

        silent.Calls.Should().Be(1);
        answering.Calls.Should().Be(1);
        unreached.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_resolved_version_is_not_asked_for_twice()
    {
        var source = ScriptedSource.Fresh("canvas", "1.0.0");
        WorkflowRegistry registry = Registry(sources: source);

        WorkflowDescriptor? first = await registry.ResolveAsync("canvas", "1.0.0", "t1");
        WorkflowDescriptor? second = await registry.ResolveAsync("canvas", "1.0.0", "t2");

        second.Should().BeSameAs(first);
        source.Calls.Should().Be(1);
    }

    [Fact]
    public async Task The_current_version_is_asked_for_every_time_and_served_as_one_object()
    {
        // A new definition object per answer, as a source that compiles on demand would return.
        var source = ScriptedSource.Fresh("canvas", "2.0.0");
        WorkflowRegistry registry = Registry(sources: source);

        WorkflowDescriptor? first = await registry.ResolveAsync("canvas", null, "t1");
        WorkflowDescriptor? second = await registry.ResolveAsync("canvas", null, "t1");
        WorkflowDescriptor? pinned = await registry.ResolveAsync("canvas", "2.0.0", "t1");

        source.Calls.Should().Be(2, "only the source knows when \"current\" moves");
        second.Should().BeSameAs(first, "one version is one object while it is held");
        pinned.Should().BeSameAs(first);
    }

    [Fact]
    public async Task Concurrent_first_lookups_share_one_trip_to_the_source()
    {
        var source = ScriptedSource.Gated(new ConfigurableWorkflow("canvas", "1.0.0"));
        WorkflowRegistry registry = Registry(sources: source);

        Task<WorkflowDescriptor?>[] lookups = Enumerable.Range(0, 10)
            .Select(_ => registry.ResolveAsync("canvas", "1.0.0", "t1").AsTask())
            .ToArray();

        await source.Entered;
        source.Release();
        WorkflowDescriptor?[] found = await Task.WhenAll(lookups);

        source.Calls.Should().Be(1);
        found.Should().AllSatisfy(d => d.Should().BeSameAs(found[0]));
    }

    [Fact]
    public async Task A_caller_that_cancels_does_not_cancel_the_lookup_for_others()
    {
        var source = ScriptedSource.Gated(new ConfigurableWorkflow("canvas", "1.0.0"));
        WorkflowRegistry registry = Registry(sources: source);
        using var impatient = new CancellationTokenSource();

        Task<WorkflowDescriptor?> abandoned = registry.ResolveAsync("canvas", "1.0.0", "t1", impatient.Token).AsTask();
        Task<WorkflowDescriptor?> patient = registry.ResolveAsync("canvas", "1.0.0", "t1").AsTask();
        await source.Entered;

        await impatient.CancelAsync();
        Func<Task> waitAbandoned = () => abandoned;
        await waitAbandoned.Should().ThrowAsync<OperationCanceledException>();

        source.Release();
        (await patient).Should().NotBeNull();
        source.TokensSeen.Should().AllSatisfy(t => t.IsCancellationRequested.Should().BeFalse());
    }

    [Fact]
    public async Task A_private_definition_is_not_served_to_another_tenant()
    {
        var source = ScriptedSource.Always(new ConfigurableWorkflow("canvas", "1.0.0"), ownerTenant: "t1");

        // Straight from the source: the first anyone asks is the wrong tenant.
        WorkflowRegistry fresh = Registry(sources: source);
        (await fresh.ResolveAsync("canvas", "1.0.0", "t2")).Should().BeNull();
        (await fresh.ResolveAsync("canvas", null, "t2")).Should().BeNull();

        // From what is held: the owner resolved it first, and it is in memory.
        WorkflowRegistry warmed = Registry(sources: source);
        WorkflowDescriptor? owned = await warmed.ResolveAsync("canvas", "1.0.0", "t1");
        owned!.TenantId.Should().Be("t1");

        (await warmed.ResolveAsync("canvas", "1.0.0", "t2")).Should().BeNull();
        (await warmed.ResolveAsync("canvas", "1.0.0", null)).Should().BeNull("no tenant sees shared definitions only");
    }

    [Fact]
    public async Task A_private_definition_does_not_hide_the_shared_one_from_other_tenants()
    {
        var shared = new ConfigurableWorkflow("canvas", "1.0.0");
        var mine = new ConfigurableWorkflow("canvas", "1.0.0");
        var source = new ScriptedSource(lookup => lookup.TenantId == "t1"
            ? new ResolvedWorkflow(mine) { TenantId = "t1" }
            : new ResolvedWorkflow(shared));
        WorkflowRegistry registry = Registry(sources: source);

        (await registry.ResolveAsync("canvas", "1.0.0", "t1"))!.Definition.Should().BeSameAs(mine);
        (await registry.ResolveAsync("canvas", "1.0.0", "t2"))!.Definition.Should().BeSameAs(shared);
        (await registry.ResolveAsync("canvas", "1.0.0", "t3"))!.Definition.Should().BeSameAs(shared);
        (await registry.ResolveAsync("canvas", "1.0.0", "t1"))!.Definition.Should().BeSameAs(mine);

        source.Calls.Should().Be(2, "both are held, each under its own owner");
    }

    [Fact]
    public async Task The_all_tenants_scope_can_resolve_a_private_definition()
    {
        var source = ScriptedSource.Always(new ConfigurableWorkflow("canvas", "1.0.0"), ownerTenant: "t1");
        WorkflowRegistry registry = Registry(sources: source);

        (await registry.ResolveAsync("canvas", "1.0.0", Tenancy.AllTenants))!.TenantId.Should().Be("t1");

        // And from what is held, without asking again.
        (await registry.ResolveAsync("canvas", "1.0.0", Tenancy.AllTenants)).Should().NotBeNull();
        source.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_source_that_returns_another_name_is_rejected()
    {
        WorkflowRegistry registry = Registry(sources: ScriptedSource.Always(new ConfigurableWorkflow("other", "1.0.0")));

        Func<Task> resolve = () => registry.ResolveAsync("canvas", "1.0.0", "t1").AsTask();

        await resolve.Should().ThrowAsync<InvalidOperationException>().WithMessage("*asked for 'canvas'*returned 'other'*");
    }

    [Fact]
    public async Task A_source_that_returns_another_version_is_rejected()
    {
        WorkflowRegistry registry = Registry(sources: ScriptedSource.Always(new ConfigurableWorkflow("canvas", "2.0.0")));

        Func<Task> resolve = () => registry.ResolveAsync("canvas", "1.0.0", "t1").AsTask();

        await resolve.Should().ThrowAsync<InvalidOperationException>().WithMessage("*version '1.0.0'*returned version '2.0.0'*");
    }

    [Fact]
    public async Task A_source_that_returns_no_version_is_rejected()
    {
        WorkflowRegistry registry = Registry(sources: ScriptedSource.Always(new ConfigurableWorkflow("canvas", " ")));

        Func<Task> resolve = () => registry.ResolveAsync("canvas", null, "t1").AsTask();

        await resolve.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no version*");
    }

    [Fact]
    public async Task A_sourced_definition_with_an_invalid_notification_name_is_rejected()
    {
        WorkflowRegistry registry = Registry(sources: ScriptedSource.Always(new NotifyingWorkflow("canvas", "1.0.0", "bad name")));

        Func<Task> resolve = () => registry.ResolveAsync("canvas", "1.0.0", "t1").AsTask();

        await resolve.Should().ThrowAsync<InvalidOperationException>().WithMessage("*canvas*invalid notification name*");
    }

    [Fact]
    public async Task A_sourced_definition_that_declares_triggers_is_rejected()
    {
        WorkflowRegistry registry = Registry(sources: ScriptedSource.Always(new TriggeredWorkflow("canvas", "1.0.0")));

        Func<Task> resolve = () => registry.ResolveAsync("canvas", "1.0.0", "t1").AsTask();

        await resolve.Should().ThrowAsync<InvalidOperationException>().WithMessage("*triggers*start-up*");
    }

    [Fact]
    public async Task The_least_recently_used_definition_is_dropped_at_capacity()
    {
        var source = new ScriptedSource(lookup => new ResolvedWorkflow(new ConfigurableWorkflow(lookup.Name, lookup.Version!)));
        WorkflowRegistry registry = Registry(capacity: 2, sources: source);

        await registry.ResolveAsync("a", "1.0.0", "t1");
        await registry.ResolveAsync("b", "1.0.0", "t1");
        await registry.ResolveAsync("a", "1.0.0", "t1");    // a is now the most recently used
        await registry.ResolveAsync("c", "1.0.0", "t1");    // over capacity: b goes
        source.Calls.Should().Be(3);

        await registry.ResolveAsync("a", "1.0.0", "t1");
        source.Calls.Should().Be(3, "a was used recently enough to stay");

        await registry.ResolveAsync("b", "1.0.0", "t1");
        source.Calls.Should().Be(4, "b was dropped, so the source is asked again");
    }

    [Fact]
    public async Task Invalidate_forgets_one_version_one_tenant_or_everything_for_a_name()
    {
        var source = new ScriptedSource(lookup => new ResolvedWorkflow(new ConfigurableWorkflow(lookup.Name, lookup.Version!))
        {
            TenantId = lookup.TenantId == "t1" ? "t1" : null
        });
        var registered = new ConfigurableWorkflow("orders", "1.0.0");
        WorkflowRegistry registry = Registry([registered], sources: source);

        async Task HoldAllAsync()
        {
            await registry.ResolveAsync("canvas", "1.0.0", "t1");   // t1's private 1.0.0
            await registry.ResolveAsync("canvas", "2.0.0", "t2");   // shared 2.0.0
            await registry.ResolveAsync("canvas", "3.0.0", "t2");   // shared 3.0.0
        }

        await HoldAllAsync();
        source.Calls.Should().Be(3);

        registry.Invalidate("canvas", "3.0.0").Should().BeTrue();
        await HoldAllAsync();
        source.Calls.Should().Be(4, "only 3.0.0 was forgotten");

        registry.Invalidate("canvas", null, "t1").Should().BeTrue();
        await HoldAllAsync();
        source.Calls.Should().Be(5, "only t1's private definition was forgotten");

        registry.Invalidate("canvas", null, Tenancy.AllTenants).Should().BeTrue();
        await HoldAllAsync();
        source.Calls.Should().Be(7, "the all-tenants scope forgets the shared definitions only");

        registry.Invalidate("canvas").Should().BeTrue();
        registry.Invalidate("canvas").Should().BeFalse("nothing is left to forget");
        await HoldAllAsync();
        source.Calls.Should().Be(10);

        registry.Invalidate("orders").Should().BeFalse();
        (await registry.ResolveAsync("orders", "1.0.0", "t1"))!.Definition.Should().BeSameAs(registered);
    }

    [Fact]
    public async Task Resolve_sees_shared_sourced_definitions_and_never_asks_a_source()
    {
        var source = new ScriptedSource(lookup => new ResolvedWorkflow(new ConfigurableWorkflow(lookup.Name, lookup.Version!))
        {
            TenantId = lookup.Name == "private" ? "t1" : null
        });
        WorkflowRegistry registry = Registry(sources: source);

        registry.Resolve("canvas", "1.0.0").Should().BeNull();
        source.Calls.Should().Be(0);

        WorkflowDescriptor? older = await registry.ResolveAsync("canvas", "1.0.0", "t1");
        WorkflowDescriptor? newer = await registry.ResolveAsync("canvas", "1.2.0", "t1");
        await registry.ResolveAsync("private", "1.0.0", "t1");

        registry.Resolve("canvas", "1.0.0").Should().BeSameAs(older);
        registry.Resolve("canvas").Should().BeSameAs(newer, "with no version it serves the newest it holds");
        registry.Resolve("private", "1.0.0").Should().BeNull("the synchronous overload cannot say who it acts for");
        source.Calls.Should().Be(3);
    }

    [Fact]
    public async Task All_and_VersionsOf_list_start_up_definitions_only()
    {
        var registered = new ConfigurableWorkflow("orders", "1.0.0");
        WorkflowRegistry registry = Registry(
            [registered],
            sources: new ScriptedSource(lookup => new ResolvedWorkflow(new ConfigurableWorkflow(lookup.Name, lookup.Version!))));

        await registry.ResolveAsync("canvas", "1.0.0", "t1");
        await registry.ResolveAsync("orders", "2.0.0", "t1");

        registry.All.Select(d => d.Definition).Should().Equal(registered);
        registry.VersionsOf("orders").Select(d => d.Version).Should().Equal("1.0.0");
        registry.VersionsOf("canvas").Should().BeEmpty();
    }

    [Fact]
    public async Task A_source_failure_is_not_remembered()
    {
        int calls = 0;
        var source = new ScriptedSource(_ => ++calls == 1
            ? throw new TimeoutException("catalog database unreachable")
            : new ResolvedWorkflow(new ConfigurableWorkflow("canvas", "1.0.0")));
        WorkflowRegistry registry = Registry(sources: source);

        Func<Task> first = () => registry.ResolveAsync("canvas", "1.0.0", "t1").AsTask();
        await first.Should().ThrowAsync<TimeoutException>();

        (await registry.ResolveAsync("canvas", "1.0.0", "t1")).Should().NotBeNull();
        source.Calls.Should().Be(2);
    }

    /// <summary>Answers from a function and records what it was asked.</summary>
    private sealed class ScriptedSource : IWorkflowDefinitionSource
    {
        private readonly Func<WorkflowLookup, ValueTask<ResolvedWorkflow?>> _answer;
        private readonly ConcurrentQueue<WorkflowLookup> _lookups = new();
        private readonly ConcurrentQueue<CancellationToken> _tokens = new();

        public ScriptedSource(Func<WorkflowLookup, ResolvedWorkflow?> answer)
            : this(lookup => ValueTask.FromResult(answer(lookup)))
        {
        }

        private ScriptedSource(Func<WorkflowLookup, ValueTask<ResolvedWorkflow?>> answer) => _answer = answer;

        private TaskCompletionSource? _entered;
        private TaskCompletionSource? _gate;

        public int Calls => _lookups.Count;
        public IReadOnlyList<WorkflowLookup> Lookups => [.. _lookups];
        public IReadOnlyList<CancellationToken> TokensSeen => [.. _tokens];
        public Task Entered => _entered!.Task;

        public static ScriptedSource Never() => new(_ => null);

        public static ScriptedSource Always(IWorkflowDefinition definition, string? ownerTenant = null)
            => new(_ => new ResolvedWorkflow(definition) { TenantId = ownerTenant });

        /// <summary>A new object for every answer, like a source that compiles on demand.</summary>
        public static ScriptedSource Fresh(string name, string version)
            => new(_ => new ResolvedWorkflow(new ConfigurableWorkflow(name, version)));

        /// <summary>Holds every lookup until <see cref="Release"/>.</summary>
        public static ScriptedSource Gated(IWorkflowDefinition definition)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            return new ScriptedSource(async _ =>
            {
                entered.TrySetResult();
                await gate.Task;
                return new ResolvedWorkflow(definition);
            })
            {
                _entered = entered,
                _gate = gate
            };
        }

        public void Release() => _gate!.TrySetResult();

        public ValueTask<ResolvedWorkflow?> FindAsync(WorkflowLookup lookup, CancellationToken cancellationToken)
        {
            _lookups.Enqueue(lookup);
            _tokens.Enqueue(cancellationToken);
            return _answer(lookup);
        }
    }

    private sealed class NotifyingWorkflow : IWorkflowDefinition<SampleContext, SampleResult>, INotifyingWorkflow
    {
        public NotifyingWorkflow(string name, string version, params string[] emits)
        {
            Name = name;
            Version = version;
            Notifications = new NotificationPolicy { Emits = emits };
        }

        public string Name { get; }
        public string Version { get; }
        public NotificationPolicy Notifications { get; }

        public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException("Rejected before it is ever built.");
    }

    private sealed class TriggeredWorkflow : IWorkflowDefinition<SampleContext, SampleResult>, IDomainEventTriggeredWorkflow
    {
        public TriggeredWorkflow(string name, string version)
        {
            Name = name;
            Version = version;
        }

        public string Name { get; }
        public string Version { get; }
        public IReadOnlyList<DomainEventTrigger> Triggers { get; } = [new DomainEventTrigger { TopicFilter = "orders.placed" }];

        public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException("Rejected before it is ever built.");
    }
}
