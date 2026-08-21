using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Abstractions.Middleware;
using Prism.Flowline.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Prism.Flowline.IntegrationTests;

/// <summary>
/// Records the descriptor metadata of every executor invocation, keyed by executor id.
/// </summary>
public sealed class NodeMetadataProbe : IExecutorMiddleware
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, object?>> _seen = new();

    public IReadOnlyDictionary<string, object?>? For(string executorId)
        => _seen.TryGetValue(executorId, out IReadOnlyDictionary<string, object?>? m) ? m : null;

    public ValueTask InvokeAsync(
        ExecutorInvocationContext context, ExecutorDelegate next, CancellationToken cancellationToken)
    {
        _seen[context.Descriptor.ExecutorId] =
            new Dictionary<string, object?>(context.Descriptor.Metadata, StringComparer.Ordinal);

        return next(context, cancellationToken);
    }
}

/// <summary>
/// The same host, with the approval sweep pushed out so the expiry documents are driven a stage at
/// a time rather than by whatever the background sweeper happened to do first.
/// </summary>
public sealed class DslAuthoringHostFixture : DslHostFixture
{
    protected override string ApprovalSweepIntervalSeconds => "3600";

    /// <summary>
    /// What executor middleware sees for each node. Middleware is the only vantage point on an
    /// executor's metadata, and metadata is what a drift or telemetry pipeline keys on — so a
    /// document's model and prompt version are either here or nowhere.
    /// </summary>
    public NodeMetadataProbe Metadata { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
            services.AddSingleton<Abstractions.Middleware.IExecutorMiddleware>(Metadata));

        return base.CreateHost(builder);
    }

    public ValueTask<int> SweepAsync() => Resolve<IApprovalService>().SweepExpiredAsync(default);

    public async Task<ApprovalRequest?> ApprovalAsync(string approvalId)
        => await Resolve<IApprovalStore>().GetAsync(approvalId, default);

    public async Task<(string InstanceId, string ApprovalId)> StartAndParkAsync(
        HttpClient client, string workflow, object context)
    {
        string instanceId = await StartAsync(client, workflow, context);
        await WaitForStatusAsync(instanceId, InstanceStatus.AwaitingApproval);

        JsonElement body = await client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/approvals");
        string approvalId = body.GetProperty("items").EnumerateArray().First()
            .GetProperty("approvalId").GetString()!;

        return (instanceId, approvalId);
    }

    public ValueTask<DecisionResult> DecideAsync(
        string approvalId, string deciderId, ApprovalOutcomeKind outcome)
        => Resolve<IApprovalService>().ApplyDecisionAsync(
            approvalId,
            new ApprovalDecisionInput { Decision = outcome, DeciderIdOverride = deciderId },
            user: null,
            cancellationToken: default);
}

/// <summary>
/// The gate vocabulary as a document declares it. <see cref="DslWorkflowTests"/> covers the
/// conditional-threshold shape; these cover the rest of the keys the schema publishes, and prove a
/// gate reaches the runtime with the same force whether it was authored in C# or in JSON.
/// </summary>
public class DslGateAuthoringTests : IClassFixture<DslAuthoringHostFixture>
{
    private readonly DslAuthoringHostFixture _fixture;
    private readonly HttpClient _client;

