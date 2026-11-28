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
        ServiceTableMemory memory = new();

        HeadHold hold = HeadHolds.For(SessionPurpose.Live, new TuningRequest(TunerKind.Satellite, 13, 7), memory);

        Assert.Equal(HeadHoldKind.WhatIsHeld, hold.Kind);
        LiveService service = Assert.IsType<LiveService>(hold.Service);
        Assert.Equal(new ServiceTablesKey(TunerKind.Satellite, 13, 7), service.Key);
        Assert.Same(memory, service.Memory);
        Assert.True(HeadHolds.ReadsFromTheLock(hold));
    }

    [Fact]
    public void ALiveViewerWhoNamesNoServiceWaitsForWhatIsHeldAsBefore()
    {
        Assert.Equal(
            HeadHold.WhatIsHeld,
            HeadHolds.For(SessionPurpose.Live, new TuningRequest(TunerKind.Terrestrial, 13), new ServiceTableMemory())
        );
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
            HeadHolds.For(purpose, new TuningRequest(TunerKind.Terrestrial, 13, 7), new ServiceTableMemory())
        );
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
