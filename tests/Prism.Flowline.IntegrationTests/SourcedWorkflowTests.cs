using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Api;
using Prism.Flowline.Core;
using Prism.Flowline.Dsl.Interpretation;
using Prism.Flowline.Dsl.Validation;
using Prism.Flowline.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Prism.Flowline.IntegrationTests;

/// <summary>
/// A host composed with no workflows at all, and one source backed by a catalog the test publishes
/// to after start-up — the shape of an embedding host whose workflows live in its own database.
/// </summary>
public class SourcedWorkflowTests
{
    private readonly SharedHostStores _stores = new();
    private readonly CanvasCatalog _catalog = new();
    private readonly SideEffectLedger _ledger = new();

    [Fact]
    public async Task A_workflow_added_after_start_up_runs_to_completion()
    {
        await using LibraryHost host = await LibraryHost.StartAsync(_stores, _catalog, "replica-a");

        _catalog.Publish(new CanvasWorkflow("canvas", "1.0.0", _ledger));
        string instanceId = await StartAsync(host.Client, "canvas", "t1");

        WorkflowInstance done = await _stores.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        done.WorkflowVersion.Should().Be("1.0.0");
        _ledger.CountFor("draft").Should().Be(1);
        _ledger.CountFor("review").Should().Be(1);
        _catalog.Lookups.Should().Contain(l => l.Name == "canvas" && l.TenantId == "t1");
    }

    [Fact]
    public async Task It_resumes_from_a_checkpoint_on_a_second_host_sharing_the_stores()
    {
        var reviewFails = new Toggle(on: true);
        _catalog.Publish(new CanvasWorkflow("canvas", "1.0.0", _ledger, reviewFails: reviewFails));

        string instanceId;
        await using (LibraryHost first = await LibraryHost.StartAsync(_stores, _catalog, "replica-a"))
        {
            instanceId = await StartAsync(first.Client, "canvas", "t1");
            await _stores.WaitForStatusAsync(instanceId, InstanceStatus.DeadStopped);
        }

        _ledger.CountFor("draft").Should().Be(1);
        reviewFails.On = false;
        int lookupsBefore = _catalog.Lookups.Count;

        // A new process: a new registry that holds nothing, the same stores and the same source.
        await using LibraryHost second = await LibraryHost.StartAsync(_stores, _catalog, "replica-b");

        JsonElement checkpoints = await second.Client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/checkpoints");
        string earliest = checkpoints.GetProperty("items").EnumerateArray()
            .OrderBy(c => c.GetProperty("committedAt").GetDateTimeOffset())
            .First().GetProperty("checkpointId").GetString()!;

        HttpResponseMessage resume = await second.Client.PostAsJsonAsync(
            $"/instances/{instanceId}/rerun", new { mode = "resume", fromCheckpointId = earliest });
        resume.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await _stores.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        _ledger.CountFor("draft").Should().Be(1, "the checkpoint was taken after draft, so it does not run again");
        _ledger.CountFor("review").Should().Be(2, "once failing on the first host, once succeeding on the second");
        _catalog.Lookups.Count.Should().BeGreaterThan(lookupsBefore, "the second host asked the source for itself");
    }

    [Fact]
    public async Task It_parks_on_a_gate_and_completes_after_approval()
    {
        await using LibraryHost host = await LibraryHost.StartAsync(_stores, _catalog, "replica-a");
        _catalog.Publish(new CanvasWorkflow("canvas", "1.0.0", _ledger, gateReview: true));

        string instanceId = await StartAsync(host.Client, "canvas", "t1");
        await _stores.WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);
        _ledger.CountFor("review").Should().Be(0, "no side effect may precede the decision");

        JsonElement approvals = await host.Client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/approvals");
        string approvalId = approvals.GetProperty("items").EnumerateArray().Single().GetProperty("approvalId").GetString()!;

