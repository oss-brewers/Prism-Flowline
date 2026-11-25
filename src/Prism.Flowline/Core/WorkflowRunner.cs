using System.Text.Json;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Abstractions.Middleware;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Prism.Flowline.Core;

public sealed record RunOutcome(InstanceStatus Status, object? Result, string? TerminalReason, Exception? Exception)
{
    public static RunOutcome Completed(object? result) => new(InstanceStatus.Completed, result, null, null);
}

public sealed class WorkflowRunnerDependencies
{
    public required IWorkflowRegistry Registry { get; init; }
    public required IInstanceStore Instances { get; init; }
    public required INotificationSink Events { get; init; }
    public required NotificationSequencer Sequencer { get; init; }
    public required MiddlewarePipelineFactory Pipelines { get; init; }
    public required ICheckpointStore<JsonElement> Checkpoints { get; init; }
    public IApprovalStore? Approvals { get; init; }
    public IApprovalService? ApprovalService { get; init; }
    public IGatePolicyStore? GatePolicies { get; init; }
    public IAuditStore? Audit { get; init; }

    /// <summary>
    /// Backing store for workflow audit records. Only consulted for definitions that implement
    /// <see cref="IAuditedWorkflowDefinition"/>.
    /// </summary>
    public IAuditRecordStore? AuditRecords { get; init; }

    /// <summary>Present when the host runs the event broker; used to drop waits on termination.</summary>
    public IDomainEventSubscriptionStore? EventSubscriptions { get; init; }
    public ILogStore? Logs { get; init; }
    public IServiceProvider? Services { get; init; }
    public WorkflowHostOptions Options { get; init; } = new();
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public ILogger Logger { get; init; } = NullLogger.Instance;
}

/// <summary>
/// Owns one instance's execution on one replica: builds the graph, drives the engine, translates
/// events, and applies the workflow's failure disposition.
/// </summary>
public sealed class WorkflowRunner
{
    private readonly WorkflowRunnerDependencies _deps;
    private int _currentSuperstep;
    private string? _latestCheckpointId;
    /// <summary>
    /// Executors attached through <see cref="WorkflowBuildContext.Node(IHostExecutor, Action{ApprovalGateBuilder}?)"/>:
    /// host executors and wrapped framework executors, which may announce a delivery themselves when
    /// it parks. Fixed once the graph is built and only read afterwards.
    /// </summary>
    private readonly HashSet<string> _attached = new(StringComparer.Ordinal);

    /// <summary>
    /// Who records each delivery's <c>executor.invoked</c> for an attached executor. Normally the run
    /// loop does, from the engine's event, in stream order — after the previous node's completion,
    /// which is what the graph projection infers edges from. A delivery that parks announces itself
    /// first if the run loop has not reached its event yet, so the invocation precedes the approval.
    /// One lock covers both sides, so each delivery is recorded once whichever gets there first.
    /// </summary>
    /// <remarks>
    /// Replaces handlers announcing every delivery from inside the handler. The run loop lags the
    /// engine, so an announcement could be stored before the previous node's completion — the graph
    /// then lost the edge between them — and the queue that de-duplicated the engine's event raced the
    /// run loop and recorded some invocations twice.
    /// </remarks>
    private readonly SemaphoreSlim _invocationRecord = new(1, 1);
    private readonly Dictionary<string, int> _deliveriesStarted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _engineInvocationsSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _announcedThrough = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolved once per run rather than per event, so the hot path is a dictionary lookup. Defaults
    /// to emitting everything when the definition declares no policy.
    /// </summary>
    private NotificationPolicy _notifications = NotificationPolicy.Default;

    public WorkflowRunner(WorkflowRunnerDependencies dependencies)
        => _deps = dependencies ?? throw new ArgumentNullException(nameof(dependencies));

    public async Task<RunOutcome> RunAsync(WorkflowInstance instance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);

