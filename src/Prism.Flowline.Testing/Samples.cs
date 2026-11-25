using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Prism.Flowline.Testing;

/// <summary>Payloads that find the column types and encodings a database gets wrong.</summary>
internal static class Samples
{
    /// <summary>
    /// Text from several scripts and both Unicode planes, plus the characters JSON and SQL quote.
    /// A <c>varchar</c> column, a Latin-1 collation or a UCS-2 assumption each lose part of it.
    /// </summary>
    public const string NonAscii =
        "Grüße aus Zürich, café crème, naïve — 日本語のテキスト, Ελληνικά, עברית, العربية, हिन्दी, " +
        "emoji 🚀👩🏽‍💻🇳🇿, mathematical 𝔘𝔫𝔦𝔠𝔬𝔡𝔢, quotes \" ' ` \\ and a tab\tbetween";

    private static readonly JsonSerializerOptions Raw = new()
    {
        // Left unescaped on purpose: an escaped payload is plain ASCII and would prove nothing
        // about how the store encodes what it is given.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// JSON well past any fixed-width column (and past the 256 KB inline limit the in-memory
    /// checkpoint store overflows at), carrying <see cref="NonAscii"/> throughout.
    /// </summary>
    public static string LargeJson(string marker = "sample", int entries = 3_000) => JsonSerializer.Serialize(new
    {
        marker,
        text = NonAscii,
        numbers = new object[] { 0, -1, 1.5m, 12345678901234L, 1e-7 },
        nested = new { flag = true, nothing = (string?)null, empty = Array.Empty<int>() },
        entries = Enumerable.Range(0, entries).Select(i => new { i, text = $"{i}: {NonAscii}" })
    }, Raw);

    /// <summary>Text well past any fixed-width column, made of <see cref="NonAscii"/>.</summary>
    public static string LargeText(int repeats = 2_000)
    {
        var builder = new StringBuilder(repeats * (NonAscii.Length + 8));
        for (int i = 0; i < repeats; i++)
        {
            builder.Append(i).Append(": ").Append(NonAscii).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Every byte value, then a few megabytes more, so nothing about the content is textual.</summary>
    public static byte[] LargeBytes(int size = 3 * 1024 * 1024)
    {
        byte[] bytes = new byte[size];
        for (int i = 0; i < 256; i++)
        {
            bytes[i] = (byte)i;
        }

        new Random(20260101).NextBytes(bytes.AsSpan(256));
        return bytes;
    }
}
