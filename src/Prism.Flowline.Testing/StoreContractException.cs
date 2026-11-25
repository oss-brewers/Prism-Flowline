namespace Prism.Flowline.Testing;

/// <summary>A store broke a rule of its contract.</summary>
/// <remarks>
/// The message names the case and the rule in words, so a failure reads the same in any test
/// runner: <c>Claim_hands_each_instance_to_one_replica_under_contention: no instance is leased to two
/// replicas at once. 'inst-…' was claimed by replica-1 and replica-4.</c>
/// </remarks>
public sealed class StoreContractException : Exception
{
    public StoreContractException(string rule, string? detail = null, Exception? innerException = null)
        : this(null, rule, detail, innerException)
    {
    }

    internal StoreContractException(string? caseName, string rule, string? detail, Exception? innerException)
        : base(Compose(caseName, rule, detail), innerException)
    {
        CaseName = caseName;
        Rule = rule;
        Detail = detail;
    }

    /// <summary>The case that failed. Null only while the exception is still inside the case.</summary>
    public string? CaseName { get; }

    /// <summary>The rule the store broke, as a sentence.</summary>
    public string Rule { get; }

    /// <summary>What was observed instead.</summary>
    public string? Detail { get; }

    internal StoreContractException InCase(string caseName) => new(caseName, Rule, Detail, this);

    private static string Compose(string? caseName, string rule, string? detail)
    {
        string body = detail is { Length: > 0 } ? $"{rule}. {detail}" : $"{rule}.";
        return caseName is null ? body : $"{caseName}: {body}";
    }
}