        HttpResponseMessage decision = await host.Client.PostAsJsonAsync(
            $"/approvals/{approvalId}/decision", new { decision = "approve" });
        decision.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await _stores.WaitForStatusAsync(instanceId, InstanceStatus.Completed);
        _ledger.CountFor("review").Should().Be(1);
    }

    [Fact]
    public async Task A_tenant_gate_policy_set_through_the_API_applies_to_it()
    {
        await using LibraryHost host = await LibraryHost.StartAsync(_stores, _catalog, "replica-a");
        _catalog.Publish(new CanvasWorkflow("canvas", "1.0.0", _ledger));

        using var put = new HttpRequestMessage(HttpMethod.Put, "/workflows/canvas/versions/1.0.0/nodes/review")
        {
            Content = JsonContent.Create(new { mode = "requireApproval", reason = "four eyes" })
        };
        put.Headers.Add("X-Tenant-Id", "t1");
        (await host.Client.SendAsync(put)).StatusCode.Should().Be(HttpStatusCode.OK);

        string gated = await StartAsync(host.Client, "canvas", "t1");
        string open = await StartAsync(host.Client, "canvas", "t2");

        await _stores.WaitForStatusAsync(gated, InstanceStatus.AwaitingApproval);
        await _stores.WaitForStatusAsync(open, InstanceStatus.Completed);
    }

    [Fact]
    public async Task Starting_an_unknown_name_is_404_and_creates_no_instance()
    {
        await using LibraryHost host = await LibraryHost.StartAsync(_stores, _catalog, "replica-a");

        HttpResponseMessage response = await PostStartAsync(host.Client, "nothing-by-this-name", "t1");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _stores.Instances.QueryAsync(new InstanceQuery(), default)).Items.Should().BeEmpty();
        _catalog.Lookups.Should().Contain(l => l.Name == "nothing-by-this-name", "the source was asked before the answer was no");
    }

    [Fact]
    public async Task Another_tenant_cannot_start_a_private_workflow()
    {
        await using LibraryHost host = await LibraryHost.StartAsync(_stores, _catalog, "replica-a");
        _catalog.Publish(new CanvasWorkflow("canvas", "1.0.0", _ledger), ownerTenant: "t1");

        HttpResponseMessage stranger = await PostStartAsync(host.Client, "canvas", "t2");
        HttpResponseMessage owner = await PostStartAsync(host.Client, "canvas", "t1");

        stranger.StatusCode.Should().Be(HttpStatusCode.NotFound, "indistinguishable from a workflow that does not exist");
        owner.StatusCode.Should().Be(HttpStatusCode.Accepted);

        (await _stores.Instances.QueryAsync(new InstanceQuery(), default)).Items
            .Should().ContainSingle().Which.TenantId.Should().Be("t1");
    }

    [Fact]
    public async Task It_is_absent_from_the_catalog_listing_and_present_on_its_own_routes()
    {
        await using LibraryHost host = await LibraryHost.StartAsync(_stores, _catalog, "replica-a");
        _catalog.Publish(new CanvasWorkflow("canvas", "1.0.0", _ledger));

        string instanceId = await StartAsync(host.Client, "canvas", "t1");
        await _stores.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        // The deliberate limit: a source is asked for one workflow at a time and cannot be listed.
        JsonElement listing = await host.Client.GetFromJsonAsync<JsonElement>("/workflows");
        listing.EnumerateArray().Should().BeEmpty();
        (await host.Client.GetAsync("/workflows/canvas")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Everything that resolves by name and version works.
        using var nodesRequest = new HttpRequestMessage(HttpMethod.Get, "/workflows/canvas/versions/1.0.0/nodes");
        nodesRequest.Headers.Add("X-Tenant-Id", "t1");
        HttpResponseMessage nodes = await host.Client.SendAsync(nodesRequest);
        nodes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await nodes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("executorId").GetString()).Should().Equal("draft", "review");

        (await host.Client.GetAsync($"/instances/{instanceId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement graph = await host.Client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/graph");
        graph.GetProperty("nodes").GetProperty("review").GetString().Should().Be("Completed");
    }

    [Fact]
    public async Task A_DSL_document_served_by_a_source_runs()
    {
        await using LibraryHost host = await LibraryHost.StartAsync(_stores, _catalog, "replica-a");
        _catalog.Publish(DslWorkflowDefinition.Create(DslParser.ParseOrThrow(
            DslDocuments.Linear.Replace("\"dsl-linear\"", "\"dsl-sourced\"", StringComparison.Ordinal))));

        string instanceId = await StartAsync(host.Client, "dsl-sourced", "t1", new { orderId = "ORD-DSL", amount = 21 });

        WorkflowInstance done = await _stores.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        using JsonDocument result = JsonDocument.Parse(done.ResultJson!);
        result.RootElement.GetProperty("status").GetString().Should().Be("done");
        result.RootElement.GetProperty("carried").GetString().Should().Be("ORD-DSL");
    }

    private static async Task<string> StartAsync(HttpClient client, string workflow, string tenantId, object? context = null)
    {
        HttpResponseMessage response = await PostStartAsync(client, workflow, tenantId, context);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("instanceId").GetString()!;
    }

    private static async Task<HttpResponseMessage> PostStartAsync(
        HttpClient client, string workflow, string tenantId, object? context = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/workflows/{workflow}/instances")
        {
            Content = JsonContent.Create(new { context = context ?? new OrderContext("ORD-1") })
        };
        request.Headers.Add("X-Tenant-Id", tenantId);
        return await client.SendAsync(request);
    }
}

