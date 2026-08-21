using Prism.Flowline.Abstractions;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Xunit;

namespace Prism.Flowline.BrokerTests;

/// <summary>
/// Marks a test that needs a container runtime. Skipped rather than failed when Docker is absent, so
/// a developer machine without it still reports a green suite instead of a red one it cannot fix.
/// </summary>
/// <remarks>
/// A silent skip is the right answer on a laptop and the wrong one in CI, where a runner that lost
/// its container runtime would publish a green build that never touched Redis or RabbitMQ. Set
/// <c>PRISM_REQUIRE_DOCKER</c> to opt out of skipping: the test then runs and fails with whatever
/// Testcontainers reports, which is the diagnosis rather than a silence.
/// </remarks>
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (!DockerProbe.IsAvailable.Value && !DockerProbe.IsRequired)
        {
            Skip = "Docker is not available on this machine.";
        }
    }
}

public static class DockerProbe
{
    /// <summary>
    /// Whether the caller has declared that a container runtime must be present. Read from
    /// <c>PRISM_REQUIRE_DOCKER</c>; any value other than empty, <c>0</c> or <c>false</c> counts.
    /// </summary>
    public static bool IsRequired =>
        Environment.GetEnvironmentVariable("PRISM_REQUIRE_DOCKER") is { Length: > 0 } value &&
        !string.Equals(value, "0", StringComparison.Ordinal) &&
        !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a container runtime looks reachable. Checked once for the whole assembly, so a
    /// machine without Docker pays one probe rather than a failed container start per test class.
    /// </summary>
    /// <remarks>
    /// Deliberately a cheap endpoint check rather than a real ping: the probe decides whether to
    /// <em>skip</em>, and a skip that itself takes seconds to reach defeats the point. If the
    /// endpoint is there but broken, Testcontainers reports the real failure — which is the more
    /// useful outcome than silently skipping.
    /// </remarks>
    public static readonly Lazy<bool> IsAvailable = new(() =>
    {
        try
        {
            if (Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 })
            {
                return true;
            }

            return OperatingSystem.IsWindows()
                ? Directory.EnumerateFiles(@"\\.\pipe\").Any(p =>
                    p.Contains("docker_engine", StringComparison.OrdinalIgnoreCase))
                : File.Exists("/var/run/docker.sock");
        }
        catch
        {
            return false;
        }
    });
}

/// <summary>
/// Owns a Redis container for the whole assembly. One container, not one per class: the brokers
/// namespace their own keys, and starting Redis per class would dominate the run time.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public string ConnectionString { get; private set; } = "";

    public bool Available => _container is not null;

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable.Value && !DockerProbe.IsRequired)
        {
            return;
        }

        _container = new RedisBuilder().WithImage("redis:7-alpine").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

public sealed class RabbitMqFixture : IAsyncLifetime
{
    private RabbitMqContainer? _container;

    public string ConnectionString { get; private set; } = "";

    public bool Available => _container is not null;

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable.Value && !DockerProbe.IsRequired)
        {
            return;
        }

        _container = new RabbitMqBuilder().WithImage("rabbitmq:4-alpine").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}

[CollectionDefinition(Name)]
public sealed class RabbitMqCollection : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "rabbitmq";
}

/// <summary>Collects deliveries so a test can wait for an expected count rather than sleeping.</summary>
public sealed class DeliveryRecorder
{
    private readonly List<DomainEventMessage> _messages = [];
    private readonly SemaphoreSlim _signal = new(0);

    public Func<DomainEventDelivery, CancellationToken, ValueTask<DeliveryResult>> Handler => (delivery, _) =>
    {
        lock (_messages)
        {
            _messages.Add(delivery.Message);
        }

        _signal.Release();
        return ValueTask.FromResult(DeliveryResult.Ack);
    };

    public IReadOnlyList<DomainEventMessage> Messages
    {
        get { lock (_messages) { return [.. _messages]; } }
    }

    public int Count
    {
        get { lock (_messages) { return _messages.Count; } }
    }

    /// <summary>Waits for <paramref name="count"/> deliveries. Returns false on timeout.</summary>
    public async Task<bool> WaitAsync(int count, TimeSpan? timeout = null)
    {
        TimeSpan budget = timeout ?? TimeSpan.FromSeconds(20);
        DateTime deadline = DateTime.UtcNow + budget;

        for (int i = 0; i < count; i++)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !await _signal.WaitAsync(remaining))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Asserts nothing further arrives. Needs a real wait — the absence of a message cannot be
    /// observed any faster than the time you are willing to give it to show up.
    /// </summary>
    public async Task<bool> StaysAtAsync(int expected, TimeSpan? window = null)
    {
        await Task.Delay(window ?? TimeSpan.FromSeconds(2));
        return Count == expected;
    }
}

public static class Msg
{
    public static DomainEventMessage Distributed(
        string topic, string? correlationKey = null, string? tenantId = null, string payload = """{"v":1}""")
        => new()
        {
            MessageId = Prism.Flowline.Core.IdGenerator.NewId("msg"),
            Topic = topic,
            PayloadJson = payload,
            Scope = DeliveryScope.Distributed,
            CorrelationKey = correlationKey,
            TenantId = tenantId
        };

    public static DomainEventMessage Local(string topic, string payload = """{"v":1}""")
        => new()
        {
            MessageId = Prism.Flowline.Core.IdGenerator.NewId("msg"),
            Topic = topic,
            PayloadJson = payload,
            Scope = DeliveryScope.Local
        };
}
