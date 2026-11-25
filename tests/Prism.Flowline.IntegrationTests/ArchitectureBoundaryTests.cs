using System.Reflection;
using Prism.Flowline.Messaging.RabbitMQ;
using Prism.Flowline.Cache.Redis;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Api;
using Prism.Flowline.Messaging;
using Prism.Flowline.Notifications;
using Prism.Flowline.Service.ControlPlane;
using Prism.Flowline.Core;
using Prism.Flowline.Persistence;
using Prism.Flowline.Service;
using Prism.Flowline.Stores.SqlServer;
using FluentAssertions;
using Xunit;

namespace Prism.Flowline.IntegrationTests;

/// <summary>
/// Locks in the split between the reusable framework and the deployable host: <c>Prism.Flowline</c>
/// carries the headless workflow host — runtime, dispatch and HTTP API — the adapter projects carry
/// the infrastructure, and <c>Prism.Flowline.Service</c> contributes the user interface and the
/// wiring that selects that infrastructure.
/// </summary>
public class ArchitectureBoundaryTests
{
    private static readonly Assembly Library = typeof(WorkflowRunner).Assembly;
    private static readonly Assembly Host = typeof(PrismServiceCollectionExtensions).Assembly;
    private static readonly Assembly RedisAdapter = typeof(RedisNotificationBus).Assembly;
    private static readonly Assembly RabbitMqAdapter = typeof(RabbitMqDomainEventBroker).Assembly;
    private static readonly Assembly SqlServerAdapter = typeof(SqlServerInstanceStore).Assembly;
    private static readonly Assembly Dsl = typeof(Prism.Flowline.Dsl.Interpretation.DslMessage).Assembly;

    private static readonly Dictionary<string, Assembly> Adapters = new(StringComparer.Ordinal)
    {
        ["Prism.Flowline.Cache.Redis"] = RedisAdapter,
        ["Prism.Flowline.Messaging.RabbitMQ"] = RabbitMqAdapter,
        ["Prism.Flowline.Stores.SqlServer"] = SqlServerAdapter
    };

    [Fact]
    public void The_library_and_the_host_are_separate_assemblies()
    {
        Library.GetName().Name.Should().Be("Prism.Flowline");
        Host.GetName().Name.Should().Be("Prism.Flowline.Service");
    }

    [Fact]
    public void The_user_interface_belongs_to_the_host()
    {
        // The framework is headless: it serves the API and nothing else. Shipping Razor Pages inside
        // it would force every consumer to take an MVC dependency and a UI they may not want.
        typeof(ControlPlaneExtensions).Assembly.Should().BeSameAs(Host);
        typeof(Prism.Flowline.Service.ControlPlane.Services.WorkflowApiClient).Assembly.Should().BeSameAs(Host);

        Library.GetReferencedAssemblies().Select(a => a.Name)
            .Should().NotContain("Microsoft.AspNetCore.Mvc.RazorPages");
    }

    [Fact]
    public void The_library_does_not_reference_the_host()
        => Library.GetReferencedAssemblies().Select(a => a.Name)
            .Should().NotContain("Prism.Flowline.Service", "the framework must not depend on any single deployment");

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer")]
    [InlineData("StackExchange.Redis")]
    public void The_library_is_free_of_infrastructure_dependencies(string forbidden)
        => Library.GetReferencedAssemblies().Select(a => a.Name)
            .Should().NotContain(forbidden,
                "storage and transport choices belong to the host, not the framework");

    [Fact]
    public void The_framework_surface_lives_in_the_library()
    {
        // API, dispatch, persistence defaults and runtime ship together, so a consumer gets a
        // working headless host from one reference.
        typeof(Endpoints).Assembly.Should().BeSameAs(Library);
        typeof(Sse).Assembly.Should().BeSameAs(Library);
        typeof(InstanceControlService).Assembly.Should().BeSameAs(Library);
        typeof(InstanceLauncher).Assembly.Should().BeSameAs(Library);
        typeof(WorkflowHostBuilder).Assembly.Should().BeSameAs(Library);
        typeof(InMemoryNotificationBus).Assembly.Should().BeSameAs(Library);
        typeof(InProcessDomainEventBroker).Assembly.Should().BeSameAs(Library);
        typeof(InMemoryInstanceStore).Assembly.Should().BeSameAs(Library);
        typeof(OverflowCheckpointStore).Assembly.Should().BeSameAs(Library);
    }

