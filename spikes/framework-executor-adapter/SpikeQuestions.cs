using Microsoft.Agents.AI.Workflows;
using Xunit;
using Xunit.Abstractions;

namespace FrameworkExecutorAdapterSpike;

/// <summary>
/// The five questions of design §5.2, each asked of the same graph raw and wrapped. Output is written
/// to the test log so the report can quote it.
/// </summary>
public class SpikeQuestions(ITestOutputHelper log)
{
    // ---- 1. Runs, sends and yields correctly, through fan-out, fan-in and a conditional edge ----

    [Fact]
    public async Task Q1_a_wrapped_executor_runs_sends_and_yields_through_fan_out_fan_in_and_conditional_edges()
    {
        (Run raw, Ledger rawLedger) = await RunBranchingAsync(wrap: false);
        (Run wrapped, Ledger wrappedLedger) = await RunBranchingAsync(wrap: true);

        Dump("raw", raw);
        Dump("wrapped", wrapped);

        Assert.Equal(raw.Outputs.Order(), wrapped.Outputs.Order());
        Assert.Equal(rawLedger.Entries.Order(), wrappedLedger.Entries.Order());
        Assert.Empty(wrapped.Errors);
    }

    // ---- 2. Invoked and completed raised once, with the wrapped executor's id ----

    [Fact]
    public async Task Q2a_as_found_the_inner_executor_raises_a_second_pair_under_the_same_id()
    {
        (Run raw, _) = await RunBranchingAsync(wrap: false);
        (Run wrapped, _) = await RunBranchingAsync(wrap: true, filterInnerLifecycle: false);

        log.WriteLine("raw:     " + string.Join(" ", raw.Lifecycle.Select(l => $"{l.Type[8..^5]}:{l.ExecutorId}")));
        log.WriteLine("wrapped: " + string.Join(" ", wrapped.Lifecycle.Select(l => $"{l.Type[8..^5]}:{l.ExecutorId}")));

        Assert.Equal(raw.Lifecycle.Count * 2 - 2, wrapped.Lifecycle.Count);   // every node but the raw source, twice
    }

    [Fact]
    public async Task Q2b_invoked_and_completed_are_raised_once_when_the_adapter_filters_the_inner_pair()
    {
        (Run raw, _) = await RunBranchingAsync(wrap: false);
        (Run wrapped, _) = await RunBranchingAsync(wrap: true, filterInnerLifecycle: true);

        Assert.Equal(raw.Lifecycle, wrapped.Lifecycle);
        Assert.Equal(raw.Outputs.Order(), wrapped.Outputs.Order());
    }

    [Fact]
    public async Task Q2c_a_wrapped_executor_that_throws_raises_one_failure_with_its_id()
    {
        async Task<Run> RunFailingAsync(bool wrap)
        {
            var source = new Source("source");
            ExecutorBinding broken = wrap ? new SpikeAdapter(new Broken("broken")) : new Broken("broken");
            Workflow workflow = new WorkflowBuilder(source).AddEdge(source, broken).WithOutputFrom(broken).Build();
            return await RunAsync(workflow, 1);
        }

        Run raw = await RunFailingAsync(wrap: false);
        Run wrapped = await RunFailingAsync(wrap: true);
        Dump("failing, raw", raw);
        Dump("failing, wrapped", wrapped);

        Assert.Equal(raw.Lifecycle, wrapped.Lifecycle);
        Assert.Single(wrapped.Lifecycle, l => l.Type == nameof(ExecutorFailedEvent) && l.ExecutorId == "broken");
        Assert.Contains(wrapped.Errors, e => e.Contains("broken by design"));
    }

    // ---- 3. Context-held state survives checkpoint and resume ----

    [Fact]
    public async Task Q3_context_held_state_survives_checkpoint_and_resume_when_wrapped()
    {
        var ledger = new Ledger();
        Workflow Build() => CounterGraph(new SpikeAdapter(new ContextCounter("counter", ledger)));

        IReadOnlyList<string> resumed = await RunThenResumeAfterFirstVisitAsync(Build, "context-held");

        Assert.Equal(["visit:2"], resumed);
    }

    // ---- 4. Lifecycle hooks forwarded by reflection, surviving a resume ----

