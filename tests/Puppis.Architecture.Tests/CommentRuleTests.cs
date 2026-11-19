namespace Puppis.Architecture.Tests;

public sealed class CommentRuleTests
{
    [Fact]
    public void NoSourceFileOrTestFileCarriesAComment()
    {
        Assert.Empty(CommentRules.PlacesThatCarryAComment(RepositoryLayout.Root));
    }

    [Fact]
    public void TheFoldersTheRuleReadsHoldSourceForItToRead()
    {
        Assert.NotEmpty(SourceScan.Files(RepositoryLayout.SourceDirectory));
        Assert.NotEmpty(SourceScan.Files(RepositoryLayout.TestDirectory));
    }
}
