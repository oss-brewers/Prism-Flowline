# Framework executor adapter — spike report

Phase 4.1 of the embedded-host plan, design §5.2. Run on 2026-11-25 against Agent Framework
1.28.0, .NET 10. The spike is this folder: a test project outside the solution that depends on Agent
Framework only, so its answers are about what the framework permits, not about Flowline. `dotnet test`
here reproduces every finding below.

## Outcome

**All five questions pass. The adapter ships, with lifecycle hooks forwarded by reflection and a
test that fails if a later Agent Framework version renames one** (the first row of the design's
outcome table).

One finding changes the design. Delivering a message through the public
`Executor.ExecuteCoreAsync` makes the wrapped executor raise its own invoked/completed/failed events,
under the same id as the adapter's. The engine raises that pair for the adapter as well, so each
appears twice. The adapter therefore hands the wrapped executor a decorated `IWorkflowContext` that
drops the wrapped executor's lifecycle events about itself. No reflection is needed for this.

## The five questions

| # | Question | Answer | Evidence |
| - | -------- | ------ | -------- |
| 1 | Does the wrapped executor run, send and yield correctly through the adapter, including fan-in and a conditional edge? | **Yes.** Same outputs and the same side effects, in the same order, as the raw graph: fan-out to two wrapped nodes, a fan-in barrier into a wrapped join, and two conditional edges out of it | `Q1_…` |
| 2 | Are invoked and completed raised once, not twice, with the wrapped executor's id? | **Not as found; yes with the context decorator.** As found, each wrapped node's invoked and completed appear twice (`Q2a`). With the wrapped executor's own lifecycle events dropped, the lifecycle stream is identical to the raw graph's, on success (`Q2b`) and on failure (`Q2c`: one `ExecutorFailedEvent`, same id, same `WorkflowErrorEvent`) | `Q2a_…`, `Q2b_…`, `Q2c_…` |
| 3 | Does a wrapped executor that keeps state in the workflow context survive checkpoint and resume? | **Yes.** A counter visited twice through a loop resumes from the checkpoint after its first visit, in a freshly built graph with new executor instances, and continues at visit 2 | `Q3_…` |
| 4 | Can the lifecycle hooks be forwarded by reflection, and does that survive a resume? | **Yes.** Without forwarding, a counter that keeps its count in a field and saves it in `OnCheckpointingAsync` loses it and counts from zero again after a resume (`Q4a`). With `InitializeAsync`, `OnCheckpointingAsync`, `OnCheckpointRestoredAsync` and the two delivery hooks forwarded by reflection, it resumes at visit 2, exactly as unwrapped (`Q4b`, `Q4c`) | `Q4a_…`, `Q4b_…`, `Q4c_…` |
| 5 | Does a gate that parks before the wrapped executor leave no trace of it having run? | **Yes.** The adapter received the message and halted: no side effect, no state written, no send, no output, and the node after it never invoked | `Q5_…` |

## Things the spike also established

- **The wrapped executor sends its own result.** `ExecuteCoreAsync` auto-sends the handler's return
  value itself, so the adapter's routes return nothing and the adapter never sends on its behalf
  (`Note_…`). An adapter that also sent the result would deliver it twice.
- **The protocol mirrors cleanly.** `DescribeProtocol()` gives the accepted, sent and yielded types.
  The adapter registers one typed route per accepted type (by reflection over
  `RouteBuilder.AddHandler<T>`, because the types are only known at run time) or a catch-all when the
  wrapped executor accepts all. Routing, edge type checks and output filtering behave as for the raw
  executor.
- **The hooks are `protected internal`.** They are reachable on another instance only by reflection.
  That is the price of question 4, and the reason for the guard test.

## What ships

1. `WorkflowBuildContext.Node(Executor, gate)`, backed by `FrameworkExecutorAdapter`. It does what
   `HostExecutor.HandleAsync` does — announce the invocation, evaluate the gate, park or reject, run the
   executor middleware pipeline — and at the centre hands the message to the wrapped executor through a
   context that drops its own lifecycle events. It forwards the five lifecycle hooks by reflection. The
   node is listed as configurable, so a tenant can gate it without the canvas declaring a gate.
2. A test that fails when any forwarded hook can no longer be found by name and signature.
3. `Gate<T>`, regardless, for what the adapter cannot wrap.

## For Prism-RAG

- A canvas node that is an Agent Framework `Executor` instance can be wrapped. It then gets gates
  (declared, or configured per tenant), executor middleware, and the same events it raises unwrapped.
  That includes executors that keep state in fields and save it in the checkpoint hooks.
- `AIAgent` bindings, request ports and sub-workflow bindings are factories, not executor instances,
  so the adapter does not apply. Put `Gate<T>` in front of one to require approval before it runs.
  Middleware around them is not offered. Their events are recorded (agent updates stream as
  `agent.update`, responses are stored as `agent.response`).
- The adapter relies on reflection over five `protected internal` hook names. A later Agent Framework
  version that renames one fails Flowline's guard test before it can ship. It does not fail silently
  at run time.