    [Fact]
    public async Task Q4a_field_held_state_is_lost_across_a_resume_without_forwarding()
    {
        var ledger = new Ledger();
        Workflow Build() => CounterGraph(new SpikeAdapter(new FieldCounter("counter", ledger), forwardLifecycle: false));

        IReadOnlyList<string> resumed = await RunThenResumeAfterFirstVisitAsync(Build, "fields, not forwarded");
        log.WriteLine("ledger: " + string.Join(", ", ledger.Entries));

        Assert.Equal(["visit:1", "visit:2"], resumed);   // counted again from zero
    }

    [Fact]
    public async Task Q4b_field_held_state_survives_a_resume_with_lifecycle_forwarded_by_reflection()
    {
        var ledger = new Ledger();
        Workflow Build() => CounterGraph(new SpikeAdapter(new FieldCounter("counter", ledger), forwardLifecycle: true));

        IReadOnlyList<string> resumed = await RunThenResumeAfterFirstVisitAsync(Build, "fields, forwarded");
        log.WriteLine("ledger: " + string.Join(", ", ledger.Entries));

        Assert.Equal(["visit:2"], resumed);
    }

    [Fact]
    public async Task Q4c_the_raw_field_counter_is_the_baseline()
    {
        var ledger = new Ledger();
        Workflow Build() => CounterGraph(new FieldCounter("counter", ledger));

        IReadOnlyList<string> resumed = await RunThenResumeAfterFirstVisitAsync(Build, "fields, raw");

        Assert.Equal(["visit:2"], resumed);
    }

    // ---- 5. A gate that parks before the wrapped executor leaves no trace of it ----

    [Fact]
    public async Task Q5_a_gate_that_parks_before_the_wrapped_executor_leaves_no_trace_of_it()
    {
        var ledger = new Ledger();
        var effect = new SpikeAdapter(new SideEffect("effect", ledger), parkWhen: _ => true);
        var after = new Labeller("after", ledger);

        var source = new Source("source");
        Workflow workflow = new WorkflowBuilder(source)
            .AddEdge(source, effect)
            .AddEdge(effect, after)
            .WithOutputFrom(after)
            .Build();

        Run run = await RunAsync(workflow, 5);
        Dump("parked", run);

        Assert.Equal(1, effect.Received);
        Assert.Empty(effect.InnerResults);
        Assert.Empty(ledger.Entries);
        Assert.Empty(run.Outputs);
        Assert.DoesNotContain(run.Lifecycle, e => e.ExecutorId == "after");
    }

    // ---- What ExecuteCoreAsync hands back, which decides whether the adapter must send it ----

