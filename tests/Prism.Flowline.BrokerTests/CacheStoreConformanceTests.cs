using Prism.Flowline.Cache.Redis;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Testing;
using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace Prism.Flowline.BrokerTests;

/// <summary>
/// The cache contract against Redis. Any disagreement between Redis and the in-memory store — which
/// runs the same contract in Prism.Flowline.UnitTests — shows up here rather than in production,
/// which is the whole reason the contract is shared.
/// </summary>
[Collection(RedisCollection.Name)]
public class RedisCacheStoreConformanceTests : IAsyncLifetime
{
    private readonly RedisFixture _redis;
    private readonly CacheStoreContract _contract;
    private IConnectionMultiplexer? _connection;

    public RedisCacheStoreConformanceTests(RedisFixture redis)
    {
        _redis = redis;
        _contract = new CacheStoreContract(_ => ValueTask.FromResult<ICacheStore>(new RedisCacheStore(_connection!)));
    }

    public static TheoryData<ContractCaseName> Cases => ContractCaseName.Of<CacheStoreContract>();

    public async Task InitializeAsync()
    {
        if (_redis.Available)
        {
            _connection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        }
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    [RequiresDockerTheory]
    [MemberData(nameof(Cases))]
    public Task Keeps_the_contract(ContractCaseName @case) => _contract.Cases.Named(@case.Value).Run();

    /// <summary>
    /// Redis only: two stores with different prefixes share one server without colliding, which is
    /// what lets several applications point at the same instance.
    /// </summary>
    [RequiresDockerFact]
    public async Task Prefixes_isolate_two_applications()
    {
        var first = new RedisCacheStore(_connection!, "app-one:");
        var second = new RedisCacheStore(_connection!, "app-two:");

        await first.SetAsync("shared", "one", null, default);
        await second.SetAsync("shared", "two", null, default);

        (await first.GetAsync<string>("shared", default)).Should().Be("one");
        (await second.GetAsync<string>("shared", default)).Should().Be("two");
    }
}
