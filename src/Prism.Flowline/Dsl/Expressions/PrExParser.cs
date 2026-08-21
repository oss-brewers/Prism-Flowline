using System.Globalization;

namespace Prism.Flowline.Dsl.Expressions;

/// <summary>Where a parse failed, and why.</summary>
public sealed record PrExError(string Message, int Offset)
{
    public override string ToString() => $"{Message} (at offset {Offset})";
}

/// <summary>An AST or an error. Never both, and never an exception.</summary>
public sealed record PrExResult(PrExNode? Node, PrExError? Error)
{
    public bool IsSuccess => Node is not null;

    public static PrExResult Ok(PrExNode node) => new(node, null);
    public static PrExResult Fail(string message, int offset) => new(null, new PrExError(message, offset));
}

/// <summary>
/// Recursive-descent parser for PrEx.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than generated: the grammar is a page long, and a hand-written parser is what
/// gives every construct the exact source offset the diagnostics depend on.
/// </para>
/// <para>
/// Parsing never throws. A malformed expression is data — it arrives in a document from outside the
/// build, and the only useful response is a diagnostic pointing at it.
/// </para>
/// </remarks>
public static class PrExParser
{
    /// <summary>Guards against a pathological document; the semantic validator enforces its own limit too.</summary>
    public const int MaxDepth = 32;

