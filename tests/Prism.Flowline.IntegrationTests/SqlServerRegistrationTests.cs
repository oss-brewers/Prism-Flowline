using Prism.Flowline.Stores.SqlServer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Prism.Flowline.IntegrationTests;

/// <summary>
/// The SQL Server stores cannot run in invariant globalization mode, and this test process runs in
/// it (Directory.Build.props), as the host did until it opted out.
/// </summary>
public class SqlServerRegistrationTests
{
    [Fact]
    public void Registering_the_sql_server_stores_in_invariant_globalization_mode_fails_at_startup()
    {
        AppContext.TryGetSwitch("System.Globalization.Invariant", out bool invariant).Should().BeTrue();
        invariant.Should().BeTrue("this suite is built with InvariantGlobalization, which is what makes it the witness");

        // Without the check, registration succeeds and the first query fails instead — in a host,
        // on whichever request first touches an instance.
        new ServiceCollection()
            .Invoking(services => services.AddSqlServerStores("Server=localhost;Database=prism"))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*InvariantGlobalization*");
    }
}
