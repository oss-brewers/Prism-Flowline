using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Core;
using FluentAssertions;
using Xunit;

namespace Prism.Flowline.IntegrationTests;

/// <summary>
/// Every approval-gate variation the builder exposes, exercised against the real host: quorum,
/// assignees, segregation of duties, modification, and each expiry action. The gate builder is the
/// widest authoring surface a compiled workflow has, and until now the suite covered only the
/// conditional-with-threshold shape.
/// </summary>
public class CodeGateVariationTests : IClassFixture<CodeAuthoringHostFixture>
{
    private readonly CodeAuthoringHostFixture _host;
    private readonly HttpClient _client;

    public CodeGateVariationTests(CodeAuthoringHostFixture host)
    {
        _host = host;
        _client = host.CreateClient();
    }

    // ---- quorum ---------------------------------------------------------------------------

    [Fact]
    public async Task One_vote_does_not_satisfy_a_two_approver_gate()
    {
        (string instanceId, string approvalId) =
            await _host.StartAndParkAsync(_client, "quorum-order", new OrderContext("ORD-Q1"));

        DecisionResult first = await _host.DecideAsync(approvalId, "alice", ApprovalOutcomeKind.Approve);

        first.Kind.Should().Be(DecisionResultKind.QuorumPending);
        first.Detail.Should().Contain("1 of 2");

        ApprovalRequest? approval = await _host.ApprovalAsync(approvalId);
        approval!.State.Should().Be(ApprovalState.Pending, "quorum is not met, so the request stays open");

        _host.Inputs.For("settle", instanceId).Should().BeNull("the gated node must not run on one vote");
    }

    [Fact]
    public async Task The_second_approver_completes_the_quorum_and_the_node_runs_once()
    {
        (string instanceId, string approvalId) =
            await _host.StartAndParkAsync(_client, "quorum-order", new OrderContext("ORD-Q2"));

        await _host.DecideAsync(approvalId, "alice", ApprovalOutcomeKind.Approve);
        DecisionResult second = await _host.DecideAsync(approvalId, "bob", ApprovalOutcomeKind.Approve);

        second.Kind.Should().Be(DecisionResultKind.Accepted);

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        _host.Inputs.Seen.Count(s => s.ExecutorId == "settle" && s.InstanceId == instanceId)
            .Should().Be(1, "an approved node runs exactly once however many votes it took");
    }

    [Fact]
    public async Task The_same_principal_cannot_vote_twice_towards_a_quorum()
    {
        (_, string approvalId) =
            await _host.StartAndParkAsync(_client, "quorum-order", new OrderContext("ORD-Q3"));

        await _host.DecideAsync(approvalId, "alice", ApprovalOutcomeKind.Approve);
        DecisionResult again = await _host.DecideAsync(approvalId, "alice", ApprovalOutcomeKind.Approve);

        again.Kind.Should().Be(DecisionResultKind.AlreadyDecided);
        again.Detail.Should().Contain("already voted");
    }