/// <summary>Flipped by a test between hosts, read by the executors the source builds.</summary>
public sealed class Toggle(bool on)
{
    private volatile bool _on = on;

    public bool On
    {
        get => _on;
        set => _on = value;
    }
}

/// <summary>
/// Two nodes, draft then review. Review can be gated, or made to dead-stop the run while a toggle
/// is on, so a checkpoint taken after draft is left to resume from.
/// </summary>
public sealed class CanvasWorkflow(
    string name, string version, SideEffectLedger ledger, bool gateReview = false, Toggle? reviewFails = null)
    : IWorkflowDefinition<OrderContext, OrderResult>
{
    public string Name => name;
    public string Version => version;

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding draft = context.Node(new Draft("draft", ledger, context.InstanceId));
        ExecutorBinding review = context.Node(
            new Review("review", ledger, context.InstanceId, reviewFails),
            gateReview ? gate => gate.Mode(ExecutionMode.RequireApproval).Reason("CanvasReview") : null);

        return new ValueTask<Workflow>(new WorkflowBuilder(draft)
            .AddEdge(draft, review)
            .WithOutputFrom(review)
            .WithName(Name)
            .Build());
    }

    private sealed class Draft(string id, SideEffectLedger ledger, string instanceId)
        : HostExecutor<OrderContext, OrderContext>(id)
    {
        protected override ValueTask<OrderContext> ExecuteCoreAsync(
            OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            ledger.Record(Id, instanceId);
            return ValueTask.FromResult(input);
        }
    }

    private sealed class Review(string id, SideEffectLedger ledger, string instanceId, Toggle? fails)
        : HostExecutor<OrderContext, OrderResult>(id)
    {
        protected override ValueTask<OrderResult> ExecuteCoreAsync(
            OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            ledger.Record(Id, instanceId);

            if (fails?.On == true)
            {
                throw new WorkflowDeadStopException("Review refused while the toggle is on.");
            }

            return ValueTask.FromResult(new OrderResult(input.OrderId, "reviewed"));
        }
    }
}

/// <summary>
/// The embedding host's own storage, reduced to a list: definitions published at any time, some
/// private to one tenant, the newest version current.
/// </summary>
public sealed class CanvasCatalog : IWorkflowDefinitionSource
{
    private readonly ConcurrentQueue<(IWorkflowDefinition Definition, string? Owner)> _published = new();
    private readonly ConcurrentQueue<WorkflowLookup> _lookups = new();

