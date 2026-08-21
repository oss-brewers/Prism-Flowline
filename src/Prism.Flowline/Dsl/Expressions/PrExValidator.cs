namespace Prism.Flowline.Dsl.Expressions;

/// <summary>One problem found by static analysis of a parsed expression.</summary>
public sealed record PrExIssue(string Message, int Offset, string? Suggestion = null);

/// <summary>What static analysis concluded about an expression.</summary>
public sealed record ExpressionFacts(
    int Depth,
    bool IsDeterministic,
    IReadOnlyList<PrExIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

/// <summary>
/// Static analysis over a parsed tree: everything decidable without a document to evaluate against.
/// </summary>
/// <remarks>
/// Separate from the parser because these are different failures with different audiences. A parse
/// error means the text is not an expression; an issue here means it is a well-formed expression
/// that will never do what it says.
/// </remarks>
public static class PrExValidator
{
    public static ExpressionFacts Analyse(PrExNode node, int maxDepth = PrExParser.MaxDepth)
    {
        ArgumentNullException.ThrowIfNull(node);

        var issues = new List<PrExIssue>();
        bool deterministic = true;

        Walk(node, issues, ref deterministic);

        if (node.Depth > maxDepth)
        {
            issues.Add(new PrExIssue(
                $"Expression nests {node.Depth} deep; the limit is {maxDepth}.", node.Offset));
        }

        return new ExpressionFacts(node.Depth, deterministic, issues);
    }

    /// <summary>Parses and analyses in one step, folding a parse error into the issue list.</summary>
    public static ExpressionFacts Check(string expression, int maxDepth = PrExParser.MaxDepth)
    {
        PrExResult parsed = PrExParser.Parse(expression);

        return parsed.IsSuccess
            ? Analyse(parsed.Node!, maxDepth)
            : new ExpressionFacts(0, true, [new PrExIssue(parsed.Error!.Message, parsed.Error.Offset)]);
    }

    private static void Walk(PrExNode node, List<PrExIssue> issues, ref bool deterministic)
    {
        switch (node)
        {
            case PrExPath path:
                if (path.IsNonDeterministic)
                {
                    deterministic = false;
                }

                break;

            case PrExUnary unary:
                Walk(unary.Operand, issues, ref deterministic);
                break;

            case PrExBinary binary:
                Walk(binary.Left, issues, ref deterministic);
                Walk(binary.Right, issues, ref deterministic);
                break;

            case PrExCall call:
                CheckCall(call, issues);
                foreach (PrExNode argument in call.Arguments)
                {
                    Walk(argument, issues, ref deterministic);
                }

                break;
        }
    }

    private static void CheckCall(PrExCall call, List<PrExIssue> issues)
    {
        if (!PrExFunctions.TryGet(call.Name, out PrExFunction function))
        {
            string? suggestion = PrExFunctions.Suggest(call.Name);
            issues.Add(new PrExIssue(
                $"Unknown function '{call.Name}'.", call.Offset,
                suggestion is null ? null : $"Did you mean '{suggestion}'?"));
            return;
        }

        if (!function.AcceptsArity(call.Arguments.Count))
        {
            issues.Add(new PrExIssue(
                $"'{call.Name}' takes {function.DescribeArity()} argument(s) but was given {call.Arguments.Count}.",
                call.Offset));
        }

        // A pattern assembled at run time cannot be checked at authoring time, and an unbounded
        // pattern is the one genuinely dangerous thing in the grammar. Requiring a literal keeps
        // every regex in a document reviewable by reading the document.
        if (call.Name == "matches" && call.Arguments.Count == 2)
        {
            if (call.Arguments[1] is not PrExLiteral { Value.Kind: PrExValueKind.String } literal)
            {
                issues.Add(new PrExIssue(
                    "The pattern argument to 'matches' must be a string literal.", call.Arguments[1].Offset));
                return;
            }

            try
            {
                _ = new System.Text.RegularExpressions.Regex(literal.Value.AsString);
            }
            catch (ArgumentException ex)
            {
                issues.Add(new PrExIssue(
                    $"Invalid regular expression: {ex.Message}", call.Arguments[1].Offset));
            }
        }
    }
}
