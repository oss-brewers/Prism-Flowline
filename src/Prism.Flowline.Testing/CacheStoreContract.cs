using Prism.Flowline.Abstractions;

namespace Prism.Flowline.Testing;

/// <summary>What every <see cref="ICacheStore"/> must do, regardless of technology.</summary>
/// <remarks>
/// <para>
/// Written once and run against every implementation, so "Memcached behaves like Redis behaves like
/// in-memory" is a checked fact rather than an intention.
/// </para>
/// <para>
/// The two expiry cases wait in real time. A ttl is the one thing a controllable clock cannot verify
/// across a wire — the server keeps its own time — so this contract, unlike the others, does not
/// ask the factory to build the store over <see cref="StoreContractContext.Clock"/>.
/// </para>
/// </remarks>
public sealed class CacheStoreContract
{
    private readonly Func<StoreContractContext, ValueTask<ICacheStore>> _create;

    public CacheStoreContract(Func<StoreContractContext, ValueTask<ICacheStore>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
        Cases = ContractCases.Discover(this);
    }

    /// <summary>Every case, by name, for a runner that would rather enumerate than list.</summary>
    public IReadOnlyList<ContractCase> Cases { get; }

    public Task A_missing_key_reads_as_absent() => Run(nameof(A_missing_key_reads_as_absent), async (ctx, store)
        => Expect.Null(await store.GetAsync<string>(ctx.NewId("k"), default),
            "a cache miss is an ordinary outcome, not an error"));

    public Task A_value_round_trips() => Run(nameof(A_value_round_trips), async (ctx, store) =>
    {
        string key = ctx.NewId("k");
        await store.SetAsync(key, "value", null, default);

        Expect.Equal("value", await store.GetAsync<string>(key, default), "a value reads back as it was set");
    });

    public Task A_complex_value_round_trips() => Run(nameof(A_complex_value_round_trips), async (ctx, store) =>
    {
        string key = ctx.NewId("k");
        await store.SetAsync(key, new CachedThing(Samples.NonAscii, 42), null, default);

        CachedThing read = Expect.NotNull(await store.GetAsync<CachedThing>(key, default), "a complex value reads back");
        Expect.Equal(Samples.NonAscii, read.Name, "a complex value's non-ASCII text reads back unchanged");
        Expect.Equal(42, read.Count, "a complex value's number reads back unchanged");
    });

    public Task Setting_the_same_key_replaces_the_value() => Run(nameof(Setting_the_same_key_replaces_the_value), async (ctx, store) =>
    {
        string key = ctx.NewId("k");
        await store.SetAsync(key, "first", null, default);
        await store.SetAsync(key, "second", null, default);

        Expect.Equal("second", await store.GetAsync<string>(key, default), "a second set replaces the first");
    });

    public Task Remove_reports_whether_anything_was_there() => Run(nameof(Remove_reports_whether_anything_was_there), async (ctx, store) =>
    {
        string key = ctx.NewId("k");
        await store.SetAsync(key, "v", null, default);

        Expect.That(await store.RemoveAsync(key, default), "removing a key that is there reports true");
        Expect.That(!await store.RemoveAsync(key, default),
            "removing nothing is not a failure, but it is distinguishable: it reports false");
        Expect.Null(await store.GetAsync<string>(key, default), "a removed key reads as absent");
    });

    public Task A_value_with_no_ttl_persists() => Run(nameof(A_value_with_no_ttl_persists), async (ctx, store) =>
    {
        string key = ctx.NewId("k");
        await store.SetAsync(key, "v", null, default);
        await Task.Delay(1200);

        Expect.Equal("v", await store.GetAsync<string>(key, default),
            "no ttl means the entry lives until evicted or replaced");
    });

    public Task A_value_expires_on_its_ttl() => Run(nameof(A_value_expires_on_its_ttl), async (ctx, store) =>
    {
        string key = ctx.NewId("k");
        await store.SetAsync(key, "v", TimeSpan.FromSeconds(1), default);

        Expect.Equal("v", await store.GetAsync<string>(key, default), "a value is readable before its ttl runs out");

        await Task.Delay(1500);

        Expect.Null(await store.GetAsync<string>(key, default), "a value is gone once its ttl has run out");
    });

    public Task GetOrCreate_computes_on_a_miss_and_reads_on_a_hit() =>
        Run(nameof(GetOrCreate_computes_on_a_miss_and_reads_on_a_hit), async (ctx, store) =>
        {
            string key = ctx.NewId("k");
            int calls = 0;

            ValueTask<string> Factory(CancellationToken _)
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult("computed");
            }

            Expect.Equal("computed", await store.GetOrCreateAsync(key, Factory, null, default), "a miss is computed");
            Expect.Equal("computed", await store.GetOrCreateAsync(key, Factory, null, default), "a hit reads the computed value");
            Expect.Equal(1, calls, "the second call is served from the cache, not computed again");
        });

    public Task Keys_are_independent() => Run(nameof(Keys_are_independent), async (ctx, store) =>
    {
        string a = ctx.NewId("k");
        string b = ctx.NewId("k");

        await store.SetAsync(a, "A", null, default);
        await store.SetAsync(b, "B", null, default);
        await store.RemoveAsync(a, default);

        Expect.Equal("B", await store.GetAsync<string>(b, default), "removing one key does not disturb another");
    });

    private Task Run(string name, Func<StoreContractContext, ICacheStore, Task> body)
        => ContractRunner.RunAsync(name, _create, body);

    /// <summary>A record with a constructor, which is what a serializer gets wrong first.</summary>
    public sealed record CachedThing(string Name, int Count);
}
