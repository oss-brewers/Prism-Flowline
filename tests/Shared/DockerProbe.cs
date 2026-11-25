using Xunit;

namespace Prism.Flowline.TestSupport;

// Linked into every test project that starts a container (BrokerTests, StoreTests), so the rule for
// when such a test may skip is written once.

/// <summary>
/// Marks a test that needs a container runtime. Skipped rather than failed when Docker is absent, so
/// a developer machine without it still reports a green suite instead of a red one it cannot fix.
/// </summary>
/// <remarks>
/// A silent skip is the right answer on a laptop and the wrong one in CI, where a runner that lost
/// its container runtime would publish a green build that never touched Redis, RabbitMQ or SQL
/// Server. Set <c>PRISM_REQUIRE_DOCKER</c> to opt out of skipping: the test then runs and fails with
/// whatever Testcontainers reports, which is the diagnosis rather than a silence.
/// </remarks>
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (DockerProbe.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>The theory form of <see cref="RequiresDockerFactAttribute"/>: every row skips or none does.</summary>
public sealed class RequiresDockerTheoryAttribute : TheoryAttribute
{
    public RequiresDockerTheoryAttribute()
    {
        if (DockerProbe.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

public static class DockerProbe
{
    /// <summary>Why a container test should skip here, or null when it should run.</summary>
    public static string? SkipReason =>
        !IsAvailable.Value && !IsRequired ? "Docker is not available on this machine." : null;

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

    /// <summary>Whether a fixture should start its container: available, or required regardless.</summary>
    public static bool ShouldStart => IsAvailable.Value || IsRequired;
}
