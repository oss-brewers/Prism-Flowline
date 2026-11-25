using System.Collections.Concurrent;
using System.Text.Json;
using Prism.Flowline.Abstractions;

namespace Prism.Flowline.Core;

public sealed record WorkflowDescriptor(IWorkflowDefinition Definition)
{
    public string Name => Definition.Name;
    public string Version => Definition.Version;
    public Type ContextType => Definition.ContextType;
    public Type ResultType => Definition.ResultType;

    /// <summary>
    /// The tenant this definition belongs to, or null when every tenant may use it. Only a sourced
    /// definition can be private; one registered at start-up is always shared.
    /// </summary>
    public string? TenantId { get; init; }
}

public sealed record ContextValidationResult(bool IsValid, IReadOnlyDictionary<string, string[]> Errors)
{
    public static ContextValidationResult Valid { get; } =
        new(true, new Dictionary<string, string[]>());

    public static ContextValidationResult Invalid(string field, string error) =>
        new(false, new Dictionary<string, string[]> { [field] = [error] });
}

/// <summary>
/// Implemented by a workflow definition that validates its own start payload beyond binding it to
/// <see cref="IWorkflowDefinition.ContextType"/>.
/// </summary>
/// <remarks>
/// <para>
/// The registry's default check is a type bind, which is the whole story for a definition whose
/// context is a C# record. It is no story at all for one whose context is a JSON schema declared in
/// a document, so such a definition answers for itself.
/// </para>
/// <para>
/// Opt-in and additive: a definition that does not implement this behaves exactly as before, and the
/// hook runs only after the type bind has already succeeded.
/// </para>
/// </remarks>
public interface IContextValidatingWorkflow
{
    ContextValidationResult ValidateContext(JsonElement context);
}

public interface IWorkflowRegistry
{
    /// <summary>The definitions registered at start-up. Sourced definitions are never listed.</summary>
    IReadOnlyList<WorkflowDescriptor> All { get; }

    /// <summary>
    /// Synchronous resolution, for callers that cannot await. Sees start-up definitions and shared
    /// sourced definitions already held; never asks a source and never returns a private definition.
    /// </summary>
    WorkflowDescriptor? Resolve(string name, string? version = null);

    /// <summary>The start-up versions of one workflow, newest first.</summary>
    IReadOnlyList<WorkflowDescriptor> VersionsOf(string name);

    ContextValidationResult ValidateContext(WorkflowDescriptor descriptor, JsonElement? context);

    /// <summary>
    /// Resolves a workflow for one tenant, asking the registered sources for what was not
    /// registered at start-up. Null when there is no such workflow for that tenant.
    /// </summary>
    /// <param name="name">The workflow name.</param>
    /// <param name="version">The exact version, or null for the current one.</param>
    /// <param name="tenantId">
    /// The tenant the caller acts for. A private definition is returned only to its owner, or to
    /// <see cref="Tenancy.AllTenants"/>; null sees shared definitions only.
    /// </param>
    /// <param name="cancellationToken">Stops this caller waiting; never the lookup itself.</param>
    /// <exception cref="Exception">Whatever a source threw: the source could not say right now.</exception>
    ValueTask<WorkflowDescriptor?> ResolveAsync(
        string name, string? version = null, string? tenantId = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Resolve(name, version));

    /// <summary>
    /// Forgets sourced definitions held on this replica: one version, one tenant's, or every one
    /// for a name. Start-up definitions are never forgotten. Exists for a host that withdraws a
    /// draft, not as a way to change a published version.
    /// </summary>
    /// <param name="name">The workflow name.</param>
    /// <param name="version">One version, or null for every held version of the name.</param>
    /// <param name="tenantId">
    /// Null forgets every owner's; <see cref="Tenancy.AllTenants"/> forgets the shared ones only;
    /// anything else forgets that tenant's only.
    /// </param>
    /// <returns>Whether anything was forgotten.</returns>
    bool Invalidate(string name, string? version = null, string? tenantId = null) => false;
}