    [Fact]
    public async Task A_rejection_ends_a_quorum_gate_without_waiting_for_the_rest()
    {
        (string instanceId, string approvalId) =
            await _host.StartAndParkAsync(_client, "quorum-order", new OrderContext("ORD-Q4"));

        await _host.DecideAsync(approvalId, "alice", ApprovalOutcomeKind.Approve);
        DecisionResult rejection = await _host.DecideAsync(approvalId, "bob", ApprovalOutcomeKind.Reject);

        rejection.Kind.Should().Be(DecisionResultKind.Accepted);

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.DeadStopped);
        _host.Inputs.For("settle", instanceId).Should().BeNull();
    }

    [Fact]
    public async Task The_quorum_a_gate_requires_is_published_on_the_approval()
    {
        (string instanceId, _) =
            await _host.StartAndParkAsync(_client, "quorum-order", new OrderContext("ORD-Q5"));

        JsonElement body = await _client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/approvals");
        JsonElement approval = body.GetProperty("items").EnumerateArray().First();

        approval.GetProperty("requiredApprovers").GetInt32().Should().Be(2);
        approval.GetProperty("reason").GetString().Should().Be("DualControl");
    }

    // ---- assignees ------------------------------------------------------------------------

    [Fact]
    public async Task A_principal_outside_the_assignee_list_is_refused()
    {
        (_, string approvalId) =
            await _host.StartAndParkAsync(_client, "assigned-order", new OrderContext("ORD-A1"));

        DecisionResult result = await _host.DecideAsync(approvalId, "mallory", ApprovalOutcomeKind.Approve);

        result.Kind.Should().Be(DecisionResultKind.Forbidden);
        result.Detail.Should().Contain("not an assignee");
    }

    [Fact]
    public async Task A_named_assignee_may_decide_and_the_run_resumes()
    {
        (string instanceId, string approvalId) =
            await _host.StartAndParkAsync(_client, "assigned-order", new OrderContext("ORD-A2"));

        DecisionResult result = await _host.DecideAsync(approvalId, "alice", ApprovalOutcomeKind.Approve);

        result.Kind.Should().Be(DecisionResultKind.Accepted);
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);
    }

    [Fact]
    public async Task An_anonymous_request_cannot_decide_an_assigned_gate()
    {
        (_, string approvalId) =
            await _host.StartAndParkAsync(_client, "assigned-order", new OrderContext("ORD-A3"));

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/approvals/{approvalId}/decision", new { decision = "approve" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an unauthenticated caller matches no assignee, and the API must say so rather than accept");
    }

    [Fact]
    public async Task The_assignees_a_gate_declares_are_published_on_the_approval()
    {
        (string instanceId, _) =
            await _host.StartAndParkAsync(_client, "assigned-order", new OrderContext("ORD-A4"));

        JsonElement body = await _client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}/approvals");
        JsonElement approval = body.GetProperty("items").EnumerateArray().First();

        approval.GetProperty("assignees").EnumerateArray().Select(a => a.GetString())
            .Should().BeEquivalentTo(["alice", "group:finance"]);
    }

    // ---- modification ---------------------------------------------------------------------

    [Fact]
    public async Task A_gate_that_forbids_modification_refuses_a_modified_decision()
    {
        (_, string approvalId) =
            await _host.StartAndParkAsync(_client, "fixed-input-order", new OrderContext("ORD-M1"));

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/approvals/{approvalId}/decision",
            new { decision = "approve_with_modification", modifiedInput = new { orderId = "ORD-M1", amount = 5m } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("does not permit input modification");
    }

    [Fact]
    public async Task A_gate_that_permits_modification_accepts_the_amended_decision()
    {
        (string instanceId, string approvalId) =
            await _host.StartAndParkAsync(_client, "modifiable-order", new OrderContext("ORD-M2", Amount: 900m));

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/approvals/{approvalId}/decision",
            new
            {
                decision = "approve_with_modification",
                comment = "capped",
                modifiedInput = new { orderId = "ORD-M2", amount = 100m }
            });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        ApprovalRequest? approval = await _host.ApprovalAsync(approvalId);
        approval!.State.Should().Be(ApprovalState.Approved);
        approval.AllowModification.Should().BeTrue();

        // Kept separate from what the resumed node does with that amendment, which is asserted — and
        // currently found wanting — by the defect pin below.
        _ = instanceId;
    }

    /// <summary>
    /// Was a defect pin: <c>GateEvaluator</c> hands the resumed executor the amendment as the JSON the
    /// approver submitted, and <c>HostExecutor</c> cast it straight to its declared input type, so the
    /// node failed the cast and the instance dead-stopped under an approval that read Approved.
    /// The amendment is now bound to the input type first.
    /// </summary>
    [Fact]
    public async Task An_amended_decision_runs_the_resumed_node_on_the_amended_input()
    {
        (string instanceId, string approvalId) =
            await _host.StartAndParkAsync(_client, "modifiable-order", new OrderContext("ORD-M3", Amount: 900m));

        await _client.PostAsJsonAsync(
            $"/approvals/{approvalId}/decision",
            new
            {
                decision = "approve_with_modification",
                modifiedInput = new { orderId = "ORD-M3", amount = 100m }
            });

        WorkflowInstance instance = await _host.WaitForStatusAsync(
            instanceId, InstanceStatus.DeadStopped, InstanceStatus.Completed, InstanceStatus.Failed);

        instance.Status.Should().Be(InstanceStatus.Completed, instance.TerminalReason);
        _host.Inputs.For("settle", instanceId).Should().Be(("ORD-M3", 100m), "the node ran on the amendment");
    }

    // ---- a gate in front of a node that cannot carry one -----------------------------------

    [Fact]
    public async Task A_gate_in_front_of_a_raw_executor_parks_before_it_runs()
    {
        (string instanceId, string approvalId) =
            await _host.StartAndParkAsync(_client, "gate-before-raw", new OrderContext("ORD-G1", Amount: 700m));

        _host.Inputs.For("prepare", instanceId).Should().NotBeNull();
        _host.Inputs.For("raw-settle", instanceId).Should().BeNull("nothing behind the gate may run before the decision");

        ApprovalRequest? approval = await _host.ApprovalAsync(approvalId);
        approval!.ExecutorId.Should().Be("approve");
        approval.Reason.Should().Be("BeforeRawSettlement");
    }

    [Fact]
    public async Task A_gate_in_front_of_a_raw_executor_passes_the_message_through_unchanged()
    {
        (string instanceId, string approvalId) =
            await _host.StartAndParkAsync(_client, "gate-before-raw", new OrderContext("ORD-G2", Amount: 700m));

        (await _client.PostAsJsonAsync($"/approvals/{approvalId}/decision", new { decision = "approve" }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);
        _host.Inputs.For("raw-settle", instanceId).Should().Be(("ORD-G2", 700m));
    }

    [Fact]
    public async Task A_gate_in_front_of_a_raw_executor_lets_an_approver_modify_the_message()
    {
        (string instanceId, string approvalId) =
            await _host.StartAndParkAsync(_client, "gate-before-raw", new OrderContext("ORD-G3", Amount: 700m));

        (await _client.PostAsJsonAsync($"/approvals/{approvalId}/decision", new
        {
            decision = "approve_with_modification",
            modifiedInput = new { orderId = "ORD-G3", amount = 70m }
        })).StatusCode.Should().Be(HttpStatusCode.Accepted);

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);
        _host.Inputs.For("raw-settle", instanceId).Should().Be(("ORD-G3", 70m),
            "the raw node behind the gate receives the amended message");
    }

    // ---- segregation of duties --------------------------------------------------------------

    [Fact]
    public async Task A_gate_can_declare_segregation_of_duties()
    {
        (_, string approvalId) =
            await _host.StartAndParkAsync(_client, "segregated-order", new OrderContext("ORD-S1"));

        ApprovalRequest? approval = await _host.ApprovalAsync(approvalId);

        approval!.RequireSegregationOfDuties.Should().BeTrue(
            "the flag the author declared must survive onto the raised request");
    }

    /// <summary>
    /// Pins a defect. <c>IsAuthorized</c> only refuses a decider when the request carries an
    /// <c>InitiatorId</c>, and nothing in the framework ever sets one — so the rule the author
    /// declared has no effect, and the same principal who started the run may approve it.
    /// </summary>
    [Fact]
    public async Task Segregation_of_duties_currently_refuses_nobody_because_no_initiator_is_recorded()
    {
        (string instanceId, string approvalId) =
            await _host.StartAndParkAsync(_client, "segregated-order", new OrderContext("ORD-S2"));

        ApprovalRequest? raised = await _host.ApprovalAsync(approvalId);
        raised!.InitiatorId.Should().BeNull("nothing populates the initiator, which is the defect");

        DecisionResult result = await _host.DecideAsync(approvalId, "anonymous", ApprovalOutcomeKind.Approve);

        result.Kind.Should().Be(DecisionResultKind.Accepted,
            "when an initiator starts being recorded, this becomes Forbidden and the test should say so");

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);
    }

    // ---- expiry ---------------------------------------------------------------------------

    [Fact]
    public async Task An_expired_gate_that_dead_stops_ends_the_run_without_the_node()
    {
        (string instanceId, _) =
            await _host.StartAndParkAsync(_client, "expire-dead-stop", new OrderContext("ORD-E1"));

        (await _host.SweepAsync()).Should().BeGreaterThan(0, "the window has already closed");

        WorkflowInstance instance = await _host.WaitForStatusAsync(instanceId, InstanceStatus.DeadStopped);

        instance.TerminalReason.Should().Be("ApprovalExpired");
        _host.Inputs.For("settle", instanceId).Should().BeNull();
    }

    [Fact]
    public async Task An_expired_gate_that_rejects_ends_the_run_without_the_node()
    {
        (string instanceId, string approvalId) =
            await _host.StartAndParkAsync(_client, "expire-reject", new OrderContext("ORD-E2"));

        await _host.SweepAsync();

        (await _host.ApprovalAsync(approvalId))!.State.Should().Be(ApprovalState.Rejected);

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.DeadStopped);

        _host.Inputs.For("settle", instanceId).Should().BeNull(
            "a rejection on expiry must not let the side effect through");
    }

    [Fact]
    public async Task An_expired_gate_that_auto_approves_lets_the_run_finish()
    {
        (string instanceId, string approvalId) =
            await _host.StartAndParkAsync(_client, "expire-auto-approve", new OrderContext("ORD-E3"));

        await _host.SweepAsync();

        (await _host.ApprovalAsync(approvalId))!.State.Should().Be(ApprovalState.Approved);

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        _host.Inputs.For("settle", instanceId).Should().NotBeNull(
            "auto-approval on expiry is the one action that lets the gated node run");
    }

    /// <summary>
    /// Escalation is a two-stage rule: the first expiry puts the request back in front of someone
    /// rather than ending the run, and only the second closes it. Driven by explicit sweeps, since
    /// which stage an assertion lands on is otherwise a matter of the sweeper's timing.
    /// </summary>
    [Fact]
    public async Task An_escalating_gate_survives_its_first_expiry()
    {
        (string instanceId, string approvalId) =
            await _host.StartAndParkAsync(_client, "expire-escalate", new OrderContext("ORD-E4"));

        await _host.SweepAsync();

        ApprovalRequest? approval = await _host.ApprovalAsync(approvalId);

        approval!.State.Should().Be(ApprovalState.Pending, "escalation re-opens rather than closes");
        approval.EscalatedOnce.Should().BeTrue("the second expiry must be able to tell it has been here before");

        WorkflowInstance? instance = await _host.Resolve<IInstanceStore>().GetAsync(instanceId, default);
        instance!.Status.Should().Be(InstanceStatus.AwaitingApproval, "the run is still waiting on a human");
    }

    [Fact]
    public async Task A_second_expiry_after_an_escalation_ends_the_run()
    {
        (string instanceId, string approvalId) =
            await _host.StartAndParkAsync(_client, "expire-escalate", new OrderContext("ORD-E5"));

        await _host.SweepAsync();   // escalates
        await _host.SweepAsync();   // falls through

        (await _host.ApprovalAsync(approvalId))!.State.Should().Be(ApprovalState.Expired);

        WorkflowInstance instance = await _host.WaitForStatusAsync(instanceId, InstanceStatus.DeadStopped);
        instance.TerminalReason.Should().Be("ApprovalExpired");
    }

    /// <summary>
    /// The escalation branch re-opens the request but never moves <c>ExpiresAt</c>, so the reprieve
    /// it grants is zero-length: the very next sweep finds the request due again. Pinned because an
    /// author reading <c>OnExpiry(ExpiryAction.Escalate)</c> would expect a fresh window.
    /// </summary>
    [Fact]
    public async Task An_escalation_does_not_grant_a_fresh_window()
    {
        (_, string approvalId) =
            await _host.StartAndParkAsync(_client, "expire-escalate", new OrderContext("ORD-E6"));

        DateTimeOffset before = (await _host.ApprovalAsync(approvalId))!.ExpiresAt;

        await _host.SweepAsync();

        ApprovalRequest? after = await _host.ApprovalAsync(approvalId);

        after!.ExpiresAt.Should().Be(before,
            "when escalation starts extending the window, this assertion should become BeAfter(before)");
    }
}

