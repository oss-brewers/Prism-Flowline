using System.Runtime.ExceptionServices;

namespace Prism.Flowline.Testing;

/// <summary>The one way a case runs: build a context, ask the factory for a store, run, tidy up.</summary>
internal static class ContractRunner
{
    public static async Task RunAsync<TStore>(
        string caseName,
        Func<StoreContractContext, ValueTask<TStore>> create,
        Func<StoreContractContext, TStore, Task> body)
    {
        var context = new StoreContractContext(caseName);

        TStore store;
        try
        {
            store = await create(context).ConfigureAwait(false);
        }
        catch
        {
            // The factory's own failure is the diagnosis; it is not a broken rule, so it is not
            // dressed up as one.
            await DisposeQuietlyAsync(context).ConfigureAwait(false);
            throw;
        }

        Exception? failure = null;
        try
        {
            await body(context, store).ConfigureAwait(false);
        }
        catch (StoreContractException ex)
        {
            failure = ex.CaseName is null ? ex.InCase(caseName) : ex;
        }
        catch (Exception ex)
        {
            // A store that throws where the case expected an answer has still broken the rule the
            // case is about; naming the case keeps that readable in a runner that shows only the
            // message.
            failure = new StoreContractException(
                caseName,
                $"the store threw {ex.GetType().Name} where the case expected an answer",
                ex.Message,
                ex);
        }

        try
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (failure is null)
        {
            failure = new StoreContractException(
                caseName, "the store could not be tidied after the case", ex.InnerException?.Message ?? ex.Message, ex);
        }
        catch
        {
            // The case already failed; that failure is the one worth reading.
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    private static async ValueTask DisposeQuietlyAsync(StoreContractContext context)
    {
        try
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best effort: the factory failure is already on its way out.
        }
    }
}
