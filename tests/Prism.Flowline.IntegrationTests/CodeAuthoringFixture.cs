using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Abstractions.Middleware;
using Prism.Flowline.Api;
using Prism.Flowline.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Prism.Flowline.IntegrationTests;

/// <summary>
/// Records the input each node actually received, which is what makes an approved-with-modification
/// run distinguishable from an ordinary one: the assertion is about the value the node saw, not
/// about the decision the API accepted.
/// </summary>
public sealed class InputLedger
{
    private readonly ConcurrentBag<(string ExecutorId, string InstanceId, string OrderId, decimal Amount)> _seen = [];

    public void Record(string executorId, string instanceId, OrderContext input)
        => _seen.Add((executorId, instanceId, input.OrderId, input.Amount));

    public IReadOnlyList<(string ExecutorId, string InstanceId, string OrderId, decimal Amount)> Seen => [.. _seen];

    public (string OrderId, decimal Amount)? For(string executorId, string instanceId)
    {
        var match = Seen.Where(s => s.ExecutorId == executorId && s.InstanceId == instanceId).ToArray();
        return match.Length == 0 ? null : (match[^1].OrderId, match[^1].Amount);
    }
}

/// <summary>Counts attempts per instance so a workflow can fail once and then succeed.</summary>
public sealed class AttemptLedger
{
    private readonly ConcurrentDictionary<string, int> _attempts = new();

    public int Next(string instanceId) => _attempts.AddOrUpdate(instanceId, 1, (_, n) => n + 1);

    public int CountFor(string instanceId) => _attempts.TryGetValue(instanceId, out int n) ? n : 0;
}

/// <summary>A node that records what it saw and passes the context on unchanged.</summary>
internal sealed class RecordingStep(string id, InputLedger ledger, string instanceId)
    : HostExecutor<OrderContext, OrderContext>(id)
{
    protected override ValueTask<OrderContext> ExecuteCoreAsync(
        OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        ledger.Record(Id, instanceId, input);
        return ValueTask.FromResult(input);
    }
}

/// <summary>A terminal node that records what it saw and settles the run.</summary>
internal sealed class RecordingFinish(string id, InputLedger ledger, string instanceId)
    : HostExecutor<OrderContext, OrderResult>(id)
{
    protected override ValueTask<OrderResult> ExecuteCoreAsync(
        OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        ledger.Record(Id, instanceId, input);
        return ValueTask.FromResult(new OrderResult(input.OrderId, "settled"));
    }
}

/// <summary>
/// Two nodes, the second behind a gate the test supplies. One shape covers every gate variation the
/// builder exposes, so each workflow below differs only in the gate it declares.
/// </summary>
internal abstract class GatedShape(InputLedger ledger) : IWorkflowDefinition<OrderContext, OrderResult>
{
    public abstract string Name { get; }
    public string Version => "1.0.0";

    protected abstract void Gate(ApprovalGateBuilder gate);

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding prepare = context.Node(new RecordingStep("prepare", ledger, context.InstanceId));
        ExecutorBinding settle = context.Node(
            new RecordingFinish("settle", ledger, context.InstanceId), Gate);

        return new ValueTask<Workflow>(new WorkflowBuilder(prepare)
            .AddEdge(prepare, settle)
            .WithOutputFrom(settle)
            .WithName(Name)
            .Build());
    }
}

/// <summary>Dual control: one vote is not enough, and the second must come from someone else.</summary>
internal sealed class QuorumWorkflow(InputLedger ledger) : GatedShape(ledger)
{
    public override string Name => "quorum-order";

    protected override void Gate(ApprovalGateBuilder gate) => gate
        .Mode(ExecutionMode.RequireApproval)
        .Reason("DualControl")
        .RequireApprovers(2);
}

/// <summary>Named assignees, so a principal outside the list is refused.</summary>
internal sealed class AssignedWorkflow(InputLedger ledger) : GatedShape(ledger)
{
    public override string Name => "assigned-order";

    protected override void Gate(ApprovalGateBuilder gate) => gate
        .Mode(ExecutionMode.RequireApproval)
        .Reason("NamedApprover")
        .AssignTo("alice", "group:finance");
}

/// <summary>Segregation of duties declared on the gate.</summary>
internal sealed class SegregatedWorkflow(InputLedger ledger) : GatedShape(ledger)
{
    public override string Name => "segregated-order";

    protected override void Gate(ApprovalGateBuilder gate) => gate
        .Mode(ExecutionMode.RequireApproval)
        .Reason("FourEyes")
        .RequireSegregationOfDuties();
}