    [Fact]
    public async Task Note_execute_core_sends_the_handler_result_itself()
    {
        var ledger = new Ledger();
        var doubler = new SpikeAdapter(new Doubler("double", ledger));
        var after = new Labeller("after", ledger);

        var source = new Source("source");
        Workflow workflow = new WorkflowBuilder(source)
            .AddEdge(source, doubler)
            .AddEdge(doubler, after)
            .WithOutputFrom(after)
            .Build();

        Run run = await RunAsync(workflow, 4);
        log.WriteLine($"inner results: {string.Join(", ", doubler.InnerResults.Select(r => r?.GetType().Name + ":" + r))}");

        Assert.Equal(["after:8"], run.Outputs);
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<(Run Run, Ledger Ledger)> RunBranchingAsync(bool wrap, bool filterInnerLifecycle = true)
    {
        var ledger = new Ledger();
        ExecutorBinding Bind(Executor executor)
            => wrap ? new SpikeAdapter(executor, filterInnerLifecycle: filterInnerLifecycle) : executor;

        var source = new Source("source");
        ExecutorBinding doubler = Bind(new Doubler("double", ledger));
        ExecutorBinding incrementer = Bind(new Incrementer("inc", ledger));
        ExecutorBinding join = Bind(new Join("join", ledger));
        ExecutorBinding big = Bind(new Labeller("big", ledger));
        ExecutorBinding small = Bind(new Labeller("small", ledger));

        Workflow workflow = new WorkflowBuilder(source)
            .AddFanOutEdge(source, [doubler, incrementer])
            .AddFanInBarrierEdge([doubler, incrementer], join)
            .AddEdge<int>(join, big, x => x > 8)
            .AddEdge<int>(join, small, x => x <= 8)
            .WithOutputFrom(big, small)
            .Build();

        return (await RunAsync(workflow, 5), ledger);
    }

    private static Workflow CounterGraph(ExecutorBinding counter)
    {
        var start = new Source("source");
        var echo = new Echo("echo");

        return new WorkflowBuilder(start)
            .AddEdge(start, counter)
            .AddEdge(counter, echo)
            .AddEdge(echo, counter)
            .WithOutputFrom(counter)
            .Build();
    }

    /// <summary>
    /// Runs the loop with checkpointing, then resumes a freshly built graph — new executor instances,
    /// as a second replica would have — from the checkpoint taken after the counter's first visit, and
    /// returns what the resumed run yields.
    /// </summary>
    private async Task<IReadOnlyList<string>> RunThenResumeAfterFirstVisitAsync(Func<Workflow> build, string label)
    {
        CheckpointManager checkpoints = CheckpointManager.CreateInMemory();
        string session = Guid.NewGuid().ToString("N");

        Run first = await RunAsync(build(), 1, checkpoints, session);
        Dump($"{label}: first run", first);

        // Superstep 0 runs the source, 1 the counter's first visit: resume from just after it.
        CheckpointInfo afterFirstVisit = first.Checkpoints[1];

        await using StreamingRun resumed = await InProcessExecution.ResumeStreamingAsync(build(), afterFirstVisit, checkpoints);
        Run second = await CollectAsync(resumed);
        Dump($"{label}: resumed", second);

        return [.. second.Outputs];
    }

    private static async Task<Run> RunAsync(Workflow workflow, int input, CheckpointManager? checkpoints = null, string? session = null)
    {
        await using StreamingRun run = checkpoints is null
            ? await InProcessExecution.RunStreamingAsync(workflow, input)
            : await InProcessExecution.RunStreamingAsync(workflow, input, checkpoints, session!);

        return await CollectAsync(run);
    }

    private static async Task<Run> CollectAsync(StreamingRun run)
    {
        var result = new Run();

        await foreach (WorkflowEvent evt in run.WatchStreamAsync())
        {
            result.All.Add(evt);

            switch (evt)
            {
                case ExecutorInvokedEvent or ExecutorCompletedEvent or ExecutorFailedEvent:
                    result.Lifecycle.Add((evt.GetType().Name, ((ExecutorEvent)evt).ExecutorId));
                    if (evt is ExecutorFailedEvent failed)
                    {
                        result.Errors.Add($"{failed.ExecutorId}: {failed.Data?.GetBaseException().Message}");
                    }
                    break;

                case WorkflowOutputEvent output:
                    result.Outputs.Add(output.Data?.ToString() ?? "null");
                    break;

                case WorkflowErrorEvent error:
                    result.Errors.Add(error.Exception?.GetBaseException().Message ?? "workflow error");
                    break;

                case SuperStepCompletedEvent step when step.CompletionInfo?.Checkpoint is { } checkpoint:
                    result.Checkpoints.Add(checkpoint);
                    break;
            }
        }

        return result;
    }

    private void Dump(string label, Run run)
    {
        log.WriteLine($"--- {label}");
        foreach (WorkflowEvent evt in run.All)
        {
            string detail = evt switch
            {
                ExecutorEvent e => e.ExecutorId,
                WorkflowOutputEvent o => $"{o.ExecutorId} -> {o.Data}",
                SuperStepEvent s => s.StepNumber.ToString(),
                WorkflowErrorEvent err => err.Exception?.GetBaseException().Message ?? "",
                _ => ""
            };
            log.WriteLine($"  {evt.GetType().Name} {detail}");
        }
    }

    private sealed class Run
    {
        public List<WorkflowEvent> All { get; } = [];
        public List<(string Type, string ExecutorId)> Lifecycle { get; } = [];
        public List<string> Outputs { get; } = [];
        public List<string> Errors { get; } = [];
        public List<CheckpointInfo> Checkpoints { get; } = [];
    }
}
