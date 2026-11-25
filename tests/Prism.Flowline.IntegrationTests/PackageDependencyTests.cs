using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Prism.Flowline.IntegrationTests;

/// <summary>
/// The package's dependency list, pinned. A host that embeds Flowline must load one copy of Agent
/// Framework and should be constrained by nothing else Flowline added, so every dependency is
/// declared here with the highest floor it may carry.
/// </summary>
/// <remarks>
/// A dependency's version in a <c>.nuspec</c> is a floor, and a floor above what the host has
/// forces the host to upgrade. Raising one here should therefore be a decision someone makes on
/// purpose, not something a package bump does on the way past.
/// </remarks>
public class PackageDependencyTests
{
    /// <summary>Every package the library may depend on, and the highest floor allowed for each.</summary>
    private static readonly Dictionary<string, Version> Ceilings = new(StringComparer.OrdinalIgnoreCase)
    {
        // The host's pin; one copy of Workflow, Executor and IWorkflowContext must cross the boundary.
        ["Microsoft.Agents.AI.Workflows"] = new(1, 28, 0),

        // Agent Framework 1.28.0's own minimum, so it adds no constraint of its own.
        ["Microsoft.Extensions.AI.Abstractions"] = new(10, 11, 0),

        // The DSL's schema validation.
        ["JsonSchema.Net"] = new(7, 3, 4),
    };

    /// <summary>
    /// Supplies Microsoft.Extensions.* itself, which is why none of them appear as packages.
    /// </summary>
    private static readonly string[] FrameworkReferences = ["Microsoft.AspNetCore.App"];

    [Fact]
    public async Task The_package_depends_on_nothing_beyond_its_declared_floor()
    {
        XDocument nuspec = await PackAndReadNuspecAsync();

        XElement[] groups = Descendants(nuspec, "dependencies").SelectMany(d => Elements(d, "group")).ToArray();
        groups.Select(g => (string?)g.Attribute("targetFramework"))
            .Should().Equal(["net10.0"], "the library targets .NET 10 only");

        foreach (XElement dependency in groups.SelectMany(g => Elements(g, "dependency")))
        {
            string id = (string)dependency.Attribute("id")!;
            Version floor = FloorOf((string)dependency.Attribute("version")!);

            Ceilings.Should().ContainKey(id, $"'{id}' is not a dependency the package is allowed to carry");
            floor.Should().BeLessThanOrEqualTo(Ceilings[id],
                $"'{id}' {floor} is above its ceiling; raise the ceiling here deliberately if that is intended");
        }

        Descendants(nuspec, "frameworkReference").Select(f => (string?)f.Attribute("name"))
            .Should().BeEquivalentTo(FrameworkReferences);
    }

    /// <summary>
    /// Packs the library already built for this test run's configuration, so the package read is the
    /// one this build would ship.
    /// </summary>
    private static async Task<XDocument> PackAndReadNuspecAsync()
    {
        string root = RepositoryRoot();
        string configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
        string output = Path.Combine(Path.GetTempPath(), $"prism-package-check-{Guid.NewGuid():N}");

        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                ArgumentList =
                {
                    "pack", Path.Combine(root, "src", "Prism.Flowline", "Prism.Flowline.csproj"),
                    "--configuration", configuration, "--no-build", "--nologo", "--output", output
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root
            };

            using Process pack = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            Task<string> stdout = pack.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = pack.StandardError.ReadToEndAsync(timeout.Token);
            await pack.WaitForExitAsync(timeout.Token);

            pack.ExitCode.Should().Be(0, $"dotnet pack failed:\n{await stdout}\n{await stderr}");

            string package = Directory.GetFiles(output, "Prism.Flowline.*.nupkg").Single();
            using ZipArchive archive = ZipFile.OpenRead(package);
            ZipArchiveEntry entry = archive.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));

            await using Stream stream = entry.Open();
            return await XDocument.LoadAsync(stream, LoadOptions.None, timeout.Token);
        }
        finally
        {
            try
            {
                if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is untidy, not a failure.
            }
        }
    }

    /// <summary>"1.28.0" or "[1.28.0, )": either way the lower bound is what a host must meet.</summary>
    private static Version FloorOf(string range)
        => Version.Parse(range.Trim('[', '(', ' ').Split(',')[0].Trim());

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Prism.Flowline.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root above " + AppContext.BaseDirectory);
    }

    // The nuspec namespace changes with the schema version NuGet writes; match on local names.
    private static IEnumerable<XElement> Descendants(XContainer container, string name)
        => container.Descendants().Where(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Elements(XContainer container, string name)
        => container.Elements().Where(e => e.Name.LocalName == name);
}