/// <summary>Modification permitted: an approver may change the input the node runs on.</summary>
internal sealed class ModifiableWorkflow(InputLedger ledger) : GatedShape(ledger)
{
    public override string Name => "modifiable-order";

    protected override void Gate(ApprovalGateBuilder gate) => gate
        .Mode(ExecutionMode.RequireApproval)
        .Reason("MayAmend")
        .AllowModification();
}

/// <summary>Modification withheld, which is the default and the case worth pinning.</summary>
internal sealed class FixedInputWorkflow(InputLedger ledger) : GatedShape(ledger)
{
    public override string Name => "fixed-input-order";

    protected override void Gate(ApprovalGateBuilder gate) => gate
        .Mode(ExecutionMode.RequireApproval)
        .Reason("AsSubmitted");
}

/// <summary>
/// One workflow per expiry action. The window is the shortest the builder accepts, so the request is
/// already due by the time the first sweep looks at it.
/// </summary>
internal sealed class ExpiringWorkflow(InputLedger ledger, string name, ExpiryAction action, string[] escalateTo)
    : GatedShape(ledger)
{
    public override string Name => name;

    protected override void Gate(ApprovalGateBuilder gate) => gate
        .Mode(ExecutionMode.RequireApproval)
        .Reason("ShortWindow")
        .ExpiresAfter(TimeSpan.FromMilliseconds(1))
        .OnExpiry(action, escalateTo);
}

/// <summary>
/// Mixes an attached node with a <see cref="WorkflowBuildContext.RawNode"/> binding — the escape
/// hatch for executors that run outside the host pipeline and therefore cannot be gated.
/// </summary>
internal sealed class RawNodeWorkflow(InputLedger ledger) : IWorkflowDefinition<OrderContext, OrderResult>
{
    public string Name => "raw-order";
    public string Version => "1.0.0";

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding prepare = context.Node(new RecordingStep("prepare", ledger, context.InstanceId));
        ExecutorBinding settle = context.RawNode(
            (Executor)new RecordingFinish("raw-settle", ledger, context.InstanceId));

        return new ValueTask<Workflow>(new WorkflowBuilder(prepare)
            .AddEdge(prepare, settle)
            .WithOutputFrom(settle)
            .WithName(Name)
            .Build());
    }
}

/// <summary>Classifies its own failures as retryable, and fails only on the first attempt.</summary>
internal sealed class RetryingWorkflow(AttemptLedger attempts) : IWorkflowDefinition<OrderContext, OrderResult>
{
    public string Name => "retrying-order";
    public string Version => "1.0.0";

    public FailureDisposition Classify(WorkflowFailure failure)
        => failure.Exception is InvalidOperationException
            ? FailureDisposition.Retry
            : DefaultFailureClassifier.Instance.Classify(failure);

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding flaky = context.Node(new Flaky("flaky", attempts, context.InstanceId));

        return new ValueTask<Workflow>(new WorkflowBuilder(flaky)
            .WithOutputFrom(flaky)
            .WithName(Name)
            .Build());
    }

    private sealed class Flaky(string id, AttemptLedger attempts, string instanceId)
        : HostExecutor<OrderContext, OrderResult>(id)
    {
        protected override ValueTask<OrderResult> ExecuteCoreAsync(
            OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            if (attempts.Next(instanceId) == 1)
            {
                throw new InvalidOperationException("Transient dependency failure.");
            }

            return ValueTask.FromResult(new OrderResult(input.OrderId, "settled-after-retry"));
        }
    }
}

/// <summary>Classifies its own failures as needing a human, which parks rather than stops.</summary>
internal sealed class EscalatingWorkflow : IWorkflowDefinition<OrderContext, OrderResult>
{
    public string Name => "escalating-order";
    public string Version => "1.0.0";

    public FailureDisposition Classify(WorkflowFailure failure) => FailureDisposition.Escalate;

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding fails = context.Node(new Fails("fails"));

        return new ValueTask<Workflow>(new WorkflowBuilder(fails)
            .WithOutputFrom(fails)
            .WithName(Name)
            .Build());
    }

    private sealed class Fails(string id) : HostExecutor<OrderContext, OrderResult>(id)
    {
        protected override ValueTask<OrderResult> ExecuteCoreAsync(
            OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Needs a human.");
    }
}

/// <summary>
/// Validates its own start context, which is the seam the DSL uses for its declared context schema
/// and which a compiled definition may implement for the same effect.
/// </summary>
internal sealed class ValidatingWorkflow : IWorkflowDefinition<OrderContext, OrderResult>, IContextValidatingWorkflow
{
    public string Name => "validating-order";
    public string Version => "1.0.0";

