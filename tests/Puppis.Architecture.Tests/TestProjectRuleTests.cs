namespace Puppis.Architecture.Tests;

public sealed class TestProjectRuleTests
{
    private static readonly ProjectGraph Graph = ProjectGraph.Load(
        RepositoryLayout.SourceDirectory,
        RepositoryLayout.TestDirectory);

    [Fact]
    public void EveryProjectOnDiskIsOneTheSolutionBuilds()
    {
        Assert.Empty(ProjectGraph.ProjectsOutsideTheSolution(
            RepositoryLayout.SolutionFile,
            RepositoryLayout.SourceDirectory,
            RepositoryLayout.TestDirectory));
    }

    [Fact]
    public void NoTestProjectReferencesAnotherTestProject()
    {
        Assert.Empty(Graph.TestProjectsReferencingAnotherTestProject());
    }

    [Fact]
    public void EachProductionProjectHasATestProjectAndTheRulesHaveTheirs()
    {
        Assert.Equal(
            [
                "Puppis.Architecture.Tests",
                "Puppis.Contracts.Tests",
                "Puppis.Driver.Tests",
            ],
            Graph.ProjectNames
                .Where(name => name.EndsWith(".Tests", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheRulesReferenceNoProductionProject()
    {
        Assert.Empty(Graph.Node("Puppis.Architecture.Tests").ProjectReferences);
    }
}
