namespace Prism.Flowline.Dsl.Expressions;

/// <summary>Which object a path is rooted in.</summary>
public enum PrExRoot
{
    /// <summary><c>$</c> — the current message's <c>data</c>.</summary>
    Data,

    /// <summary><c>$ctx</c> — the frozen start context, reachable from every node.</summary>
    Context,

    /// <summary><c>$run</c> — instance, tenant, attempt, superstep, workflow, version, now.</summary>
    Run
}

/// <summary>One step of a path: a property name or an array index.</summary>
public readonly record struct PrExSegment(string? Name, int Index)
{
    public bool IsIndex => Name is null;

    public static PrExSegment Property(string name) => new(name, -1);
    public static PrExSegment At(int index) => new(null, index);

    public override string ToString() => IsIndex ? $"[{Index}]" : $".{Name}";
}

/// <summary>
/// An immutable PrEx syntax tree. Parsed once at registration and evaluated many times, so nothing
/// here holds evaluation state.
/// </summary>
public abstract record PrExNode
{
    /// <summary>Nesting depth, used to enforce the configured limit at validation time.</summary>
    public abstract int Depth { get; }

    /// <summary>Source offset of the construct's first token, for diagnostics.</summary>
    public int Offset { get; init; }
}

public sealed record PrExLiteral(PrExValue Value) : PrExNode
{
    public override int Depth => 1;
}

public sealed record PrExPath(PrExRoot Root, IReadOnlyList<PrExSegment> Segments) : PrExNode
{
    public override int Depth => 1;

    /// <summary>True for <c>$run.now</c>, the one value that differs between two evaluations.</summary>
    public bool IsNonDeterministic =>
        Root == PrExRoot.Run && Segments.Count > 0 &&
        string.Equals(Segments[0].Name, "now", StringComparison.Ordinal);

    public override string ToString() =>
        (Root switch { PrExRoot.Data => "$", PrExRoot.Context => "$ctx", _ => "$run" }) +
        string.Concat(Segments);
}

public sealed record PrExUnary(string Operator, PrExNode Operand) : PrExNode
{
    public override int Depth => Operand.Depth + 1;
}

public sealed record PrExBinary(string Operator, PrExNode Left, PrExNode Right) : PrExNode
{
    public override int Depth => Math.Max(Left.Depth, Right.Depth) + 1;
}

public sealed record PrExCall(string Name, IReadOnlyList<PrExNode> Arguments) : PrExNode
{
    public override int Depth => Arguments.Count == 0 ? 1 : Arguments.Max(a => a.Depth) + 1;
}