    public IReadOnlyList<WorkflowLookup> Lookups => [.. _lookups];

    public void Publish(IWorkflowDefinition definition, string? ownerTenant = null)
        => _published.Enqueue((definition, ownerTenant));

    public ValueTask<ResolvedWorkflow?> FindAsync(WorkflowLookup lookup, CancellationToken cancellationToken)
    {
        _lookups.Enqueue(lookup);

        var match = _published
            .Where(p => string.Equals(p.Definition.Name, lookup.Name, StringComparison.OrdinalIgnoreCase))
            .Where(p => lookup.Version is null
                        || string.Equals(p.Definition.Version, lookup.Version, StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Owner is null || p.Owner == lookup.TenantId || lookup.TenantId == Tenancy.AllTenants)
            .OrderByDescending(p => Version.Parse(p.Definition.Version))
            .Select(p => new ResolvedWorkflow(p.Definition) { TenantId = p.Owner })
            .FirstOrDefault();

        return ValueTask.FromResult(match);
    }
}

/// <summary>What two replicas share: the database, in memory.</summary>
public sealed class SharedHostStores
{
    public InMemoryInstanceStore Instances { get; } = new(TimeProvider.System);
    public InMemoryEventStore Events { get; } = new();
    public InMemoryApprovalStore Approvals { get; } = new();
    public InMemoryGatePolicyStore GatePolicies { get; } = new();
    public InMemoryLogStore Logs { get; } = new();
    public InMemoryAuditStore Audit { get; } = new();
    public InMemoryBlobStore Blobs { get; } = new();
    public OverflowCheckpointStore Checkpoints { get; }

    public SharedHostStores() => Checkpoints = new OverflowCheckpointStore(Blobs, clock: TimeProvider.System);

    /// <summary>Registered ahead of the host's defaults, which are only added when absent.</summary>
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IInstanceStore>(Instances);
        services.AddSingleton<IEventStore>(Events);
        services.AddSingleton<IApprovalStore>(Approvals);
        services.AddSingleton<IGatePolicyStore>(GatePolicies);
        services.AddSingleton<ILogStore>(Logs);
        services.AddSingleton<IAuditStore>(Audit);
        services.AddSingleton<IBlobStore>(Blobs);
        services.AddSingleton(Checkpoints);
    }

    public async Task<WorkflowInstance> WaitForStatusAsync(string instanceId, params InstanceStatus[] expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            WorkflowInstance? instance = await Instances.GetAsync(instanceId, default);
            if (instance is not null && expected.Contains(instance.Status))
            {
                return instance;
            }

            await Task.Delay(25);
        }

        WorkflowInstance? last = await Instances.GetAsync(instanceId, default);
        throw new TimeoutException(
            $"Instance '{instanceId}' was '{last?.Status.ToString() ?? "missing"}' ({last?.TerminalReason}), " +
            $"expected one of {string.Join(", ", expected)}.");
    }
}

/// <summary>
/// The library alone on a test server — no deployable host, no example workflows, nothing
/// registered — which is how an embedding host composes Flowline.
/// </summary>
public sealed class LibraryHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LibraryHost(WebApplication app)
    {
        _app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public static async Task<LibraryHost> StartAsync(
        SharedHostStores stores, IWorkflowDefinitionSource source, string replicaId)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        stores.Register(builder.Services);
        builder.Services
            .AddWorkflowHost(builder.Configuration, options =>
            {
                options.ReplicaId = replicaId;
                options.Approvals.SweepIntervalSeconds = 1;
            })
            .AddWorkflowSource(source)
            .AddBackgroundServices();

        WebApplication app = builder.Build();
        app.MapWorkflowApi();
        await app.StartAsync();

        return new LibraryHost(app);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
