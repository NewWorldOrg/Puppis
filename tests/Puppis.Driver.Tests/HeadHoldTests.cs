using Puppis.Contracts;
using Puppis.Driver.Descrambling;

namespace Puppis.Driver.Tests;

public sealed class HeadHoldTests
{
    [Fact]
    public void ALiveViewerWaitsForNoMoreThanWhatIsHeld()
    {
        Assert.Equal(HeadHold.WhatIsHeld, HeadHolds.For(SessionPurpose.Live));
    }

    [Theory]
    [InlineData(SessionPurpose.Recording)]
    [InlineData(SessionPurpose.Survey)]
    [InlineData(SessionPurpose.SurveyNow)]
    [InlineData(SessionPurpose.Scan)]
    [InlineData(SessionPurpose.Logo)]
    public void EveryOtherPurposeWaitsForEveryProgramme(SessionPurpose purpose)
    {
        Assert.Equal(HeadHold.EveryProgramme, HeadHolds.For(purpose));
    }

    [Fact]
    public void ALiveViewerWhoNamesAServiceIsHeldForThatServiceOnItsTunerKindAndChannel()
    {
        TableMemory memory = new();

        HeadHold hold = HeadHolds.For(SessionPurpose.Live, new TuningRequest(TunerKind.Satellite, 13, 7), memory);

        Assert.Equal(HeadHoldKind.WhatIsHeld, hold.Kind);
        LiveTables service = Assert.IsType<LiveTables>(hold.Tables);
        Assert.Equal(new TablesKey(TunerKind.Satellite, 13, 7), service.Key);
        Assert.Same(memory, service.Memory);
        Assert.True(HeadHolds.ReadsFromTheLock(hold));
    }

    [Fact]
    public void ALiveViewerWhoNamesNoServiceWaitsForWhatIsHeldAndHasTheTablesOfItsWholeChannelKept()
    {
        HeadHold hold = HeadHolds.For(SessionPurpose.Live, new TuningRequest(TunerKind.Terrestrial, 13), new TableMemory());

        Assert.Equal(HeadHoldKind.WhatIsHeld, hold.Kind);
        Assert.Equal(new TablesKey(TunerKind.Terrestrial, 13, null), Assert.IsType<LiveTables>(hold.Tables).Key);
    }

    [Fact]
    public void ALiveViewerTunedByTypeHasItsTablesKeptUnderTheTuneItGave()
    {
        TuneParams tune = TuneParams.Terrestrial(13);

        HeadHold hold = HeadHolds.For(SessionPurpose.Live, new TuningRequest(TunerKind.Terrestrial, 99), tune, new TableMemory());

        Assert.Equal(new TablesKey(TunerKind.Terrestrial, 13, null, tune), Assert.IsType<LiveTables>(hold.Tables).Key);
    }

    [Theory]
    [InlineData(SessionPurpose.Recording)]
    [InlineData(SessionPurpose.Survey)]
    [InlineData(SessionPurpose.SurveyNow)]
    [InlineData(SessionPurpose.Scan)]
    [InlineData(SessionPurpose.Logo)]
    public void EveryOtherPurposeWaitsForEveryProgrammeWhateverServiceItNames(SessionPurpose purpose)
    {
        Assert.Equal(
            HeadHold.EveryProgramme,
            HeadHolds.For(purpose, new TuningRequest(TunerKind.Terrestrial, 13, 7), new TableMemory())
        );
    }

    [Fact]
    public void AHoldOpenedPartWayThroughTheStreamPutsNoTablesInFrontAndIsOtherwiseTheSame()
    {
        HeadHold hold = HeadHolds.For(SessionPurpose.Live, new TuningRequest(TunerKind.Terrestrial, 13, 7), new TableMemory());

        HeadHold later = hold.FromTheMiddle();

        Assert.True(hold.Tables!.PutsTablesInFront);
        Assert.False(later.Tables!.PutsTablesInFront);
        Assert.Equal(hold.Tables.Key, later.Tables.Key);
        Assert.Equal(HeadHold.WhatIsHeld, HeadHold.WhatIsHeld.FromTheMiddle());
        Assert.Equal(HeadHold.EveryProgramme, HeadHold.EveryProgramme.FromTheMiddle());
    }

    [Fact]
    public void ALiveViewerIsReadFromTheLock()
    {
        Assert.True(HeadHolds.ReadsFromTheLock(HeadHolds.For(SessionPurpose.Live)));
    }

    [Theory]
    [InlineData(SessionPurpose.Recording)]
    [InlineData(SessionPurpose.Survey)]
    [InlineData(SessionPurpose.SurveyNow)]
    [InlineData(SessionPurpose.Scan)]
    [InlineData(SessionPurpose.Logo)]
    public void EveryOtherPurposeIsReadOnlyOnceTheFrontendHasLocked(SessionPurpose purpose)
    {
        Assert.False(HeadHolds.ReadsFromTheLock(HeadHolds.For(purpose)));
    }
}
