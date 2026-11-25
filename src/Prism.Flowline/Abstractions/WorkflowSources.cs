namespace Prism.Flowline.Abstractions;

/// <summary>
/// What the registry asks a <see cref="IWorkflowDefinitionSource"/> for: one workflow, by name, and
/// optionally by version, on behalf of one tenant.
/// </summary>
/// <param name="Name">The workflow name, as the caller spelled it.</param>
/// <param name="Version">
/// The exact version, or null for whichever version the source considers current.
/// </param>
/// <param name="TenantId">
/// The tenant the lookup is made for, so a source can refuse at the origin as well as the registry
/// refusing on the way out. <see cref="Tenancy.AllTenants"/> for operator paths that act across
/// tenants; null when the caller named no tenant, which a source should treat as "shared only".
/// </param>
public sealed record WorkflowLookup(string Name, string? Version, string? TenantId);

/// <summary>A source's answer: the definition, and who may use it.</summary>
public sealed record ResolvedWorkflow(IWorkflowDefinition Definition)
{
    /// <summary>The one tenant this definition belongs to, or null when every tenant may use it.</summary>
    public string? TenantId { get; init; }
}

/// <summary>
/// Supplies workflow definitions the host was not composed with — kept in a database, published
/// from an editor, compiled from a canvas — at the moment the runtime needs one.
/// </summary>
/// <remarks>
/// <para>
/// Pull rather than push, because an instance resolves its definition again on every resume, retry
/// and claim, on whichever replica runs it. A definition handed to one process at run time would be
/// gone after the first restart; a source is asked again.
/// </para>
/// <para>A source makes four promises, and the runtime depends on each:</para>
/// <list type="number">
/// <item><description>
/// <b>A version is immutable.</b> Two answers for one name and version describe the same graph.
/// Instances, checkpoints and gate policies are keyed by name and version, so a version whose graph
/// changes under a parked instance resumes a checkpoint that no longer fits it.
/// </description></item>
/// <item><description>
/// <b>It answers after a restart, on any replica.</b> A source that only knows what this process
/// was told leaves the next replica's resume dead-stopped as <c>WorkflowVersionUnavailable</c>.
/// </description></item>
/// <item><description>
/// <b>Null means "no such workflow"; an exception means "cannot say right now".</b> A start request
/// for an unknown workflow is a 404 and a run whose version has gone is dead-stopped; a source that
/// throws fails the start and reschedules the run instead.
/// </description></item>
/// <item><description>
/// <b>It returns what it was asked for.</b> The registry refuses a definition whose name differs
/// from the lookup, or whose version differs from the one asked for, as a defect in the source.
/// </description></item>
/// </list>
/// <para>
/// Definitions registered at start-up always win: a source can add workflows and versions but not
/// replace one the host was composed with. The registry holds what a source returns, so a source need
/// not cache, but it is asked every time a lookup names no version, because only the source knows
/// when "current" moves.
/// </para>
/// </remarks>
public interface IWorkflowDefinitionSource
{
    ValueTask<ResolvedWorkflow?> FindAsync(WorkflowLookup lookup, CancellationToken cancellationToken);
}
