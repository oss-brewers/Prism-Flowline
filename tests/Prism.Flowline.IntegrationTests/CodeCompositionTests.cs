using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Abstractions.Middleware;
using Prism.Flowline.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FluentAssertions;
using Xunit;

namespace Prism.Flowline.IntegrationTests;

/// <summary>Two nodes and nothing else, so a pipeline test is about the pipeline.</summary>
internal sealed class TwoStepWorkflow(string name, string version, string firstId, string secondId)
    : IWorkflowDefinition<OrderContext, OrderResult>
{
    public string Name => name;
    public string Version => version;

    public ValueTask<Workflow> BuildAsync(WorkflowBuildContext context, CancellationToken cancellationToken)
    {
        ExecutorBinding first = context.Node(new Step(firstId));
        ExecutorBinding second = context.Node(new Finish(secondId, version));

        return new ValueTask<Workflow>(new WorkflowBuilder(first)
            .AddEdge(first, second)
            .WithOutputFrom(second)
            .WithName(Name)
            .Build());
    }

    private sealed class Step(string id) : HostExecutor<OrderContext, OrderContext>(id)
    {
        protected override ValueTask<OrderContext> ExecuteCoreAsync(
            OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(input);
    }

    private sealed class Finish(string id, string version) : HostExecutor<OrderContext, OrderResult>(id)
    {
        protected override ValueTask<OrderResult> ExecuteCoreAsync(
            OrderContext input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new OrderResult(input.OrderId, $"settled-by-{version}"));
    }
}

/// <summary>Middleware that records its own name on the way in and on the way out.</summary>
internal sealed class OrderedExecutorMiddleware(MiddlewareTrace trace, string name, int order, string? onlyNode = null)
    : IExecutorMiddleware
{
    public int Order => order;

    public bool AppliesTo(ExecutorDescriptor descriptor)
        => onlyNode is null || string.Equals(descriptor.ExecutorId, onlyNode, StringComparison.Ordinal);

    public async ValueTask InvokeAsync(
        ExecutorInvocationContext context, ExecutorDelegate next, CancellationToken cancellationToken)
    {
        trace.Add($"in:{name}:{context.Descriptor.ExecutorId}");
        await next(context, cancellationToken);
        trace.Add($"out:{name}:{context.Descriptor.ExecutorId}");
    }
}

internal sealed class OrderedWorkflowMiddleware(MiddlewareTrace trace, string name, int order) : IWorkflowMiddleware
{
    public int Order => order;

    public async ValueTask InvokeAsync(
        WorkflowInvocationContext context, WorkflowDelegate next, CancellationToken cancellationToken)
    {
        trace.Add($"in:{name}");
        await next(context, cancellationToken);
        trace.Add($"out:{name}");
    }
}

/// <summary>
/// A host whose only interesting feature is how many middlewares it has and what they are ordered
/// by. Two versions of one workflow live here too, because version resolution is the other piece of
/// composition an author controls and neither needs a fixture of its own.
/// </summary>
public sealed class CodeCompositionHostFixture : WebApplicationFactory<Program>
{
    public MiddlewareTrace Trace { get; } = new();

    private readonly string _auditDatabasePath =
        Path.Combine(Path.GetTempPath(), $"prism-composition-audit-{Guid.NewGuid():N}.db");

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Trace);

            services.AddSingleton<IWorkflowDefinition>(
                new TwoStepWorkflow("layered-order", "1.0.0", "alpha", "omega"));

            // Same name, two versions, deliberately out of registration order so "the default is the
            // highest" cannot pass by accident of ordering.
            services.AddSingleton<IWorkflowDefinition>(
                new TwoStepWorkflow("versioned-order", "2.0.0", "prepare", "settle-v2"));
            services.AddSingleton<IWorkflowDefinition>(
                new TwoStepWorkflow("versioned-order", "1.0.0", "prepare", "settle-v1"));

            // Declared out of Order order for the same reason.
            services.AddSingleton<IExecutorMiddleware>(new OrderedExecutorMiddleware(Trace, "middle", 20));
            services.AddSingleton<IExecutorMiddleware>(new OrderedExecutorMiddleware(Trace, "outer", 10));
            services.AddSingleton<IExecutorMiddleware>(
                new OrderedExecutorMiddleware(Trace, "alpha-only", 30, onlyNode: "alpha"));

            services.AddSingleton<IWorkflowMiddleware>(new OrderedWorkflowMiddleware(Trace, "wf-inner", 15));
            services.AddSingleton<IWorkflowMiddleware>(new OrderedWorkflowMiddleware(Trace, "wf-outer", 5));
        });

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.UseSetting("Prism:AuditRecords:ConnectionString", $"Data Source={_auditDatabasePath}");

    public T Resolve<T>() where T : notnull => Services.GetRequiredService<T>();

    public async Task<WorkflowInstance> WaitForStatusAsync(string instanceId, params InstanceStatus[] expected)
    {
        var store = Resolve<IInstanceStore>();
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            WorkflowInstance? instance = await store.GetAsync(instanceId, default);
            if (instance is not null && expected.Contains(instance.Status)) return instance;
            await Task.Delay(25);
        }

        WorkflowInstance? last = await store.GetAsync(instanceId, default);
        throw new TimeoutException(
            $"Instance '{instanceId}' was '{last?.Status.ToString() ?? "missing"}'. " +
            $"Reason: {last?.TerminalReason ?? "(none)"}");
    }

    public async Task<string> StartAsync(HttpClient client, string path, object context)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(path, new { context });
        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("instanceId").GetString()!;
    }

    /// <summary>Waits for an entry, since the outermost middleware unwinds after the terminal status.</summary>
    public Task<bool> TraceHasAsync(string entry) => Trace.WaitForAsync(entry);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        try
        {
            if (File.Exists(_auditDatabasePath)) File.Delete(_auditDatabasePath);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// How several middlewares compose. One middleware proves the seam exists; the order they nest in
/// and which nodes they see is the part an author actually has to reason about.
/// </summary>
public class MiddlewarePipelineCompositionTests : IClassFixture<CodeCompositionHostFixture>
{
    private readonly CodeCompositionHostFixture _host;
    private readonly HttpClient _client;

    public MiddlewarePipelineCompositionTests(CodeCompositionHostFixture host)
    {
        _host = host;
        _client = host.CreateClient();
    }

    private async Task<IReadOnlyList<string>> RunAndTraceAsync(string orderId)
    {
        string instanceId = await _host.StartAsync(
            _client, "/workflows/layered-order/instances", new OrderContext(orderId));

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);
        await _host.TraceHasAsync("out:wf-outer");

        return _host.Trace.Entries;
    }

    [Fact]
    public async Task Executor_middleware_nests_by_ascending_order()
    {
        IReadOnlyList<string> trace = await RunAndTraceAsync("ORD-MW1");

        int outer = trace.ToList().IndexOf("in:outer:omega");
        int middle = trace.ToList().IndexOf("in:middle:omega");

        outer.Should().BeGreaterThanOrEqualTo(0);
        middle.Should().BeGreaterThan(outer, "a lower Order is the outer wrapper, so it is entered first");
    }

    [Fact]
    public async Task Executor_middleware_unwinds_in_the_reverse_order_it_entered()
    {
        IReadOnlyList<string> trace = await RunAndTraceAsync("ORD-MW2");
        List<string> list = [.. trace];

        int middleOut = list.IndexOf("out:middle:omega");
        int outerOut = list.IndexOf("out:outer:omega");

        middleOut.Should().BeGreaterThanOrEqualTo(0);
        outerOut.Should().BeGreaterThan(middleOut, "the outermost wrapper is the last to finish");
    }

    [Fact]
    public async Task Applies_to_keeps_a_middleware_off_the_nodes_it_declined()
    {
        IReadOnlyList<string> trace = await RunAndTraceAsync("ORD-MW3");

        trace.Should().Contain("in:alpha-only:alpha",
            "the middleware declared it applies to alpha");

        trace.Should().NotContain("in:alpha-only:omega",
            "and AppliesTo is a filter, not a suggestion");
    }

    [Fact]
    public async Task A_middleware_without_a_filter_sees_every_attached_node()
    {
        IReadOnlyList<string> trace = await RunAndTraceAsync("ORD-MW4");

        trace.Should().Contain("in:outer:alpha").And.Contain("in:outer:omega");
    }

    [Fact]
    public async Task Workflow_middleware_nests_by_ascending_order_and_wraps_the_executors()
    {
        IReadOnlyList<string> trace = await RunAndTraceAsync("ORD-MW5");
        List<string> list = [.. trace];

        int outerIn = list.IndexOf("in:wf-outer");
        int innerIn = list.IndexOf("in:wf-inner");
        int firstExecutor = list.FindIndex(e => e.StartsWith("in:outer:", StringComparison.Ordinal));

        outerIn.Should().BeGreaterThanOrEqualTo(0);
        innerIn.Should().BeGreaterThan(outerIn);
        firstExecutor.Should().BeGreaterThan(innerIn, "workflow middleware is outside every executor");
    }

    /// <summary>
    /// Registering middleware adds to the framework's own rather than replacing it — the host ships
    /// telemetry and request logging, and an author who wanted them gone would have to say so.
    /// </summary>
    [Fact]
    public void Author_middleware_is_composed_alongside_the_frameworks_own()
    {
        var factory = _host.Resolve<MiddlewarePipelineFactory>();

        factory.ExecutorMiddlewareCount.Should().BeGreaterThan(3,
            "three were registered here, and the host contributes its own on top");

        factory.WorkflowMiddlewareCount.Should().BeGreaterThan(2);
    }
}

