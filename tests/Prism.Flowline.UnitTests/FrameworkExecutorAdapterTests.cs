using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Abstractions.Middleware;
using Prism.Flowline.Core;
using Prism.Flowline.Persistence;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Prism.Flowline.UnitTests;

/// <summary>
/// <see cref="WorkflowBuildContext.Node(Executor, Action{ApprovalGateBuilder}?)"/>: an Agent
/// Framework executor nobody wrote as a host executor, given a gate, middleware and the runtime's
/// events, through the real engine and <see cref="WorkflowRunner"/>. Every graph here is compared
/// with, or built like, the same graph bound with <see cref="WorkflowBuildContext.RawNode"/>.
/// </summary>
public class FrameworkExecutorAdapterTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-11-25T10:00:00Z"));
    private readonly InMemoryInstanceStore _instances;
    private readonly InMemoryEventStore _events = new();
    private readonly InMemoryApprovalStore _approvals = new();
    private readonly InMemoryGatePolicyStore _policies = new();
    private readonly NotificationSequencer _sequencer = new();
    private readonly CommitOrderCheckpoints _checkpoints;
    private readonly Ledger _ledger = new();

    public FrameworkExecutorAdapterTests()
    {
        _instances = new InMemoryInstanceStore(_clock);
        _checkpoints = new CommitOrderCheckpoints(new OverflowCheckpointStore(clock: _clock));
    }

    [Fact]
    public async Task A_wrapped_executor_runs_sends_and_yields_as_it_does_unwrapped()
    {
        IReadOnlyList<(string, string?)> raw = await StreamOfBranchingGraphAsync(wrap: false);
        IReadOnlyList<string> rawEffects = _ledger.Take();

        IReadOnlyList<(string, string?)> wrapped = await StreamOfBranchingGraphAsync(wrap: true);
        IReadOnlyList<string> wrappedEffects = _ledger.Take();

        // Invocations are recorded from the engine's own events in stream order, for wrapped and raw
        // alike, so the two streams are the same event for event.
        wrapped.Should().Equal(raw, "same events, same order, same ids as the raw binding");

        wrappedEffects.Should().BeEquivalentTo(rawEffects);
        rawEffects.Should().Contain(["double:5", "inc:5", "big:10", "small:6"]);
    }

    [Fact]
    public async Task Invoked_and_completed_are_published_once()
    {
        await RunAsync(Graph(c =>
        {
            ExecutorBinding start = c.Node(new Start("start"));
            ExecutorBinding effect = c.Node(new SideEffect("effect", _ledger));
            return new WorkflowBuilder(start).AddEdge(start, effect).WithOutputFrom(effect).Build();
        }));

        IReadOnlyList<EventEnvelope> stored = await StoredAsync();

        foreach (string id in new[] { "start", "effect" })
        {
            stored.Count(e => e.EventType == WorkflowEventTypes.ExecutorInvoked && e.ExecutorId == id).Should().Be(1, id);
            stored.Count(e => e.EventType == WorkflowEventTypes.ExecutorCompleted && e.ExecutorId == id).Should().Be(1, id);
        }
    }

    [Fact]
    public async Task A_gate_parks_before_the_wrapped_executor_runs()
    {
        RunOutcome outcome = await RunAsync(GatedGraph(), withApprovals: true);

        outcome.Status.Should().Be(InstanceStatus.AwaitingApproval);
        _ledger.Take().Should().BeEmpty("no side effect may precede the decision");

        ApprovalRequest approval = (await _approvals.ListForInstanceAsync("i1", default)).Single();
        approval.ExecutorId.Should().Be("effect");
        approval.State.Should().Be(ApprovalState.Pending);

        IReadOnlyList<EventEnvelope> stored = await StoredAsync();
        stored.Where(e => e.ExecutorId == "effect").Select(e => e.EventType).Should().Equal(
            [WorkflowEventTypes.ExecutorInvoked, WorkflowEventTypes.ApprovalRequested],
            "announced once, before the approval it raised, as a host executor's is");
    }

    [Fact]
    public async Task An_approved_gate_lets_the_wrapped_executor_run()
    {
        ApprovalCoordinator coordinator = Coordinator();
        await RunAsync(GatedGraph(), approvals: coordinator);

        ApprovalRequest approval = (await _approvals.ListForInstanceAsync("i1", default)).Single();
        await coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Approve }, Approver(), default);

        RunOutcome outcome = await RunAgainAsync(GatedGraph(), coordinator);

        outcome.Status.Should().Be(InstanceStatus.Completed);
        _ledger.Take().Should().Equal("effect:5");
    }

    [Fact]
    public async Task A_rejected_approval_dead_stops_the_instance()
    {
        ApprovalCoordinator coordinator = Coordinator();
        await RunAsync(GatedGraph(), approvals: coordinator);

        ApprovalRequest approval = (await _approvals.ListForInstanceAsync("i1", default)).Single();
        await coordinator.ApplyDecisionAsync(approval.ApprovalId,
            new ApprovalDecisionInput { Decision = ApprovalOutcomeKind.Reject, Comment = "not today" }, Approver(), default);

        RunOutcome outcome = await RunAgainAsync(GatedGraph(), coordinator);

        outcome.Status.Should().Be(InstanceStatus.DeadStopped);
        _ledger.Take().Should().BeEmpty();
    }

    [Fact]
    public async Task A_tenant_policy_can_gate_it_without_the_definition_declaring_one()
    {
        await _policies.SetAsync("t1", "graph", "1.0.0", "effect",
            new ApprovalGate { Mode = ExecutionMode.RequireApproval, Reason = "TenantSaysSo" }, default);

        RunOutcome outcome = await RunAsync(Graph(c =>
        {
            ExecutorBinding start = c.Node(new Start("start"));
            ExecutorBinding effect = c.Node(new SideEffect("effect", _ledger));   // no gate declared
            return new WorkflowBuilder(start).AddEdge(start, effect).WithOutputFrom(effect).Build();
        }), withApprovals: true);

        outcome.Status.Should().Be(InstanceStatus.AwaitingApproval);
        _ledger.Take().Should().BeEmpty();
        (await _approvals.ListForInstanceAsync("i1", default)).Single().Reason.Should().Be("TenantSaysSo");
    }

    [Fact]
    public async Task Executor_middleware_wraps_it()
    {
        var seen = new ConcurrentQueue<ExecutorInvocationContext>();
        var middleware = new ObservingMiddleware(seen);

        await RunAsync(Graph(c =>
        {
            ExecutorBinding start = c.Node(new Start("start"));
            ExecutorBinding broken = c.Node(new Broken("broken"));
            return new WorkflowBuilder(start).AddEdge(start, broken).WithOutputFrom(broken).Build();
        }), pipelines: new MiddlewarePipelineFactory([middleware]));

        ExecutorInvocationContext[] invocations = [.. seen];

        invocations.Select(i => i.Descriptor.ExecutorId).Should().Equal("start", "broken");
        invocations[0].Descriptor.ExecutorType.Should().Be(typeof(Start), "middleware sees the executor the author wrote");
        invocations[0].Output.Should().Be(5);
        invocations[0].Elapsed.Should().BePositive();
        invocations[1].Exception.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("broken by design");

        (await _instances.GetAsync("i1", default))!.TerminalReason.Should().Contain("broken by design");
    }

    [Fact]
    public async Task It_resumes_from_a_checkpoint()
    {
        string[] resumed = await RunLoopThenResumeAfterFirstVisitAsync(ledger => new ContextCounter("counter", ledger));

        resumed.Should().Equal(["visit:2"], "the count lives in the workflow context, which the checkpoint carries");
    }

    [Fact]
    public async Task Field_held_state_survives_a_resume()
    {
        string[] resumed = await RunLoopThenResumeAfterFirstVisitAsync(ledger => new FieldCounter("counter", ledger));

        resumed.Should().Equal(["visit:2"], "the checkpoint hooks were forwarded to the wrapped executor");
        _ledger.Take().Should().Contain("counter:restored:1");
    }

    [Fact]
    public void The_lifecycle_hooks_it_forwards_still_exist()
    {
        // A rename in a later Agent Framework version would leave wrapped executors losing state
        // silently on resume; the adapter refuses to construct instead, and this fails first.
        FrameworkExecutorAdapter.ForwardedHooks.Should().HaveCount(5)
            .And.OnlyContain(hook => hook.Value != null, "every hook the adapter forwards must still be found by name");
    }

    [Fact]
    public async Task A_wrapped_executor_is_listed_as_configurable_under_its_own_type()
    {
        IReadOnlyList<WorkflowNodeDescriptor> nodes = await new WorkflowInspector()
            .InspectAsync(new WorkflowDescriptor(GatedGraph()), default);

        WorkflowNodeDescriptor effect = nodes.Single(n => n.ExecutorId == "effect");
        effect.Configurable.Should().BeTrue();
        effect.ExecutorType.Should().Be(nameof(SideEffect));
        effect.InputType.Should().Be(nameof(Int32));
        effect.DeclaredGate.Mode.Should().Be(ExecutionMode.RequireApproval);
    }

    [Fact]
    public void A_host_executor_given_as_an_Executor_is_attached_as_itself_not_wrapped()
    {
        var context = WorkflowBuildContext.ForInspection("probe", "1.0.0");
        Executor host = new HostStep("host");

        context.Node(host);

        context.Nodes.Single().ExecutorType.Should().Be(nameof(HostStep));
    }

    // ---- graphs -----------------------------------------------------------------------------

    private async Task<IReadOnlyList<(string, string?)>> StreamOfBranchingGraphAsync(bool wrap)
    {
        await RunAsync(Graph(c =>
        {
            ExecutorBinding Bind(Executor executor) => wrap ? c.Node(executor) : c.RawNode(executor);

            ExecutorBinding start = Bind(new Start("start"));
            ExecutorBinding doubler = Bind(new Doubler("double", _ledger));
            ExecutorBinding incrementer = Bind(new Incrementer("inc", _ledger));
            ExecutorBinding join = Bind(new Join("join"));
            ExecutorBinding big = Bind(new Labeller("big", _ledger));
            ExecutorBinding small = Bind(new Labeller("small", _ledger));

            return new WorkflowBuilder(start)
                .AddFanOutEdge(start, [doubler, incrementer])
                .AddFanInBarrierEdge([doubler, incrementer], join)
                .AddEdge<int>(join, big, x => x > 8)
                .AddEdge<int>(join, small, x => x <= 8)
                .WithOutputFrom(big, small)
                .Build();
        }), instanceId: wrap ? "wrapped" : "raw");

        return [.. (await StoredAsync(wrap ? "wrapped" : "raw")).Select(e => (e.EventType, e.ExecutorId))];
    }

    private GraphWorkflow GatedGraph() => Graph(c =>
    {
        ExecutorBinding start = c.Node(new Start("start"));
        ExecutorBinding effect = c.Node(new SideEffect("effect", _ledger), gate => gate
            .Mode(ExecutionMode.RequireApproval)
            .Reason("WrappedNeedsApproval"));
        return new WorkflowBuilder(start).AddEdge(start, effect).WithOutputFrom(effect).Build();
    });

    /// <summary>
    /// The counter is visited twice through a loop with an echo. The echo dead-stops the first run,
    /// leaving the checkpoint after the counter's first visit to resume from in a freshly built graph.
    /// </summary>
    private async Task<string[]> RunLoopThenResumeAfterFirstVisitAsync(Func<Ledger, Executor> counterFactory)
    {
        var echoFails = new StrongBox<bool>(true);

        GraphWorkflow Build() => Graph(c =>
        {
            ExecutorBinding start = c.Node(new Start("start"));
            ExecutorBinding counter = c.Node(counterFactory(_ledger));
            ExecutorBinding echo = c.Node(new Echo("echo", echoFails));
            return new WorkflowBuilder(start)
                .AddEdge(start, counter)
                .AddEdge(counter, echo)
                .AddEdge(echo, counter)
                .WithOutputFrom(counter)
                .Build();
        });

        RunOutcome first = await RunAsync(Build());
        first.Status.Should().Be(InstanceStatus.DeadStopped);

        // Superstep 0 ran the start node, 1 the counter's first visit.
        string afterFirstVisit = _checkpoints.Committed("i1")[1].CheckpointId;
        echoFails.Value = false;
        int storedBefore = (await StoredAsync()).Count;

        await _instances.UpdateAsync("i1", m =>
        {
            m.Status = InstanceStatus.Running;
            m.LatestCheckpointId = afterFirstVisit;
            m.AllowTerminalTransition = true;
        }, default);

        RunOutcome second = await RunAgainAsync(Build(), null);
        second.Status.Should().Be(InstanceStatus.Completed, second.TerminalReason);

        return [.. (await StoredAsync()).Skip(storedBefore)
            .Where(e => e.EventType == WorkflowEventTypes.WorkflowOutput)
            .Select(e => JsonDocument.Parse(e.PayloadJson).RootElement.GetProperty("result").GetString()!)
            .Select(result => JsonSerializer.Deserialize<string>(result)!)];
    }

    // ---- running ----------------------------------------------------------------------------

    private static GraphWorkflow Graph(Func<WorkflowBuildContext, Workflow> build) => new(build);

    private ApprovalCoordinator Coordinator()
        => new(_approvals, _instances, new DirectNotificationSink(_events), _sequencer, null, _clock);

    private static ClaimsPrincipal Approver()
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")], "test"));

    private async Task<RunOutcome> RunAsync(
        IWorkflowDefinition definition,
        bool withApprovals = false,
        IApprovalService? approvals = null,
        MiddlewarePipelineFactory? pipelines = null,
        string instanceId = "i1")
    {
        await _instances.CreateAsync(new CreateInstanceRequest
        {
            InstanceId = instanceId,
            TenantId = "t1",
            WorkflowName = definition.Name,
            WorkflowVersion = definition.Version,
            ContextJson = """{"value":"seed","amount":5}"""
        }, default);
        await _instances.UpdateAsync(instanceId, m => m.Status = InstanceStatus.Running, default);

        return await Runner(definition, approvals ?? (withApprovals ? Coordinator() : null), pipelines)
            .RunAsync((await _instances.GetAsync(instanceId, default))!, default);
    }

    private async Task<RunOutcome> RunAgainAsync(IWorkflowDefinition definition, IApprovalService? approvals)
    {
        await _instances.UpdateAsync("i1", m => m.Status = InstanceStatus.Running, default);
        return await Runner(definition, approvals, null).RunAsync((await _instances.GetAsync("i1", default))!, default);
    }

    private WorkflowRunner Runner(IWorkflowDefinition definition, IApprovalService? approvals, MiddlewarePipelineFactory? pipelines)
        => new(new WorkflowRunnerDependencies
        {
            Registry = new WorkflowRegistry([definition]),
            Instances = _instances,
            Events = new DirectNotificationSink(_events),
            Sequencer = _sequencer,
            Pipelines = pipelines ?? new MiddlewarePipelineFactory(),
            Checkpoints = _checkpoints,
            Approvals = _approvals,
            ApprovalService = approvals,
            GatePolicies = _policies,
            Clock = _clock
        });

    private async Task<IReadOnlyList<EventEnvelope>> StoredAsync(string instanceId = "i1")
        => (await _events.QueryAsync(new EventQuery(instanceId, Limit: 500), default)).Items;

    // ---- doubles ----------------------------------------------------------------------------

    private sealed class GraphWorkflow(Func<WorkflowBuildContext, Workflow> build) : IWorkflowDefinition<SampleContext, SampleResult>
    {
        public string Name => "graph";
        public string Version => "1.0.0";

        public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(build(context));
    }

    /// <summary>What a node did, in the order it did it; drained by each assertion.</summary>
    private sealed class Ledger
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public void Record(string entry) => _entries.Enqueue(entry);

        public IReadOnlyList<string> Take()
        {
            var taken = new List<string>();
            while (_entries.TryDequeue(out string? entry))
            {
                taken.Add(entry);
            }

            return taken;
        }
    }

    private sealed class StrongBox<T>(T value)
    {
        public T Value { get; set; } = value;
    }

    /// <summary>Remembers checkpoints in the order they were committed, which a frozen clock cannot.</summary>
    private sealed class CommitOrderCheckpoints(OverflowCheckpointStore inner) : ICheckpointStore<JsonElement>
    {
        private readonly ConcurrentDictionary<string, List<CheckpointInfo>> _committed = new();

        public IReadOnlyList<CheckpointInfo> Committed(string sessionId)
        {
            List<CheckpointInfo> list = _committed.GetOrAdd(sessionId, _ => []);
            lock (list)
            {
                return [.. list];
            }
        }

        public async ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
        {
            CheckpointInfo info = await inner.CreateCheckpointAsync(sessionId, value, parent);
            List<CheckpointInfo> list = _committed.GetOrAdd(sessionId, _ => []);
            lock (list)
            {
                list.Add(info);
            }

            return info;
        }

        public ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
            => inner.RetrieveCheckpointAsync(sessionId, key);

        public ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
            => inner.RetrieveIndexAsync(sessionId, withParent);
    }

    private sealed class ObservingMiddleware(ConcurrentQueue<ExecutorInvocationContext> seen) : IExecutorMiddleware
    {
        public async ValueTask InvokeAsync(ExecutorInvocationContext context, ExecutorDelegate next, CancellationToken cancellationToken)
        {
            await next(context, cancellationToken);
            seen.Enqueue(context);
        }
    }

    // Raw Agent Framework executors: none of them knows Flowline exists.

    private sealed class Start(string id) : Executor<SampleContext, int>(id)
    {
        public override ValueTask<int> HandleAsync(SampleContext message, IWorkflowContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult((int)message.Amount);
    }

    private sealed class Doubler(string id, Ledger ledger) : Executor<int, int>(id)
    {
        public override ValueTask<int> HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            ledger.Record($"{Id}:{message}");
            return ValueTask.FromResult(message * 2);
        }
    }

    private sealed class Incrementer(string id, Ledger ledger) : Executor<int, int>(id)
    {
        public override ValueTask<int> HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            ledger.Record($"{Id}:{message}");
            return ValueTask.FromResult(message + 1);
        }
    }

    private sealed class Join(string id) : Executor<int, int>(id)
    {
        public override ValueTask<int> HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(message);
    }

    private sealed class Labeller(string id, Ledger ledger) : Executor<int>(id)
    {
        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
            => base.ConfigureProtocol(protocolBuilder).YieldsOutput<string>();

        public override async ValueTask HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            ledger.Record($"{Id}:{message}");
            await context.YieldOutputAsync($"{Id}:{message}", cancellationToken);
        }
    }

    private sealed class SideEffect(string id, Ledger ledger) : Executor<int, string>(id)
    {
        public override ValueTask<string> HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            ledger.Record($"{Id}:{message}");
            return ValueTask.FromResult($"done:{message}");
        }
    }

    private sealed class Broken(string id) : Executor<int, string>(id)
    {
        public override ValueTask<string> HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("broken by design");
    }

    private sealed class Echo(string id, StrongBox<bool> fails) : Executor<int, int>(id)
    {
        public override ValueTask<int> HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
            => fails.Value ? throw new WorkflowDeadStopException("echo down") : ValueTask.FromResult(message);
    }

    /// <summary>Keeps its visit count in the workflow context.</summary>
    private sealed class ContextCounter(string id, Ledger ledger) : Executor<int>(id)
    {
        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
            => base.ConfigureProtocol(protocolBuilder).SendsMessage<int>().YieldsOutput<string>();

        public override async ValueTask HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            int count = await context.ReadOrInitStateAsync("count", () => 0, cancellationToken) + 1;
            await context.QueueStateUpdateAsync("count", count, cancellationToken);
            ledger.Record($"{Id}:visit:{count}");
            await context.YieldOutputAsync($"visit:{count}", cancellationToken);

            if (count < 2)
            {
                await context.SendMessageAsync(count, null, cancellationToken);
            }
        }
    }

    /// <summary>Keeps its visit count in a field, saved and restored through the checkpoint hooks.</summary>
    private sealed class FieldCounter(string id, Ledger ledger) : Executor<int>(id)
    {
        private int _count;

        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
            => base.ConfigureProtocol(protocolBuilder).SendsMessage<int>().YieldsOutput<string>();

        public override async ValueTask HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            _count++;
            ledger.Record($"{Id}:visit:{_count}");
            await context.YieldOutputAsync($"visit:{_count}", cancellationToken);

            if (_count < 2)
            {
                await context.SendMessageAsync(_count, null, cancellationToken);
            }
        }

        protected override ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken cancellationToken)
            => context.QueueStateUpdateAsync("field-count", _count, cancellationToken);

        protected override async ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken cancellationToken)
        {
            _count = await context.ReadOrInitStateAsync("field-count", () => 0, cancellationToken);
            ledger.Record($"{Id}:restored:{_count}");
        }
    }

    private sealed class HostStep(string id) : HostExecutor<int, string>(id)
    {
        protected override ValueTask<string> ExecuteCoreAsync(int input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(input.ToString());
    }
}