    public ContextValidationResult ValidateContext(JsonElement context)
    {
        if (context.ValueKind == JsonValueKind.Object &&
            context.TryGetProperty("amount", out JsonElement amount) &&
            amount.TryGetDecimal(out decimal value) &&
            value <= 0m)
        {
            return new ContextValidationResult(false, new Dictionary<string, string[]>
            {
                ["/amount"] = ["Amount must be greater than zero."]
            });
        }

        return ContextValidationResult.Valid;
    }

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding only = context.Node(new Settle("only"));

        return new ValueTask<Workflow>(new WorkflowBuilder(only)
            .WithOutputFrom(only)
            .WithName(Name)
            .Build());
    }

    private sealed class Settle(string id) : HostExecutor<OrderContext, OrderResult>(id)
    {
        protected override ValueTask<OrderResult> ExecuteCoreAsync(
            OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new OrderResult(input.OrderId, "validated"));
    }
}

/// <summary>Records the order middleware ran in, so the pipeline is observable from a test.</summary>
public sealed class MiddlewareTrace
{
    // Ordered, not a bag: half of what a pipeline test asserts is the sequence, and a
    // ConcurrentBag enumerates in whatever order it likes.
    private readonly List<string> _entries = [];
    private readonly object _sync = new();

    public void Add(string entry)
    {
        lock (_sync) { _entries.Add(entry); }
    }

    public IReadOnlyList<string> Entries
    {
        get { lock (_sync) { return [.. _entries]; } }
    }

    public IEnumerable<string> For(string workflowName)
        => Entries.Where(e => e.Contains($"({workflowName})", StringComparison.Ordinal));

    /// <summary>
    /// Waits for an entry to appear. The outermost middleware records its tail after the run has
    /// written its terminal status, so a test that observed Completed can still be ahead of it.
    /// </summary>
    public async Task<bool> WaitForAsync(string entry, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));

        while (DateTime.UtcNow < deadline)
        {
            if (Entries.Contains(entry)) return true;
            await Task.Delay(25);
        }

        return false;
    }
}

internal sealed class TracingWorkflowMiddleware(MiddlewareTrace trace) : IWorkflowMiddleware
{
    public int Order => 10;

    public async ValueTask InvokeAsync(
        WorkflowInvocationContext context, WorkflowDelegate next, CancellationToken cancellationToken)
    {
        trace.Add($"workflow:before({context.WorkflowName})");
        await next(context, cancellationToken);
        trace.Add($"workflow:after({context.WorkflowName})");
    }
}

internal sealed class TracingExecutorMiddleware(MiddlewareTrace trace) : IExecutorMiddleware
{
    public int Order => 20;

    public async ValueTask InvokeAsync(
        ExecutorInvocationContext context, ExecutorDelegate next, CancellationToken cancellationToken)
    {
        trace.Add($"executor:before:{context.Descriptor.ExecutorId}({context.Descriptor.WorkflowName})");
        await next(context, cancellationToken);
        trace.Add($"executor:after:{context.Descriptor.ExecutorId}({context.Descriptor.WorkflowName})");
    }
}

/// <summary>
/// A host carrying one compiled workflow per authoring variation the builder exposes. Kept apart
/// from <see cref="HostFixture"/> so a gate that expires on sight, or a definition that classifies
/// every failure as retryable, cannot perturb the suites that assume the ordinary shapes.
/// </summary>
public sealed class CodeAuthoringHostFixture : WebApplicationFactory<Program>
{
    public InputLedger Inputs { get; } = new();
    public AttemptLedger Attempts { get; } = new();
    public MiddlewareTrace Trace { get; } = new();

