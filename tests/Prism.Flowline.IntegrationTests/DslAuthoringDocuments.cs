namespace Prism.Flowline.IntegrationTests;

/// <summary>
/// Documents covering the authoring surface the schema publishes but <see cref="DslDocuments"/> does
/// not reach: the full gate vocabulary, the failure dispositions other than dead-stop, correlated
/// triggers, edge metadata, the notification policy block, the closed expression vocabulary, the
/// per-kind option surfaces of the http, llm, transform and fan-in nodes, and the keys the reader
/// accepts but nothing acts on.
/// </summary>
internal static class DslAuthoringDocuments
{
    /// <summary>Every key the gate vocabulary allows, on one node.</summary>
    internal const string FullGate = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-gate-full",
      "version": "1.0.0",
      "start": "prepare",
      "output": ["settle"],
      "nodes": [
        { "id": "prepare", "kind": "transform", "set": { "amount": "$ctx.amount" } },
        { "id": "settle", "kind": "transform",
          "set": { "status": "'settled'" },
          "gate": {
            "mode": "requireApproval",
            "reason": "DualControl",
            "assignTo": ["user:alice", "group:finance"],
            "requireApprovers": 2,
            "expiresAfter": "PT8H",
            "onExpiry": { "action": "escalate", "assignTo": ["group:risk"] },
            "allowModification": true,
            "requireSegregationOfDuties": true
          } }
      ],
      "edges": [ { "from": "prepare", "to": "settle" } ]
    }
    """;

    /// <summary>
    /// A gate whose window is already closed by the time anything looks at it, so the expiry action
    /// a document declares can be observed rather than waited for.
    /// </summary>
    internal const string AutoApprovingGate = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-gate-auto",
      "version": "1.0.0",
      "start": "prepare",
      "output": ["settle"],
      "nodes": [
        { "id": "prepare", "kind": "transform", "set": { "amount": "$ctx.amount" } },
        { "id": "settle", "kind": "transform",
          "set": { "status": "'settled'" },
          "gate": {
            "mode": "requireApproval",
            "reason": "ShortWindow",
            "expiresAfter": "PT0.001S",
            "onExpiry": { "action": "autoApprove" }
          } }
      ],
      "edges": [ { "from": "prepare", "to": "settle" } ]
    }
    """;

    /// <summary>The same short window, ending the run rather than letting it through.</summary>
    internal const string DeadStoppingGate = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-gate-lapse",
      "version": "1.0.0",
      "start": "prepare",
      "output": ["settle"],
      "nodes": [
        { "id": "prepare", "kind": "transform", "set": { "amount": "$ctx.amount" } },
        { "id": "settle", "kind": "transform",
          "set": { "status": "'settled'" },
          "gate": {
            "mode": "requireApproval",
            "reason": "ShortWindow",
            "expiresAfter": "PT0.001S",
            "onExpiry": { "action": "deadStop" }
          } }
      ],
      "edges": [ { "from": "prepare", "to": "settle" } ]
    }
    """;

    /// <summary>A failure the document says to retry rather than stop on.</summary>
    internal const string Retrying = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-retrying",
      "version": "1.0.0",
      "start": "call",
      "output": ["call"],
      "nodes": [
        { "id": "call", "kind": "http",
          "url": "https://ledger.internal/v1/flaky",
          "allowedHosts": ["ledger.internal"] }
      ],
      "edges": [],
      "onFailure": [
        { "match": { "exception": "ApiCallFailureException", "status": "5xx" }, "disposition": "retry" }
      ]
    }
    """;

    /// <summary>A failure the document says to put in front of a human.</summary>
    internal const string Escalating = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-escalating",
      "version": "1.0.0",
      "start": "call",
      "output": ["call"],
      "nodes": [
        { "id": "call", "kind": "http",
          "url": "https://ledger.internal/v1/escalate",
          "allowedHosts": ["ledger.internal"] }
      ],
      "edges": [],
      "onFailure": [
        { "match": { "exception": "ApiCallFailureException", "status": "4xx" }, "disposition": "escalate" }
      ]
    }
    """;

    /// <summary>
    /// A trigger that both filters and reshapes. The correlation key is a literal the publisher has
    /// to match — not a projection, which is the mistake the semantic validator warns about — and
    /// the start context is taken from a branch of the message rather than the whole of it.
    /// </summary>
    internal const string CorrelatedTrigger = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-trigger-shaped",
      "version": "1.0.0",
      "start": "handle",
      "output": ["handle"],
      "nodes": [
        { "id": "handle", "kind": "transform",
          "set": { "sawOrder": "$ctx.orderId", "sawAmount": "$ctx.amount" } }
      ],
      "edges": [],
      "triggers": [
        { "topic": "dsl.shaped.placed",
          "correlationKey": "batch-7",
          "contextFrom": "$.order" }
      ]
    }
    """;

    /// <summary>Edges carrying the metadata a reader uses to render and reason about the graph.</summary>
    internal const string LabelledEdges = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-edge-meta",
      "version": "1.0.0",
      "start": "classify",
      "output": ["large", "small"],
      "nodes": [
        { "id": "classify", "kind": "transform", "set": { "amount": "$ctx.amount" } },
        { "id": "large", "kind": "transform", "set": { "band": "'large'" } },
        { "id": "small", "kind": "transform", "set": { "band": "'small'" } }
      ],
      "edges": [
        { "from": "classify", "to": "large", "when": "$.amount > 1000",
          "label": "above threshold", "idempotent": true },
        { "from": "classify", "to": "small", "when": "$.amount <= 1000",
          "label": "below threshold", "idempotent": false }
      ]
    }
    """;

    /// <summary>
    /// The whole notification block: a quieter default, one node quieter still, live streaming
    /// declined, and the workflow's own event names published.
    /// </summary>
    internal const string QuietPolicy = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-quiet",
      "version": "1.0.0",
      "start": "price",
      "output": ["finish"],
      "nodes": [
        { "id": "price", "kind": "transform",
          "set": { "total": "$ctx.amount * 2" },
          "notify": { "name": "priced", "payload": { "total": "$.total" } } },
        { "id": "finish", "kind": "transform", "set": { "status": "'done'" } }
      ],
      "edges": [ { "from": "price", "to": "finish" } ],
      "notifications": {
        "level": "lifecycle",
        "stream": false,
        "byNode": { "finish": "minimal" },
        "emits": ["priced"]
      }
    }
    """;

    /// <summary>
    /// One node exercising the closed function vocabulary end to end. The DSL suite parses and
    /// evaluates each of these in isolation; what is asserted here is that they survive the whole
    /// path — document, interpreter, envelope, result.
    /// </summary>
    internal const string Functions = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-functions",
      "version": "1.0.0",
      "start": "apply",
      "output": ["apply"],
      "nodes": [
        { "id": "apply", "kind": "transform",
          "set": {
            "upper": "upper($ctx.orderId)",
            "lower": "lower($ctx.orderId)",
            "length": "len($ctx.orderId)",
            "starts": "startsWith($ctx.orderId, 'ORD')",
            "ends": "endsWith($ctx.orderId, '-9')",
            "holds": "contains($ctx.orderId, 'RD')",
            "present": "has($ctx.orderId)",
            "missing": "has($ctx.nothing)",
            "fallback": "coalesce($ctx.nothing, 'default')",
            "asNumber": "number('42') + $ctx.amount",
            "asString": "string($ctx.amount)",
            "asBool": "bool($ctx.active)",
            "notBool": "bool($ctx.amount)",
            "pattern": "matches($ctx.orderId, '^ORD-[0-9]+$')"
          } }
      ],
      "edges": []
    }
    """;

    /// <summary>A wait that gives up rather than waiting forever, and says which way it gives up.</summary>
    internal const string WaitWithExpiry = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-wait-expiry",
      "version": "1.0.0",
      "start": "park",
      "output": ["done"],
      "nodes": [
        { "id": "park", "kind": "wait-event",
          "topic": "dsl.expiring.settled",
          "correlationKey": "$ctx.orderId",
          "timeout": "PT2H",
          "onExpiry": "resume" },
        { "id": "done", "kind": "transform", "set": { "status": "'resumed'" } }
      ],
      "edges": [ { "from": "park", "to": "done" } ]
    }
    """;

    /// <summary>
    /// Declares <c>strict</c>, a <c>result</c> schema and <c>limits</c> — the three keys the schema
    /// publishes and the reader populates, and which no part of the runtime consumes.
    /// </summary>
    internal const string DeclaredButInert = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-inert-keys",
      "version": "1.0.0",
      "strict": true,
      "context": {
        "type": "object",
        "required": ["orderId"],
        "properties": { "orderId": { "type": "string" }, "amount": { "type": "number" } }
      },
      "result": {
        "type": "object",
        "required": ["mustBeHere"],
        "properties": { "mustBeHere": { "type": "string" } }
      },
      "limits": { "maxAttempts": 2, "maxLifetimeHours": 1 },
      "start": "only",
      "output": ["only"],
      "nodes": [
        { "id": "only", "kind": "transform",
          "output": { "type": "object", "required": ["neverChecked"] },
          "set": { "status": "'done'" } }
      ],
      "edges": []
    }
    """;

    /// <summary>
    /// Every key an <c>http</c> node accepts: a verb other than the default, templated headers and
    /// body, a narrowed timeout, a success set that admits a code no one would call successful, and
    /// the idempotency header switched off.
    /// </summary>
    internal const string FullHttp = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-http-full",
      "version": "1.0.0",
      "start": "call",
      "output": ["call"],
      "nodes": [
        { "id": "call", "kind": "http",
          "method": "POST",
          "url": "https://ledger.internal/v1/orders/{{ $ctx.orderId }}",
          "headers": {
            "X-Order": "{{ $ctx.orderId }}",
            "X-Static": "fixed"
          },
          "body": "{ \"amount\": {{ $ctx.amount }} }",
          "timeoutSeconds": 5,
          "successCodes": [418],
          "sendIdempotencyKey": false,
          "allowedHosts": ["ledger.internal"] }
      ],
      "edges": []
    }
    """;

    /// <summary>
    /// Every key an <c>llm</c> node accepts. <c>emitCompletion</c> is off and <c>streamDeltas</c> on,
    /// which is the combination that separates the durable completion event from the transient
    /// token stream.
    /// </summary>
    internal const string FullLlm = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-llm-full",
      "version": "1.0.0",
      "start": "classify",
      "output": ["classify"],
      "nodes": [
        { "id": "classify", "kind": "llm",
          "model": "stub-model-x",
          "system": "You classify orders for {{ $ctx.orderId }}.",
          "prompt": "Classify order {{ $ctx.orderId }} worth {{ $ctx.amount }}.",
          "promptVersion": "v7",
          "temperature": 0.25,
          "maxTokens": 321,
          "streamDeltas": true,
          "emitCompletion": false,
          "structuredOutput": {
            "type": "object",
            "required": ["band"],
            "properties": { "band": { "type": "string" } }
          } }
      ],
      "edges": []
    }
    """;

    /// <summary>The quiet counterpart: completion emitted, nothing streamed.</summary>
    internal const string ReportingLlm = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-llm-reporting",
      "version": "1.0.0",
      "start": "classify",
      "output": ["classify"],
      "nodes": [
        { "id": "classify", "kind": "llm",
          "model": "stub-model-y",
          "prompt": "Classify {{ $ctx.orderId }}.",
          "emitCompletion": true,
          "streamDeltas": false }
      ],
      "edges": []
    }
    """;

    /// <summary>
    /// A transform that replaces the envelope's data rather than merging into it, which is the one
    /// way a document can drop a field it no longer wants carried.
    /// </summary>
    internal const string ReplacingTransform = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-transform-replace",
      "version": "1.0.0",
      "start": "seed",
      "output": ["narrow"],
      "nodes": [
        { "id": "seed", "kind": "transform",
          "set": { "keep": "'kept'", "drop": "'dropped'" } },
        { "id": "narrow", "kind": "transform",
          "replace": true,
          "set": { "keep": "$.keep" } }
      ],
      "edges": [ { "from": "seed", "to": "narrow" } ]
    }
    """;

    /// <summary>
    /// A barrier with no <c>into</c>, which is the default path <c>dsl-fan</c> never exercises
    /// because it names one. Fan-out uses the array edge form: a node listing several targets in one
    /// edge is what branches, and several single edges out of one node is not the same thing.
    /// </summary>
    internal const string DefaultFanIn = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-fan-default",
      "version": "1.0.0",
      "start": "split",
      "output": ["join"],
      "nodes": [
        { "id": "split", "kind": "transform", "set": { "amount": "$ctx.amount" } },
        { "id": "left", "kind": "transform", "set": { "side": "'left'" } },
        { "id": "right", "kind": "transform", "set": { "side": "'right'" } },
        { "id": "join", "kind": "fan-in" }
      ],
      "edges": [
        { "from": "split", "to": ["left", "right"] },
        { "from": ["left", "right"], "to": "join" }
      ]
    }
    """;

    /// <summary>
    /// An author's floor expressed in a document: a tenant may tighten this gate but never remove
    /// it. The compiled equivalent is covered by the tenant suite; this is the same rule declared in
    /// JSON.
    /// </summary>
    internal const string LockedGate = """
    {
      "dsl": "prism.workflow/1.0",
      "name": "dsl-locked",
      "version": "1.0.0",
      "start": "prepare",
      "output": ["settle"],
      "nodes": [
        { "id": "prepare", "kind": "transform", "set": { "amount": "$ctx.amount" } },
        { "id": "settle", "kind": "transform",
          "set": { "status": "'settled'" },
          "gate": {
            "mode": "conditional",
            "when": "$.amount > 25000",
            "reason": "RegulatedSettlement",
            "locked": true
          } }
      ],
      "edges": [ { "from": "prepare", "to": "settle" } ]
    }
    """;

    internal static IReadOnlyList<(string Name, string Text)> All =>
    [
        ("gate-full", FullGate),
        ("gate-auto", AutoApprovingGate),
        ("gate-lapse", DeadStoppingGate),
        ("retrying", Retrying),
        ("escalating", Escalating),
        ("trigger-shaped", CorrelatedTrigger),
        ("edge-meta", LabelledEdges),
        ("quiet", QuietPolicy),
        ("functions", Functions),
        ("wait-expiry", WaitWithExpiry),
        ("inert-keys", DeclaredButInert),
        ("http-full", FullHttp),
        ("llm-full", FullLlm),
        ("llm-reporting", ReportingLlm),
        ("transform-replace", ReplacingTransform),
        ("fan-default", DefaultFanIn),
        ("locked", LockedGate)
    ];
}
