using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Abstractions.Middleware;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Prism.Flowline.Core;

/// <summary>
/// Stands in a graph in place of an Agent Framework <see cref="Executor"/> the author did not write
/// as a host executor — a canvas node, a library's executor — so it gets what a host executor gets:
/// an approval gate, the executor middleware pipeline, and the runtime's invocation events.
/// </summary>
/// <remarks>
/// <para>
/// Same id as the executor it wraps, same accepted, sent and yielded types, so the graph is wired
/// exactly as it would be without it. For each message it does what <c>HostExecutor.HandleAsync</c>
/// does — announce the invocation, evaluate the gate, park or reject, run the middleware — and at the
/// centre of the pipeline delivers the message through the public <see cref="Executor.ExecuteCoreAsync"/>.
/// The wrapped executor sends its own result there, so the adapter never sends on its behalf.
/// </para>
/// <para>
/// Delivered that way, the wrapped executor raises its own invoked, completed and failed events under
/// the id the engine has already raised them for, so it is handed a context that drops those three
/// about itself. Everything else it does — sends, outputs, state, its own events — passes through.
/// </para>
/// <para>
/// Its lifecycle hooks are <c>protected internal</c> on <see cref="Executor"/>, so the engine calls
/// them on the adapter and the adapter forwards them by reflection. Without that, an executor that
/// keeps state in fields and saves it in <c>OnCheckpointingAsync</c> would lose it across a resume.
/// A hook that a later Agent Framework version renames is refused at construction rather than
/// silently not forwarded, and a test fails first.
/// </para>
/// <para>
/// What it cannot change: middleware sees the wrapped executor's result and exception but cannot
/// replace what it already sent.
/// </para>
/// </remarks>
internal sealed class FrameworkExecutorAdapter(Executor inner) : Executor(inner.Id, null, false), IHostExecutor
{
    private const string DeliveredTypeKey = "prism.adapter.delivered-type";

    private static readonly MethodInfo AddHandlerOpen = typeof(RouteBuilder).GetMethods()
        .Single(m => m.Name == nameof(RouteBuilder.AddHandler)
                     && m.GetGenericArguments().Length == 1
                     && m.GetParameters() is [{ ParameterType: { IsGenericType: true } handler }, _]
                     && handler.GetGenericTypeDefinition() == typeof(Func<,,,>)
                     && handler.GetGenericArguments()[3] == typeof(ValueTask));

