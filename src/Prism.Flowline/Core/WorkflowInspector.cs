using System.Runtime.CompilerServices;
using Prism.Flowline.Abstractions;

namespace Prism.Flowline.Core;

/// <summary>Describes the executor nodes a registered workflow version declares.</summary>
public interface IWorkflowInspector
{
    ValueTask<IReadOnlyList<WorkflowNodeDescriptor>> InspectAsync(
        WorkflowDescriptor descriptor, CancellationToken cancellationToken);
}

/// <summary>
/// Builds a definition once against an inspection context to read its nodes back.
/// </summary>
/// <remarks>
/// <para>
/// The graph of a version is fixed — the registry rejects two start-up definitions for one version,
/// and a source promises a version is immutable — so the result is cached. Building attaches nothing
/// to a runtime and runs no executor: <see cref="WorkflowBuildContext.ForInspection"/> hands every
/// node <see cref="HostExecutorRuntime.Unattached"/>.
/// </para>
/// <para>
/// Keyed by the definition object rather than by name and version. Two tenants can each own a
/// private definition of one name and version, and a name-keyed cache would describe one tenant's
/// graph to the other. Weakly held, so a definition the registry has dropped takes its entry with it.
/// </para>
/// </remarks>
public sealed class WorkflowInspector : IWorkflowInspector
{
    private readonly IServiceProvider? _services;
    private readonly ConditionalWeakTable<IWorkflowDefinition, IReadOnlyList<WorkflowNodeDescriptor>> _cache = new();

    public WorkflowInspector(IServiceProvider? services = null) => _services = services;

    public async ValueTask<IReadOnlyList<WorkflowNodeDescriptor>> InspectAsync(
        WorkflowDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (_cache.TryGetValue(descriptor.Definition, out IReadOnlyList<WorkflowNodeDescriptor>? cached))
        {
            return cached;
        }

        var context = WorkflowBuildContext.ForInspection(descriptor.Name, descriptor.Version, _services);
        await descriptor.Definition.BuildAsync(context, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<WorkflowNodeDescriptor> nodes = [.. context.Nodes];
        _cache.AddOrUpdate(descriptor.Definition, nodes);
        return nodes;
    }
}
