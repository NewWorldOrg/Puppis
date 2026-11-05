namespace Puppis.Architecture.Tests;

public sealed class RecordingLeadRuleTests
{
    [Fact]
    public void TheTimeTheDriverSpendsGettingToTheFirstByteIsTheHeadTheRecorderInTheAppWasWrittenAgainst()
    {
        Assert.Empty(RecordingLeadRules.WhereTheDriverMovedAWaitTheRecorderCountsOn(RepositoryLayout.SourceDirectory));
    }

    [Fact]
    public void TheTwoFilesTheRuleReadsAreOnDiskForItToRead()
    {
        foreach (string relative in
            new[]
            {
                RecordingLeadRules.DriverSettings,
                RecordingLeadRules.DriverSessions,
            })
        {
            Assert.True(
                File.Exists(Path.Combine(RepositoryLayout.SourceDirectory, relative)),
                $"{relative} has moved, so the rule above reads nothing and passes having compared nothing.");
        }
    }
}