/// <summary>
/// Two versions of one workflow, which is the whole point of the registry's resolution rule: a
/// published version keeps running while the next one is registered beside it.
/// </summary>
public class WorkflowVersionResolutionTests : IClassFixture<CodeCompositionHostFixture>
{
    private readonly CodeCompositionHostFixture _host;
    private readonly HttpClient _client;

    public WorkflowVersionResolutionTests(CodeCompositionHostFixture host)
    {
        _host = host;
        _client = host.CreateClient();
    }

    [Fact]
    public async Task Both_versions_are_listed_by_the_catalog()
    {
        JsonElement body = await _client.GetFromJsonAsync<JsonElement>("/workflows/versioned-order");

        string[] versions = [.. body.GetProperty("versions").EnumerateArray()
            .Select(v => v.TryGetProperty("version", out JsonElement s) ? s.GetString()! : v.GetString()!)];

        versions.Should().BeEquivalentTo(["2.0.0", "1.0.0"]);
    }

    [Fact]
    public async Task A_start_without_a_version_takes_the_highest()
    {
        string instanceId = await _host.StartAsync(
            _client, "/workflows/versioned-order/instances", new OrderContext("ORD-V1"));

        WorkflowInstance instance = await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        instance.WorkflowVersion.Should().Be("2.0.0",
            "the newest published version is what an unqualified start means");
    }

