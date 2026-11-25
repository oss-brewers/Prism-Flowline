namespace Prism.Flowline.Testing;

/// <summary>Starts several callers at once, the way replicas and retried requests arrive.</summary>
internal static class Contention
{
    public sealed record Outcome<T>(int Contender, T? Result, Exception? Error);

    /// <summary>
    /// Runs <paramref name="attempt"/> once per contender on the thread pool, all released together,
    /// and records each one's result or exception rather than letting the first exception end it.
    /// </summary>
    public static async Task<Outcome<T>[]> RaceAsync<T>(int contenders, Func<int, Task<T>> attempt)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Outcome<T>>[] running =
        [
            .. Enumerable.Range(0, contenders).Select(i => Task.Run(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                try
                {
                    return new Outcome<T>(i, await attempt(i).ConfigureAwait(false), null);
                }
                catch (Exception ex)
                {
                    return new Outcome<T>(i, default, ex);
                }
            }))
        ];

        gate.SetResult();
        return await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>Fails when any contender threw, naming the first exception.</summary>
    public static void NoneThrew<T>(IEnumerable<Outcome<T>> outcomes, string rule)
    {
        Outcome<T>? failed = outcomes.FirstOrDefault(o => o.Error is not null);
        if (failed is not null)
        {
            throw new StoreContractException(
                rule,
                $"Contender {failed.Contender} got {failed.Error!.GetType().Name}: {failed.Error.Message}",
                failed.Error);
        }
    }
}