/// <summary>
/// Authoring seams other than gates: the raw-node escape hatch, a definition classifying its own
/// failures, a definition validating its own start context, and the middleware pipelines.
/// </summary>
public class CodeAuthoringSeamTests : IClassFixture<CodeAuthoringHostFixture>
{
    private readonly CodeAuthoringHostFixture _host;
    private readonly HttpClient _client;

    public CodeAuthoringSeamTests(CodeAuthoringHostFixture host)
    {
        _host = host;
        _client = host.CreateClient();
    }

    // ---- raw nodes ------------------------------------------------------------------------

    [Fact]
    public async Task A_raw_node_runs_in_the_graph_like_any_other()
    {
        string instanceId = await _host.StartAsync(_client, "raw-order", new OrderContext("ORD-R1"));

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        _host.Inputs.For("raw-settle", instanceId).Should().NotBeNull();
    }

    [Fact]
    public async Task A_raw_node_is_advertised_as_not_configurable()
    {
        JsonElement body = await _client.GetFromJsonAsync<JsonElement>("/workflows/raw-order/versions/1.0.0/nodes");
        JsonElement[] nodes = [.. body.GetProperty("nodes").EnumerateArray()];

        JsonElement raw = nodes.Single(n => n.GetProperty("executorId").GetString() == "raw-settle");
        raw.GetProperty("configurable").GetBoolean().Should().BeFalse(
            "a raw binding runs outside the executor pipeline, so it can carry no gate");

        JsonElement attached = nodes.Single(n => n.GetProperty("executorId").GetString() == "prepare");
        attached.GetProperty("configurable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Gating_a_raw_node_is_refused_at_build_time()
    {
        var context = WorkflowBuildContext.ForInspection("probe", "1.0.0");
        var executor = new RecordingStep("probe-node", new InputLedger(), "probe");

        Action gating = () => context.RawNode((Microsoft.Agents.AI.Workflows.Executor)executor, gate => gate.Reason("nope"));

        gating.Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot be approval-gated*context.Gate<TMessage>*in front of it*");
    }

    // ---- failure classification -------------------------------------------------------------

    [Fact]
    public async Task A_definition_that_classifies_a_failure_as_retryable_is_retried_and_completes()
    {
        string instanceId = await _host.StartAsync(_client, "retrying-order", new OrderContext("ORD-C1"));

        WorkflowInstance instance = await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        instance.AttemptCount.Should().BeGreaterThan(0, "the run only succeeded on a later attempt");
        _host.Attempts.CountFor(instanceId).Should().BeGreaterThan(1,
            "the node was entered more than once, which is what a retry means");
    }

    [Fact]
    public async Task A_retried_run_reports_its_attempts_on_the_instance()
    {
        string instanceId = await _host.StartAsync(_client, "retrying-order", new OrderContext("ORD-C2"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        JsonElement instance = await _client.GetFromJsonAsync<JsonElement>($"/instances/{instanceId}");
        instance.GetProperty("attemptCount").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_definition_that_escalates_a_failure_parks_for_input_rather_than_stopping()
    {
        string instanceId = await _host.StartAsync(_client, "escalating-order", new OrderContext("ORD-C3"));

        WorkflowInstance instance = await _host.WaitForStatusAsync(instanceId, InstanceStatus.AwaitingInput);

        instance.TerminalReason.Should().Contain("Needs a human");
    }

    // ---- context validation -----------------------------------------------------------------

    [Fact]
    public async Task A_definition_can_reject_its_own_start_context()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/workflows/validating-order/instances",
            new { context = new { orderId = "ORD-V1", amount = 0m } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Amount must be greater than zero");
    }

    [Fact]
    public async Task A_context_the_definition_accepts_starts_a_run()
    {
        string instanceId = await _host.StartAsync(
            _client, "validating-order", new OrderContext("ORD-V2", Amount: 10m));

        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);
    }

    [Fact]
    public async Task A_rejected_context_creates_no_instance()
    {
        JsonElement before = await _client.GetFromJsonAsync<JsonElement>("/instances?workflow=validating-order&limit=100");
        int countBefore = before.GetProperty("items").GetArrayLength();

        await _client.PostAsJsonAsync(
            "/workflows/validating-order/instances",
            new { context = new { orderId = "ORD-V3", amount = -5m } });

        JsonElement after = await _client.GetFromJsonAsync<JsonElement>("/instances?workflow=validating-order&limit=100");
        after.GetProperty("items").GetArrayLength().Should().Be(countBefore);
    }

    // ---- middleware -------------------------------------------------------------------------

    [Fact]
    public async Task Workflow_middleware_wraps_the_whole_run()
    {
        string instanceId = await _host.StartAsync(_client, "raw-order", new OrderContext("ORD-W1"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        (await _host.Trace.WaitForAsync("workflow:before(raw-order)")).Should().BeTrue();
        (await _host.Trace.WaitForAsync("workflow:after(raw-order)")).Should().BeTrue(
            "the outermost middleware must see the run end, not only start");
    }

    [Fact]
    public async Task Executor_middleware_wraps_every_attached_node_but_not_a_raw_one()
    {
        string instanceId = await _host.StartAsync(_client, "raw-order", new OrderContext("ORD-W2"));
        await _host.WaitForStatusAsync(instanceId, InstanceStatus.Completed);

        (await _host.Trace.WaitForAsync("executor:before:prepare(raw-order)")).Should().BeTrue();
        (await _host.Trace.WaitForAsync("executor:after:prepare(raw-order)")).Should().BeTrue();

        IReadOnlyList<string> trace = _host.Trace.Entries;

        trace.Should().NotContain(e => e.Contains("raw-settle", StringComparison.Ordinal),
            "a raw binding is outside the executor pipeline by definition");
    }
}
