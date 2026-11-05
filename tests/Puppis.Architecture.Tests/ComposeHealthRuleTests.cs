namespace Puppis.Architecture.Tests;

public sealed class ComposeHealthRuleTests
{
    [Fact]
    public void TheDriverIsAskedWhetherItAnswersThroughItsOwnProbeAndNothingDependsOnIt()
    {
        Assert.Empty(ComposeHealthRules.Violations(File.ReadAllText(ComposeHealthRules.ComposeFile)));
    }

    [Fact]
    public void TheRuleReadsTheServicesTheComposeFileActuallyHas()
    {
        Assert.Equal(
            ["driver"],
            ComposeHealthRules.ServiceNames(File.ReadAllText(ComposeHealthRules.ComposeFile)).Order(StringComparer.Ordinal));
    }
}
