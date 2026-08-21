using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Prism.Flowline.Dsl.Expressions;

/// <summary>The three roots an expression may read. Nothing else is in scope.</summary>
public sealed record PrExContext(JsonNode? Data, JsonNode? Context, JsonObject Run)
{
    public static PrExContext Empty { get; } = new(null, null, []);
}

/// <summary>
/// Evaluates an PrEx tree.
/// </summary>
/// <remarks>
/// <para>
/// Total by construction: every operation over every value produces a value, and
/// <see cref="PrExValue.Absent"/> is what stands in for "no answer". Nothing here throws, because a
/// workflow must not fail on an expression over a document shape the author did not anticipate — it
/// must take the other branch.
/// </para>
/// <para>
/// Pure by construction: there is no I/O, no state, and no way to reach either from the grammar.
/// </para>
/// </remarks>
public static class PrExEvaluator
{
    /// <summary>Bounds a pathological pattern. A timed-out match is a non-match, never a fault.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new(StringComparer.Ordinal);

    public static PrExValue Evaluate(PrExNode node, PrExContext context)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);

        return node switch
        {
            PrExLiteral literal => literal.Value,
            PrExPath path => ResolvePath(path, context),
            PrExUnary unary => EvaluateUnary(unary, context),
            PrExBinary binary => EvaluateBinary(binary, context),
            PrExCall call => EvaluateCall(call, context),
            _ => PrExValue.Absent
        };
    }

    /// <summary>
    /// Strict boolean coercion: only <c>true</c> is true. Absent, null, <c>0</c> and <c>""</c> are
    /// all false, and there is no truthiness ladder to remember.
    /// </summary>
    public static bool EvaluateCondition(PrExNode node, PrExContext context)
        => Evaluate(node, context).IsTruthy;

    private static PrExValue ResolvePath(PrExPath path, PrExContext context)
    {
        JsonNode? current = path.Root switch
        {
            PrExRoot.Data => context.Data,
            PrExRoot.Context => context.Context,
            _ => context.Run
        };

        // A root that is itself missing is absent, not null: '$ .x' over a null data payload has no
        // answer, and reporting null would let '== null' succeed against a value that is not there.
        if (current is null && path.Segments.Count > 0)
        {
            return PrExValue.Absent;
        }

        foreach (PrExSegment segment in path.Segments)
        {
            if (segment.IsIndex)
            {
                if (current is not JsonArray array || segment.Index < 0 || segment.Index >= array.Count)
                {
                    return PrExValue.Absent;
                }

                current = array[segment.Index];
                continue;
            }

            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment.Name!, out JsonNode? child))
            {
                return PrExValue.Absent;
            }

            current = child;
        }

        return path.Segments.Count == 0 && current is null
            ? PrExValue.Absent
            : PrExValue.FromNode(current);
    }

    private static PrExValue EvaluateUnary(PrExUnary unary, PrExContext context)
    {
        PrExValue operand = Evaluate(unary.Operand, context);

        return unary.Operator switch
        {
            "!" => operand.Kind == PrExValueKind.Boolean
                ? PrExValue.Bool(!operand.AsBoolean)
                : PrExValue.Absent,

            "-" => operand.Kind == PrExValueKind.Number
                ? PrExValue.Number(-operand.AsNumber)
                : PrExValue.Absent,

            _ => PrExValue.Absent
        };
    }

    private static PrExValue EvaluateBinary(PrExBinary binary, PrExContext context)
    {
        // Short-circuit before evaluating the right operand, so a guard like
        // 'has($.order) && $.order.total > 0' costs nothing when the guard fails.
        if (binary.Operator is "&&" or "||")
        {
            PrExValue left = Evaluate(binary.Left, context);
            if (left.Kind != PrExValueKind.Boolean)
            {
                return PrExValue.Absent;
            }

            if (binary.Operator == "&&" && !left.AsBoolean) return PrExValue.False;
            if (binary.Operator == "||" && left.AsBoolean) return PrExValue.True;

            PrExValue right = Evaluate(binary.Right, context);
            return right.Kind == PrExValueKind.Boolean ? right : PrExValue.Absent;
        }

        PrExValue a = Evaluate(binary.Left, context);
        PrExValue b = Evaluate(binary.Right, context);

        return binary.Operator switch
        {
            "==" or "!=" or "<" or "<=" or ">" or ">=" => Compare(binary.Operator, a, b),
            _ => Arithmetic(binary.Operator, a, b)
        };
    }

    /// <summary>
    /// Absence makes every comparison false, including <c>!=</c>. That is deliberate: a document
    /// asking whether a field it never set differs from a value should not be told "yes". Use
    /// <c>has()</c> to ask about presence.
    /// </summary>
    private static PrExValue Compare(string op, PrExValue a, PrExValue b)
    {
        if (a.IsAbsent || b.IsAbsent)
        {
            return PrExValue.False;
        }

        if (op is "==" or "!=")
        {
            bool equal = a.Equals(b);
            return PrExValue.Bool(op == "==" ? equal : !equal);
        }

        int comparison;
        if (a.Kind == PrExValueKind.Number && b.Kind == PrExValueKind.Number)
        {
            comparison = decimal.Compare(a.AsNumber, b.AsNumber);
        }
        else if (a.Kind == PrExValueKind.String && b.Kind == PrExValueKind.String)
        {
            comparison = string.CompareOrdinal(a.AsString, b.AsString);
        }
        else
        {
            // No coercion ladder: ordering two different JSON types has no defensible answer.
            return PrExValue.False;
        }

        return PrExValue.Bool(op switch
        {
            "<" => comparison < 0,
            "<=" => comparison <= 0,
            ">" => comparison > 0,
            _ => comparison >= 0
        });
    }

    /// <summary>
    /// Decimal, and numbers only. These documents price orders, so binary floating point is the
    /// wrong default. <c>+</c> does not concatenate strings — that is what templates are for, and a
    /// <c>+</c> that sometimes adds and sometimes joins is the single most reliable source of bugs
    /// in languages that allow it.
    /// </summary>
    private static PrExValue Arithmetic(string op, PrExValue a, PrExValue b)
    {
        if (a.Kind != PrExValueKind.Number || b.Kind != PrExValueKind.Number)
        {
            return PrExValue.Absent;
        }

        decimal x = a.AsNumber;
        decimal y = b.AsNumber;

        switch (op)
        {
            case "+": return PrExValue.Number(x + y);
            case "-": return PrExValue.Number(x - y);
            case "*": return PrExValue.Number(x * y);

            case "/":
            case "%":
                if (y == 0m)
                {
                    return PrExValue.Absent;
                }

                return PrExValue.Number(op == "/" ? x / y : x % y);

            default:
                return PrExValue.Absent;
        }
    }

    private static PrExValue EvaluateCall(PrExCall call, PrExContext context)
    {
        // An unknown or mis-arity call cannot reach here through a validated document; if it does,
        // absent keeps the evaluator total rather than surfacing a validator bug as a run failure.
        if (!PrExFunctions.TryGet(call.Name, out PrExFunction function) ||
            !function.AcceptsArity(call.Arguments.Count))
        {
            return PrExValue.Absent;
        }

        switch (call.Name)
        {
            case "has":
                return PrExValue.Bool(!Evaluate(call.Arguments[0], context).IsAbsent);

            case "coalesce":
                foreach (PrExNode argument in call.Arguments)
                {
                    PrExValue candidate = Evaluate(argument, context);
                    if (candidate.Kind is not (PrExValueKind.Absent or PrExValueKind.Null))
                    {
                        return candidate;
                    }
                }

                return PrExValue.Absent;
        }

        PrExValue first = Evaluate(call.Arguments[0], context);

        switch (call.Name)
        {
            case "len":
                return PrExValue.Number(first.Length);

            case "lower":
                return first.Kind == PrExValueKind.String
                    ? PrExValue.String(first.AsString.ToLowerInvariant())
                    : PrExValue.Absent;

            case "upper":
                return first.Kind == PrExValueKind.String
                    ? PrExValue.String(first.AsString.ToUpperInvariant())
                    : PrExValue.Absent;

            case "number":
                return first.Kind switch
                {
                    PrExValueKind.Number => first,
                    PrExValueKind.String when decimal.TryParse(
                        first.AsString, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)
                        => PrExValue.Number(parsed),
                    _ => PrExValue.Absent
                };

            case "string":
                return first.IsAbsent ? PrExValue.Absent : PrExValue.String(first.ToText());

            case "bool":
                return first.Kind switch
                {
                    PrExValueKind.Boolean => first,
                    PrExValueKind.String when bool.TryParse(first.AsString, out bool parsed)
                        => PrExValue.Bool(parsed),
                    _ => PrExValue.Absent
                };
        }

        PrExValue second = Evaluate(call.Arguments[1], context);

        if (first.Kind != PrExValueKind.String || second.Kind != PrExValueKind.String)
        {
            return PrExValue.Absent;
        }

        string subject = first.AsString;
        string operand = second.AsString;

        return call.Name switch
        {
            "contains" => PrExValue.Bool(subject.Contains(operand, StringComparison.Ordinal)),
            "startsWith" => PrExValue.Bool(subject.StartsWith(operand, StringComparison.Ordinal)),
            "endsWith" => PrExValue.Bool(subject.EndsWith(operand, StringComparison.Ordinal)),
            "matches" => Matches(subject, operand),
            _ => PrExValue.Absent
        };
    }

    private static PrExValue Matches(string subject, string pattern)
    {
        Regex? regex = RegexCache.GetOrAdd(pattern, static p =>
        {
            try
            {
                return new Regex(p, RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException)
            {
                // Cached as null so a bad pattern is compiled once, not once per evaluation.
                return null;
            }
        });

        if (regex is null)
        {
            return PrExValue.Absent;
        }

        try
        {
            return PrExValue.Bool(regex.IsMatch(subject));
        }
        catch (RegexMatchTimeoutException)
        {
            // A pattern that cannot decide in 200 ms has not matched. Failing the run instead would
            // hand any author a way to stall a dispatcher.
            return PrExValue.False;
        }
    }
}