        WorkflowDescriptor? descriptor;
        try
        {
            descriptor = await _deps.Registry
                .ResolveAsync(instance.WorkflowName, instance.WorkflowVersion, instance.TenantId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A source that cannot say right now is an outage, not a verdict. Left to the caller it
            // would only release the lease, and the instance would be re-claimed at once, in a loop.
            return await ScheduleRetryAsync(instance, ex, cancellationToken).ConfigureAwait(false);
        }

        // Null is the verdict: the version is gone, and no number of retries brings it back.
        if (descriptor is null)
        {
            await FinalizeAsync(instance, InstanceStatus.DeadStopped, null, "WorkflowVersionUnavailable", cancellationToken)
                .ConfigureAwait(false);
            return new RunOutcome(InstanceStatus.DeadStopped, null, "WorkflowVersionUnavailable", null);
        }

        _deps.Sequencer.Seed(instance.InstanceId, await MaxSequenceAsync(instance.InstanceId, cancellationToken).ConfigureAwait(false));

        var invocation = new WorkflowInvocationContext
        {
            InstanceId = instance.InstanceId,
            TenantId = instance.TenantId,
            WorkflowName = instance.WorkflowName,
            WorkflowVersion = instance.WorkflowVersion,
            Attempt = instance.AttemptCount + 1,
            Context = instance.ContextJson,
            Services = _deps.Services
        };

        WorkflowDelegate pipeline = _deps.Pipelines.BuildWorkflowPipeline(
            (ctx, ct) => ExecuteRunAsync(instance, descriptor, ctx, ct));

        try
        {
            await pipeline(invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            invocation.Exception = ex;
        }

        if (invocation.Items.TryGetValue("outcome", out object? outcome) && outcome is RunOutcome runOutcome)
        {
            return runOutcome;
        }

        if (invocation.Exception is { } failure)
        {
            return await ApplyFailureAsync(instance, descriptor, "workflow", failure, cancellationToken).ConfigureAwait(false);
        }

        return RunOutcome.Completed(invocation.Result);
    }

    private async ValueTask ExecuteRunAsync(
        WorkflowInstance instance,
        WorkflowDescriptor descriptor,
        WorkflowInvocationContext invocation,
        CancellationToken cancellationToken)
    {
        var gates = new Dictionary<string, ApprovalGate>(StringComparer.Ordinal);

        _notifications = descriptor.Definition is INotifyingWorkflow notifying
            ? notifying.Notifications
            : NotificationPolicy.Default;

        // A definition that declares an audit record gets a recorder bound to its declared shape;
        // one that doesn't gets null, and the hook costs it nothing.
        IWorkflowAuditRecorder? auditRecorder = CreateAuditRecorder(instance, descriptor);

        var buildContext = new WorkflowBuildContext(
            instance.InstanceId, instance.TenantId, instance.WorkflowName, instance.WorkflowVersion,
            invocation.Attempt, _deps.Services,
            (executor, gate) => Attach(executor, gate, instance, invocation, gates, auditRecorder),
            auditRecorder);

        Workflow workflow = await descriptor.Definition.BuildAsync(buildContext, cancellationToken).ConfigureAwait(false);

        CheckpointManager checkpointManager = CheckpointManager.CreateJson(_deps.Checkpoints);

        await PublishAsync(instance, WorkflowEventTypes.WorkflowStarted, new
        {
            workflow = instance.WorkflowName,
            version = instance.WorkflowVersion,
            attempt = invocation.Attempt,
            resumed = instance.LatestCheckpointId is not null
        }, null, cancellationToken).ConfigureAwait(false);

        StreamingRun run = instance.LatestCheckpointId is { Length: > 0 } checkpointId
            ? await InProcessExecution.ResumeStreamingAsync(
                workflow, new CheckpointInfo(instance.InstanceId, checkpointId), checkpointManager, cancellationToken)
                .ConfigureAwait(false)
            : await StartTypedAsync(
                workflow, descriptor, DeserializeContext(descriptor, instance.ContextJson),
                checkpointManager, instance.InstanceId, cancellationToken).ConfigureAwait(false);

        RunOutcome outcome = await PumpAsync(run, instance, descriptor, invocation, cancellationToken).ConfigureAwait(false);
        invocation.Items["outcome"] = outcome;
        invocation.Result = outcome.Result;
    }

    private async Task<RunOutcome> PumpAsync(
        StreamingRun run,
        WorkflowInstance instance,
        WorkflowDescriptor descriptor,
        WorkflowInvocationContext invocation,
        CancellationToken cancellationToken)
    {
        object? output = null;

        // Every case here is listed in TranslatedEngineEvents, and the order matters: a subclass is
        // matched before its base, because three framework events are subclasses of others with a
        // different meaning (agent updates and responses are outputs; sub-workflow errors and
        // warnings are workflow errors and warnings).
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (evt)
            {
                case SuperStepStartedEvent started:
                    // Set on the way in, so the executor events of this superstep carry its number
                    // rather than the previous one's.
                    _currentSuperstep = started.StepNumber;
                    await PublishAsync(instance, WorkflowEventTypes.SuperstepStarted,
                        new { superstep = started.StepNumber }, null, cancellationToken).ConfigureAwait(false);
                    break;

                case SuperStepCompletedEvent step:
                    _currentSuperstep = step.StepNumber;
                    await PublishAsync(instance, WorkflowEventTypes.SuperstepCompleted,
                        new { superstep = step.StepNumber }, null, cancellationToken).ConfigureAwait(false);
                    break;

                case ExecutorInvokedEvent invoked:
                    await RecordEngineInvocationAsync(instance, invoked.ExecutorId, cancellationToken).ConfigureAwait(false);
                    break;

                case ExecutorCompletedEvent completed:
                    await PublishAsync(instance, WorkflowEventTypes.ExecutorCompleted,
                        new { executorId = completed.ExecutorId, superstep = _currentSuperstep },
                        completed.ExecutorId, cancellationToken).ConfigureAwait(false);
                    break;

                case ExecutorFailedEvent failed:
                {
                    Exception error = failed.Data ?? new InvalidOperationException("Executor failed without an exception.");
                    await PublishAsync(instance, WorkflowEventTypes.ExecutorFailed, new
                    {
                        executorId = failed.ExecutorId,
                        superstep = _currentSuperstep,
                        exceptionType = error.GetType().Name,
                        message = error.Message
                    }, failed.ExecutorId, cancellationToken).ConfigureAwait(false);

                    await LogAsync(instance, "Error", failed.ExecutorId, error, cancellationToken).ConfigureAwait(false);
                    return await ApplyFailureAsync(instance, descriptor, failed.ExecutorId ?? "unknown", error, cancellationToken)
                        .ConfigureAwait(false);
                }

                case RequestInfoEvent request:
                    await PublishAsync(instance, WorkflowEventTypes.RequestPending, new
                    {
                        requestId = request.Request.RequestId,
                        portId = request.Request.PortInfo.PortId
                    }, null, cancellationToken).ConfigureAwait(false);

                    await TransitionAsync(instance, InstanceStatus.AwaitingInput, null, cancellationToken).ConfigureAwait(false);
                    return new RunOutcome(InstanceStatus.AwaitingInput, null, null, null);

                // Before WorkflowOutputEvent, which it derives from. Matched there, each fragment
                // was stored as an output and became the instance's result in turn.
                case AgentResponseUpdateEvent update:
                    await PublishTransientAsync(instance, WorkflowEventTypes.AgentUpdate, new
                    {
                        executorId = update.ExecutorId,
                        text = update.Update.Text,
                        role = update.Update.Role?.Value,
                        messageId = update.Update.MessageId
                    }, update.ExecutorId, cancellationToken).ConfigureAwait(false);
                    break;

                // Also before WorkflowOutputEvent. Still the run's output candidate — the framework
                // means it as one, and an agent that ends a graph has nothing else to give — but
                // recorded as what it is, with its usage.
                case AgentResponseEvent response:
                    output = response.Response;
                    await PublishAsync(instance, WorkflowEventTypes.AgentResponse, new
                    {
                        executorId = response.ExecutorId,
                        text = response.Response.Text,
                        usage = response.Response.Usage is { } usage
                            ? new { inputTokens = usage.InputTokenCount, outputTokens = usage.OutputTokenCount, totalTokens = usage.TotalTokenCount }
                            : null
                    }, response.ExecutorId, cancellationToken).ConfigureAwait(false);
                    break;

                case WorkflowOutputEvent outputEvent:
                    output = outputEvent.Data;
                    await PublishAsync(instance, WorkflowEventTypes.WorkflowOutput,
                        new { executorId = outputEvent.ExecutorId, result = Describe(outputEvent.Data) },
                        outputEvent.ExecutorId, cancellationToken).ConfigureAwait(false);
                    break;

                // Before WorkflowErrorEvent: a failed sub-workflow is a failed node of this graph,
                // reported against the node that hosts it rather than against the whole workflow.
                case SubworkflowErrorEvent subworkflowError:
                {
                    Exception error = ErrorOf(subworkflowError);
                    await PublishAsync(instance, WorkflowEventTypes.ExecutorFailed, new
                    {
                        executorId = subworkflowError.SubworkflowId,
                        superstep = _currentSuperstep,
                        exceptionType = error.GetType().Name,
                        message = error.Message
                    }, subworkflowError.SubworkflowId, cancellationToken).ConfigureAwait(false);

                    await LogAsync(instance, "Error", subworkflowError.SubworkflowId, error, cancellationToken).ConfigureAwait(false);
                    return await ApplyFailureAsync(instance, descriptor, subworkflowError.SubworkflowId ?? "unknown", error, cancellationToken)
                        .ConfigureAwait(false);
                }

                case WorkflowErrorEvent errorEvent:
                    return await ApplyFailureAsync(instance, descriptor, "workflow", ErrorOf(errorEvent), cancellationToken)
                        .ConfigureAwait(false);

                case SubworkflowWarningEvent subworkflowWarning:
                    await PublishAsync(instance, WorkflowEventTypes.WorkflowWarning, new
                    {
                        message = subworkflowWarning.Data?.ToString(),
                        subworkflowId = subworkflowWarning.SubWorkflowId
                    }, subworkflowWarning.SubWorkflowId, cancellationToken).ConfigureAwait(false);
                    break;

                case WorkflowWarningEvent warning:
                    await PublishAsync(instance, WorkflowEventTypes.WorkflowWarning,
                        new { message = warning.Data?.ToString() }, null, cancellationToken).ConfigureAwait(false);
                    break;

                case LlmDeltaWorkflowEvent delta:
                    await PublishTransientAsync(instance, WorkflowEventTypes.LlmDelta,
                        new { executorId = delta.ExecutorId, delta = delta.Delta },
                        delta.ExecutorId, cancellationToken).ConfigureAwait(false);
                    break;

                // Flowline publishes its own workflow.started, with the attempt and whether it resumed.
                case WorkflowStartedEvent:
                    break;

                default:
                    // An event the engine raised is recorded or streamed; none is dropped. This used
                    // to log at Debug and discard, which is how llm.delta stayed broken while being
                    // written down in three places, and how a raw executor's own events vanished.
                    await PublishEngineEventAsync(instance, evt, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        // Stream ended. An approval raised during this run parks the instance rather than completing it.
        if (_deps.Approvals is not null)
        {
            IReadOnlyList<ApprovalRequest> pending =
                await _deps.Approvals.ListForInstanceAsync(instance.InstanceId, cancellationToken).ConfigureAwait(false);

            if (pending.Any(a => a.State == ApprovalState.Pending))
            {
                await TransitionAsync(instance, InstanceStatus.AwaitingApproval, null, cancellationToken).ConfigureAwait(false);
                return new RunOutcome(InstanceStatus.AwaitingApproval, null, null, null);
            }
        }

        // Same reasoning for an event wait: the executor halted and emitted nothing, so the stream
        // ended with the graph unfinished. Without this the instance would look Completed while a
        // downstream node had never run.
        if (_deps.EventSubscriptions is { } subscriptions)
        {
            IReadOnlyList<DomainEventSubscription> waits = await subscriptions.QueryAsync(new DomainSubscriptionQuery
            {
                InstanceId = instance.InstanceId,
                Kind = DomainSubscriptionKind.Wait,
                PendingOnly = true
            }, cancellationToken).ConfigureAwait(false);

            if (waits.Count > 0)
            {
                await TransitionAsync(instance, InstanceStatus.AwaitingInput, null, cancellationToken).ConfigureAwait(false);
                return new RunOutcome(InstanceStatus.AwaitingInput, null, null, null);
            }
        }

        await FinalizeAsync(instance, InstanceStatus.Completed, output, null, cancellationToken).ConfigureAwait(false);
        return RunOutcome.Completed(output);
    }

    /// <summary>
    /// Builds the per-instance audit recorder when the definition declares a record shape. Returns
    /// null otherwise — auditing is opt-in per workflow, not a tax on every one.
    /// </summary>
    private IWorkflowAuditRecorder? CreateAuditRecorder(WorkflowInstance instance, WorkflowDescriptor descriptor)
    {
        if (descriptor.Definition is not IAuditedWorkflowDefinition audited) return null;

        if (_deps.AuditRecords is not { } store)
        {
            _deps.Logger.LogWarning(
                "Workflow '{Workflow}' declares an audit record but no IAuditRecordStore is registered; auditing is off.",
                instance.WorkflowName);
            return null;
        }

        return new WorkflowAuditRecorder(
            audited.AuditRecord,
            store,
            instance.InstanceId,
            instance.WorkflowName,
            instance.WorkflowVersion,
            _deps.Clock,
            _deps.Logger);
    }

    private ExecutorBinding Attach(
        IHostExecutor executor,
        ApprovalGate gate,
        WorkflowInstance instance,
        WorkflowInvocationContext invocation,
        Dictionary<string, ApprovalGate> gates,
        IWorkflowAuditRecorder? audit)
    {
        gates[executor.Id] = gate;
        _attached.Add(executor.Id);

        // Middleware sees the executor the author wrote, not the adapter standing in for it.
        Type executorType = executor is FrameworkExecutorAdapter adapter ? adapter.Inner.GetType() : executor.GetType();

        var descriptor = new ExecutorDescriptor(
            executor.Id, executorType, instance.WorkflowName, instance.WorkflowVersion, gate.Mode)
        {
            Metadata = new Dictionary<string, object?>(executor.Metadata)
            {
                ["attempt"] = invocation.Attempt,
                ["instanceId"] = instance.InstanceId
            }
        };

        ExecutorDelegate terminal = ResolveTerminal(executor);
        ExecutorDelegate pipeline = _deps.Pipelines.BuildExecutorPipeline(descriptor, terminal);

        executor.Runtime = new HostExecutorRuntime
        {
            InstanceId = instance.InstanceId,
            TenantId = instance.TenantId,
            Descriptor = descriptor,
            Attempt = invocation.Attempt,
            Pipeline = pipeline,
            Gates = new GateEvaluator(
                instance.WorkflowName, instance.WorkflowVersion, instance.TenantId, gates,
                _deps.GatePolicies, _deps.Approvals),
            Approvals = _deps.ApprovalService,
            Services = _deps.Services,
            Audit = audit,
            Notify = new NodeNotifier(
                instance.InstanceId, instance.TenantId, executor.Id,
                _deps.Events, _deps.Sequencer, _notifications,
                () => _currentSuperstep, _deps.Clock, instance.WorkflowName),
            DeliveryStarted = StartDelivery,
            AnnounceBeforePark = (executorId, delivery) => AnnounceBeforeParkAsync(instance, executorId, delivery),
            SuperstepAccessor = () => _currentSuperstep
        };

        return (Executor)executor;
    }

    private int StartDelivery(string executorId)
    {
        lock (_deliveriesStarted)
        {
            int delivery = _deliveriesStarted.GetValueOrDefault(executorId) + 1;
            _deliveriesStarted[executorId] = delivery;
            return delivery;
        }
    }

    /// <summary>The engine's invoked event, recorded unless a parking delivery already announced it.</summary>
    private async ValueTask RecordEngineInvocationAsync(WorkflowInstance instance, string executorId, CancellationToken cancellationToken)
    {
        if (!_attached.Contains(executorId))
        {
            await PublishInvokedAsync(instance, executorId, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _invocationRecord.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int delivery = _engineInvocationsSeen.GetValueOrDefault(executorId) + 1;
            _engineInvocationsSeen[executorId] = delivery;

            if (_announcedThrough.GetValueOrDefault(executorId) < delivery)
            {
                await PublishInvokedAsync(instance, executorId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _invocationRecord.Release();
        }
    }

    /// <summary>A parking delivery's invocation, recorded unless the run loop already recorded it.</summary>
    private async ValueTask AnnounceBeforeParkAsync(WorkflowInstance instance, string executorId, int delivery)
    {
        await _invocationRecord.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_engineInvocationsSeen.GetValueOrDefault(executorId) >= delivery)
            {
                return;
            }

            _announcedThrough[executorId] = delivery;
            await PublishInvokedAsync(instance, executorId, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _invocationRecord.Release();
        }
    }

    private ValueTask PublishInvokedAsync(WorkflowInstance instance, string executorId, CancellationToken cancellationToken)
        => PublishAsync(instance, WorkflowEventTypes.ExecutorInvoked,
            new { executorId, superstep = _currentSuperstep }, executorId, cancellationToken);

    private static ExecutorDelegate ResolveTerminal(IHostExecutor executor)
    {
        // ExecuteTerminalAsync is declared on the closed generic base; bind it without reflection cost
        // per invocation by resolving the delegate once here.
        Type type = executor.GetType();
        System.Reflection.MethodInfo? method = type.GetMethod(
            nameof(HostExecutor<object, object>.ExecuteTerminalAsync),
            [typeof(ExecutorInvocationContext), typeof(CancellationToken)]);

        if (method is null)
        {
            throw new InvalidOperationException($"Executor '{executor.Id}' does not expose a terminal delegate.");
        }

        return (ExecutorDelegate)Delegate.CreateDelegate(typeof(ExecutorDelegate), executor, method);
    }

    private async Task<RunOutcome> ApplyFailureAsync(
        WorkflowInstance instance,
        WorkflowDescriptor descriptor,
        string executorId,
        Exception error,
        CancellationToken cancellationToken)
    {
        var failure = new WorkflowFailure(
            executorId, error, instance.AttemptCount + 1, _currentSuperstep, new Dictionary<string, object?>());

        FailureDisposition disposition;
        try
        {
            disposition = descriptor.Definition.Classify(failure);
        }
        catch (Exception classifierError)
        {
            _deps.Logger.LogError(classifierError,
                "Failure classifier threw for instance {InstanceId}; treating as DeadStop.", instance.InstanceId);
            disposition = FailureDisposition.DeadStop;
        }

        return await ApplyDispositionAsync(instance, disposition, error, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A failure with no definition to classify it — the registry could not resolve one — treated
    /// as a classified <see cref="FailureDisposition.Retry"/>: the same attempts, lifetime and
    /// backoff, and the same <see cref="InstanceStatus.Failed"/> once they run out.
    /// </summary>
    private Task<RunOutcome> ScheduleRetryAsync(WorkflowInstance instance, Exception error, CancellationToken cancellationToken)
    {
        _deps.Logger.LogWarning(error,
            "Could not resolve {Workflow} {Version} for instance {InstanceId}; scheduling a retry.",
            instance.WorkflowName, instance.WorkflowVersion, instance.InstanceId);

        return ApplyDispositionAsync(instance, FailureDisposition.Retry, error, cancellationToken);
    }

    private async Task<RunOutcome> ApplyDispositionAsync(
        WorkflowInstance instance, FailureDisposition disposition, Exception error, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _deps.Clock.GetUtcNow();
        int nextAttempt = instance.AttemptCount + 1;
        bool attemptsExhausted = nextAttempt >= instance.MaxAttempts;
        bool lifetimeExceeded = now - instance.CreatedAt > _deps.Options.Retry.MaxLifetime;

        switch (disposition)
        {
            case FailureDisposition.Retry when !attemptsExhausted && !lifetimeExceeded:
            {
                TimeSpan delay = Backoff.Exponential(
                    nextAttempt, _deps.Options.Retry.BackoffBase, _deps.Options.Retry.BackoffCap, _deps.Options.Retry.Jitter);

                await _deps.Instances.UpdateAsync(instance.InstanceId, m =>
                {
                    m.Status = InstanceStatus.RetryScheduled;
                    m.AttemptCount = nextAttempt;
                    m.NextRetryAt = now + delay;
                    m.TerminalReason = Describe(error);
                    m.ClearLease = true;
                }, cancellationToken).ConfigureAwait(false);

                return new RunOutcome(InstanceStatus.RetryScheduled, null, Describe(error), error);
            }

            case FailureDisposition.Escalate:
                await _deps.Instances.UpdateAsync(instance.InstanceId, m =>
                {
                    m.Status = InstanceStatus.AwaitingInput;
                    m.AttemptCount = nextAttempt;
                    m.TerminalReason = Describe(error);
                    m.ClearLease = true;
                }, cancellationToken).ConfigureAwait(false);

                return new RunOutcome(InstanceStatus.AwaitingInput, null, Describe(error), error);

            case FailureDisposition.DeadStop:
                await FinalizeAsync(instance, InstanceStatus.DeadStopped, null, Describe(error), cancellationToken)
                    .ConfigureAwait(false);
                return new RunOutcome(InstanceStatus.DeadStopped, null, Describe(error), error);

            case FailureDisposition.Retry:
            default:
                await FinalizeAsync(instance, InstanceStatus.Failed, null, Describe(error), cancellationToken)
                    .ConfigureAwait(false);
                return new RunOutcome(InstanceStatus.Failed, null, Describe(error), error);
        }
    }

    private async Task FinalizeAsync(
        WorkflowInstance instance, InstanceStatus status, object? result, string? reason, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _deps.Clock.GetUtcNow();

        await _deps.Instances.UpdateAsync(instance.InstanceId, m =>
        {
            m.Status = status;
            m.CompletedAt = now;
            m.TerminalReason = reason;
            m.ResultJson = result is null ? null : SafeSerialize(result);
            m.ClearLease = true;
        }, cancellationToken).ConfigureAwait(false);

        // A wait outliving its instance would keep matching messages forever and log a warning for
        // each one. Terminal means nothing is listening any more.
        if (_deps.EventSubscriptions is { } subscriptions)
        {
            await subscriptions.RemoveForInstanceAsync(instance.InstanceId, cancellationToken).ConfigureAwait(false);
        }

        await PublishAsync(instance, WorkflowEventTypes.WorkflowTerminated, new
        {
            status = status.ToString(),
            reason,
            correlationId = instance.CorrelationId
        }, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task TransitionAsync(
        WorkflowInstance instance, InstanceStatus status, string? reason, CancellationToken cancellationToken)
        => await _deps.Instances.UpdateAsync(instance.InstanceId, m =>
        {
            m.Status = status;
            m.TerminalReason = reason;
            m.LatestCheckpointId = _latestCheckpointId;
            m.ClearLease = true;
        }, cancellationToken).ConfigureAwait(false);

    private ValueTask PublishAsync(
        WorkflowInstance instance, string eventType, object payload, string? executorId, CancellationToken cancellationToken)
    {
        // Before Next(), never after. A suppressed event that had already consumed a sequence number
        // would leave a hole in the gapless sequence, and Last-Event-ID catch-up would wait forever
        // for an event that is never coming.
        if (!_notifications.ShouldEmit(eventType, executorId))
        {
            return ValueTask.CompletedTask;
        }

        return _deps.Events.PublishAsync(
            NotificationFactory.Create(
                instance.InstanceId, _deps.Sequencer.Next(instance.InstanceId), eventType, payload,
                executorId, _currentSuperstep, instance.TenantId, _deps.Clock.GetUtcNow(),
                instance.WorkflowName, _notifications.DeliveryFor(EventDeliveryMode.StreamAndLog)),
            cancellationToken);
    }

    /// <summary>
    /// Live fan-out with no durable record and no sequence number. Used for streamed tokens, where
    /// the complete text is in the executor's output and a replayed chunk would mean nothing.
    /// </summary>
    private ValueTask PublishTransientAsync(
        WorkflowInstance instance, string eventType, object payload, string? executorId, CancellationToken cancellationToken)
    {
        if (!_notifications.ShouldEmit(eventType, executorId))
        {
            return ValueTask.CompletedTask;
        }

        // Stream-only under a log-only workflow means nowhere, which is the correct reading: opting
        // out of streaming opts out of streamed tokens too.
        if (_notifications.IsLogOnly)
        {
            return ValueTask.CompletedTask;
        }

        return _deps.Events.PublishAsync(
            NotificationFactory.Create(
                instance.InstanceId, 0, eventType, payload,
                executorId, _currentSuperstep, instance.TenantId, _deps.Clock.GetUtcNow(),
                instance.WorkflowName, EventDeliveryMode.StreamOnly),
            cancellationToken);
    }

    /// <summary>
    /// The engine event types the run loop translates by name. Kept beside the switch so an Agent
    /// Framework upgrade that adds an event fails a test that enumerates the framework's events,
    /// instead of the new event quietly taking the <c>custom.*</c> fallback.
    /// </summary>
    internal static IReadOnlyCollection<Type> TranslatedEngineEvents { get; } =
    [
        typeof(SuperStepStartedEvent), typeof(SuperStepCompletedEvent),
        typeof(ExecutorInvokedEvent), typeof(ExecutorCompletedEvent), typeof(ExecutorFailedEvent),
        typeof(RequestInfoEvent),
        typeof(AgentResponseUpdateEvent), typeof(AgentResponseEvent), typeof(WorkflowOutputEvent),
        typeof(SubworkflowErrorEvent), typeof(WorkflowErrorEvent),
        typeof(SubworkflowWarningEvent), typeof(WorkflowWarningEvent),
        typeof(WorkflowStartedEvent),
        typeof(LlmDeltaWorkflowEvent)
    ];

    /// <summary>
    /// Records an engine event the run loop has no case for, as <c>custom.&lt;kebab-case type
    /// name&gt;</c> with its data serialised.
    /// </summary>
    /// <remarks>
    /// The <c>custom.</c> prefix is the one <see cref="INodeNotifier.NotifyAsync"/> already uses, so an
    /// executor's own events share a namespace with workflow-defined ones: they cannot shadow a
    /// framework event, they pass through redaction, and the workflow's notification policy still
    /// decides whether they are emitted.
    /// </remarks>
    private ValueTask PublishEngineEventAsync(WorkflowInstance instance, WorkflowEvent evt, CancellationToken cancellationToken)
    {
        Type type = evt.GetType();

        // The framework's own non-public events are signals between its parts — a halt request, an
        // internal stop — not things that happened in the workflow.
        if (type.Assembly == typeof(WorkflowEvent).Assembly && !type.IsVisible)
        {
            return ValueTask.CompletedTask;
        }

        string? executorId = (evt as ExecutorEvent)?.ExecutorId;

        return PublishAsync(instance, NodeNotifier.CustomPrefix + KebabName(type), new
        {
            executorId,
            eventType = type.FullName,
            data = ToJsonValue(evt.Data)
        }, executorId, cancellationToken);
    }

    /// <summary><c>MagenticPlanCreatedEvent</c> becomes <c>magentic-plan-created</c>.</summary>
    internal static string KebabName(Type type)
    {
        string name = type.Name;

        int arity = name.IndexOf('`', StringComparison.Ordinal);
        if (arity >= 0)
        {
            name = name[..arity];
        }

        // Every engine event ends in "Event"; repeating it in the name adds nothing.
        if (name.Length > "Event".Length && name.EndsWith("Event", StringComparison.Ordinal))
        {
            name = name[..^"Event".Length];
        }

        var kebab = new System.Text.StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c) && i > 0 &&
                (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) ||
                 (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
            {
                kebab.Append('-');
            }

            kebab.Append(char.ToLowerInvariant(c));
        }

        return kebab.ToString();
    }

    /// <summary>
    /// The event's data as JSON when it serialises, and as text when it does not: an unknown type's
    /// payload is recorded either way rather than costing the event.
    /// </summary>
    private static object? ToJsonValue(object? data)
    {
        if (data is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.SerializeToElement(data, data.GetType(), JsonOptions.Default);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return data.ToString();
        }
    }

    private static Exception ErrorOf(WorkflowErrorEvent errorEvent)
        => errorEvent.Exception
           ?? errorEvent.Data as Exception
           ?? new InvalidOperationException(errorEvent.Data?.ToString() ?? "Workflow error.");

    private ValueTask LogAsync(
        WorkflowInstance instance, string level, string? executorId, Exception error, CancellationToken cancellationToken)
        => _deps.Logs is null
            ? ValueTask.CompletedTask
            : _deps.Logs.AppendAsync(new InstanceLogEntry
            {
                InstanceId = instance.InstanceId,
                Sequence = _deps.Sequencer.Peek(instance.InstanceId),
                Level = level,
                ExecutorId = executorId,
                Superstep = _currentSuperstep,
                Message = error.Message,
                ExceptionType = error.GetType().FullName,
                StackTrace = error.StackTrace,
                LoggedAt = _deps.Clock.GetUtcNow()
            }, cancellationToken);

    private async ValueTask<long> MaxSequenceAsync(string instanceId, CancellationToken cancellationToken)
    {
        if (_deps.Services?.GetService(typeof(IEventStore)) is IEventStore store)
        {
            return await store.MaxSequenceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }
        return _deps.Sequencer.Peek(instanceId);
    }

    /// <summary>
    /// Starts the run with the context bound to its <em>declared</em> type.
    /// </summary>
    /// <remarks>
    /// <c>RunStreamingAsync&lt;TInput&gt;</c> infers <c>TInput</c> from the static type of the argument.
    /// Passing an <c>object</c>-typed context would make the start message an <c>object</c>, which
    /// matches no typed executor route — the workflow would complete having invoked nothing. The
    /// generic method is therefore closed over the descriptor's context type at run time.
    /// </remarks>
    private static async ValueTask<StreamingRun> StartTypedAsync(
        Workflow workflow,
        WorkflowDescriptor descriptor,
        object context,
        CheckpointManager checkpointManager,
        string sessionId,
        CancellationToken cancellationToken)
    {
        System.Reflection.MethodInfo definition = typeof(InProcessExecution)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Single(m => m.Name == nameof(InProcessExecution.RunStreamingAsync)
                         && m.IsGenericMethodDefinition
                         && m.GetParameters().Length == 5
                         && m.GetParameters()[2].ParameterType == typeof(CheckpointManager));

        object? result = definition
            .MakeGenericMethod(context.GetType())
            .Invoke(null, [workflow, context, checkpointManager, sessionId, cancellationToken]);

        return await (ValueTask<StreamingRun>)result!;
    }

    private static object DeserializeContext(WorkflowDescriptor descriptor, string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
        {
            return Activator.CreateInstance(descriptor.ContextType)
                ?? throw new InvalidOperationException($"Cannot construct empty context of '{descriptor.ContextType.Name}'.");
        }

        return JsonSerializer.Deserialize(contextJson, descriptor.ContextType, JsonOptions.Default)
            ?? throw new WorkflowValidationException("context", "Context payload deserialized to null.");
    }

    private static string Describe(Exception error) => $"{error.GetType().Name}: {error.Message}";

    private static string? Describe(object? value)
        => value is null ? null : SafeSerialize(value);

    private static string SafeSerialize(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value, JsonOptions.Default);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return JsonSerializer.Serialize(value.ToString());
        }
    }
}
