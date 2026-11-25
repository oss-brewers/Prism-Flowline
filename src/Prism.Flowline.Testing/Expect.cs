using System.Collections;
using System.Text.Json;

namespace Prism.Flowline.Testing;

/// <summary>
/// The contract's assertions. Each takes the rule in words; a failure throws
/// <see cref="StoreContractException"/> carrying that rule and what was seen instead.
/// </summary>
/// <remarks>
/// Deliberately small and private to the package: an assertion library would be a dependency every
/// provider's test project inherits, and would tie the messages to its formatting.
/// </remarks>
internal static class Expect
{
    private const int MaxShown = 120;

    public static void That(bool condition, string rule, string? detail = null)
    {
        if (!condition)
        {
            throw new StoreContractException(rule, detail);
        }
    }

    public static void Equal<T>(T expected, T actual, string rule)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new StoreContractException(rule, $"Expected {Show(expected)}, got {Show(actual)}");
        }
    }

    public static T NotNull<T>(T? value, string rule) where T : class
        => value ?? throw new StoreContractException(rule, "Got null");

    public static void Null<T>(T? value, string rule) where T : class
    {
        if (value is not null)
        {
            throw new StoreContractException(rule, $"Expected null, got {Show(value)}");
        }
    }

    public static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string rule)
    {
        T[] want = [.. expected];
        T[] got = [.. actual];

        if (!want.SequenceEqual(got))
        {
            throw new StoreContractException(rule, $"Expected [{ShowAll(want)}], got [{ShowAll(got)}]");
        }
    }

    public static void Contains<T>(IEnumerable<T> items, T item, string rule)
    {
        if (!items.Contains(item))
        {
            throw new StoreContractException(rule, $"{Show(item)} was missing");
        }
    }

    public static void DoesNotContain<T>(IEnumerable<T> items, T item, string rule)
    {
        if (items.Contains(item))
        {
            throw new StoreContractException(rule, $"{Show(item)} was present");
        }
    }

    public static async Task<Exception> ThrowsAsync(Func<Task> act, string rule)
    {
        try
        {
            await act().ConfigureAwait(false);
        }
        catch (StoreContractException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new StoreContractException(rule, "Nothing was thrown");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> act, string rule)
        where TException : Exception
    {
        Exception thrown = await ThrowsAsync(act, rule).ConfigureAwait(false);

        return thrown as TException ?? throw new StoreContractException(
            rule, $"Expected {typeof(TException).Name}, got {thrown.GetType().Name}: {thrown.Message}", thrown);
    }

    /// <summary>
    /// JSON compared as JSON: a store may normalise whitespace, key order or number spelling — a
    /// <c>jsonb</c> column does all three — but not the values.
    /// </summary>
    public static void Json(string? expected, string? actual, string rule)
    {
        if (expected is null || actual is null)
        {
            Equal(expected, actual, rule);
            return;
        }

        using JsonDocument want = JsonDocument.Parse(expected);
        using JsonDocument got = ParseOrFail(actual, rule);

        if (!JsonEquivalent(want.RootElement, got.RootElement))
        {
            throw new StoreContractException(rule, $"Expected {Show(expected)}, got {Show(actual)}");
        }
    }

    public static bool JsonEquivalent(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                Dictionary<string, JsonElement> left = [];
                foreach (JsonProperty property in a.EnumerateObject())
                {
                    left[property.Name] = property.Value;
                }

                int count = 0;
                foreach (JsonProperty property in b.EnumerateObject())
                {
                    count++;
                    if (!left.TryGetValue(property.Name, out JsonElement value) || !JsonEquivalent(value, property.Value))
                    {
                        return false;
                    }
                }

                return count == left.Count;

            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength())
                {
                    return false;
                }

                using (JsonElement.ArrayEnumerator x = a.EnumerateArray(), y = b.EnumerateArray())
                {
                    while (x.MoveNext() && y.MoveNext())
                    {
                        if (!JsonEquivalent(x.Current, y.Current))
                        {
                            return false;
                        }
                    }
                }

                return true;

            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);

            case JsonValueKind.Number:
                return a.TryGetDecimal(out decimal m) && b.TryGetDecimal(out decimal n)
                    ? m == n
                    : string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal);

            default:
                return true;   // true, false, null: the kind is the value
        }
    }

    internal static string Show(object? value) => value switch
    {
        null => "null",
        string s => Quote(s),
        DateTimeOffset d => d.ToString("O"),
        IEnumerable sequence and not string => $"[{ShowAll(sequence.Cast<object?>())}]",
        _ => Clip(value.ToString() ?? string.Empty)
    };

    private static string ShowAll<T>(IEnumerable<T> items) => string.Join(", ", items.Select(i => Show(i)));

    private static string Quote(string s) => s.Length <= MaxShown
        ? $"'{s}'"
        : $"'{s[..MaxShown]}…' ({s.Length} chars)";

    private static string Clip(string s) => s.Length <= MaxShown ? s : $"{s[..MaxShown]}…";

    private static JsonDocument ParseOrFail(string json, string rule)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new StoreContractException(rule, $"What came back is not JSON: {ex.Message}", ex);
        }
    }
}
