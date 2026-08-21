using System.Globalization;
using System.Text;

namespace Prism.Flowline.Dsl.Expressions;

internal enum PrExTokenKind
{
    Root,           // $ | $ctx | $run
    Identifier,
    Number,
    String,
    True,
    False,
    NullLiteral,
    Dot,
    LBracket,
    RBracket,
    LParen,
    RParen,
    Comma,
    Operator,
    End,
    Invalid
}

internal readonly record struct PrExToken(PrExTokenKind Kind, string Text, int Offset)
{
    public override string ToString() => Kind == PrExTokenKind.End ? "end of expression" : $"'{Text}'";
}

/// <summary>
/// Hand-written lexer. Every token carries its source offset, because the diagnostics the DSL lives
/// or dies by are only as precise as the positions they are built from.
/// </summary>
internal sealed class PrExLexer
{
    private readonly string _source;
    private int _index;

    internal PrExLexer(string source) => _source = source;

    /// <summary>Set when a token comes back <see cref="PrExTokenKind.Invalid"/>.</summary>
    internal string? Error { get; private set; }

    internal PrExToken Next()
    {
        SkipWhitespace();

        if (_index >= _source.Length)
        {
            return new PrExToken(PrExTokenKind.End, string.Empty, _index);
        }

        int start = _index;
        char c = _source[_index];

        if (c == '$')
        {
            _index++;
            while (_index < _source.Length && (char.IsLetterOrDigit(_source[_index]) || _source[_index] == '_'))
            {
                _index++;
            }

            return new PrExToken(PrExTokenKind.Root, _source[start.._index], start);
        }

        if (char.IsLetter(c) || c == '_')
        {
            while (_index < _source.Length && (char.IsLetterOrDigit(_source[_index]) || _source[_index] == '_'))
            {
                _index++;
            }

            string word = _source[start.._index];
            return word switch
            {
                "true" => new PrExToken(PrExTokenKind.True, word, start),
                "false" => new PrExToken(PrExTokenKind.False, word, start),
                "null" => new PrExToken(PrExTokenKind.NullLiteral, word, start),
                _ => new PrExToken(PrExTokenKind.Identifier, word, start)
            };
        }

        if (char.IsDigit(c))
        {
            return ReadNumber(start);
        }

        if (c is '\'' or '"')
        {
            return ReadString(start, c);
        }

        return ReadPunctuation(start, c);
    }

    private void SkipWhitespace()
    {
        while (_index < _source.Length && char.IsWhiteSpace(_source[_index]))
        {
            _index++;
        }
    }

    private PrExToken ReadNumber(int start)
    {
        while (_index < _source.Length && char.IsDigit(_source[_index]))
        {
            _index++;
        }

        if (_index < _source.Length && _source[_index] == '.' &&
            _index + 1 < _source.Length && char.IsDigit(_source[_index + 1]))
        {
            _index++;
            while (_index < _source.Length && char.IsDigit(_source[_index]))
            {
                _index++;
            }
        }

        string text = _source[start.._index];

        // Rejected here rather than at evaluation: a literal too large for decimal is a mistake in
        // the document, and the author should hear about it while editing it.
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            Error = $"Number '{text}' is out of range.";
            return new PrExToken(PrExTokenKind.Invalid, text, start);
        }

        return new PrExToken(PrExTokenKind.Number, text, start);
    }

    private PrExToken ReadString(int start, char quote)
    {
        _index++;   // opening quote
        var builder = new StringBuilder();

        while (_index < _source.Length)
        {
            char c = _source[_index];

            if (c == '\\')
            {
                if (_index + 1 >= _source.Length)
                {
                    break;
                }

                char escaped = _source[_index + 1];
                builder.Append(escaped switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '\\' => '\\',
                    '\'' => '\'',
                    '"' => '"',
                    _ => escaped
                });
                _index += 2;
                continue;
            }

            if (c == quote)
            {
                _index++;
                return new PrExToken(PrExTokenKind.String, builder.ToString(), start);
            }

            builder.Append(c);
            _index++;
        }

        Error = "Unterminated string literal.";
        return new PrExToken(PrExTokenKind.Invalid, _source[start..], start);
    }

    private PrExToken ReadPunctuation(int start, char c)
    {
        switch (c)
        {
            case '.': _index++; return new PrExToken(PrExTokenKind.Dot, ".", start);
            case '[': _index++; return new PrExToken(PrExTokenKind.LBracket, "[", start);
            case ']': _index++; return new PrExToken(PrExTokenKind.RBracket, "]", start);
            case '(': _index++; return new PrExToken(PrExTokenKind.LParen, "(", start);
            case ')': _index++; return new PrExToken(PrExTokenKind.RParen, ")", start);
            case ',': _index++; return new PrExToken(PrExTokenKind.Comma, ",", start);
        }

        // Two-character operators first, so '==' never lexes as two '=' tokens.
        if (_index + 1 < _source.Length)
        {
            string pair = _source.Substring(_index, 2);
            if (pair is "==" or "!=" or "<=" or ">=" or "&&" or "||")
            {
                _index += 2;
                return new PrExToken(PrExTokenKind.Operator, pair, start);
            }
        }

        if (c is '<' or '>' or '!' or '+' or '-' or '*' or '/' or '%')
        {
            _index++;
            return new PrExToken(PrExTokenKind.Operator, c.ToString(), start);
        }

        // A bare '=' is the classic slip for '=='; naming it is worth more than "unexpected character".
        Error = c == '='
            ? "'=' is not an operator. Use '==' to compare."
            : $"Unexpected character '{c}'.";

        _index++;
        return new PrExToken(PrExTokenKind.Invalid, c.ToString(), start);
    }
}
