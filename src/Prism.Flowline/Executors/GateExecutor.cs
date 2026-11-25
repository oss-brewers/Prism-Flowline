using Prism.Flowline.Abstractions;
using Microsoft.Agents.AI.Workflows;

namespace Prism.Flowline.Executors;

/// <summary>
/// An approval gate standing in front of another node: it passes its message on unchanged once the
/// gate allows it, or with an approver's amendment when the gate permits one.
/// </summary>
/// <remarks>
/// Created by <see cref="WorkflowBuildContext.Gate{TMessage}"/>. It exists for the nodes a gate
/// cannot sit on — a raw framework executor, an <c>AIAgent</c> binding, a sub-workflow — because
/// whatever is wired after it does not run until the gate has decided. It is a node of its own: one
/// more pair of executor events in the stream, and its own id in the catalog and gate configuration.
/// </remarks>
public sealed class GateExecutor<TMessage> : HostExecutor<TMessage, TMessage>
    where TMessage : class
{
    public GateExecutor(string id) : base(id) { }

    protected override ValueTask<TMessage> ExecuteCoreAsync(
        TMessage input, IWorkflowContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(input);
}