public sealed class RegistryOptions
{
    /// <summary>
    /// How many sourced definitions one replica holds before dropping the least recently used.
    /// Dropping is harmless: a run in progress holds its own descriptor, and the next lookup asks
    /// the source again.
    /// </summary>
    public int ResolvedCapacity { get; set; } = 1024;
}

/// <summary>
/// The workflows a host can run: the ones it was composed with, and whatever its
/// <see cref="IWorkflowDefinitionSource"/>s answer for. Version selection is SemVer-ordered;
/// instances always resolve the exact version they were created with.
/// </summary>
/// <remarks>
/// <para>Resolution order:</para>
/// <list type="number">
/// <item><description>
/// A definition registered at start-up. It always wins, and a lookup with no version for a workflow
/// registered at start-up never reaches a source, so adding a source changes nothing for a host's
/// own workflows.
/// </description></item>
/// <item><description>
/// A definition a source already produced, when the lookup names a version: the tenant's own first,
/// then the shared one.
/// </description></item>
/// <item><description>
/// The sources, in registration order; the first to return a definition wins. A lookup with no
/// version goes here every time, because only the source knows when "current" moves, and the
/// version it names is then served from what is held, so one version is one object while held.
/// </description></item>
/// </list>
/// <para>
/// Held definitions are keyed by owner as well as name and version. A cache that ignored the
/// tenant would make a source's own check a formality: tenant A resolves its private canvas and
/// tenant B is served it from memory.
/// </para>
/// </remarks>
public sealed class WorkflowRegistry : IWorkflowRegistry
{
    private readonly Dictionary<string, List<WorkflowDescriptor>> _byName;
    private readonly IReadOnlyList<IWorkflowDefinitionSource> _sources;
    private readonly HeldDefinitions _held;

    /// <summary>
    /// Lookups on their way to the sources. Concurrent first lookups for one workflow join the trip
    /// already made, so a burst of starts for a graph nobody has run yet compiles it once.
    /// </summary>
    private readonly ConcurrentDictionary<FlightKey, Task<WorkflowDescriptor?>> _inFlight = new();

    public WorkflowRegistry(IEnumerable<IWorkflowDefinition> definitions)
        : this(definitions, [], null)
    {
    }

    public WorkflowRegistry(
        IEnumerable<IWorkflowDefinition> definitions,
        IEnumerable<IWorkflowDefinitionSource> sources,
        RegistryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(sources);

        _sources = [.. sources];
        _held = new HeldDefinitions((options ?? new RegistryOptions()).ResolvedCapacity);

        _byName = definitions
            .Select(d => new WorkflowDescriptor(d))
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => ParseVersion(d.Version)).ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach ((string name, List<WorkflowDescriptor> versions) in _byName)
        {
            IEnumerable<string> duplicates = versions
                .GroupBy(v => v.Version, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);

            if (duplicates.FirstOrDefault() is { } duplicate)
            {
                throw new InvalidOperationException(
                    $"Workflow '{name}' has more than one definition registered for version '{duplicate}'.");
            }
        }

