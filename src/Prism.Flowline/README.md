# Prism.Flowline

A headless, durable workflow host built on [Microsoft Agent Framework](https://github.com/microsoft/agent-framework)
workflows. Register workflow definitions, start instances over HTTP, and let the host own the
durable lifecycle: checkpointing, resumption after process loss, retry-vs-dead-stop policy, human
approval gates, and event streaming.

The package is headless — it serves the HTTP API and nothing else. It takes no dependency on Razor,
MVC, Entity Framework, or Redis, so referencing it does not impose a UI or a storage choice.

## Install

```bash
dotnet add package Prism.Flowline
```

Requires .NET 10 and an ASP.NET Core host (the package uses the `Microsoft.AspNetCore.App` shared
framework).

## Quick start

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddWorkflowHost(builder.Configuration)
    .AddBuiltInMiddleware()      // OpenTelemetry, request/response logging, LLM drift
    .AddBackgroundServices()     // dispatcher + approval expiry sweeper
    .AddWorkflow<OrderWorkflow>();

var app = builder.Build();
app.MapWorkflowApi();
app.Run();
```

With no storage configured the host runs entirely on in-memory implementations, so it works
standalone for local development and tests.

## Authoring a workflow

```csharp
public sealed class OrderWorkflow : IWorkflowDefinition<OrderContext, OrderResult>
{
    public string Name => "order";
    public string Version => "1.0.0";

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext ctx, CancellationToken ct)
    {
        var validate = ctx.Node(new ValidateExecutor("validate"));

        // Gating is configuration, not code: this executor is unchanged by the gate.
        var pay = ctx.Node(new PayExecutor("post-payment"), gate => gate
            .When<OrderContext>(o => o.Amount > 25_000m)
            .Reason("AmountAboveThreshold")
            .AssignTo("group:finance-approvers")
            .ExpiresAfter(TimeSpan.FromHours(8))
            .OnExpiry(ExpiryAction.DeadStop));

        return new ValueTask<Workflow>(new WorkflowBuilder(validate)
            .AddEdge(validate, pay)
            .WithOutputFrom(pay)
            .Build());
    }

    // The workflow decides what a failure means; the host applies the decision.
    public FailureDisposition Classify(WorkflowFailure f) => f.Exception switch
    {
        ApiCallFailureException { StatusCode: 409 } => FailureDisposition.DeadStop,
        InsufficientFundsException => FailureDisposition.Escalate,
        _ => DefaultFailureClassifier.Instance.Classify(f)
    };
}
```

Executors derive from `HostExecutor<TIn, TOut>` and implement `ExecuteCoreAsync`. `HandleAsync` is
sealed so gate evaluation and the middleware pipeline cannot be bypassed.

## Embedding it in your own platform

**Workflows from your own storage.** A host that keeps workflows in a database, or compiles them
from a canvas, registers a source. The registry asks it for any workflow it was not composed with:

```csharp
builder.Services
    .AddWorkflowHost(builder.Configuration)
    .AddWorkflowSource<CanvasCatalog>();

public sealed class CanvasCatalog : IWorkflowDefinitionSource
{
    public async ValueTask<ResolvedWorkflow?> FindAsync(WorkflowLookup lookup, CancellationToken ct)
        => await LoadAsync(lookup.Name, lookup.Version, lookup.TenantId, ct) is { } canvas
            ? new ResolvedWorkflow(Compile(canvas)) { TenantId = canvas.OwnerTenantId }   // null = shared
            : null;                                                                       // null = no such workflow
}
```

A source promises that a version never changes and that it answers on any replica after a restart.
Returning null means the workflow does not exist; throwing means "cannot say right now", and a run
is then retried rather than dead-stopped. A definition with a `TenantId` is private: any other tenant
is told there is no such workflow. Sourced workflows run, resume and gate like registered ones. They
are not listed by `GET /workflows`.

**Executors you did not write as host executors.** `Node` also takes any Agent Framework `Executor`.
It wraps it with the same id and protocol, so the executor can be gated (by its declaration or by
a tenant), runs inside the executor middleware pipeline, and produces the same events it would
bound raw:

```csharp
var review = ctx.Node(compiledCanvasNode, gate => gate.Mode(ExecutionMode.RequireApproval));
```

For an `AIAgent` binding, a request port or a sub-workflow, none of which is an executor instance,
put a gate in front instead:

```csharp
var approve = ctx.Gate<OrderContext>("approve-dispatch", gate => gate.Mode(ExecutionMode.RequireApproval));
var dispatch = ctx.RawNode(agent.BindAsExecutor());
```

Every event the engine raises is recorded. Agent output streams as `agent.update`, complete
responses are stored as `agent.response`, and an executor's own events are stored as `custom.*`.

## What it provides

| Area | Behaviour |
| --- | --- |
| Execution | Versioned registration, or definitions resolved from your own storage at run time; instances pin the version they started with |
| Durability | Checkpoint per superstep by default; resume from the last committed checkpoint |
| Multi-instance | Lease-based dispatch; at most one replica executes an instance at a time |
| Failure policy | The workflow's `Classify` chooses retry, dead-stop, or escalate |
| Approvals | Any executor can be gated by configuration; decisions resume the run on any replica |
| Middleware | Run-level and executor-level pipelines with `next()` semantics |
| Events | Gapless per-instance sequence, durable history, and SSE with `Last-Event-ID` resume |
| Executors | `ApiCallExecutor`, `LlmExecutor`, transform, delay, fan-in, human approval |
| Observability | OpenTelemetry spans and metrics, redacted request/response logging, LLM drift detection |

## HTTP API

`POST /workflows/{name}/instances` · `GET /instances/{id}` · `GET /instances/{id}/events` (SSE) ·
`GET /instances/{id}/events/history` · `GET /instances/{id}/graph` ·
`POST /instances/{id}/{cancel,rerun,retry,suspend,resume}` · `POST /approvals/{id}/decision`

## Swapping storage and transport

Every store and the event bus sit behind interfaces (`IInstanceStore`, `IEventStore`,
`IApprovalStore`, `ICheckpointStore<JsonElement>`, `INotificationBus`, and others). Register your own
implementation to displace the in-memory default:

```csharp
services.RemoveAll<IInstanceStore>();
services.AddSingleton<IInstanceStore, SqlServerInstanceStore>();
```

Implementations must preserve a few invariants: optimistic concurrency on instance updates, terminal
state never overwritten by a losing writer, exclusive renewable leases, unique ordered event
sequences per instance, and checkpoint indexes returned oldest-first. `Prism.Flowline.Testing` states
every one of them as a runnable case — one contract class per store, no test framework required — so
a new implementation can be checked against the same rules the in-memory stores pass.

## Links

- [Repository](https://github.com/oss-brewers/Prism-Flowline)
- [Wiki](https://github.com/oss-brewers/Prism-Flowline/blob/master/docs/wiki.md)

MIT licensed.
