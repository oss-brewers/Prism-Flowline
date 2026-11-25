using Prism.Flowline.Core;

namespace Prism.Flowline.Testing;

/// <summary>What every <see cref="IBlobStore"/> must do, whatever it is built on.</summary>
/// <remarks>
/// The checkpoint store's overflow: a key goes in, an opaque URI comes out, and that URI downloads
/// exactly the bytes uploaded until it is deleted. A missing blob is a
/// <see cref="KeyNotFoundException"/>, the same signal a missing checkpoint gives.
/// </remarks>
public sealed class BlobStoreContract
{
    private readonly Func<StoreContractContext, ValueTask<IBlobStore>> _create;

    public BlobStoreContract(Func<StoreContractContext, ValueTask<IBlobStore>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
        Cases = ContractCases.Discover(this);
    }

    /// <summary>Every case, by name, for a runner that would rather enumerate than list.</summary>
    public IReadOnlyList<ContractCase> Cases { get; }

    public Task A_blob_round_trips() => Run(nameof(A_blob_round_trips), async (ctx, store) =>
    {
        byte[] content = [1, 2, 3, 4];

        string uri = await store.UploadAsync(Key(ctx), content, default);

        Expect.Sequence(content, await store.DownloadAsync(uri, default), "a blob downloads the bytes it was uploaded with");
    });

    public Task Downloading_a_missing_blob_throws() => Run(nameof(Downloading_a_missing_blob_throws), async (ctx, store) =>
    {
        string uri = await store.UploadAsync(Key(ctx), [1, 2, 3], default);
        await store.DeleteAsync(uri, default);

        await Expect.ThrowsAsync<KeyNotFoundException>(() => store.DownloadAsync(uri, default).AsTask(),
            "downloading a blob that is not there throws KeyNotFoundException");
    });

    public Task Deleting_a_missing_blob_is_harmless() => Run(nameof(Deleting_a_missing_blob_is_harmless), async (ctx, store) =>
    {
        string uri = await store.UploadAsync(Key(ctx), [1], default);
        await store.DeleteAsync(uri, default);

        // Pruning races itself across replicas; the second delete is ordinary, not an error.
        await store.DeleteAsync(uri, default);
    });

    public Task Uploading_the_same_key_again_serves_the_new_content() =>
        Run(nameof(Uploading_the_same_key_again_serves_the_new_content), async (ctx, store) =>
        {
            string key = Key(ctx);
            await store.UploadAsync(key, [1, 1, 1], default);
            string second = await store.UploadAsync(key, [2, 2], default);

            Expect.Sequence([(byte)2, (byte)2], await store.DownloadAsync(second, default),
                "a second upload to a key serves what was uploaded second, not a mixture");
        });

    public Task Keys_are_independent() => Run(nameof(Keys_are_independent), async (ctx, store) =>
    {
        string a = await store.UploadAsync(Key(ctx), [1], default);
        string b = await store.UploadAsync(Key(ctx), [2], default);
        await store.DeleteAsync(a, default);

        Expect.Sequence([(byte)2], await store.DownloadAsync(b, default), "deleting one blob leaves another alone");
    });

    public Task Large_and_non_ASCII_payloads_round_trip() => Run(nameof(Large_and_non_ASCII_payloads_round_trip), async (ctx, store) =>
    {
        byte[] content = Samples.LargeBytes();

        string uri = await store.UploadAsync(Key(ctx), content, default);
        byte[] read = await store.DownloadAsync(uri, default);

        Expect.Equal(content.Length, read.Length, "a large blob downloads at the size it was uploaded");
        Expect.That(content.AsSpan().SequenceEqual(read), "a large blob of every byte value downloads unchanged",
            $"first difference at byte {content.AsSpan().CommonPrefixLength(read)}");
    });

    private Task Run(string name, Func<StoreContractContext, IBlobStore, Task> body)
        => ContractRunner.RunAsync(name, _create, body);

    /// <summary>Shaped like the checkpoint store's keys: a session, a slash, a file name.</summary>
    private static string Key(StoreContractContext ctx) => $"{ctx.NewId("session")}/{ctx.NewId("cp")}.json";
}