    private readonly string _auditDatabasePath =
        Path.Combine(Path.GetTempPath(), $"prism-authoring-audit-{Guid.NewGuid():N}.db");

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Inputs);
            services.AddSingleton(Attempts);
            services.AddSingleton(Trace);

            services.AddSingleton<IWorkflowDefinition>(new QuorumWorkflow(Inputs));
            services.AddSingleton<IWorkflowDefinition>(new AssignedWorkflow(Inputs));
            services.AddSingleton<IWorkflowDefinition>(new SegregatedWorkflow(Inputs));
            services.AddSingleton<IWorkflowDefinition>(new ModifiableWorkflow(Inputs));
            services.AddSingleton<IWorkflowDefinition>(new FixedInputWorkflow(Inputs));
            services.AddSingleton<IWorkflowDefinition>(new RawNodeWorkflow(Inputs));
            services.AddSingleton<IWorkflowDefinition>(new RetryingWorkflow(Attempts));
            services.AddSingleton<IWorkflowDefinition>(new EscalatingWorkflow());
            services.AddSingleton<IWorkflowDefinition>(new ValidatingWorkflow());

            services.AddSingleton<IWorkflowDefinition>(
                new ExpiringWorkflow(Inputs, "expire-dead-stop", ExpiryAction.DeadStop, []));
            services.AddSingleton<IWorkflowDefinition>(
                new ExpiringWorkflow(Inputs, "expire-reject", ExpiryAction.Reject, []));
            services.AddSingleton<IWorkflowDefinition>(
                new ExpiringWorkflow(Inputs, "expire-auto-approve", ExpiryAction.AutoApprove, []));
            services.AddSingleton<IWorkflowDefinition>(
                new ExpiringWorkflow(Inputs, "expire-escalate", ExpiryAction.Escalate, ["group:risk"]));

            services.AddSingleton<IWorkflowMiddleware>(new TracingWorkflowMiddleware(Trace));
            services.AddSingleton<IExecutorMiddleware>(new TracingExecutorMiddleware(Trace));
        });

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The expiry suites drive the sweep themselves, one call at a time, because escalation is a
        // two-stage rule and a background sweeper firing between the stages would make which stage
        // an assertion saw a matter of timing. Pushed far out rather than off, since there is no
        // switch for off.
        builder.UseSetting("WorkflowHost:Approvals:SweepIntervalSeconds", "3600");
        builder.UseSetting("WorkflowHost:Retry:BackoffBaseSeconds", "0.05");
        builder.UseSetting("WorkflowHost:Retry:BackoffCapSeconds", "0.2");
        builder.UseSetting("Prism:AuditRecords:ConnectionString", $"Data Source={_auditDatabasePath}");
    }

    public T Resolve<T>() where T : notnull => Services.GetRequiredService<T>();

    public async Task<WorkflowInstance> WaitForStatusAsync(string instanceId, params InstanceStatus[] expected)
    {
        var store = Resolve<IInstanceStore>();
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            WorkflowInstance? instance = await store.GetAsync(instanceId, default);
            if (instance is not null && expected.Contains(instance.Status))
            {
                return instance;
            }

            await Task.Delay(25);
        }

        WorkflowInstance? last = await store.GetAsync(instanceId, default);
        throw new TimeoutException(
            $"Instance '{instanceId}' was '{last?.Status.ToString() ?? "missing"}', " +
            $"expected one of {string.Join(", ", expected)}. Reason: {last?.TerminalReason ?? "(none)"}");
    }

    public async Task<string> StartAsync(HttpClient client, string workflow, object context)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/workflows/{workflow}/instances", new { context });

        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("instanceId").GetString()!;
    }

    /// <summary>Starts a run and waits for it to park on its gate, returning the approval id.</summary>
    public async Task<(string InstanceId, string ApprovalId)> StartAndParkAsync(
        HttpClient client, string workflow, OrderContext context)
    {
        string instanceId = await StartAsync(client, workflow, context);
        await WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);

        JsonElement body = await client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/approvals");
        string approvalId = body.GetProperty("items").EnumerateArray().First()
            .GetProperty("approvalId").GetString()!;

        return (instanceId, approvalId);
    }

    /// <summary>
    /// Applies a decision as a named principal. The HTTP endpoint takes its decider from the
    /// request's identity, and every test request is anonymous, so quorum and assignee rules are
    /// only reachable through the service with an explicit decider.
    /// </summary>
    public ValueTask<DecisionResult> DecideAsync(
        string approvalId, string deciderId, ApprovalOutcomeKind outcome,
        JsonElement? modifiedInput = null, string? comment = null)
        => Resolve<IApprovalService>().ApplyDecisionAsync(
            approvalId,
            new ApprovalDecisionInput
            {
                Decision = outcome,
                Comment = comment,
                ModifiedInput = modifiedInput,
                DeciderIdOverride = deciderId
            },
            user: null,
            cancellationToken: default);

    /// <summary>Runs the expiry sweep now rather than waiting on the background interval.</summary>
    public ValueTask<int> SweepAsync() => Resolve<IApprovalService>().SweepExpiredAsync(default);

    public async Task<ApprovalRequest?> ApprovalAsync(string approvalId)
        => await Resolve<IApprovalStore>().GetAsync(approvalId, default);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        try
        {
            if (File.Exists(_auditDatabasePath)) File.Delete(_auditDatabasePath);
        }
        catch (IOException)
        {
        }
    }
}
