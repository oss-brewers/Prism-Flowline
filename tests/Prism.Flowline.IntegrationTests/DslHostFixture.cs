using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Api;
using Prism.Flowline.Core;
using Prism.Flowline.Dsl.Hosting;
using Prism.Flowline.Dsl.Interpretation;
using Prism.Flowline.Executors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Prism.Flowline.IntegrationTests;

/// <summary>What a node actually put on the wire, captured before the request is disposed.</summary>
public sealed record SentRequest(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string Body)
{
    public bool HasHeader(string name) => Headers.ContainsKey(name);

    public string? Header(string name) => Headers.TryGetValue(name, out string? v) ? v : null;
}

/// <summary>Canned HTTP responses, so an <c>http</c> node can be exercised without a network.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    /// <summary>
    /// Snapshots of what was sent. A retrying instance from an earlier test keeps issuing requests,
    /// and an <see cref="HttpRequestMessage"/> disposes its content once the call returns — so a
    /// test that wants to read a header or a body has to take a copy here and select by URL rather
    /// than assume it owns the queue.
    /// </summary>
    public ConcurrentQueue<SentRequest> Sent { get; } = new();

    public Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> Respond { get; set; } =
        _ => (HttpStatusCode.OK, """{ "ok": true }""");

    /// <summary>The one request whose URL contains <paramref name="marker"/>.</summary>
    public SentRequest SentFor(string marker)
        => Sent.Single(r => r.Url.Contains(marker, StringComparison.Ordinal));

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, IEnumerable<string> values) in request.Headers)
        {
            headers[name] = string.Join(",", values);
        }

        string sentBody = request.Content is { } content
            ? content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()
            : string.Empty;

        if (request.Content is { } withHeaders)
        {
            foreach ((string name, IEnumerable<string> values) in withHeaders.Headers)
            {
                headers[name] = string.Join(",", values);
            }
        }

        Sent.Enqueue(new SentRequest(
            request.Method.Method, request.RequestUri?.AbsoluteUri ?? string.Empty, headers, sentBody));

        (HttpStatusCode status, string body) = Respond(request);

        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>A chat client that answers with whatever the test told it to.</summary>
public sealed class StubChatClient : IChatClient
{
    public string Reply { get; set; } = "stub reply";

    public List<string> Prompts { get; } = [];

    /// <summary>
    /// The options of the most recent call. A document declares temperature, token ceiling and model
    /// on the node; the only place to see whether they survived the interpreter is what the client
    /// was actually asked for.
    /// </summary>
    public ChatOptions? LastOptions { get; private set; }

    /// <summary>Every role/text pair of the last call, so a system prompt is distinguishable.</summary>
    public List<(string Role, string Text)> LastMessages { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatMessage[] sent = [.. messages];

        LastOptions = options;
        LastMessages.Clear();
        LastMessages.AddRange(sent.Select(m => (m.Role.Value, m.Text ?? string.Empty)));

        Prompts.AddRange(sent.Select(m => m.Text ?? string.Empty));

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply))
        {
            ModelId = options?.ModelId ?? "stub",
            Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7 }
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>In-memory timers, since the host ships no <see cref="ITimerService"/> of its own.</summary>
public sealed class InMemoryTimerService : ITimerService
{
    private readonly ConcurrentBag<(string InstanceId, string ExecutorId, DateTimeOffset WakeAt)> _timers = [];

    public IReadOnlyCollection<(string InstanceId, string ExecutorId, DateTimeOffset WakeAt)> Scheduled => _timers;

