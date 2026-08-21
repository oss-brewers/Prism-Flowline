using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Abstractions.Middleware;
using FluentAssertions;
using Xunit;

namespace Prism.Flowline.IntegrationTests;

/// <summary>
/// The per-kind option surface. <see cref="DslWorkflowTests"/> proves each node kind runs at all;
/// what is covered here is every key the schema lets an author put on one, because an option that is
/// accepted by the validator and then dropped by the interpreter is indistinguishable from a working
/// one until production disagrees.
/// </summary>
public class DslHttpNodeOptionTests : IClassFixture<DslAuthoringHostFixture>
{
    private readonly DslAuthoringHostFixture _fixture;
    private readonly HttpClient _client;

    public DslHttpNodeOptionTests(DslAuthoringHostFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    /// <summary>418, which no default success set would ever include — the point of the document.</summary>
    private const HttpStatusCode Teapot = (HttpStatusCode)418;

    [Fact]
    public async Task Every_request_key_a_document_declares_reaches_the_wire()
    {
        _fixture.Http.Respond = _ => (Teapot, """{ "ok": true }""");

        string id = await _fixture.StartAsync(
            _client, "dsl-http-full", new { orderId = "ORD-HF1", amount = 12m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        SentRequest request = _fixture.Http.SentFor("ORD-HF1");

        request.Method.Should().Be("POST", "the document asked for POST, not the GET default");
        request.Url.Should().Be("https://ledger.internal/v1/orders/ORD-HF1");

        request.Header("X-Order").Should().Be("ORD-HF1", "the header template read the start context");
        request.Header("X-Static").Should().Be("fixed");
    }

    [Fact]
    public async Task A_declared_body_template_is_rendered_before_it_is_sent()
    {
        _fixture.Http.Respond = _ => (Teapot, """{ "ok": true }""");

        string id = await _fixture.StartAsync(
            _client, "dsl-http-full", new { orderId = "ORD-HF2", amount = 34m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        _fixture.Http.SentFor("ORD-HF2").Body.Should()
            .Contain("34", "the amount came from the start context through the body template");
    }

    [Fact]
    public async Task A_declared_success_set_replaces_the_default_rather_than_widening_it()
    {
        _fixture.Http.Respond = _ => (Teapot, """{ "brewed": false }""");

        string id = await _fixture.StartAsync(
            _client, "dsl-http-full", new { orderId = "ORD-HF3", amount = 1m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);
        result!["status"]!.GetValue<int>().Should().Be(418,
            "the document named 418 a success, so the run must treat it as one");
    }

    [Fact]
    public async Task A_code_outside_the_declared_success_set_fails_the_run_even_when_it_is_a_200()
    {
        _fixture.Http.Respond = _ => (HttpStatusCode.OK, """{ "ok": true }""");

        string id = await _fixture.StartAsync(
            _client, "dsl-http-full", new { orderId = "ORD-HF4", amount = 1m });

        WorkflowInstance instance = await _fixture.WaitForStatusAsync(
            id, InstanceStatus.DeadStopped, InstanceStatus.Failed, InstanceStatus.RetryScheduled);

        instance.Status.Should().NotBe(InstanceStatus.Completed,
            "successCodes is the whole set, so an unlisted 200 is a failure like any other");
    }

    [Fact]
    public async Task An_idempotency_key_can_be_declined_by_the_document()
    {
        _fixture.Http.Respond = _ => (Teapot, """{ "ok": true }""");

        string id = await _fixture.StartAsync(
            _client, "dsl-http-full", new { orderId = "ORD-HF5", amount = 1m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        _fixture.Http.SentFor("ORD-HF5").HasHeader("Idempotency-Key").Should().BeFalse(
            "sendIdempotencyKey was false, and a header sent anyway would defeat the point of saying so");
    }
}

/// <summary>
/// The <c>llm</c> node's option surface: what reaches the model client, what reaches the event
/// stream, and the one key the schema publishes that the interpreter cannot carry.
/// </summary>
public class DslLlmNodeOptionTests : IClassFixture<DslAuthoringHostFixture>
{
    private readonly DslAuthoringHostFixture _fixture;
    private readonly HttpClient _client;

    public DslLlmNodeOptionTests(DslAuthoringHostFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    [Fact]
    public async Task The_model_temperature_and_token_ceiling_reach_the_client()
    {
        string id = await _fixture.StartAsync(
            _client, "dsl-llm-full", new { orderId = "ORD-LF1", amount = 10m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        _fixture.Chat.LastOptions.Should().NotBeNull();
        _fixture.Chat.LastOptions!.ModelId.Should().Be("stub-model-x");
        _fixture.Chat.LastOptions.Temperature.Should().BeApproximately(0.25f, 0.0001f);
        _fixture.Chat.LastOptions.MaxOutputTokens.Should().Be(321);
    }

    [Fact]
    public async Task A_declared_system_prompt_is_sent_as_a_system_message()
    {
        string id = await _fixture.StartAsync(
            _client, "dsl-llm-full", new { orderId = "ORD-LF2", amount = 10m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        (string Role, string Text)[] messages = [.. _fixture.Chat.LastMessages];

        messages.Should().Contain(m => m.Role == "system",
            "a document that declares a system prompt must not have it folded into the user turn");

        messages.Should().Contain(m =>
            m.Role == "user" && m.Text.Contains("worth 10", StringComparison.Ordinal),
            "the user template is rendered against the start context");
    }

    /// <summary>
    /// Pins a defect. The schema types <c>system</c> as a template and the semantic validator checks
    /// the expressions inside it, but <c>LlmExecutor</c> renders only the user template — the system
    /// prompt is passed through verbatim. A document therefore sends the model literal
    /// <c>{{ $ctx.orderId }}</c> braces in the one message meant to set its frame.
    /// </summary>
    [Fact]
    public async Task A_system_prompt_template_is_currently_sent_unrendered()
    {
        string id = await _fixture.StartAsync(
            _client, "dsl-llm-full", new { orderId = "ORD-LF8", amount = 10m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        string system = _fixture.Chat.LastMessages.Single(m => m.Role == "system").Text;

        system.Should().Contain("{{ $ctx.orderId }}",
            "when the system prompt starts being rendered this should assert it contains ORD-LF8");

        system.Should().NotContain("ORD-LF8");
    }

    /// <summary>
    /// Pins a gap. <c>LlmExecutor</c> publishes the model and prompt version as executor metadata,
    /// which is what <c>LlmDriftMiddleware</c> keys a baseline on. <c>DslExecutor</c> overrides
    /// <c>Metadata</c> with the node's own kind and id and does not forward the inner executor's, so
    /// for a document-authored node neither value reaches middleware at all.
    /// </summary>
    [Fact]
    public async Task A_declared_prompt_version_does_not_reach_middleware()
    {
        string id = await _fixture.StartAsync(
            _client, "dsl-llm-full", new { orderId = "ORD-LF9", amount = 10m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        IReadOnlyDictionary<string, object?>? metadata = _fixture.Metadata.For("classify");

        metadata.Should().NotBeNull("the node ran through the executor pipeline");
        metadata!["node.kind"].Should().Be("llm", "what the DSL does publish");

        metadata.ContainsKey(MiddlewareContextKeys.PromptVersion).Should().BeFalse(
            "when the wrapper starts forwarding the inner executor's metadata this should read v7");
        metadata.ContainsKey("llm.model").Should().BeFalse();
    }

    [Fact]
    public async Task Emit_completion_false_keeps_the_completion_event_off_the_log()
    {
        string id = await _fixture.StartAsync(
            _client, "dsl-llm-full", new { orderId = "ORD-LF3", amount = 10m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        string[] types = await EventTypesAsync(id);

        types.Should().NotContain(WorkflowEventTypes.LlmCompleted,
            "the document declined the completion event");
    }

    [Fact]
    public async Task Emit_completion_true_puts_one_completion_on_the_log()
    {
        string id = await _fixture.StartAsync(
            _client, "dsl-llm-reporting", new { orderId = "ORD-LF4", amount = 10m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        string[] types = await EventTypesAsync(id);

        types.Count(t => t == WorkflowEventTypes.LlmCompleted).Should().Be(1,
            "one call, one completion — the event is what a cost report counts");
    }

    [Fact]
    public async Task Streamed_deltas_stay_out_of_the_durable_log()
    {
        string id = await _fixture.StartAsync(
            _client, "dsl-llm-full", new { orderId = "ORD-LF5", amount = 10m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        string[] types = await EventTypesAsync(id);

        types.Should().NotContain(WorkflowEventTypes.LlmDelta,
            "deltas are live fan-out; storing them would put model output in the history");
    }

    [Fact]
    public async Task The_model_response_is_projected_onto_the_envelope_with_its_usage()
    {
        string id = await _fixture.StartAsync(
            _client, "dsl-llm-reporting", new { orderId = "ORD-LF6", amount = 10m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);

        result!["model"]!.GetValue<string>().Should().Be("stub-model-y");
        result["inputTokens"]!.GetValue<int>().Should().Be(11);
        result["outputTokens"]!.GetValue<int>().Should().Be(7);
        result["text"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Streaming trades the usage figures away: the response is assembled from deltas, and what the
    /// provider reports at the end is whatever the stream carried. Stated so that the projection
    /// test above is not read as a promise the streaming path also makes.
    /// </summary>
    [Fact]
    public async Task A_streamed_call_still_projects_its_text()
    {
        string id = await _fixture.StartAsync(
            _client, "dsl-llm-full", new { orderId = "ORD-LFA", amount = 10m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);

        result!["text"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        result["model"]!.GetValue<string>().Should().Be("stub-model-x");
    }

    private async Task<string[]> EventTypesAsync(string instanceId)
    {
        JsonElement history = await _client.GetFromJsonAsync<JsonElement>(
            $"/instances/{instanceId}/events/history");

        return [.. history.GetProperty("items").EnumerateArray()
            .Select(e => e.TryGetProperty("eventType", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty)];
    }

    /// <summary>
    /// Pins a gap. A document declares <c>structuredOutput</c> as a JSON Schema and the reader keeps
    /// it, but <c>LlmOptions.StructuredOutput</c> is a CLR <see cref="Type"/> — there is nothing to
    /// map a schema onto, so the interpreter drops it and the node returns raw text. A document
    /// therefore cannot fail a run whose model ignored the shape it asked for.
    /// </summary>
    [Fact]
    public async Task A_declared_structured_output_schema_is_not_enforced()
    {
        _fixture.Chat.Reply = "not json at all, and nothing like the declared shape";

        string id = await _fixture.StartAsync(
            _client, "dsl-llm-full", new { orderId = "ORD-LF7", amount = 10m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonNode? result = await _fixture.ResultOfAsync(id);

        result!["value"]!.GetValue<string>().Should().Be("not json at all, and nothing like the declared shape",
            "when the schema starts being enforced this run should dead-stop instead");

        _fixture.Chat.Reply = "stub reply";
    }
}

/// <summary>
/// The option surfaces of the two nodes that shape the envelope rather than call out of the process,
/// plus an author's floor declared in a document rather than in C#.
/// </summary>
public class DslShapingNodeOptionTests : IClassFixture<DslAuthoringHostFixture>
{
    private readonly DslAuthoringHostFixture _fixture;
    private readonly HttpClient _client;

    public DslShapingNodeOptionTests(DslAuthoringHostFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    [Fact]
    public async Task A_replacing_transform_drops_what_it_does_not_carry_forward()
    {
        string id = await _fixture.StartAsync(
            _client, "dsl-transform-replace", new { orderId = "ORD-TR1", amount = 1m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonObject result = (await _fixture.ResultOfAsync(id))!.AsObject();

        result["keep"]!.GetValue<string>().Should().Be("kept");
        result.ContainsKey("drop").Should().BeFalse("replace:true means the prior data is gone, not merged");
    }

    [Fact]
    public async Task A_merging_transform_keeps_what_came_before_it()
    {
        string id = await _fixture.StartAsync(
            _client, "dsl-linear", new { orderId = "ORD-TR2", amount = 3m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonObject result = (await _fixture.ResultOfAsync(id))!.AsObject();

        result.ContainsKey("total").Should().BeTrue("the default is a merge, which is the contrast worth stating");
        result.ContainsKey("status").Should().BeTrue();
    }

    [Fact]
    public async Task A_barrier_that_names_no_path_aggregates_into_the_default_one()
    {
        string id = await _fixture.StartAsync(
            _client, "dsl-fan-default", new { orderId = "ORD-FI1", amount = 5m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonObject result = (await _fixture.ResultOfAsync(id))!.AsObject();

        result.ContainsKey("items").Should().BeTrue("no 'into' was declared, so the default path applies");
        result.ContainsKey("branches").Should().BeFalse();

        result["items"]!.AsArray().Should().HaveCount(2, "both branches reached the barrier");
    }

    [Fact]
    public async Task A_barrier_aggregates_into_the_path_the_document_names()
    {
        string id = await _fixture.StartAsync(
            _client, "dsl-fan", new { orderId = "ORD-FI2", amount = 5m });

        await _fixture.WaitForStatusAsync(id, InstanceStatus.Completed);

        JsonObject result = (await _fixture.ResultOfAsync(id))!.AsObject();

        result.ContainsKey("branches").Should().BeTrue("the document named the path");
        result.ContainsKey("items").Should().BeFalse();
    }

    [Fact]
    public async Task A_locked_gate_declared_in_a_document_is_advertised_as_locked()
    {
        JsonElement body = await _client.GetFromJsonAsync<JsonElement>(
            "/workflows/dsl-locked/versions/1.0.0/nodes");

        JsonElement settle = body.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("executorId").GetString() == "settle");

        settle.GetProperty("locked").GetBoolean().Should().BeTrue();
        settle.GetProperty("declared").GetProperty("reason").GetString().Should().Be("RegulatedSettlement");
    }

    [Fact]
    public async Task A_tenant_cannot_remove_a_gate_a_document_locked()
    {
        using HttpClient tenant = _fixture.CreateClient();
        tenant.DefaultRequestHeaders.Add("X-Tenant-Id", "dsl-unlocker");

        HttpResponseMessage put = await tenant.PutAsJsonAsync(
            "/workflows/dsl-locked/versions/1.0.0/nodes/settle", new { mode = "autonomous" });

        put.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a document's locked gate is the author's floor, whatever the tenant would prefer");
    }

    [Fact]
    public async Task A_tenant_may_still_tighten_a_gate_a_document_locked()
    {
        using HttpClient tenant = _fixture.CreateClient();
        tenant.DefaultRequestHeaders.Add("X-Tenant-Id", "dsl-tightener");

        HttpResponseMessage put = await tenant.PutAsJsonAsync(
            "/workflows/dsl-locked/versions/1.0.0/nodes/settle",
            new { mode = "requireApproval", reason = "always", assignees = new[] { "group:ops" } });

        put.IsSuccessStatusCode.Should().BeTrue("tightening is the direction a lock allows");
    }
}