    private static readonly MethodInfo HandleOpen =
        typeof(FrameworkExecutorAdapter).GetMethod(nameof(HandleAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>The lifecycle hooks forwarded to the wrapped executor, by name; null if the framework no longer has one.</summary>
    internal static IReadOnlyDictionary<string, MethodInfo?> ForwardedHooks { get; } = new[]
    {
        nameof(InitializeAsync),
        nameof(OnMessageDeliveryStartingAsync),
        nameof(OnMessageDeliveryFinishedAsync),
        nameof(OnCheckpointingAsync),
        nameof(OnCheckpointRestoredAsync)
    }.ToDictionary(
        name => name,
        name => typeof(Executor).GetMethod(
            name, BindingFlags.NonPublic | BindingFlags.Instance, [typeof(IWorkflowContext), typeof(CancellationToken)]));

    private readonly ProtocolDescriptor _protocol = Describe(inner);

    public Executor Inner => inner;

    public HostExecutorRuntime Runtime { get; set; } = HostExecutorRuntime.Unattached;

    public Type InputType => _protocol.Accepts.Count() == 1 ? _protocol.Accepts.Single() : typeof(object);

    public Type OutputType => _protocol.Sends.Concat(_protocol.Yields).Distinct().Count() == 1
        ? _protocol.Sends.Concat(_protocol.Yields).First()
        : typeof(object);

    public IReadOnlyDictionary<string, object?> Metadata { get; } = new Dictionary<string, object?>();

    /// <summary>How the catalog names what it accepts: one type, several joined, or anything.</summary>
    internal string? InputTypeName => _protocol.AcceptsAll
        ? "*"
        : string.Join("|", _protocol.Accepts.Select(t => t.Name)) is { Length: > 0 } names ? names : null;

    internal string? OutputTypeName
        => string.Join("|", _protocol.Sends.Concat(_protocol.Yields).Distinct().Select(t => t.Name)) is { Length: > 0 } names
            ? names
            : null;

    private static ProtocolDescriptor Describe(Executor inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (ForwardedHooks.FirstOrDefault(h => h.Value is null) is { Key: { } missing })
        {
            throw new NotSupportedException(
                $"Agent Framework no longer has Executor.{missing}, so executor '{inner.Id}' cannot be wrapped without " +
                "losing it. Bind the executor with RawNode, or update FrameworkExecutorAdapter for this framework version.");
        }

        return inner.DescribeProtocol();
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        protocolBuilder.SendsMessageTypes(_protocol.Sends).YieldsOutputTypes(_protocol.Yields);

        if (_protocol.AcceptsAll)
        {
            protocolBuilder.RouteBuilder.AddCatchAll(
                (PortableValue value, IWorkflowContext context, CancellationToken cancellationToken) =>
                    DeliverAsync(value, value.TypeId, null, context, cancellationToken), false);
            return protocolBuilder;
        }

        // One typed route per accepted type, so edge type checks and routing see exactly what they
        // would see for the wrapped executor. The types are only known at run time, hence reflection.
        foreach (Type accepted in _protocol.Accepts)
        {
            Type handlerType = typeof(Func<,,,>).MakeGenericType(
                accepted, typeof(IWorkflowContext), typeof(CancellationToken), typeof(ValueTask));
            Delegate handler = Delegate.CreateDelegate(handlerType, this, HandleOpen.MakeGenericMethod(accepted));
            AddHandlerOpen.MakeGenericMethod(accepted).Invoke(protocolBuilder.RouteBuilder, [handler, false]);
        }

        return protocolBuilder;
    }

    private ValueTask HandleAsync<T>(T message, IWorkflowContext context, CancellationToken cancellationToken)
        => DeliverAsync(message!, new TypeId(typeof(T)), typeof(T), context, cancellationToken);

    private async ValueTask DeliverAsync(
        object message, TypeId deliveredAs, Type? clrType, IWorkflowContext context, CancellationToken cancellationToken)
    {
        // Numbered first, before anything is awaited, exactly as a host executor does. The run loop
        // records the engine's own invoked event in stream order, so a wrapped executor's stream is
        // the one it would produce bound raw; only parking announces early (below), and the run loop
        // publishes each delivery's invocation once, whichever of the two gets there first.
        int delivery = Runtime.DeliveryStarted?.Invoke(Id) ?? 0;
        object input = message;

        if (Runtime.Gates is { } gates)
        {
            GateOutcome outcome = await gates
                .EvaluateAsync(Runtime.InstanceId, Id, message, cancellationToken)
                .ConfigureAwait(false);

            switch (outcome.Kind)
            {
                case GateOutcomeKind.Rejected:
                    throw new ApprovalRejectedException(Id, outcome.ApprovalId ?? "unknown", outcome.Comment);

                case GateOutcomeKind.Pause:
                    // The wrapped executor has not been called, so nothing it does has happened.
                    if (Runtime.AnnounceBeforePark is { } announce)
                    {
                        await announce(Id, delivery).ConfigureAwait(false);
                    }

                    if (Runtime.Approvals is { } approvals)
                    {
                        await approvals.RaiseAsync(
                            Runtime.InstanceId, Id, outcome.Gate ?? ApprovalGate.Autonomous,
                            message, Runtime.CurrentSuperstep, cancellationToken).ConfigureAwait(false);
                    }

                    await context.RequestHaltAsync().ConfigureAwait(false);
                    return;

                case GateOutcomeKind.ProceedApproved:
                    input = outcome.ModifiedInput is { } amended ? BindAmended(amended, clrType ?? message.GetType()) : message;
                    break;
            }
        }

        var invocation = new ExecutorInvocationContext
        {
            InstanceId = Runtime.InstanceId,
            Descriptor = Runtime.Descriptor,
            Superstep = Runtime.CurrentSuperstep,
            Attempt = Runtime.Attempt,
            Input = input,
            WorkflowContext = context,
            Services = Runtime.Services
        };
        invocation.Items[DeliveredTypeKey] = deliveredAs;

        long start = Stopwatch.GetTimestamp();
        try
        {
            ExecutorDelegate pipeline = Runtime.Pipeline ?? ExecuteTerminalAsync;
            await pipeline(invocation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            invocation.Elapsed = Stopwatch.GetElapsedTime(start);
        }

        if (invocation.Exception is { } exception)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    /// <summary>
    /// The innermost delegate of the executor pipeline, found by name by the runner as it is for a
    /// host executor. Exceptions are captured so middleware can observe or replace them.
    /// </summary>
    public async ValueTask ExecuteTerminalAsync(ExecutorInvocationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            TypeId deliveredAs = context.Items.TryGetValue(DeliveredTypeKey, out object? type) && type is TypeId id
                ? id
                : new TypeId(context.Input!.GetType());

            context.Output = await inner.ExecuteCoreAsync(
                context.Input!, deliveredAs, new OwnLifecycleFilter(context.WorkflowContext!, Id), cancellationToken)
                .ConfigureAwait(false);
            context.Exception = null;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // The framework reaches the handler by reflection and wraps what it throws. Middleware and
            // the workflow's failure classifier must see the executor's own exception — a dead-stop
            // classified as a reflection failure would be retried.
            context.Exception = ex.InnerException;
            context.Output = null;
        }
        catch (Exception ex)
        {
            context.Exception = ex;
            context.Output = null;
        }
    }

    private object BindAmended(object amended, Type target)
    {
        if (target.IsInstanceOfType(amended))
        {
            return amended;
        }

        if (amended is not JsonElement json)
        {
            throw new InvalidOperationException(
                $"Executor '{Id}' was approved with a modified input of type '{amended.GetType().Name}', " +
                $"which is neither '{target.Name}' nor JSON.");
        }

        try
        {
            return JsonSerializer.Deserialize(json, target, JsonOptions.Default)
                ?? throw new InvalidOperationException($"Executor '{Id}' was approved with a modified input that binds to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Executor '{Id}' was approved with a modified input that does not bind to '{target.Name}': {ex.Message}", ex);
        }
    }

    // ---- lifecycle, forwarded ----------------------------------------------------------------

    protected override async ValueTask InitializeAsync(IWorkflowContext context, CancellationToken cancellationToken)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        await ForwardAsync(nameof(InitializeAsync), context, cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask OnMessageDeliveryStartingAsync(IWorkflowContext context, CancellationToken cancellationToken)
    {
        await base.OnMessageDeliveryStartingAsync(context, cancellationToken).ConfigureAwait(false);
        await ForwardAsync(nameof(OnMessageDeliveryStartingAsync), context, cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask OnMessageDeliveryFinishedAsync(IWorkflowContext context, CancellationToken cancellationToken)
    {
        await base.OnMessageDeliveryFinishedAsync(context, cancellationToken).ConfigureAwait(false);
        await ForwardAsync(nameof(OnMessageDeliveryFinishedAsync), context, cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken cancellationToken)
    {
        await base.OnCheckpointingAsync(context, cancellationToken).ConfigureAwait(false);
        await ForwardAsync(nameof(OnCheckpointingAsync), context, cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken cancellationToken)
    {
        await base.OnCheckpointRestoredAsync(context, cancellationToken).ConfigureAwait(false);
        await ForwardAsync(nameof(OnCheckpointRestoredAsync), context, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask ForwardAsync(string hook, IWorkflowContext context, CancellationToken cancellationToken)
    {
        try
        {
            return (ValueTask)ForwardedHooks[hook]!.Invoke(inner, [new OwnLifecycleFilter(context, Id), cancellationToken])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>
    /// Drops the invoked, completed and failed events the wrapped executor raises about itself; the
    /// engine raises that pair for the adapter, which has the same id.
    /// </summary>
    private sealed class OwnLifecycleFilter(IWorkflowContext context, string executorId) : IWorkflowContext
    {
        public ValueTask AddEventAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default)
            => workflowEvent is ExecutorInvokedEvent or ExecutorCompletedEvent or ExecutorFailedEvent
               && string.Equals(((ExecutorEvent)workflowEvent).ExecutorId, executorId, StringComparison.Ordinal)
                ? ValueTask.CompletedTask
                : context.AddEventAsync(workflowEvent, cancellationToken);

        public ValueTask SendMessageAsync(object message, string? targetId = null, CancellationToken cancellationToken = default)
            => context.SendMessageAsync(message, targetId, cancellationToken);

        public ValueTask YieldOutputAsync(object output, CancellationToken cancellationToken = default)
            => context.YieldOutputAsync(output, cancellationToken);

        public ValueTask RequestHaltAsync() => context.RequestHaltAsync();

        public ValueTask<T?> ReadStateAsync<T>(string key, string? scopeName = null, CancellationToken cancellationToken = default)
            => context.ReadStateAsync<T>(key, scopeName, cancellationToken);

        public ValueTask<T> ReadOrInitStateAsync<T>(
            string key, Func<T> initialStateFactory, string? scopeName = null, CancellationToken cancellationToken = default)
            => context.ReadOrInitStateAsync(key, initialStateFactory, scopeName, cancellationToken);

        public ValueTask<HashSet<string>> ReadStateKeysAsync(string? scopeName = null, CancellationToken cancellationToken = default)
            => context.ReadStateKeysAsync(scopeName, cancellationToken);

        public ValueTask QueueStateUpdateAsync<T>(string key, T? value, string? scopeName = null, CancellationToken cancellationToken = default)
            => context.QueueStateUpdateAsync(key, value, scopeName, cancellationToken);

        public ValueTask QueueClearScopeAsync(string? scopeName = null, CancellationToken cancellationToken = default)
            => context.QueueClearScopeAsync(scopeName, cancellationToken);

        public IReadOnlyDictionary<string, string>? TraceContext => context.TraceContext;

        public bool ConcurrentRunsEnabled => context.ConcurrentRunsEnabled;
    }
}
