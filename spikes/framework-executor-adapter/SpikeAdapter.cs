using System.Reflection;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace FrameworkExecutorAdapterSpike;

/// <summary>
/// The crudest adapter that can answer the design's five questions: it stands in the graph with the
/// wrapped executor's id, mirrors its protocol, and hands each message to it through the public
/// <see cref="Executor.ExecuteCoreAsync"/>. A gate is a predicate that parks; lifecycle forwarding by
/// reflection can be switched on and off.
/// </summary>
public sealed class SpikeAdapter(
    Executor inner, Func<object, bool>? parkWhen = null, bool forwardLifecycle = false, bool filterInnerLifecycle = true)
    : Executor(inner.Id, null, false)
{
    private static readonly MethodInfo AddHandlerOpen = typeof(RouteBuilder).GetMethods()
        .Single(m => m.Name == nameof(RouteBuilder.AddHandler)
                     && m.GetGenericArguments().Length == 1
                     && m.GetParameters()[0].ParameterType.IsGenericType
                     && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Func<,,,>)
                     && m.GetParameters()[0].ParameterType.GetGenericArguments()[3] == typeof(ValueTask));

    private static readonly MethodInfo HandleOpen =
        typeof(SpikeAdapter).GetMethod(nameof(Handle), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public Executor Inner => inner;

    /// <summary>Messages the adapter received, whether or not it passed them on.</summary>
    public int Received { get; private set; }

    /// <summary>What ExecuteCoreAsync handed back, to see whether the adapter must send it.</summary>
    public List<object?> InnerResults { get; } = [];

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        ProtocolDescriptor protocol = inner.DescribeProtocol();

        protocolBuilder.SendsMessageTypes(protocol.Sends).YieldsOutputTypes(protocol.Yields);

        if (protocol.AcceptsAll)
        {
            protocolBuilder.RouteBuilder.AddCatchAll(
                (PortableValue value, IWorkflowContext context, CancellationToken ct) =>
                    DeliverAsync(value, value.TypeId, context, ct), false);
        }
        else
        {
            foreach (Type accepted in protocol.Accepts)
            {
                Type handlerType = typeof(Func<,,,>).MakeGenericType(
                    accepted, typeof(IWorkflowContext), typeof(CancellationToken), typeof(ValueTask));
                Delegate handler = Delegate.CreateDelegate(handlerType, this, HandleOpen.MakeGenericMethod(accepted));
                AddHandlerOpen.MakeGenericMethod(accepted).Invoke(protocolBuilder.RouteBuilder, [handler, false]);
            }
        }

        return protocolBuilder;
    }

    private ValueTask Handle<T>(T message, IWorkflowContext context, CancellationToken cancellationToken)
        => DeliverAsync(message!, new TypeId(typeof(T)), context, cancellationToken);

    private async ValueTask DeliverAsync(object message, TypeId type, IWorkflowContext context, CancellationToken cancellationToken)
    {
        Received++;

        if (parkWhen?.Invoke(message) == true)
        {
            await context.RequestHaltAsync();
            return;
        }

        IWorkflowContext handed = filterInnerLifecycle ? new OwnLifecycleFilter(context, Id) : context;
        object? result = await inner.ExecuteCoreAsync(message, type, handed, cancellationToken);
        InnerResults.Add(result);
    }

    // ---- lifecycle: protected internal on Executor, so only reachable on another instance by reflection

    protected override ValueTask InitializeAsync(IWorkflowContext context, CancellationToken cancellationToken)
        => Forward(nameof(InitializeAsync), context, cancellationToken);

    protected override ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken cancellationToken)
        => Forward(nameof(OnCheckpointingAsync), context, cancellationToken);

    protected override ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken cancellationToken)
        => Forward(nameof(OnCheckpointRestoredAsync), context, cancellationToken);

    protected override ValueTask OnMessageDeliveryStartingAsync(IWorkflowContext context, CancellationToken cancellationToken)
        => Forward(nameof(OnMessageDeliveryStartingAsync), context, cancellationToken);

    protected override ValueTask OnMessageDeliveryFinishedAsync(IWorkflowContext context, CancellationToken cancellationToken)
        => Forward(nameof(OnMessageDeliveryFinishedAsync), context, cancellationToken);

    private ValueTask Forward(string hook, IWorkflowContext context, CancellationToken cancellationToken)
    {
        if (!forwardLifecycle)
        {
            return ValueTask.CompletedTask;
        }

        MethodInfo method = typeof(Executor).GetMethod(
            hook, BindingFlags.NonPublic | BindingFlags.Instance, [typeof(IWorkflowContext), typeof(CancellationToken)])
            ?? throw new MissingMethodException(nameof(Executor), hook);

        return (ValueTask)method.Invoke(inner, [context, cancellationToken])!;
    }
}

/// <summary>
/// Drops the invoked, completed and failed events the wrapped executor raises about itself: the
/// engine already raises that pair for the adapter, which carries the same id. Everything else —
/// sends, outputs, state, the executor's own events — passes through untouched.
/// </summary>
public sealed class OwnLifecycleFilter(IWorkflowContext inner, string executorId) : IWorkflowContext
{
    public ValueTask AddEventAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default)
        => workflowEvent is ExecutorInvokedEvent or ExecutorCompletedEvent or ExecutorFailedEvent
           && ((ExecutorEvent)workflowEvent).ExecutorId == executorId
            ? ValueTask.CompletedTask
            : inner.AddEventAsync(workflowEvent, cancellationToken);

    public ValueTask SendMessageAsync(object message, string? targetId = null, CancellationToken cancellationToken = default)
        => inner.SendMessageAsync(message, targetId, cancellationToken);

    public ValueTask YieldOutputAsync(object output, CancellationToken cancellationToken = default)
        => inner.YieldOutputAsync(output, cancellationToken);

    public ValueTask RequestHaltAsync() => inner.RequestHaltAsync();

    public ValueTask<T?> ReadStateAsync<T>(string key, string? scopeName = null, CancellationToken cancellationToken = default)
        => inner.ReadStateAsync<T>(key, scopeName, cancellationToken);

    public ValueTask<T> ReadOrInitStateAsync<T>(string key, Func<T> initialStateFactory, string? scopeName = null, CancellationToken cancellationToken = default)
        => inner.ReadOrInitStateAsync(key, initialStateFactory, scopeName, cancellationToken);

    public ValueTask<HashSet<string>> ReadStateKeysAsync(string? scopeName = null, CancellationToken cancellationToken = default)
        => inner.ReadStateKeysAsync(scopeName, cancellationToken);

    public ValueTask QueueStateUpdateAsync<T>(string key, T? value, string? scopeName = null, CancellationToken cancellationToken = default)
        => inner.QueueStateUpdateAsync(key, value, scopeName, cancellationToken);

    public ValueTask QueueClearScopeAsync(string? scopeName = null, CancellationToken cancellationToken = default)
        => inner.QueueClearScopeAsync(scopeName, cancellationToken);

    public IReadOnlyDictionary<string, string>? TraceContext => inner.TraceContext;

    public bool ConcurrentRunsEnabled => inner.ConcurrentRunsEnabled;
}
