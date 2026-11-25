using Prism.Flowline.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Prism.Flowline.TestSupport;

/// <summary>A store contract case's name as theory data, shown in full in the test name.</summary>
/// <remarks>
/// xunit cuts a string argument to 50 characters when it names a theory row, and most case names are
/// longer than that, so a plain <c>string</c> row would read
/// <c>Keeps_the_contract(case: "A_duplicate_idempotency_key_for_one_tenant_is_refu"···)</c>. A type
/// with its own <see cref="ToString"/> is shown whole; being serializable keeps every row its own
/// test in discovery. Linked into each test project that runs a contract.
/// </remarks>
public sealed class ContractCaseName : IXunitSerializable
{
    [Obsolete("For the xunit serializer only.")]
    public ContractCaseName()
    {
    }

    private ContractCaseName(string value) => Value = value;

    public string Value { get; private set; } = string.Empty;

    public static TheoryData<ContractCaseName> Of<TContract>() where TContract : class
        => [.. ContractCase.NamesOf<TContract>().Select(name => new ContractCaseName(name))];

    public void Deserialize(IXunitSerializationInfo info) => Value = info.GetValue<string>(nameof(Value));

    public void Serialize(IXunitSerializationInfo info) => info.AddValue(nameof(Value), Value);

    public override string ToString() => Value;
}