    public static PrExResult Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return PrExResult.Fail("An expression is required.", 0);
        }

        var parser = new Parser(expression);
        return parser.ParseAll();
    }

    /// <summary>Parses or throws. For framework-internal call sites that have already validated.</summary>
    public static PrExNode ParseOrThrow(string expression)
    {
        PrExResult result = Parse(expression);
        return result.Node ?? throw new FormatException(
            $"Invalid PrEx expression '{expression}'. {result.Error!.Message}");
    }

    private sealed class Parser
    {
        private readonly string _source;
        private readonly PrExLexer _lexer;
        private PrExToken _current;
        private PrExError? _error;

        internal Parser(string source)
        {
            _source = source;
            _lexer = new PrExLexer(source);
            _current = _lexer.Next();
            CaptureLexError();
        }

        internal PrExResult ParseAll()
        {
            if (_error is not null)
            {
                return new PrExResult(null, _error);
            }

            PrExNode? node = ParseOr();

            if (_error is not null)
            {
                return new PrExResult(null, _error);
            }

            if (_current.Kind != PrExTokenKind.End)
            {
                return PrExResult.Fail(
                    $"Unexpected {_current} after a complete expression.", _current.Offset);
            }

            if (node!.Depth > MaxDepth)
            {
                return PrExResult.Fail(
                    $"Expression nests {node.Depth} deep; the limit is {MaxDepth}.", 0);
            }

            return PrExResult.Ok(node);
        }

        private void Advance()
        {
            _current = _lexer.Next();
            CaptureLexError();
        }

        private void CaptureLexError()
        {
            if (_current.Kind == PrExTokenKind.Invalid && _error is null)
            {
                _error = new PrExError(_lexer.Error ?? "Invalid token.", _current.Offset);
            }
        }

        private void Fail(string message, int offset) => _error ??= new PrExError(message, offset);

        private bool IsOperator(params string[] operators) =>
            _current.Kind == PrExTokenKind.Operator && Array.IndexOf(operators, _current.Text) >= 0;

        private PrExNode? ParseOr()
        {
            PrExNode? left = ParseAnd();
            while (_error is null && IsOperator("||"))
            {
                int offset = _current.Offset;
                Advance();
                PrExNode? right = ParseAnd();
                if (_error is not null) return null;
                left = new PrExBinary("||", left!, right!) { Offset = offset };
            }

            return left;
        }

        private PrExNode? ParseAnd()
        {
            PrExNode? left = ParseComparison();
            while (_error is null && IsOperator("&&"))
            {
                int offset = _current.Offset;
                Advance();
                PrExNode? right = ParseComparison();
                if (_error is not null) return null;
                left = new PrExBinary("&&", left!, right!) { Offset = offset };
            }

            return left;
        }

        /// <summary>
        /// Non-associative: <c>a &lt; b &lt; c</c> is a mistake in every language that quietly allows
        /// it, so it is refused here rather than silently comparing a boolean to a number.
        /// </summary>
        private PrExNode? ParseComparison()
        {
            PrExNode? left = ParseAdditive();
            if (_error is not null || !IsOperator("==", "!=", "<", "<=", ">", ">="))
            {
                return left;
            }

            string op = _current.Text;
            int offset = _current.Offset;
            Advance();

            PrExNode? right = ParseAdditive();
            if (_error is not null) return null;

            if (IsOperator("==", "!=", "<", "<=", ">", ">="))
            {
                Fail($"Chained comparison '{_current.Text}'. Combine two comparisons with '&&' instead.",
                    _current.Offset);
                return null;
            }

            return new PrExBinary(op, left!, right!) { Offset = offset };
        }

        private PrExNode? ParseAdditive()
        {
            PrExNode? left = ParseMultiplicative();
            while (_error is null && IsOperator("+", "-"))
            {
                string op = _current.Text;
                int offset = _current.Offset;
                Advance();
                PrExNode? right = ParseMultiplicative();
                if (_error is not null) return null;
                left = new PrExBinary(op, left!, right!) { Offset = offset };
            }

            return left;
        }

        private PrExNode? ParseMultiplicative()
        {
            PrExNode? left = ParseUnary();
            while (_error is null && IsOperator("*", "/", "%"))
            {
                string op = _current.Text;
                int offset = _current.Offset;
                Advance();
                PrExNode? right = ParseUnary();
                if (_error is not null) return null;
                left = new PrExBinary(op, left!, right!) { Offset = offset };
            }

            return left;
        }

        private PrExNode? ParseUnary()
        {
            if (IsOperator("!", "-"))
            {
                string op = _current.Text;
                int offset = _current.Offset;
                Advance();
                PrExNode? operand = ParseUnary();
                if (_error is not null) return null;
                return new PrExUnary(op, operand!) { Offset = offset };
            }

            return ParsePrimary();
        }

        private PrExNode? ParsePrimary()
        {
            int offset = _current.Offset;

            switch (_current.Kind)
            {
                case PrExTokenKind.Number:
                {
                    decimal value = decimal.Parse(_current.Text, NumberStyles.Number, CultureInfo.InvariantCulture);
                    Advance();
                    return new PrExLiteral(PrExValue.Number(value)) { Offset = offset };
                }

                case PrExTokenKind.String:
                {
                    string text = _current.Text;
                    Advance();
                    return new PrExLiteral(PrExValue.String(text)) { Offset = offset };
                }

                case PrExTokenKind.True:
                    Advance();
                    return new PrExLiteral(PrExValue.True) { Offset = offset };

                case PrExTokenKind.False:
                    Advance();
                    return new PrExLiteral(PrExValue.False) { Offset = offset };

                case PrExTokenKind.NullLiteral:
                    Advance();
                    return new PrExLiteral(PrExValue.Null) { Offset = offset };

                case PrExTokenKind.LParen:
                {
                    Advance();
                    PrExNode? inner = ParseOr();
                    if (_error is not null) return null;

                    if (_current.Kind != PrExTokenKind.RParen)
                    {
                        Fail($"Expected ')' but found {_current}.", _current.Offset);
                        return null;
                    }

                    Advance();
                    return inner;
                }

                case PrExTokenKind.Identifier:
                    return ParseCall();

                case PrExTokenKind.Root:
                    return ParsePath();

                case PrExTokenKind.End:
                    Fail("Expression ends where a value was expected.", offset);
                    return null;

                default:
                    Fail($"Expected a value but found {_current}.", offset);
                    return null;
            }
        }

        private PrExNode? ParseCall()
        {
            string name = _current.Text;
            int offset = _current.Offset;
            Advance();

            if (_current.Kind != PrExTokenKind.LParen)
            {
                // The only bare identifiers PrEx has are function names. A path must start with a
                // root, and saying so is far more useful than "unexpected token".
                Fail($"'{name}' is not a value. Paths start with '$', '$ctx' or '$run'; " +
                     $"functions are called as {name}(...).", offset);
                return null;
            }

            Advance();
            var arguments = new List<PrExNode>();

            if (_current.Kind != PrExTokenKind.RParen)
            {
                while (true)
                {
                    PrExNode? argument = ParseOr();
                    if (_error is not null) return null;
                    arguments.Add(argument!);

                    if (_current.Kind == PrExTokenKind.Comma)
                    {
                        Advance();
                        continue;
                    }

                    break;
                }
            }

            if (_current.Kind != PrExTokenKind.RParen)
            {
                Fail($"Expected ')' to close {name}(...) but found {_current}.", _current.Offset);
                return null;
            }

            Advance();
            return new PrExCall(name, arguments) { Offset = offset };
        }

        private PrExNode? ParsePath()
        {
            int offset = _current.Offset;
            string rootText = _current.Text;

            PrExRoot root;
            switch (rootText)
            {
                case "$": root = PrExRoot.Data; break;
                case "$ctx": root = PrExRoot.Context; break;
                case "$run": root = PrExRoot.Run; break;
                default:
                    Fail($"Unknown root '{rootText}'. Use '$', '$ctx' or '$run'.", offset);
                    return null;
            }

            Advance();
            var segments = new List<PrExSegment>();

            while (true)
            {
                if (_current.Kind == PrExTokenKind.Dot)
                {
                    Advance();
                    if (_current.Kind is not (PrExTokenKind.Identifier or PrExTokenKind.True
                        or PrExTokenKind.False or PrExTokenKind.NullLiteral))
                    {
                        Fail($"Expected a property name after '.' but found {_current}.", _current.Offset);
                        return null;
                    }

                    segments.Add(PrExSegment.Property(_current.Text));
                    Advance();
                    continue;
                }

                if (_current.Kind == PrExTokenKind.LBracket)
                {
                    Advance();
                    if (_current.Kind != PrExTokenKind.Number)
                    {
                        Fail($"Array index must be a literal integer but found {_current}.", _current.Offset);
                        return null;
                    }

                    if (!int.TryParse(_current.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    {
                        Fail($"'{_current.Text}' is not a valid array index.", _current.Offset);
                        return null;
                    }

                    segments.Add(PrExSegment.At(index));
                    Advance();

                    if (_current.Kind != PrExTokenKind.RBracket)
                    {
                        Fail($"Expected ']' but found {_current}.", _current.Offset);
                        return null;
                    }

                    Advance();
                    continue;
                }

                break;
            }

            return new PrExPath(root, segments) { Offset = offset };
        }
    }
}