        All = _byName.Values.SelectMany(v => v).ToArray();
    }

    public IReadOnlyList<WorkflowDescriptor> All { get; }

    public WorkflowDescriptor? Resolve(string name, string? version = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (FromStartUp(name, version) is { } registered)
        {
            return registered;
        }

        // Only the shared ones: this overload has no tenant, and a private definition must not
        // reach a caller that cannot say who it is acting for.
        return version is null
            ? _held.HighestShared(name)
            : _held.Find(owner: null, name, version);
    }

    public async ValueTask<WorkflowDescriptor?> ResolveAsync(
        string name, string? version = null, string? tenantId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Also covers a lookup with no version for a name the host was composed with: it keeps its
        // start-up meaning of "the newest registered version", without a trip to a source that could
        // only add to it.
        if (FromStartUp(name, version) is { } registered)
        {
            return registered;
        }

        if (version is not null && FindHeld(name, version, tenantId) is { } held)
        {
            return held;
        }

        WorkflowDescriptor? found = await JoinFlight(new WorkflowLookup(name, version, tenantId))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return found is not null && IsVisibleTo(found, tenantId) ? found : null;
    }

    public bool Invalidate(string name, string? version = null, string? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _held.Remove(name, version, tenantId);
    }

    private WorkflowDescriptor? FromStartUp(string name, string? version)
    {
        if (!_byName.TryGetValue(name, out List<WorkflowDescriptor>? versions) || versions.Count == 0)
        {
            return null;
        }

        return version is null
            ? versions[0]   // highest SemVer
            : versions.FirstOrDefault(v => string.Equals(v.Version, version, StringComparison.OrdinalIgnoreCase));
    }

    private WorkflowDescriptor? FindHeld(string name, string version, string? tenantId)
    {
        if (IsOperatorScope(tenantId))
        {
            return _held.Find(owner: null, name, version) ?? _held.FindAnyPrivate(name, version);
        }

        return (tenantId is null ? null : _held.Find(tenantId, name, version))
               ?? _held.Find(owner: null, name, version);
    }

    private static bool IsVisibleTo(WorkflowDescriptor descriptor, string? tenantId)
        => descriptor.TenantId is null
           || IsOperatorScope(tenantId)
           || string.Equals(descriptor.TenantId, tenantId, StringComparison.Ordinal);

    /// <summary>
    /// The all-tenants sentinel, typed. Null is deliberately not this: elsewhere null drifts into
    /// meaning "everyone", and here that would hand any tenant's private definition to a caller
    /// that simply forgot to say who it was.
    /// </summary>
    private static bool IsOperatorScope(string? tenantId)
        => string.Equals(tenantId, Tenancy.AllTenants, StringComparison.Ordinal);

    private Task<WorkflowDescriptor?> JoinFlight(WorkflowLookup lookup)
    {
        var key = FlightKey.For(lookup);

        while (true)
        {
            if (_inFlight.TryGetValue(key, out Task<WorkflowDescriptor?>? existing))
            {
                return existing;
            }

            var flight = new TaskCompletionSource<WorkflowDescriptor?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_inFlight.TryAdd(key, flight.Task))
            {
                _ = FlyAsync(key, lookup, flight);
                return flight.Task;
            }
        }
    }

    /// <summary>
    /// One trip to the sources on behalf of everyone waiting for it. It runs without any caller's
    /// token, so a caller that gives up waiting does not cancel the lookup for the others.
    /// </summary>
    private async Task FlyAsync(FlightKey key, WorkflowLookup lookup, TaskCompletionSource<WorkflowDescriptor?> flight)
    {
        WorkflowDescriptor? found = null;
        Exception? failure = null;

        try
        {
            found = await AskSourcesAsync(lookup).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Out of the table before the waiters hear, so nobody arriving later joins a finished trip.
        // A failure is therefore not remembered: the next lookup asks again.
        _inFlight.TryRemove(new KeyValuePair<FlightKey, Task<WorkflowDescriptor?>>(key, flight.Task));

        if (failure is not null)
        {
            flight.SetException(failure);
        }
        else
        {
            flight.SetResult(found);
        }
    }

    private async Task<WorkflowDescriptor?> AskSourcesAsync(WorkflowLookup lookup)
    {
        foreach (IWorkflowDefinitionSource source in _sources)
        {
            ResolvedWorkflow? answer = await source.FindAsync(lookup, CancellationToken.None).ConfigureAwait(false);
            if (answer is null)
            {
                continue;
            }

            Accept(source, lookup, answer);

            var descriptor = new WorkflowDescriptor(answer.Definition)
            {
                TenantId = Tenancy.Normalize(answer.TenantId)
            };

            // The first answer wins even when this caller may not see it: a source that hands one
            // tenant's definition to another has said what it knows, and asking the next source
            // would let a second answer shadow the owner's.
            return _held.GetOrAdd(descriptor);
        }

        return null;
    }

    /// <summary>
    /// The checks start-up applies to a registered definition, plus the fourth promise. A failure
    /// is a defect in the source, reported loudly rather than served.
    /// </summary>
    private static void Accept(IWorkflowDefinitionSource source, WorkflowLookup lookup, ResolvedWorkflow answer)
    {
        string origin = source.GetType().Name;

        IWorkflowDefinition definition = answer.Definition
            ?? throw new InvalidOperationException(
                $"Workflow source '{origin}' returned an answer with no definition for '{lookup.Name}'.");

        if (!string.Equals(definition.Name, lookup.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Workflow source '{origin}' was asked for '{lookup.Name}' and returned '{definition.Name}'.");
        }

        if (string.IsNullOrWhiteSpace(definition.Version))
        {
            throw new InvalidOperationException(
                $"Workflow source '{origin}' returned a definition of '{lookup.Name}' with no version.");
        }

        if (lookup.Version is not null &&
            !string.Equals(definition.Version, lookup.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Workflow source '{origin}' was asked for version '{lookup.Version}' of '{lookup.Name}' " +
                $"and returned version '{definition.Version}'.");
        }

        if (definition is INotifyingWorkflow notifying)
        {
            notifying.Notifications.Validate(definition.Name);
        }

        // Triggers are subscribed from the start-up catalog, which a sourced definition is never in.
        // Accepting one would accept a trigger that never fires.
        if (definition is IDomainEventTriggeredWorkflow { Triggers.Count: > 0 })
        {
            throw new InvalidOperationException(
                $"Workflow source '{origin}' returned '{lookup.Name}' {definition.Version}, which declares " +
                "domain-event triggers. Triggers are subscribed at start-up only; register this workflow " +
                "with the host instead.");
        }
    }

    public IReadOnlyList<WorkflowDescriptor> VersionsOf(string name)
        => _byName.TryGetValue(name, out List<WorkflowDescriptor>? versions) ? versions : [];

    public ContextValidationResult ValidateContext(WorkflowDescriptor descriptor, JsonElement? context)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (context is null || context.Value.ValueKind == JsonValueKind.Undefined)
        {
            return ContextValidationResult.Invalid("context", "A context payload is required.");
        }

        if (context.Value.ValueKind == JsonValueKind.Null)
        {
            return ContextValidationResult.Invalid("context", "Context must not be null.");
        }

        try
        {
            object? deserialized = context.Value.Deserialize(descriptor.ContextType, JsonOptions.Default);
            if (deserialized is null)
            {
                return ContextValidationResult.Invalid(
                    "context", $"Could not bind context to '{descriptor.ContextType.Name}'.");
            }

            // Runs only after the bind succeeded, so a definition's own check never has to repeat
            // what the type system already established.
            return descriptor.Definition is IContextValidatingWorkflow validating
                ? validating.ValidateContext(context.Value)
                : ContextValidationResult.Valid;
        }
        catch (JsonException ex)
        {
            return ContextValidationResult.Invalid("context", ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return ContextValidationResult.Invalid("context", ex.Message);
        }
    }

    internal static Version ParseVersion(string version)
    {
        // Tolerates SemVer pre-release suffixes ("1.2.0-beta.1") by taking the numeric core.
        string core = version.Split('-', '+')[0];
        return Version.TryParse(core, out Version? parsed) ? parsed : new Version(0, 0, 0);
    }

    private readonly record struct FlightKey(string Name, string? Version, string? TenantId)
    {
        public static FlightKey For(WorkflowLookup lookup)
            => new(lookup.Name.ToUpperInvariant(), lookup.Version?.ToUpperInvariant(), lookup.TenantId);
    }

    /// <summary>
    /// Sourced descriptors, least recently used first out. Keyed by owner, name and version; names
    /// and versions compare case-insensitively like the start-up catalog, tenants exactly.
    /// </summary>
    private sealed class HeldDefinitions
    {
        private readonly int _capacity;
        private readonly object _gate = new();
        private readonly Dictionary<Key, LinkedListNode<WorkflowDescriptor>> _byKey = [];

        /// <summary>Most recently used at the front.</summary>
        private readonly LinkedList<WorkflowDescriptor> _order = new();

        public HeldDefinitions(int capacity) => _capacity = Math.Max(0, capacity);

        public WorkflowDescriptor? Find(string? owner, string name, string version)
        {
            lock (_gate)
            {
                return _byKey.TryGetValue(new Key(owner, Fold(name), Fold(version)), out var node) ? Touch(node) : null;
            }
        }

        public WorkflowDescriptor? FindAnyPrivate(string name, string version)
        {
            string foldedName = Fold(name), foldedVersion = Fold(version);

            lock (_gate)
            {
                foreach ((Key key, LinkedListNode<WorkflowDescriptor> node) in _byKey)
                {
                    if (key.Owner is not null && key.Name == foldedName && key.Version == foldedVersion)
                    {
                        return Touch(node);
                    }
                }

                return null;
            }
        }

        public WorkflowDescriptor? HighestShared(string name)
        {
            string folded = Fold(name);

            lock (_gate)
            {
                return _byKey
                    .Where(entry => entry.Key.Owner is null && entry.Key.Name == folded)
                    .Select(entry => entry.Value.Value)
                    .OrderByDescending(d => ParseVersion(d.Version))
                    .FirstOrDefault();
            }
        }

        /// <summary>
        /// The descriptor already held for this owner, name and version, or this one, now held.
        /// Returning the existing object is what makes one version one object.
        /// </summary>
        public WorkflowDescriptor GetOrAdd(WorkflowDescriptor descriptor)
        {
            var key = new Key(descriptor.TenantId, Fold(descriptor.Name), Fold(descriptor.Version));

            lock (_gate)
            {
                if (_byKey.TryGetValue(key, out var existing))
                {
                    return Touch(existing);
                }

                _byKey[key] = _order.AddFirst(descriptor);

                while (_order.Count > _capacity && _order.Last is { } eldest)
                {
                    _order.RemoveLast();
                    _byKey.Remove(KeyOf(eldest.Value));
                }

                return descriptor;
            }
        }

        public bool Remove(string name, string? version, string? tenantId)
        {
            string foldedName = Fold(name);
            string? foldedVersion = version is null ? null : Fold(version);
            bool sharedOnly = string.Equals(tenantId, Tenancy.AllTenants, StringComparison.Ordinal);

            lock (_gate)
            {
                Key[] doomed = _byKey.Keys
                    .Where(key => key.Name == foldedName
                                  && (foldedVersion is null || key.Version == foldedVersion)
                                  && (tenantId is null
                                      || (sharedOnly ? key.Owner is null : string.Equals(key.Owner, tenantId, StringComparison.Ordinal))))
                    .ToArray();

                foreach (Key key in doomed)
                {
                    _order.Remove(_byKey[key]);
                    _byKey.Remove(key);
                }

                return doomed.Length > 0;
            }
        }

        private WorkflowDescriptor Touch(LinkedListNode<WorkflowDescriptor> node)
        {
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value;
        }

        private static Key KeyOf(WorkflowDescriptor descriptor)
            => new(descriptor.TenantId, Fold(descriptor.Name), Fold(descriptor.Version));

        private static string Fold(string value) => value.ToUpperInvariant();

        private readonly record struct Key(string? Owner, string Name, string Version);
    }
}

public static class JsonOptions
{
    public static JsonSerializerOptions Default { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