    [Fact]
    public void Sql_server_storage_lives_in_its_own_adapter_assembly()
    {
        // Deployment-specific, but not deployment-owned: a store inside the host could only be run
        // by booting the host, which is why the contract suite references this assembly directly.
        string[] storeTypes = SqlServerAdapter.GetTypes()
            .Where(t => t.IsClass && !t.IsNested)
            .Select(t => t.Name)
            .ToArray();

        storeTypes.Should().Contain(n => n.StartsWith("SqlServer", StringComparison.Ordinal),
            "SQL Server stores are an adapter, like the transports");
    }

    [Fact]
    public void Infrastructure_lives_in_its_own_adapter_assemblies()
    {
        // Not in the host. Infrastructure shipped inside a deployable is only reusable by copying
        // it, and forces every consumer of that host to take its client library.
        RedisAdapter.GetName().Name.Should().Be("Prism.Flowline.Cache.Redis");
        RabbitMqAdapter.GetName().Name.Should().Be("Prism.Flowline.Messaging.RabbitMQ");
        SqlServerAdapter.GetName().Name.Should().Be("Prism.Flowline.Stores.SqlServer");

        typeof(RedisNotificationBus).Assembly.Should().BeSameAs(RedisAdapter);
        typeof(RabbitMqDomainEventBroker).Assembly.Should().BeSameAs(RabbitMqAdapter);
        typeof(SqlServerServiceCollectionExtensions).Assembly.Should().BeSameAs(SqlServerAdapter);

        Host.GetTypes().Select(t => t.Name).Should()
            .NotContain(n => n.StartsWith("Redis", StringComparison.Ordinal)
                          || n.StartsWith("RabbitMq", StringComparison.Ordinal)
                          || n.StartsWith("SqlServer", StringComparison.Ordinal),
                "an adapter left behind in the host would be the copy nobody updates");
    }