    public ValueTask ScheduleAsync(
        string instanceId, string executorId, DateTimeOffset wakeAt, CancellationToken cancellationToken)
    {
        _timers.Add((instanceId, executorId, wakeAt));
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<(string InstanceId, string ExecutorId)>> ClaimDueAsync(
        DateTimeOffset now, int max, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<(string, string)>>(
            [.. _timers.Where(t => t.WakeAt <= now).Take(max).Select(t => (t.InstanceId, t.ExecutorId))]);
}

/// <summary>
/// A custom node, registered by name — the seam that keeps the DSL from having a ceiling. It doubles
/// whatever number the document points it at, which is dull on purpose: the point under test is the
/// registration and parameter contract, not the arithmetic.
/// </summary>
public sealed class DoublerNodeFactory : IDslNodeFactory
{
    public string Name => "doubler";

    public JsonNode? ParameterSchema => JsonNode.Parse("""
    {
      "type": "object",
      "required": ["field"],
      "properties": {
        "field": { "type": "string" },
        "times": { "type": "number" }
      }
    }
    """);

    public IHostExecutor Create(DslNodeContext context)
    {
        string field = context.Parameters["field"]!.GetValue<string>();
        decimal times = context.Parameters["times"]?.GetValue<decimal>() ?? 2m;

        return new Doubler(context.Node.Id, field, times);
    }

    private sealed class Doubler(string id, string field, decimal times)
        : HostExecutor<DslMessage, DslMessage>(id)
    {
        protected override ValueTask<DslMessage> ExecuteCoreAsync(
            DslMessage input, IWorkflowContext context, CancellationToken cancellationToken)
        {
            var data = input.Data as JsonObject ?? [];
            var updated = (JsonObject)data.DeepClone();

            decimal current = updated[field]?.GetValue<decimal>() ?? 0m;
            updated[field] = current * times;

            return ValueTask.FromResult(input.WithData(updated));
        }
    }
}

/// <summary>A custom node that returns the wrong executor shape, to prove the check bites.</summary>
public sealed class WrongShapeNodeFactory : IDslNodeFactory
{
    public string Name => "wrong-shape";

    public IHostExecutor Create(DslNodeContext context)
        => new Wrong(context.Node.Id);

    private sealed class Wrong(string id) : HostExecutor<string, string>(id)
    {
        protected override ValueTask<string> ExecuteCoreAsync(
            string input, IWorkflowContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(input);
    }
}

/// <summary>
/// A host with only DSL workflows registered, so a failure is unambiguously about the DSL rather
/// than about a compiled definition sitting beside it.
/// </summary>
public class DslHostFixture : WebApplicationFactory<Program>
{
    public StubHttpHandler Http { get; } = new();
    public StubChatClient Chat { get; } = new();
    public InMemoryTimerService Timers { get; } = new();

    private readonly string _auditDatabasePath =
        Path.Combine(Path.GetTempPath(), $"prism-dsl-audit-{Guid.NewGuid():N}.db");

    /// <summary>
    /// The redaction policy this host runs under, or null for the framework default. Overridden by
    /// the fixture that exists to prove a DSL message is redacted like any other.
    /// </summary>
    protected virtual IRedactionPolicy? Redaction => null;

    /// <summary>
    /// How often the host sweeps expired approvals. A fixture whose documents declare a window
    /// measured in milliseconds pushes this out and drives the sweep itself, so which side of a
    /// two-stage expiry rule an assertion lands on is not left to timing.
    /// </summary>
    protected virtual string ApprovalSweepIntervalSeconds => "1";

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ITimerService>(Timers);
            services.AddSingleton<IChatClient>(Chat);

            if (Redaction is { } redaction)
            {
                // Registered last so it wins over the framework's TryAdd default, whichever order
                // the host builder happens to run its callbacks in.
                services.AddSingleton(redaction);
            }

            // The DSL's http node resolves this named client, so a stub handler here reaches every
            // http node without any of them knowing they are under test.
            services.AddHttpClient(ApiCallOptions.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Http);

            var host = new WorkflowHostBuilder(services);

            host.AddDslNode(new DoublerNodeFactory())
                .AddDslNode(new WrongShapeNodeFactory())
                .ConfigureDsl(registry => registry.EnforceEgress = false);

            foreach ((string name, string text) in DslDocuments.All.Concat(DslAuthoringDocuments.All))
            {
                host.AddDslWorkflowText(text, name);
            }
        });

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("WorkflowHost:Approvals:SweepIntervalSeconds", ApprovalSweepIntervalSeconds);
        builder.UseSetting("Prism:AuditRecords:ConnectionString", $"Data Source={_auditDatabasePath}");
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient<Prism.Flowline.Service.ControlPlane.Services.WorkflowApiClient>(client =>
            {
                client.BaseAddress = new Uri("http://localhost");
            }).ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
        });
    }

    public T Resolve<T>() where T : notnull => Services.GetRequiredService<T>();

    public async Task<WorkflowInstance> WaitForStatusAsync(string instanceId, params InstanceStatus[] expected)
    {
        var store = Resolve<IInstanceStore>();
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            WorkflowInstance? instance = await store.GetAsync(instanceId, default);
            if (instance is not null && expected.Contains(instance.Status))
            {
                return instance;
            }

            await Task.Delay(25);
        }

        WorkflowInstance? last = await store.GetAsync(instanceId, default);

        // The status alone says a run stalled but never why, and the reason is almost always in the
        // terminal reason or the instance log. Reading them here turns "expected Completed" into a
        // message that names the actual fault.
        IReadOnlyList<InstanceLogEntry> logs = await Resolve<ILogStore>()
            .QueryAsync(instanceId, null, null, 50, default);

        string detail = logs.Count == 0
            ? "(no log entries)"
            : string.Join(Environment.NewLine, logs.Select(l => $"  [{l.Level}] {l.ExecutorId}: {l.Message}"));

        throw new TimeoutException(
            $"Instance '{instanceId}' was '{last?.Status.ToString() ?? "missing"}', " +
            $"expected one of {string.Join(", ", expected)}.{Environment.NewLine}" +
            $"Terminal reason: {last?.TerminalReason ?? "(none)"}{Environment.NewLine}{detail}");
    }

    public async Task<string> StartAsync(HttpClient client, string workflow, object context)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/workflows/{workflow}/instances", new { context });

        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("instanceId").GetString()!;
    }

    /// <summary>Starts and waits, for the common "did this document run" assertion.</summary>
    public async Task<WorkflowInstance> RunAsync(
        HttpClient client, string workflow, object context, params InstanceStatus[] expected)
    {
        string id = await StartAsync(client, workflow, context);
        return await WaitForStatusAsync(id, expected.Length > 0
            ? expected
            : [InstanceStatus.Completed]);
    }

    /// <summary>Reads the workflow result off a completed instance.</summary>
    public async Task<JsonNode?> ResultOfAsync(string instanceId)
    {
        WorkflowInstance? instance = await Resolve<IInstanceStore>().GetAsync(instanceId, default);
        return instance?.ResultJson is { Length: > 0 } json ? JsonNode.Parse(json) : null;
    }

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
/// The same host under a restrictive redaction policy: every body field is masked except the few
/// named here. A DSL envelope carries more per message than a compiled one — the whole start context
/// travels with every hop — so this fixture exists to prove that extra reach does not widen what
/// leaves the process.
/// </summary>
public sealed class DslRedactedHostFixture : DslHostFixture
{
    public RedactionPolicy Policy { get; } = new(
        bodyAllowList: ["order", "workflow", "version", "attempt", "resumed", "status", "executorId"],
        allowAllBodyFields: false);

    protected override IRedactionPolicy? Redaction => Policy;
}
