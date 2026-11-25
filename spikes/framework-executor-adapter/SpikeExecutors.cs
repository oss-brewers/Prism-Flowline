using System.Collections.Concurrent;
using Microsoft.Agents.AI.Workflows;

namespace FrameworkExecutorAdapterSpike;

/// <summary>Side effects, by executor, so a test can tell whether a node really ran.</summary>
public sealed class Ledger
{
    private readonly ConcurrentQueue<string> _entries = new();

    public void Record(string entry) => _entries.Enqueue(entry);

    public IReadOnlyList<string> Entries => [.. _entries];
}

public sealed class Source(string id) : Executor<int, int>(id)
{
    public override ValueTask<int> HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(message);
}

public sealed class Doubler(string id, Ledger ledger) : Executor<int, int>(id)
{
    public override ValueTask<int> HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ledger.Record($"{Id}:{message}");
        return ValueTask.FromResult(message * 2);
    }
}

public sealed class Incrementer(string id, Ledger ledger) : Executor<int, int>(id)
{
    public override ValueTask<int> HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ledger.Record($"{Id}:{message}");
        return ValueTask.FromResult(message + 1);
    }
}

/// <summary>The fan-in target: passes each value on, so the conditional edges after it decide.</summary>
public sealed class Join(string id, Ledger ledger) : Executor<int, int>(id)
{
    public override ValueTask<int> HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ledger.Record($"{Id}:{message}");
        return ValueTask.FromResult(message);
    }
}

/// <summary>Yields a labelled value; one sits behind each conditional edge.</summary>
public sealed class Labeller(string id, Ledger ledger) : Executor<int>(id)
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
        => base.ConfigureProtocol(protocolBuilder).YieldsOutput<string>();

    public override async ValueTask HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ledger.Record($"{Id}:{message}");
        await context.YieldOutputAsync($"{Id}:{message}", cancellationToken);
    }
}

/// <summary>
/// Visited twice through a loop with <see cref="Echo"/>; keeps its visit count in the workflow
/// context, which the engine checkpoints itself.
/// </summary>
public sealed class ContextCounter(string id, Ledger ledger) : Executor<int>(id)
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

/// <summary>
/// The same, but the count lives in a field and is saved and restored through the checkpoint hooks —
/// the shape the design calls out as the risk.
/// </summary>
public sealed class FieldCounter(string id, Ledger ledger) : Executor<int>(id)
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

    protected override async ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken cancellationToken)
    {
        ledger.Record($"{Id}:checkpointing:{_count}");
        await context.QueueStateUpdateAsync("field-count", _count, cancellationToken);
    }

    protected override async ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken cancellationToken)
    {
        _count = await context.ReadOrInitStateAsync("field-count", () => 0, cancellationToken);
        ledger.Record($"{Id}:restored:{_count}");
    }
}

public sealed class Echo(string id) : Executor<int, int>(id)
{
    public override ValueTask<int> HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(message);
}

/// <summary>Everything a node could do that would count as having run: a record, state, a send.</summary>
public sealed class SideEffect(string id, Ledger ledger) : Executor<int, int>(id)
{
    public override async ValueTask<int> HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ledger.Record($"{Id}:{message}");
        await context.QueueStateUpdateAsync("touched", true, cancellationToken);
        return message;
    }
}

public sealed class Broken(string id) : Executor<int, int>(id)
{
    public override ValueTask<int> HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("broken by design");
}
