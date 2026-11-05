namespace Puppis.Architecture.Tests;

public sealed class ReferenceRuleTests
{
    private static readonly ProjectGraph Graph = ProjectGraph.Load(RepositoryLayout.SourceDirectory);

    [Fact]
    public void TheDriverReferencesTheContractAndNothingBeyondIt()
    {
        Assert.Empty(Graph.ForbiddenReferencesOf("Puppis.Driver", "Puppis.Contracts"));
        Assert.Contains("Puppis.Contracts", Graph.Node("Puppis.Driver").ProjectReferences, StringComparer.Ordinal);
    }

    [Fact]
    public void TheDriverCarriesNoPackage()
    {
        Assert.Empty(Graph.Node("Puppis.Driver").PackageReferences);
    }

    [Fact]
    public void ContractsDependsOnNothing()
    {
        ProjectNode contracts = Graph.Node("Puppis.Contracts");

        Assert.Empty(contracts.ProjectReferences);
        Assert.Empty(contracts.PackageReferences);
    }

    [Fact]
    public void EveryExpectedProjectIsPresent()
    {
        Assert.Equal(
            [
                "Puppis.Contracts",
                "Puppis.Driver",
            ],
            Graph.ProjectNames.Order(StringComparer.Ordinal));
    }
}
