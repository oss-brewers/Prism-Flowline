using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Prism.Flowline.Dsl.Expressions;

public enum PrExValueKind
{
    /// <summary>
    /// The path resolved to nothing. Distinct from <see cref="Null"/>, which is a JSON value the
    /// document actually contains.
    /// </summary>
    Absent,
    Null,
    Boolean,
    Number,
    String,
    Array,
    Object
}

/// <summary>
/// One value in an PrEx evaluation. Absence is a value rather than an exception, which is what makes
/// the evaluator total: no expression over any document can throw.
/// </summary>
public readonly struct PrExValue : IEquatable<PrExValue>
{
    private readonly decimal _number;
    private readonly bool _boolean;
    private readonly string? _string;
    private readonly JsonNode? _node;

    private PrExValue(PrExValueKind kind, decimal number = 0, bool boolean = false,
        string? text = null, JsonNode? node = null)
    {
        Kind = kind;
        _number = number;
        _boolean = boolean;
        _string = text;
        _node = node;
    }

    public PrExValueKind Kind { get; }

    public static PrExValue Absent { get; } = new(PrExValueKind.Absent);
    public static PrExValue Null { get; } = new(PrExValueKind.Null);
    public static PrExValue True { get; } = new(PrExValueKind.Boolean, boolean: true);
    public static PrExValue False { get; } = new(PrExValueKind.Boolean, boolean: false);

    public static PrExValue Bool(bool value) => value ? True : False;
    public static PrExValue Number(decimal value) => new(PrExValueKind.Number, number: value);
    public static PrExValue String(string value) => new(PrExValueKind.String, text: value);

    public bool IsAbsent => Kind == PrExValueKind.Absent;
    public bool IsTruthy => Kind == PrExValueKind.Boolean && _boolean;

    public bool AsBoolean => _boolean;
    public decimal AsNumber => _number;
    public string AsString => _string ?? string.Empty;
    public JsonNode? AsNode => _node;

    /// <summary>
    /// Classifies a <see cref="JsonNode"/> into a value. A null reference is <see cref="Absent"/>
    /// only when the caller means "not there"; a JSON null arrives as <see cref="JsonValue"/> and
    /// maps to <see cref="Null"/>.
    /// </summary>
    public static PrExValue FromNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return Null;

            case JsonArray array:
                return new PrExValue(PrExValueKind.Array, node: array);

            case JsonObject obj:
                return new PrExValue(PrExValueKind.Object, node: obj);

            case JsonValue value:
                // Classified by JSON kind first, not by trying CLR types in turn. A JsonValue holds
                // whatever the writer put in it — JsonValue.Create(200) is backed by int, and asking
                // it for a decimal simply fails — so type-probing quietly turned numbers into
                // strings and made '$.status == 200' false against a genuine 200.
                switch (value.GetValueKind())
                {
                    case JsonValueKind.True:
                        return True;

                    case JsonValueKind.False:
                        return False;

                    case JsonValueKind.Number:
                        return Number(ReadNumber(value));

                    case JsonValueKind.String:
                        return value.TryGetValue(out string? text) && text is not null
                            ? String(text)
                            : String(value.ToJsonString().Trim('"'));

                    case JsonValueKind.Null:
                        return Null;

                    default:
                        return String(value.ToJsonString().Trim('"'));
                }

            default:
                return String(node.ToJsonString());
        }
    }

    /// <summary>
    /// Reads a number whatever CLR type backs it. Decimal throughout, so money keeps its scale; a
    /// double that will not fit is taken as a double and converted, which loses precision but never
    /// loses the value.
    /// </summary>
    private static decimal ReadNumber(JsonValue value)
    {
        if (value.TryGetValue(out decimal d)) return d;
        if (value.TryGetValue(out long l)) return l;
        if (value.TryGetValue(out int i)) return i;
        if (value.TryGetValue(out double dbl)) return (decimal)dbl;
        if (value.TryGetValue(out float f)) return (decimal)f;

        return decimal.TryParse(
            value.ToJsonString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)
            ? parsed
            : 0m;
    }

    /// <summary>Length as <c>len()</c> defines it: characters, elements, or properties.</summary>
    public int Length => Kind switch
    {
        PrExValueKind.String => AsString.Length,
        PrExValueKind.Array => ((JsonArray)_node!).Count,
        PrExValueKind.Object => ((JsonObject)_node!).Count,
        _ => 0
    };

    /// <summary>Renders for template interpolation. Absent and null both render as empty.</summary>
    public string ToText() => Kind switch
    {
        PrExValueKind.Absent or PrExValueKind.Null => string.Empty,
        PrExValueKind.Boolean => _boolean ? "true" : "false",
        PrExValueKind.Number => FormatNumber(_number),
        PrExValueKind.String => AsString,
        _ => _node?.ToJsonString() ?? string.Empty
    };

    /// <summary>Converts back to a JSON node for writing into an envelope.</summary>
    public JsonNode? ToNode() => Kind switch
    {
        PrExValueKind.Absent or PrExValueKind.Null => null,
        PrExValueKind.Boolean => JsonValue.Create(_boolean),
        PrExValueKind.Number => JsonValue.Create(_number),
        PrExValueKind.String => JsonValue.Create(AsString),
        _ => _node?.DeepClone()
    };

    /// <summary>
    /// Trailing zeros are dropped so <c>1.50</c> and <c>1.5</c> render alike — decimal preserves
    /// scale, and a template that produced "1.50" where the author wrote arithmetic would look wrong.
    /// </summary>
    internal static string FormatNumber(decimal value)
    {
        decimal normalized = value == decimal.Truncate(value) && Math.Abs(value) < 1e15m
            ? decimal.Truncate(value)
            : value / 1.000000000000000000000000000000000m;

        return normalized.ToString(CultureInfo.InvariantCulture);
    }

    public bool Equals(PrExValue other)
    {
        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            PrExValueKind.Absent or PrExValueKind.Null => true,
            PrExValueKind.Boolean => _boolean == other._boolean,
            PrExValueKind.Number => _number == other._number,
            PrExValueKind.String => string.Equals(_string, other._string, StringComparison.Ordinal),
            _ => string.Equals(_node?.ToJsonString(), other._node?.ToJsonString(), StringComparison.Ordinal)
        };
    }

    public override bool Equals(object? obj) => obj is PrExValue other && Equals(other);

    public override int GetHashCode() => Kind switch
    {
        PrExValueKind.Boolean => _boolean.GetHashCode(),
        PrExValueKind.Number => _number.GetHashCode(),
        PrExValueKind.String => StringComparer.Ordinal.GetHashCode(_string ?? string.Empty),
        _ => (int)Kind
    };

    public override string ToString() => Kind == PrExValueKind.Absent ? "<absent>" : ToText();
}
