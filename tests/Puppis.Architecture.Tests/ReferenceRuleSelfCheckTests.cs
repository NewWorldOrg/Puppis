namespace Puppis.Architecture.Tests;

public sealed class ReferenceRuleSelfCheckTests
{
    private static ProjectGraph ViolatingGraph() => ProjectGraph.FromNodes(
        new ProjectNode("Puppis.Contracts", [], ["System.Text.Json"]),
        new ProjectNode("Puppis.Ledger", ["Puppis.Contracts"], []),
        new ProjectNode("Puppis.Driver", ["Puppis.Contracts", "Puppis.Ledger"], ["Microsoft.Extensions.Hosting"]));

    [Fact]
    public void DetectsAProjectOnDiskTheSolutionDoesNotList()
    {
        DirectoryInfo held = Directory.CreateTempSubdirectory();

        try
        {
            Laid(held, "Puppis.Kept.Tests");
            Laid(held, "Puppis.Forgotten.Tests");

            string solution = Path.Combine(held.FullName, "Puppis.slnx");
            File.WriteAllText(
                solution,
                """
                <Solution>
                  <Project Path="tests/Puppis.Kept.Tests/Puppis.Kept.Tests.csproj" />
                </Solution>
                """);

            Assert.Equal(
                ["tests/Puppis.Forgotten.Tests/Puppis.Forgotten.Tests.csproj"],
                ProjectGraph.ProjectsOutsideTheSolution(solution, Path.Combine(held.FullName, "tests")));
        }
        finally
        {
            held.Delete(recursive: true);
        }
    }

    private static void Laid(DirectoryInfo held, string project)
    {
        string directory = Path.Combine(held.FullName, "tests", project);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{project}.csproj"), "<Project />");
    }

    [Fact]
    public void DetectsADriverThatReachesBeyondTheContract()
    {
        IReadOnlyList<string> forbidden = ViolatingGraph().ForbiddenReferencesOf("Puppis.Driver", "Puppis.Contracts");

        Assert.Equal(["Puppis.Ledger"], forbidden);
    }

    [Fact]
    public void DetectsADriverThatTookOnAPackage()
    {
        Assert.NotEmpty(ViolatingGraph().Node("Puppis.Driver").PackageReferences);
    }

    [Fact]
    public void DetectsAContractThatTakesOnAPackage()
    {
        ProjectNode contracts = ViolatingGraph().Node("Puppis.Contracts");

        Assert.Empty(contracts.ProjectReferences);
        Assert.NotEmpty(contracts.PackageReferences);
    }

    [Fact]
    public void DetectsASourceFileThatNamesWhatItIsAskedAbout()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("puppis-source-scan-");

        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "Clean.cs"),
                "namespace Sample; public sealed record Vocabulary(string Name);");
            File.WriteAllText(
                Path.Combine(directory.FullName, "Leaky.cs"),
                "namespace Sample; public static class Leak { public const string P = DriverEndpoints.Health; }");

            Assert.Equal(
                ["Leaky.cs"],
                SourceScan.FilesMentioning(directory.FullName, "DriverEndpoints", "StatusCode"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DetectsATestProjectThatBorrowsFromAnotherTestProject()
    {
        var graph = ProjectGraph.FromNodes(
            new ProjectNode("Puppis.Contracts", [], []),
            new ProjectNode("Puppis.Contracts.Tests", ["Puppis.Contracts"], []),
            new ProjectNode("Puppis.Driver.Tests", ["Puppis.Contracts.Tests"], []));

        Assert.Equal(
            ["Puppis.Driver.Tests -> Puppis.Contracts.Tests"],
            graph.TestProjectsReferencingAnotherTestProject());
    }

    [Fact]
    public void ReadsTheProjectFilesOnDisk()
    {
        var graph = ProjectGraph.Load(RepositoryLayout.SourceDirectory);

        Assert.Contains("Puppis.Contracts", graph.Node("Puppis.Driver").ProjectReferences, StringComparer.Ordinal);
        Assert.Equal(["Puppis.Driver"], graph.DependentsOf("Puppis.Contracts"));
    }
}