    /// <summary>
    /// The two adapter families answer different questions, and keeping them apart is what lets a
    /// deployment take one without the other. Cache is the SSE backplane — how a subscriber reaches
    /// a run it is watching. Messaging is domain events — how work reaches another service.
    /// </summary>
    [Fact]
    public void A_cache_adapter_carries_no_domain_messaging()
    {
        Type[] cacheTypes = [.. RedisAdapter.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false })];

        cacheTypes.Should().Contain(t => typeof(INotificationBus).IsAssignableFrom(t),
            "the cache adapter exists to be the SSE backplane");

        cacheTypes.Should().NotContain(t => typeof(IDomainEventBroker).IsAssignableFrom(t),
            "domain messaging belongs to an Prism.Flowline.Messaging.* adapter, not to the cache");
    }

    /// <summary>
    /// Every cache adapter supplies the whole cache surface. A future
    /// <c>Prism.Flowline.Cache.Memcached</c> satisfies this by implementing one interface, and
    /// substituting it moves general caching and the SSE backplane in one step.
    /// </summary>
    [Fact]
    public void A_cache_adapter_supplies_every_cache_capability()
    {
        Type[] adapters =
        [
            .. RedisAdapter.GetTypes().Where(t =>
                t is { IsClass: true, IsAbstract: false } && typeof(ICacheAdapter).IsAssignableFrom(t))
        ];

        adapters.Should().ContainSingle(
            "a cache adapter assembly offers one substitution point, not a menu of half-choices");

        Type[] builtIn =
        [
            .. Library.GetTypes().Where(t =>
                t is { IsClass: true, IsAbstract: false } && typeof(ICacheAdapter).IsAssignableFrom(t))
        ];

        builtIn.Should().NotBeEmpty(
            "the framework ships an in-memory adapter so a host needs no cache infrastructure at all");
    }

    [Fact]
    public void A_messaging_adapter_carries_no_cache()
    {
        Type[] messagingTypes = [.. RabbitMqAdapter.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false })];

        messagingTypes.Should().Contain(t => typeof(IDomainEventBroker).IsAssignableFrom(t),
            "the messaging adapter exists to carry domain events");

        messagingTypes.Should().NotContain(t => typeof(INotificationBus).IsAssignableFrom(t),
            "the SSE backplane belongs to an Prism.Flowline.Cache.* adapter");
    }

    /// <summary>
    /// Both defaults ship with the framework, so a single-service deployment with no infrastructure
    /// at all still streams SSE and still routes domain events.
    /// </summary>
    [Fact]
    public void The_framework_defaults_need_no_adapter()
    {
        typeof(InMemoryNotificationBus).Assembly.Should().BeSameAs(Library);
        typeof(InProcessDomainEventBroker).Assembly.Should().BeSameAs(Library);
    }

    [Theory]
    [InlineData("Prism.Flowline.Cache.Redis")]
    [InlineData("Prism.Flowline.Messaging.RabbitMQ")]
    [InlineData("Prism.Flowline.Stores.SqlServer")]
    public void An_adapter_depends_on_the_framework_and_nothing_else_of_ours(string adapterName)
    {
        Assembly adapter = Adapters[adapterName];

        string[] references = [.. adapter.GetReferencedAssemblies().Select(a => a.Name!)];

        references.Should().Contain("Prism.Flowline", "an adapter exists to implement the framework's contracts");

        references.Should().NotContain("Prism.Flowline.Service",
            "an adapter that referenced a host would be tied to one deployment");

        // The adapters must not know about each other, or choosing one would drag in another's
        // client library.
        references.Should().NotContain(Adapters.Keys.Where(name => name != adapterName),
            "adapters are alternatives or neighbours, not collaborators");
    }

    [Theory]
    [InlineData("Prism.Flowline.Cache.Redis", "StackExchange.Redis", "RabbitMQ.Client")]
    [InlineData("Prism.Flowline.Messaging.RabbitMQ", "RabbitMQ.Client", "StackExchange.Redis")]
    [InlineData("Prism.Flowline.Stores.SqlServer", "Microsoft.EntityFrameworkCore.SqlServer", "StackExchange.Redis")]
    [InlineData("Prism.Flowline.Stores.SqlServer", "Microsoft.EntityFrameworkCore.SqlServer", "RabbitMQ.Client")]
    public void An_adapter_carries_only_its_own_client_library(
        string adapterName, string expected, string forbidden)
    {
        Assembly adapter = Adapters[adapterName];

        string[] references = [.. adapter.GetReferencedAssemblies().Select(a => a.Name!)];

        references.Should().Contain(expected);
        references.Should().NotContain(forbidden,
            "referencing a transport means taking its client; taking both would defeat the split");
    }

    [Fact]
    public void Every_framework_contract_the_host_implements_is_an_infrastructure_adapter()
    {
        // The host may still implement a storage contract — the SQLite audit-record store lives
        // here with its migrations — but each such type must be a named adapter for a specific
        // technology, not framework logic that drifted back into the deployable.
        Type[] contracts =
        [
            typeof(IInstanceStore), typeof(IEventStore), typeof(ILogStore), typeof(IApprovalStore),
            typeof(IGatePolicyStore), typeof(IAuditStore), typeof(IBlobStore), typeof(INotificationBus),
            typeof(IDomainEventBroker), typeof(IDomainEventSubscriptionStore), typeof(IAuditRecordStore)
        ];

        string[] adapters = Host.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => contracts.Any(c => c.IsAssignableFrom(t)))
            .Select(t => t.Name)
            .ToArray();

        adapters.Should().NotBeEmpty("the host supplies the audit-record store it configures");

        // The prefix is the technology the adapter speaks. Adding one here is a deliberate act;
        // an adapter named for what it does rather than what it talks to is framework logic that
        // has drifted back into the deployable. Transports and SQL Server are not on this list
        // because they no longer live in the host at all; the SQLite audit-record store does.
        string[] technologies = ["Sqlite"];

        adapters.Should().OnlyContain(
            n => technologies.Any(t => n.StartsWith(t, StringComparison.Ordinal)));
    }

    [Fact]
    public void The_host_defines_no_framework_extension_points_outside_declared_workflows()
    {
        // Framework extension points (workflow definitions, host executors, middleware) belong to
        // consumers of Prism.Flowline — not to the shell that wires infrastructure. The rule carves out
        // workflows that ship inside the Service assembly by convention: anything under the
        // "…Workflows.<Name>" namespace is a documented consumer of the framework, not shell code
        // that has drifted.
        Type[] extensionPoints =
        [
            typeof(Prism.Flowline.Abstractions.IWorkflowDefinition),
            typeof(Prism.Flowline.Abstractions.IHostExecutor),
            typeof(Prism.Flowline.Abstractions.Middleware.IWorkflowMiddleware),
            typeof(Prism.Flowline.Abstractions.Middleware.IExecutorMiddleware)
        ];

        string[] offenders = Host.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => extensionPoints.Any(e => e.IsAssignableFrom(t)))
            .Where(t => t.Namespace is null
                || !t.Namespace.StartsWith("Prism.Flowline.Service.Workflows.", StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .ToArray();

        offenders.Should().BeEmpty(
            "extension points outside a declared workflow namespace would mean the shell had grown behaviour of its own");
    }

    /// <summary>
    /// The DSL is a second front end onto the framework, and it ships inside it: declarative and
    /// compiled authoring travel in one package. What still holds is where that front end sits —
    /// below any deployment, and knowing about neither the host nor infrastructure.
    /// </summary>
    [Fact]
    public void The_dsl_ships_inside_the_framework_and_knows_nothing_else_of_ours()
    {
        Dsl.Should().BeSameAs(Library, "the DSL is part of the framework assembly, not a package beside it");

        string[] references = [.. Dsl.GetReferencedAssemblies().Select(a => a.Name!)];

        references.Should().NotContain("Prism.Flowline.Service",
            "a front end that referenced a host would be tied to one deployment");

        foreach (string forbidden in new[]
                 {
                     "Prism.Flowline.Cache.Redis", "Prism.Flowline.Messaging.RabbitMQ",
                     "Microsoft.EntityFrameworkCore", "StackExchange.Redis", "RabbitMQ.Client"
                 })
        {
            references.Should().NotContain(forbidden,
                "the DSL declares what runs, never where it is stored or how it is transported");
        }
    }

    /// <summary>
    /// The seams the DSL is built on stay public. They are what a front end written outside this
    /// assembly would reach for — <c>IContextValidatingWorkflow</c>, <c>ITemplateBindingSource</c>,
    /// <c>IDocumentAuthoredWorkflow</c> — and folding the DSL inward must not quietly demote them
    /// to internals only the bundled front end can see.
    /// </summary>
    [Fact]
    public void The_seams_the_dsl_is_built_on_stay_public()
    {
        // The seams themselves, named: each is public, so a third front end has the same reach.
        typeof(IContextValidatingWorkflow).IsPublic.Should().BeTrue();
        typeof(IDocumentAuthoredWorkflow).IsPublic.Should().BeTrue();
        typeof(Prism.Flowline.Executors.ITemplateBindingSource).IsPublic.Should().BeTrue();
        typeof(IContextValidatingWorkflow).Assembly.Should().BeSameAs(Library);
        typeof(IDocumentAuthoredWorkflow).Assembly.Should().BeSameAs(Library);
    }

    /// <summary>
    /// The schema ships inside the framework assembly as one embedded resource. Byte equality with
    /// the published file is asserted in the DSL suite; what belongs here is that there is exactly
    /// one copy and it travels with the code that enforces it.
    /// </summary>
    [Fact]
    public void The_dsl_carries_the_schema_as_an_embedded_resource()
    {
        string[] schemaResources = [.. Library.GetManifestResourceNames()
            .Where(n => n.Contains("workflow-dsl", StringComparison.Ordinal))];

        schemaResources.Should().ContainSingle("one copy, or the published and enforced schemas can drift")
            .Which.Should().EndWith(".json");
    }

    [Fact]
    public void The_host_runs_on_the_library_defaults_when_no_infrastructure_is_configured()
    {
        // The integration fixture configures neither SQL Server nor Redis, so the framework's
        // in-memory implementations are what served every other test in this project.
        using var fixture = new HostFixture();
        using HttpClient client = fixture.CreateClient();

        fixture.Resolve<IInstanceStore>().Should().BeOfType<InMemoryInstanceStore>();
        fixture.Resolve<IEventStore>().Should().BeOfType<InMemoryEventStore>();
    }
}
