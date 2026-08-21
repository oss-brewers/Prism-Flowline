using Prism.Flowline.Dsl.Expressions;
using FluentAssertions;
using Xunit;

namespace Prism.Flowline.DslTests;

public class PrExValidatorTests
{
    [Theory]
    [InlineData("$.total > 0")]
    [InlineData("has($.a) && lower($.b) == 'x'")]
    [InlineData("coalesce($.a, $.b, $.c, 'fallback')")]
    [InlineData("matches($.sku, '^[A-Z]+$')")]
    public void Valid_expressions_have_no_issues(string expression)
        => PrExValidator.Check(expression).IsValid.Should().BeTrue();

    [Fact]
    public void Unknown_function_is_reported()
    {
        ExpressionFacts facts = PrExValidator.Check("lookupCustomer($.id)");

        facts.IsValid.Should().BeFalse();
        facts.Issues.Should().ContainSingle()
            .Which.Message.Should().Contain("Unknown function 'lookupCustomer'");
    }

    [Theory]
    [InlineData("lenn($.a)", "len")]
    [InlineData("upperr($.a)", "upper")]
    [InlineData("startswith($.a, 'x')", "startsWith")]
    [InlineData("containz($.a, 'x')", "contains")]
    public void Near_misses_get_a_suggestion(string expression, string expected)
        => PrExValidator.Check(expression).Issues[0].Suggestion.Should().Contain(expected);

    [Fact]
    public void A_wildly_wrong_name_gets_no_suggestion()
        => PrExValidator.Check("zzzzzzzzzzzz($.a)").Issues[0].Suggestion.Should().BeNull();

    [Theory]
    [InlineData("len()", "1")]
    [InlineData("len($.a, $.b)", "1")]
    [InlineData("contains($.a)", "2")]
    [InlineData("coalesce()", "at least 1")]
    public void Wrong_arity_is_reported(string expression, string expectedArity)
    {
        ExpressionFacts facts = PrExValidator.Check(expression);

        facts.IsValid.Should().BeFalse();
        facts.Issues[0].Message.Should().Contain(expectedArity);
    }

    [Fact]
    public void Variadic_coalesce_accepts_many_arguments()
        => PrExValidator.Check("coalesce($.a, $.b, $.c, $.d, $.e, 'z')").IsValid.Should().BeTrue();

    /// <summary>
    /// A pattern assembled at run time cannot be reviewed by reading the document, and an unbounded
    /// pattern is the one genuinely dangerous construct in the grammar.
    /// </summary>
    [Fact]
    public void Matches_requires_a_literal_pattern()
    {
        ExpressionFacts facts = PrExValidator.Check("matches($.a, $.pattern)");

        facts.IsValid.Should().BeFalse();
        facts.Issues[0].Message.Should().Contain("must be a string literal");
    }

    [Fact]
    public void Matches_rejects_an_invalid_pattern()
        => PrExValidator.Check("matches($.a, '[')").Issues[0].Message
            .Should().Contain("Invalid regular expression");

    [Fact]
    public void Determinism_is_reported()
    {
        PrExValidator.Check("$.total > 0").IsDeterministic.Should().BeTrue();
        PrExValidator.Check("$run.attempt > 1").IsDeterministic.Should().BeTrue();
        PrExValidator.Check("$run.now > '2020'").IsDeterministic.Should().BeFalse();
        PrExValidator.Check("has($.a) && $run.now != null").IsDeterministic.Should().BeFalse();
    }

    [Fact]
    public void Depth_is_reported()
        => PrExValidator.Check("1 + 2 + 3").Depth.Should().Be(3);

    [Fact]
    public void Depth_beyond_a_custom_limit_is_reported()
    {
        ExpressionFacts facts = PrExValidator.Check("1 + 2 + 3 + 4", maxDepth: 2);

        facts.IsValid.Should().BeFalse();
        facts.Issues[0].Message.Should().Contain("the limit is 2");
    }

    [Fact]
    public void A_parse_error_becomes_an_issue()
    {
        ExpressionFacts facts = PrExValidator.Check("$.a = 1");

        facts.IsValid.Should().BeFalse();
        facts.Issues[0].Message.Should().Contain("'=='");
        facts.Issues[0].Offset.Should().Be(4);
    }

    [Fact]
    public void Issues_from_nested_calls_are_collected()
    {
        ExpressionFacts facts = PrExValidator.Check("lower(nope($.a)) == upper(alsoNope($.b))");
        facts.Issues.Should().HaveCount(2);
    }

    [Fact]
    public void Edit_distance_is_case_insensitive()
        => PrExFunctions.EditDistance("STARTSWITH", "startsWith").Should().Be(0);

    [Fact]
    public void Every_registered_function_is_self_consistent()
    {
        foreach (string name in PrExFunctions.Names)
        {
            PrExFunctions.TryGet(name, out PrExFunction function).Should().BeTrue();
            function.Name.Should().Be(name);
            function.MinArguments.Should().BeGreaterThan(0);
            function.MaxArguments.Should().BeGreaterThanOrEqualTo(function.MinArguments);
        }
    }
}