    [Fact]
    public async Task A_start_naming_an_older_version_gets_that_one()
    {
        string instanceId = await _host.StartAsync(
            _client, "/workflows/versioned-order/instances?version=1.0.0", new OrderContext("ORD-V2"));

        WorkflowInstance instance = await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        instance.WorkflowVersion.Should().Be("1.0.0",
            "a run already in flight against a published version must stay on it");
    }

    [Fact]
    public async Task The_version_that_ran_is_reported_on_the_instance()
    {
        string instanceId = await _host.StartAsync(
            _client, "/workflows/versioned-order/instances?version=1.0.0", new OrderContext("ORD-V3"));

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement dto = await _client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}");
        dto.GetProperty("workflowVersion").GetString().Should().Be("1.0.0");
    }

    [Fact]
    public async Task An_unknown_version_is_not_found_rather_than_falling_back()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/workflows/versioned-order/instances?version=9.9.9",
            new { context = new OrderContext("ORD-V4") });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "silently running a different version than the caller asked for is the worst answer here");
    }

    [Fact]
    public async Task Each_version_publishes_its_own_nodes()
    {
        JsonElement one = await _client.GetFromJsonAsync<JsonElement>(
            "/workflows/versioned-order/versions/1.0.0/nodes");
        JsonElement two = await _client.GetFromJsonAsync<JsonElement>(
            "/workflows/versioned-order/versions/2.0.0/nodes");

        Ids(one).Should().Contain("settle-v1").And.NotContain("settle-v2");
        Ids(two).Should().Contain("settle-v2").And.NotContain("settle-v1");

        static string[] Ids(JsonElement body) =>
            [.. body.GetProperty("nodes").EnumerateArray()
                .Select(n => n.GetProperty("executorId").GetString()!)];
    }

    [Fact]
    public async Task The_two_versions_produce_their_own_results()
    {
        string first = await _host.StartAsync(
            _client, "/workflows/versioned-order/instances?version=1.0.0", new OrderContext("ORD-V5"));
        string second = await _host.StartAsync(
            _client, "/workflows/versioned-order/instances?version=2.0.0", new OrderContext("ORD-V6"));

        WorkflowInstance one = await _host.WaitForStatusAsync(first, InstanceStatus.Completed);
        WorkflowInstance two = await _host.WaitForStatusAsync(second, InstanceStatus.Completed);

        one.ResultJson.Should().Contain("settled-by-1.0.0");
        two.ResultJson.Should().Contain("settled-by-2.0.0");
    }
}
