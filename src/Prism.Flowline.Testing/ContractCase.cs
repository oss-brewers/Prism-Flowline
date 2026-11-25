using System.Reflection;

namespace Prism.Flowline.Testing;

/// <summary>One rule of a store contract, by name, ready to run.</summary>
/// <param name="Name">The case's method name on its contract class. Stable: a runner may key on it.</param>
/// <param name="Run">
/// Runs the case against a store the contract's factory builds for it. Completes when the store
/// keeps the rule; faults with <see cref="StoreContractException"/> when it does not.
/// </param>
public sealed record ContractCase(string Name, Func<Task> Run)
{
    /// <summary>The names of every case <typeparamref name="TContract"/> declares, in declaration order.</summary>
    /// <remarks>
    /// Needs no instance, and so no store: a test framework that wants its case list from a static
    /// member — an xunit <c>MemberData</c>, an NUnit <c>TestCaseSource</c> — can have it before the
    /// fixture that builds the store exists.
    /// </remarks>
    public static IReadOnlyList<string> NamesOf<TContract>() where TContract : class
        => [.. ContractCases.Methods(typeof(TContract)).Select(m => m.Name)];

    public override string ToString() => Name;
}

public static class ContractCaseExtensions
{
    /// <summary>The case called <paramref name="name"/>.</summary>
    /// <remarks>
    /// For runners that enumerate names rather than delegates — an xunit theory, for instance, whose
    /// data must be serializable for each case to show up as its own test.
    /// </remarks>
    public static ContractCase Named(this IEnumerable<ContractCase> cases, string name)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return cases.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
            ?? throw new ArgumentException($"The contract has no case named '{name}'.", nameof(name));
    }
}

/// <summary>Builds a contract's <c>Cases</c> list from the contract itself.</summary>
internal static class ContractCases
{
    /// <remarks>
    /// Every public, parameterless, <see cref="Task"/>-returning method the contract declares is a
    /// case, in declaration order. Discovered rather than listed by hand, so a case added to a
    /// contract cannot be forgotten by the runners that enumerate <c>Cases</c>.
    /// </remarks>
    public static IReadOnlyList<ContractCase> Discover(object contract)
        => [.. Methods(contract.GetType()).Select(m => new ContractCase(m.Name, m.CreateDelegate<Func<Task>>(contract)))];

    public static IEnumerable<MethodInfo> Methods(Type contract)
        => contract
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(Task) && m.GetParameters().Length == 0 && !m.IsSpecialName)
            .OrderBy(m => m.MetadataToken);
}
