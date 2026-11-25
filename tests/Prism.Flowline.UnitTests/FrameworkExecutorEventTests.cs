using System.Runtime.CompilerServices;
using System.Text.Json;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Core;
using Prism.Flowline.Persistence;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Prism.Flowline.UnitTests;

/// <summary>
/// What the run loop does with events a framework executor raises — an agent bound through
/// <see cref="WorkflowBuildContext.RawNode"/>, or a raw executor's own event — through the real
/// engine and <see cref="WorkflowRunner"/>. The rule under test: an event the engine raised is
/// recorded or streamed, and none is dropped.
/// </summary>
/// <remarks>
/// Characterised on Agent Framework 1.28.0 before the run loop changed: each
/// <see cref="AgentResponseUpdateEvent"/> was stored as <c>workflow.output</c>, and so was the
/// <see cref="AgentResponseEvent"/>, whichever came last becoming the instance's result — the
/// response when response events were on, an empty fragment when they were off.
/// </remarks>
public class FrameworkExecutorEventTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-11-25T10:00:00Z"));
    private readonly InMemoryInstanceStore _instances;
    private readonly InMemoryEventStore _events = new();
    private readonly NotificationSequencer _sequencer = new();
    private readonly List<EventEnvelope> _published = [];

    public FrameworkExecutorEventTests() => _instances = new InMemoryInstanceStore(_clock);

    [Fact]
    public async Task An_event_a_raw_executor_raises_is_stored_as_a_custom_event()
    {
        await RunAsync(new RawEventWorkflow());

        EventEnvelope booked = (await StoredAsync()).Single(e => e.EventType == "custom.shipment-booked");

        booked.ExecutorId.Should().Be("ship");
        using JsonDocument payload = JsonDocument.Parse(booked.PayloadJson);
        payload.RootElement.GetProperty("eventType").GetString().Should().EndWith(nameof(ShipmentBooked));
        payload.RootElement.GetProperty("data").GetProperty("carrier").GetString().Should().Be("acme");
    }

    [Fact]
    public async Task Its_payload_is_redacted_like_any_other()
    {
        await RunAsync(new RawEventWorkflow(),
            redaction: new RedactionPolicy(bodyAllowList: ["executorId", "eventType", "carrier"]));

        EventEnvelope booked = (await StoredAsync()).Single(e => e.EventType == "custom.shipment-booked");

        booked.PayloadJson.Should().Contain("acme").And.NotContain("s3cr3t");
        booked.PayloadJson.Should().Contain(RedactionPolicy.Mask);
    }

    [Fact]
    public async Task A_workflow_that_opts_out_of_a_custom_event_does_not_receive_it()
    {
        await RunAsync(new RawEventWorkflow(QuietShip));

        (await StoredAsync()).Should().NotContain(e => e.EventType.StartsWith("custom.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_suppressed_event_leaves_no_gap_in_the_sequence()
    {
        await RunAsync(new RawEventWorkflow(QuietShip));

        long[] sequences = (await StoredAsync()).Select(e => e.Sequence).ToArray();

        sequences.Should().Equal(Enumerable.Range(1, sequences.Length).Select(i => (long)i));
    }

    [Fact]
    public async Task A_workflow_warning_is_stored()
    {
        await RunAsync(new RawEventWorkflow());

        EventEnvelope warning = (await StoredAsync()).Single(e => e.EventType == WorkflowEventTypes.WorkflowWarning);
        warning.PayloadJson.Should().Contain("carrier is slow");
    }

    [Fact]
    public async Task Agent_updates_are_streamed_and_not_stored()
    {
        await RunAsync(new AgentWorkflow(new ScriptedChatClient()));

        EventEnvelope[] updates = Published(WorkflowEventTypes.AgentUpdate);

        updates.Should().NotBeEmpty();
        updates.Should().OnlyContain(e => e.Delivery == EventDeliveryMode.StreamOnly && e.Sequence == 0);
        updates.Should().OnlyContain(e => e.ExecutorId!.StartsWith(AgentWorkflow.AgentId, StringComparison.Ordinal));
        string.Concat(updates.Select(e => JsonDocument.Parse(e.PayloadJson).RootElement.GetProperty("text").GetString()))
            .Should().Be("The invoice is valid");

        (await StoredAsync()).Should().NotContain(e => e.EventType == WorkflowEventTypes.AgentUpdate);
    }

    [Fact]
    public async Task Agent_updates_do_not_become_the_instance_result()
    {
        // Response events off, so updates are all the agent raises: before the fix the last of them
        // — an empty usage-only fragment — was the result.
        await RunAsync(new AgentWorkflow(new ScriptedChatClient(),
            new AIAgentHostOptions { EmitAgentUpdateEvents = true, EmitAgentResponseEvents = false }));

        WorkflowInstance? done = await _instances.GetAsync("i1", default);

        done!.Status.Should().Be(InstanceStatus.Completed);
        done.ResultJson.Should().BeNull();
        (await StoredAsync()).Should().NotContain(e => e.EventType == WorkflowEventTypes.WorkflowOutput);
    }

    [Fact]
    public async Task An_agent_response_is_stored_once()
    {
        await RunAsync(new AgentWorkflow(new ScriptedChatClient()));

        IReadOnlyList<EventEnvelope> stored = await StoredAsync();
        EventEnvelope response = stored.Should().ContainSingle(e => e.EventType == WorkflowEventTypes.AgentResponse).Subject;

        using JsonDocument payload = JsonDocument.Parse(response.PayloadJson);
        payload.RootElement.GetProperty("text").GetString().Should().Be("The invoice is valid");
        payload.RootElement.GetProperty("usage").GetProperty("outputTokens").GetInt64().Should().Be(8);

        stored.Should().NotContain(e => e.EventType == WorkflowEventTypes.WorkflowOutput,
            "the response is recorded as what it is, not as a generic output");
        (await _instances.GetAsync("i1", default))!.ResultJson.Should().Contain("The invoice is valid",
            "an agent that ends the graph still hands back its answer as the result");
    }

    [Fact]
    public async Task A_log_only_workflow_receives_no_agent_updates()
    {
        await RunAsync(new AgentWorkflow(new ScriptedChatClient(), notifications: new NotificationPolicy { StreamEvents = false }));

        Published(WorkflowEventTypes.AgentUpdate).Should().BeEmpty("opting out of streaming opts out of streamed tokens too");
        (await StoredAsync()).Should().ContainSingle(e => e.EventType == WorkflowEventTypes.AgentResponse);
    }

    [Fact]
    public async Task Superstep_starts_are_recorded_and_number_the_executors_that_run_in_them()
    {
        await RunAsync(new AgentWorkflow(new ScriptedChatClient()));

        IReadOnlyList<EventEnvelope> stored = await StoredAsync();

        stored.Where(e => e.EventType == WorkflowEventTypes.SuperstepStarted).Should().HaveCount(2);
        stored.Single(e => e.EventType == WorkflowEventTypes.ExecutorInvoked && e.ExecutorId == "prompt").Superstep.Should().Be(0);
        stored.Where(e => e.EventType == WorkflowEventTypes.ExecutorInvoked && e.ExecutorId != "prompt")
            .Should().OnlyContain(e => e.Superstep == 1, "the agent runs in the second superstep");
    }

    [Fact]
    public void Every_public_engine_event_has_a_case_or_is_deliberately_left_to_the_fallback()
    {
        // Recorded as custom.<name> by the fallback, on purpose: orchestration detail with no
        // Flowline meaning, and the base types, which a raw executor may raise directly.
        Type[] leftToFallback =
        [
            typeof(WorkflowEvent), typeof(ExecutorEvent), typeof(SuperStepEvent),
            typeof(Microsoft.Agents.AI.Workflows.Specialized.Magentic.MagenticPlanCreatedEvent),
            typeof(Microsoft.Agents.AI.Workflows.Specialized.Magentic.MagenticProgressLedgerUpdatedEvent),
            typeof(Microsoft.Agents.AI.Workflows.Specialized.Magentic.MagenticReplannedEvent)
        ];

        Type[] unaccounted = typeof(WorkflowEvent).Assembly.GetExportedTypes()
            .Where(t => typeof(WorkflowEvent).IsAssignableFrom(t) && !t.IsAbstract)
            .Except(WorkflowRunner.TranslatedEngineEvents)
            .Except(leftToFallback)
            .ToArray();

        unaccounted.Should().BeEmpty(
            "an Agent Framework upgrade added an event: give it a case in WorkflowRunner.PumpAsync, or add it " +
            "here if custom.<name> is the right record of it");
    }

    [Theory]
    [InlineData(typeof(Microsoft.Agents.AI.Workflows.Specialized.Magentic.MagenticPlanCreatedEvent), "magentic-plan-created")]
    [InlineData(typeof(ShipmentBooked), "shipment-booked")]
    [InlineData(typeof(HTTPCallMadeEvent), "http-call-made")]
    [InlineData(typeof(WorkflowEvent), "workflow")]
    public void Custom_names_are_the_type_name_in_kebab_case(Type type, string expected)
        => WorkflowRunner.KebabName(type).Should().Be(expected);

    private static readonly NotificationPolicy QuietShip = new()
    {
        ByNode = new Dictionary<string, NotificationLevel> { ["ship"] = NotificationLevel.Lifecycle }
    };

    private async Task RunAsync(IWorkflowDefinition definition, IRedactionPolicy? redaction = null)
    {
        await _instances.CreateAsync(new CreateInstanceRequest
        {
            InstanceId = "i1",
            TenantId = "t1",
            WorkflowName = definition.Name,
            WorkflowVersion = definition.Version,
            ContextJson = """{"value":"an invoice"}"""
        }, default);
        await _instances.UpdateAsync("i1", m => m.Status = InstanceStatus.Running, default);

        var runner = new WorkflowRunner(new WorkflowRunnerDependencies
        {
            Registry = new WorkflowRegistry([definition]),
            Instances = _instances,
            Events = new RecordingSink(new DirectNotificationSink(_events, redaction: redaction), _published),
            Sequencer = _sequencer,
            Pipelines = new MiddlewarePipelineFactory(),
            Checkpoints = new OverflowCheckpointStore(clock: _clock),
            Clock = _clock
        });

        await runner.RunAsync((await _instances.GetAsync("i1", default))!, default);
    }

    private async Task<IReadOnlyList<EventEnvelope>> StoredAsync()
        => (await _events.QueryAsync(new EventQuery("i1", Limit: 500), default)).Items;

    private EventEnvelope[] Published(string eventType)
    {
        lock (_published)
        {
            return [.. _published.Where(e => e.EventType == eventType)];
        }
    }

    /// <summary>Sees every envelope, the stream-only ones the store never receives included.</summary>
    private sealed class RecordingSink(INotificationSink inner, List<EventEnvelope> seen) : INotificationSink
    {
        public ValueTask PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken)
        {
            lock (seen)
            {
                seen.Add(envelope);
            }

            return inner.PublishAsync(envelope, cancellationToken);
        }
    }

    /// <summary>An executor's own event, the kind a canvas node might raise.</summary>
    public sealed class ShipmentBooked(string executorId, object data) : ExecutorEvent(executorId, data);

    /// <summary>Exists only to have an acronym in its name.</summary>
    public sealed class HTTPCallMadeEvent() : WorkflowEvent(null);

    /// <summary>One raw executor that raises its own event and a warning, then finishes.</summary>
    private sealed class RawEventWorkflow(NotificationPolicy? notifications = null)
        : IWorkflowDefinition<SampleContext, SampleResult>, INotifyingWorkflow
    {
        public string Name => "raw-events";
        public string Version => "1.0.0";
        public NotificationPolicy Notifications => notifications ?? NotificationPolicy.Default;

        public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
        {
            ExecutorBinding ship = context.RawNode(new Ship("ship"));
            return new ValueTask<Workflow>(new WorkflowBuilder(ship).WithOutputFrom(ship).Build());
        }

        private sealed class Ship(string id) : Executor<SampleContext, SampleResult>(id)
        {
            public override async ValueTask<SampleResult> HandleAsync(
                SampleContext message, IWorkflowContext context, CancellationToken cancellationToken = default)
            {
                await context.AddEventAsync(new ShipmentBooked(Id, new { carrier = "acme", secret = "s3cr3t" }), cancellationToken);
                await context.AddEventAsync(new WorkflowWarningEvent("carrier is slow"), cancellationToken);
                return new SampleResult(message.Value);
            }
        }
    }

    /// <summary>A prompt node and an agent, both raw: the shape a canvas compiler produces.</summary>
    private sealed class AgentWorkflow(IChatClient chat, AIAgentHostOptions? options = null, NotificationPolicy? notifications = null)
        : IWorkflowDefinition<SampleContext, SampleResult>, INotifyingWorkflow
    {
        /// <summary>The agent's id; the framework names its executor after it.</summary>
        public const string AgentId = "writer";

        public string Name => "agent";
        public string Version => "1.0.0";
        public NotificationPolicy Notifications => notifications ?? NotificationPolicy.Default;

        public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
        {
            var agent = new ChatClientAgent(chat, new ChatClientAgentOptions { Id = AgentId, Name = AgentId });

            ExecutorBinding prompt = context.RawNode(new Prompt("prompt"));
            ExecutorBinding writer = context.RawNode(agent.BindAsExecutor(options ?? new AIAgentHostOptions
            {
                EmitAgentUpdateEvents = true,
                EmitAgentResponseEvents = true
            }));

            return new ValueTask<Workflow>(new WorkflowBuilder(prompt)
                .AddEdge(prompt, writer)
                .WithOutputFrom(writer)
                .Build());
        }

        /// <summary>An agent collects messages and answers on a turn token.</summary>
        private sealed class Prompt(string id) : Executor<SampleContext>(id)
        {
            protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
                => base.ConfigureProtocol(protocolBuilder).SendsMessage<ChatMessage>().SendsMessage<TurnToken>();

            public override async ValueTask HandleAsync(
                SampleContext message, IWorkflowContext context, CancellationToken cancellationToken = default)
            {
                await context.SendMessageAsync(new ChatMessage(ChatRole.User, "Is this valid? " + message.Value), cancellationToken);
                await context.SendMessageAsync(new TurnToken(emitEvents: true), cancellationToken);
            }
        }
    }

    /// <summary>Streams three chunks of one message, then usage.</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private static readonly string[] Chunks = ["The ", "invoice ", "is valid"];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(Chunks)))
            {
                Usage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 8 }
            });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (string chunk in Chunks)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk) { MessageId = "m1" };
                await Task.Yield();
            }

            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = 12, OutputTokenCount = 8 })]
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
