namespace Puppis.Architecture.Tests;

public sealed class AuthenticationBypassRuleTests
{
    [Fact]
    public void NoProductionSourceReadsAnIdentityHandedToItByAnEdge()
    {
        Assert.Empty(SourceScan.FilesMentioning(
            RepositoryLayout.SourceDirectory,
            [.. AuthenticationBypasses.EdgeIdentityHeaders]));
    }

    [Fact]
    public void NoProductionSourceNamesAnExemptionFromADenialBecauseThereIsNoDenialToExemptItselfFrom()
    {
        Assert.Empty(SourceScan.FilesMentioning(
            RepositoryLayout.SourceDirectory,
            [.. AuthenticationBypasses.AnonymityAttributes]));
    }

    [Fact]
    public void TheDriverAsksNobodyWhoTheyAreBecauseTheSocketPermissionsAreTheWholeGate()
    {
        Assert.Empty(SourceScan.FilesMentioning(
            Path.Combine(RepositoryLayout.SourceDirectory, "Puppis.Driver"),
            [.. AuthenticationBypasses.AskingWhoIsCalling]));
    }
}
