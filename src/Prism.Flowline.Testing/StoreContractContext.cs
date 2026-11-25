namespace Prism.Flowline.Testing;

/// <summary>
/// What one case hands the store factory: a clock the case controls, names no other case uses, and
/// a place to register teardown.
/// </summary>
/// <remarks>
/// <para>
/// A fresh context is built for every case. The factory receives it before the case starts, so a
/// store that takes a <see cref="TimeProvider"/> must be built over <see cref="Clock"/>: cases about
/// leases, retries and expiry move that clock forward rather than sleeping.
/// </para>
/// <para>
/// Every tenant, id, workflow name and topic a case writes comes from <see cref="Tenant"/>,
/// <see cref="NewTenant"/> or <see cref="NewId"/>, so cases do not collide when a provider's suite
/// shares one database. What a case cannot scope that way — an instance claim or an expiry sweep
/// reads the whole store — it cleans up after itself, through <see cref="OnDispose"/>, so a later
/// case never finds anything of an earlier one left to claim.
/// </para>
/// </remarks>
public sealed class StoreContractContext : IAsyncDisposable
{
    /// <summary>Where every case's clock starts unless the provider says otherwise.</summary>
    public static DateTimeOffset DefaultStart { get; } = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly List<Func<ValueTask>> _cleanups = [];
    private readonly Lock _sync = new();
    private bool _disposed;

    public StoreContractContext(string caseName, DateTimeOffset? start = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseName);

        CaseName = caseName;
        Clock = new ContractClock(start ?? DefaultStart);
        Tenant = NewTenant();
    }

    /// <summary>The case this context belongs to.</summary>
    public string CaseName { get; }

    /// <summary>The time the store must read. Only the case moves it.</summary>
    public ContractClock Clock { get; }

    /// <summary>A tenant id no other case uses.</summary>
    public string Tenant { get; }

    /// <summary>Another tenant id no other case uses, for cases about more than one tenant.</summary>
    public string NewTenant() => $"tenant-{Unique()}";

    /// <summary>An id no other case uses, for instances, approvals, sessions, workflows and topics.</summary>
    public string NewId(string prefix = "id")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return $"{prefix}-{Unique()}";
    }

    /// <summary>
    /// Runs <paramref name="cleanup"/> when the case ends, pass or fail, after any registered later.
    /// </summary>
    /// <remarks>
    /// A factory can use this to drop what it built for the case — a schema, a database, a
    /// connection. Cleanups run last-registered first, so the contract's own tidying runs while the
    /// store the factory registered still exists.
    /// </remarks>
    public void OnDispose(Func<ValueTask> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _cleanups.Add(cleanup);
        }
    }

    /// <summary>Runs every registered cleanup, then reports any that failed together.</summary>
    public async ValueTask DisposeAsync()
    {
        Func<ValueTask>[] cleanups;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cleanups = [.. _cleanups];
        }

        var failures = new List<Exception>();
        for (int i = cleanups.Length - 1; i >= 0; i--)
        {
            try
            {
                await cleanups[i]().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException($"Cleanup after '{CaseName}' failed.", failures);
        }
    }

    private static string Unique() => Guid.NewGuid().ToString("N");
}

/// <summary>A <see cref="TimeProvider"/> that stands still until a case moves it.</summary>
/// <remarks>
/// Only <see cref="GetUtcNow"/> is controlled. No case waits on a timer or measures elapsed time
/// through the store, so the inherited timer and timestamp members are left as they are.
/// </remarks>
public sealed class ContractClock : TimeProvider
{
    private readonly Lock _sync = new();
    private DateTimeOffset _now;

    public ContractClock(DateTimeOffset start) => _now = start;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _now;
        }
    }

    /// <summary>Moves the clock forward. Time never runs backwards in a case.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);

        lock (_sync)
        {
            _now += by;
        }
    }
}