    public DslGateAuthoringTests(DslAuthoringHostFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    private static object Context(string orderId, decimal amount = 100m)
        => new { orderId, amount };

    [Fact]
    public async Task Every_gate_key_a_document_declares_reaches_the_raised_request()
    {
        (_, string approvalId) =
            await _fixture.StartAndParkAsync(_client, "dsl-gate-full", Context("ORD-DG1"));

        ApprovalRequest? approval = await _fixture.ApprovalAsync(approvalId);

        approval!.Reason.Should().Be("DualControl");
        approval.Assignees.Should().BeEquivalentTo(["user:alice", "group:finance"]);
        approval.RequiredApprovers.Should().Be(2);
        approval.AllowModification.Should().BeTrue();
        approval.RequireSegregationOfDuties.Should().BeTrue();
        approval.OnExpiry.Should().Be(ExpiryAction.Escalate);
        approval.ExpiresAt.Should().BeCloseTo(approval.CreatedAt.AddHours(8), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_documents_quorum_holds_the_run_until_the_second_approver()
    {
        (string instanceId, string approvalId) =
            await _fixture.StartAndParkAsync(_client, "dsl-gate-full", Context("ORD-DG2"));

        DecisionResult first = await _fixture.DecideAsync(approvalId, "user:alice", ApprovalOutcomeKind.Approve);
        first.Kind.Should().Be(DecisionResultKind.QuorumPending);

        (await _fixture.ApprovalAsync(approvalId))!.State.Should().Be(ApprovalState.Pending);

        DecisionResult second = await _fixture.DecideAsync(approvalId, "group:finance", ApprovalOutcomeKind.Approve);
        second.Kind.Should().Be(DecisionResultKind.Accepted);

        await _fixture.WaitForStatusAsync(instanceId, InstanceStatus.Completed);
    }

    [Fact]
    public async Task A_documents_assignee_list_refuses_anyone_else()
    {
        (_, string approvalId) =
            await _fixture.StartAndParkAsync(_client, "dsl-gate-full", Context("ORD-DG3"));

        DecisionResult result = await _fixture.DecideAsync(approvalId, "mallory", ApprovalOutcomeKind.Approve);

        result.Kind.Should().Be(DecisionResultKind.Forbidden);
    }

    [Fact]
    public async Task A_documents_auto_approval_on_expiry_lets_the_run_finish()
    {
        (string instanceId, string approvalId) =
            await _fixture.StartAndParkAsync(_client, "dsl-gate-auto", Context("ORD-DG4"));

        await _fixture.SweepAsync();

        (await _fixture.ApprovalAsync(approvalId))!.State.Should().Be(ApprovalState.Approved);
        await _fixture.WaitForStatusAsync(instanceId, InstanceStatus.Completed);
    }

    [Fact]
    public async Task A_documents_dead_stop_on_expiry_ends_the_run()
    {
        (string instanceId, string approvalId) =
            await _fixture.StartAndParkAsync(_client, "dsl-gate-lapse", Context("ORD-DG5"));

        await _fixture.SweepAsync();

        (await _fixture.ApprovalAsync(approvalId))!.State.Should().Be(ApprovalState.Expired);

        WorkflowInstance instance = await _fixture.WaitForStatusAsync(instanceId, InstanceStatus.DeadStopped);
        instance.TerminalReason.Should().Be("ApprovalExpired");
    }

    [Fact]
    public async Task The_expiry_window_a_document_declares_is_the_one_the_host_enforces()
    {
        (_, string approvalId) =
            await _fixture.StartAndParkAsync(_client, "dsl-gate-auto", Context("ORD-DG6"));

        ApprovalRequest? approval = await _fixture.ApprovalAsync(approvalId);

        approval!.ExpiresAt.Should().BeCloseTo(approval.CreatedAt, TimeSpan.FromSeconds(1),
            "a millisecond window must not silently become the 24-hour default");
    }
}

/// <summary>
/// The failure dispositions a document can declare. Dead-stop is covered in
/// <see cref="DslWorkflowTests"/>; retry and escalate are the two that change what happens next
/// rather than merely ending the run.
/// </summary>
public class DslFailureRuleTests : IClassFixture<DslAuthoringHostFixture>
{
    private readonly DslAuthoringHostFixture _fixture;
    private readonly HttpClient _client;

    public DslFailureRuleTests(DslAuthoringHostFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    [Fact]
    public async Task A_retry_rule_runs_the_node_again_and_the_run_completes()
    {
        int calls = 0;
        _fixture.Http.Respond = _ =>
            Interlocked.Increment(ref calls) == 1
                ? (HttpStatusCode.InternalServerError, """{ "error": "flaky" }""")
                : (HttpStatusCode.OK, """{ "ok": true }""");

        string id = await _fixture.StartAsync(_client, "dsl-retrying", new { orderId = "ORD-DR1" });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        calls.Should().BeGreaterThan(1, "the first call failed and the document said to try again");
    }

    [Fact]
    public async Task A_retry_rule_parks_the_instance_as_retry_scheduled_between_attempts()
    {
        _fixture.Http.Respond = _ => (HttpStatusCode.InternalServerError, """{ "error": "always" }""");

        string id = await _fixture.StartAsync(_client, "dsl-retrying", new { orderId = "ORD-DR2" });

        WorkflowInstance instance = await _fixture.WaitForStatusAsync(
            id, InstanceStatus.RetryScheduled, InstanceStatus.DeadStopped, InstanceStatus.Failed);

        instance.Status.Should().Be(InstanceStatus.RetryScheduled,
            "a retryable failure schedules another attempt rather than ending the run");
        instance.NextRetryAt.Should().NotBeNull();
    }

    [Fact]
    public async Task An_escalate_rule_parks_for_input_rather_than_stopping()
    {
        _fixture.Http.Respond = _ => (HttpStatusCode.BadRequest, """{ "error": "nope" }""");

        string id = await _fixture.StartAsync(_client, "dsl-escalating", new { orderId = "ORD-DE1" });

        WorkflowInstance instance = await _fixture.WaitForStatusAsync(id, InstanceStatus.AwaitingInput);

        instance.TerminalReason.Should().NotBeNullOrEmpty(
            "the run is parked for a person, so it must say what stopped it");
    }
}

/// <summary>
/// Everything a document declares around the graph rather than inside a node: triggers that reshape
/// their message, edge metadata, the notification policy block, and the keys that are accepted and
/// then ignored.
/// </summary>
public class DslDocumentSurfaceTests : IClassFixture<DslAuthoringHostFixture>
{
    private readonly DslAuthoringHostFixture _fixture;
    private readonly HttpClient _client;

    public DslDocumentSurfaceTests(DslAuthoringHostFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    // ---- triggers ---------------------------------------------------------------------------

    [Fact]
    public async Task A_trigger_builds_its_start_context_from_the_branch_it_names()
    {
        HttpResponseMessage published = await _client.PostAsJsonAsync("/events", new
        {
            topic = "dsl.shaped.placed",
            correlationKey = "batch-7",
            payload = new
            {
                orderId = "ORD-DT1",
                order = new { orderId = "ORD-DT1", amount = 41m },
                noise = "ignored"
            }
        });

        published.IsSuccessStatusCode.Should().BeTrue();

        WorkflowInstance instance = await WaitForInstanceOfAsync("dsl-trigger-shaped");
        await _fixture.WaitForStatusAsync(instance.InstanceId, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(instance.InstanceId);

        result!["sawOrder"]!.GetValue<string>().Should().Be("ORD-DT1");
        result["sawAmount"]!.GetValue<decimal>().Should().Be(41m,
            "contextFrom selected the order branch, so the node saw its fields and not the envelope's");
    }

    [Fact]
    public async Task A_correlated_trigger_is_listed_with_the_key_it_correlates_on()
    {
        string body = await _client.GetStringAsync("/subscriptions?kind=Trigger");

        body.Should().Contain("dsl.shaped.placed");
        body.Should().Contain("batch-7",
            "an operator has to be able to see which messages a trigger will actually accept");
    }

    [Fact]
    public async Task A_message_outside_the_correlation_filter_starts_nothing()
    {
        var store = _fixture.Resolve<IInstanceStore>();

        Page<WorkflowInstance> before = await store.QueryAsync(
            new InstanceQuery { WorkflowName = "dsl-trigger-shaped", Limit = 100 }, default);

        HttpResponseMessage published = await _client.PostAsJsonAsync("/events", new
        {
            topic = "dsl.shaped.placed",
            correlationKey = "batch-99",
            payload = new { order = new { orderId = "ORD-DT9", amount = 1m } }
        });

        published.IsSuccessStatusCode.Should().BeTrue("an unmatched message is accepted, then dropped");

        await Task.Delay(750);

        Page<WorkflowInstance> after = await store.QueryAsync(
            new InstanceQuery { WorkflowName = "dsl-trigger-shaped", Limit = 100 }, default);

        after.Total.Should().Be(before.Total,
            "the document filters on batch-7, so a message from another batch is not its business");
    }

    // ---- edges ------------------------------------------------------------------------------

    [Fact]
    public async Task Edge_metadata_does_not_disturb_the_routing_it_annotates()
    {
        string large = await _fixture.StartAsync(_client, "dsl-edge-meta", new { orderId = "ORD-DL1", amount = 5000m });
        await _fixture.WaitForStatusAsync(large, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(large);
        result!["band"]!.GetValue<string>().Should().Be("large");
    }

    [Fact]
    public async Task A_labelled_edge_reaches_the_graph_an_operator_reads()
    {
        string id = await _fixture.StartAsync(_client, "dsl-edge-meta", new { orderId = "ORD-DL2", amount = 10m });
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonElement graph = await _client.GetFromJsonAsync<JsonElement>($"/instances/{id}/graph");

        graph.GetRawText().Should().Contain("small",
            "the traversed edge must be identifiable in the graph the control plane renders");
    }

    // ---- notifications ----------------------------------------------------------------------

    [Fact]
    public async Task A_document_that_declines_streaming_is_refused_by_the_live_endpoint()
    {
        string id = await _fixture.StartAsync(_client, "dsl-quiet", new { orderId = "ORD-DQ1", amount = 3m });
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        HttpResponseMessage response = await _client.GetAsync(
            $"/instances/{id}/events", HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "stream:false must be refused outright rather than left to hang on an empty stream");
    }

    [Fact]
    public async Task A_quiet_document_still_writes_a_durable_history()
    {
        string id = await _fixture.StartAsync(_client, "dsl-quiet", new { orderId = "ORD-DQ2", amount = 3m });
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonElement history = await _client.GetFromJsonAsync<JsonElement>($"/instances/{id}/events/history");

        history.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0,
            "the durable log is not optional however quiet the policy");
    }

    [Fact]
    public async Task A_documents_declared_event_names_are_advertised_by_the_catalog()
    {
        JsonElement workflow = await _client.GetFromJsonAsync<JsonElement>("/workflows/dsl-quiet");

        workflow.GetRawText().Should().Contain("priced",
            "a consumer must be able to discover what a workflow emits without running it");
    }

    /// <summary>
    /// A level is a filter on everything, the workflow's own events included: <c>dsl-notify</c>
    /// declares no level and its <c>custom.priced</c> reaches the log, while the same node under
    /// <c>lifecycle</c> does not. Worth stating explicitly, because "emits" is still advertised by
    /// the catalog for a workflow whose level then suppresses the emission.
    /// </summary>
    [Fact]
    public async Task A_quieter_level_silences_a_nodes_own_notification()
    {
        string id = await _fixture.StartAsync(_client, "dsl-quiet", new { orderId = "ORD-DQ3", amount = 6m });
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonElement history = await _client.GetFromJsonAsync<JsonElement>($"/instances/{id}/events/history");

        string[] types = [.. history.GetProperty("items").EnumerateArray()
            .Select(e => e.TryGetProperty("eventType", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty)];

        types.Should().NotContain("custom.priced",
            "the document asked for lifecycle, and node chatter is not lifecycle");

        types.Should().Contain("workflow.started").And.Contain("workflow.terminated",
            "lifecycle is what remains");
    }

    // ---- expressions ------------------------------------------------------------------------

    [Fact]
    public async Task The_closed_function_vocabulary_survives_the_whole_path()
    {
        string id = await _fixture.StartAsync(
            _client, "dsl-functions", new { orderId = "ORD-9", amount = 8m, active = true });
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);

        result!["upper"]!.GetValue<string>().Should().Be("ORD-9");
        result["lower"]!.GetValue<string>().Should().Be("ord-9");
        result["length"]!.GetValue<int>().Should().Be(5);
        result["starts"]!.GetValue<bool>().Should().BeTrue();
        result["ends"]!.GetValue<bool>().Should().BeTrue();
        result["holds"]!.GetValue<bool>().Should().BeTrue();
        result["present"]!.GetValue<bool>().Should().BeTrue();
        result["missing"]!.GetValue<bool>().Should().BeFalse();
        result["fallback"]!.GetValue<string>().Should().Be("default");
        result["asNumber"]!.GetValue<decimal>().Should().Be(50m);
        result["asString"]!.GetValue<string>().Should().Be("8");
        result["asBool"]!.GetValue<bool>().Should().BeTrue();
        result["notBool"].Should().BeNull(
            "bool() coerces a boolean and is absent for anything else, and an absent value is written as null");
        result["pattern"]!.GetValue<bool>().Should().BeTrue();
    }

    // ---- waits ------------------------------------------------------------------------------

    [Fact]
    public async Task A_wait_that_declares_a_timeout_still_parks_on_its_topic()
    {
        string id = await _fixture.StartAsync(_client, "dsl-wait-expiry", new { orderId = "ORD-DW1" });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.AwaitingInput);

        string subscriptions = await _client.GetStringAsync($"/subscriptions?instanceId={id}&pendingOnly=true");
        subscriptions.Should().Contain("dsl.expiring.settled");
    }

    [Fact]
    public async Task A_declared_timeout_reaches_the_subscription_rather_than_defaulting_to_forever()
    {
        string id = await _fixture.StartAsync(_client, "dsl-wait-expiry", new { orderId = "ORD-DW2" });
        await _fixture.WaitForStatusAsync(id, InstanceStatus.AwaitingInput);

        IReadOnlyList<DomainEventSubscription> subscriptions = await _fixture
            .Resolve<IDomainEventSubscriptionStore>()
            .QueryAsync(new DomainSubscriptionQuery { InstanceId = id }, default);

        subscriptions.Should().NotBeEmpty();
        subscriptions[0].ExpiresAt.Should().NotBeNull("the document declared PT2H, not 'wait forever'");
    }

    // ---- keys the reader accepts and nothing acts on -------------------------------------------

    /// <summary>
    /// Pins a gap rather than a behaviour. <c>strict</c>, <c>result</c> and <c>limits</c> are in the
    /// published schema and <c>DslDocumentReader</c> populates all three onto the document, but no
    /// part of the interpreter or the runtime reads them back: node output schemas are not enforced
    /// under <c>strict</c>, the result is not validated against <c>result</c>, and an instance takes
    /// its attempt ceiling from host options rather than from <c>limits</c>.
    /// </summary>
    [Fact]
    public async Task A_document_declaring_strict_and_a_result_schema_runs_without_enforcing_either()
    {
        string id = await _fixture.StartAsync(_client, "dsl-inert-keys", new { orderId = "ORD-DI1", amount = 2m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);

        result!["status"]!.GetValue<string>().Should().Be("done");
        result.AsObject().ContainsKey("mustBeHere").Should().BeFalse(
            "the declared result schema requires a key the run never produced, and the run completed anyway");
    }

    [Fact]
    public async Task A_documents_declared_limits_do_not_reach_the_instance()
    {
        string id = await _fixture.StartAsync(_client, "dsl-inert-keys", new { orderId = "ORD-DI2", amount = 2m });
        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        WorkflowInstance? instance = await _fixture.Resolve<IInstanceStore>().GetAsync(id, default);

        instance!.MaxAttempts.Should().Be(5,
            "the document asked for 2; the instance took the host default, which is the gap");
    }

    [Fact]
    public async Task A_document_declaring_inert_keys_is_still_accepted_by_the_validator()
    {
        HttpResponseMessage response = await _client.PostAsync(
            "/dsl/validate",
            new StringContent(DslAuthoringDocuments.DeclaredButInert, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("valid").GetBoolean().Should().BeTrue();
    }

    private async Task<WorkflowInstance> WaitForInstanceOfAsync(string workflowName)
    {
        var store = _fixture.Resolve<IInstanceStore>();
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            Page<WorkflowInstance> page = await store.QueryAsync(
                new InstanceQuery { WorkflowName = workflowName, Limit = 10 }, default);

            if (page.Items.Count > 0)
            {
                return page.Items[0];
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"No instance of '{workflowName}' was started.");
    }
}
