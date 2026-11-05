namespace Puppis.Architecture.Tests;

public sealed class MeasurementRuleTests
{
    [Fact]
    public void OneLoopCountsWhatTheTunerGaveAndItIsTheOneThatWritesTheRecording()
    {
        Assert.Equal(
            ["Puppis.Driver/Sessions/TunerSession.cs"],
            MeasurementRules.PlacesThatCountWhatTheTunerGave(RepositoryLayout.SourceDirectory));
    }

    [Fact]
    public void EveryPlaceThatShowsTheMarksOfAParserIsOneOfTheSixNamedHere()
    {
        Assert.Equal(
            MeasurementRules.AllowedToTakeTheStreamApart,
            MeasurementRules.PlacesThatShowTheMarksOfAParser(RepositoryLayout.SourceDirectory));
    }

    [Fact]
    public void TheContractCarriesNoParserBecauseItIsVocabularyAndNothingElse()
    {
        Assert.DoesNotContain(
            MeasurementRules.AllowedToTakeTheStreamApart,
            file => file.StartsWith("Puppis.Contracts/", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRecordingWriterTakesBytesAndReadsNoPacketOfItsOwn()
    {
        Assert.Empty(
            MeasurementRules.PacketsReadInsideTheRecordingWriter(RepositoryLayout.SourceDirectory));
    }

    [Fact]
    public void TheWriterThoseRulesGuardIsOnDiskForThemToHaveRead()
    {
        Assert.NotEmpty(
            Directory.EnumerateFiles(
                Path.Combine(RepositoryLayout.SourceDirectory, "Puppis.Driver", "Recording"),
                "*.cs",
                SearchOption.AllDirectories));
    }
}
