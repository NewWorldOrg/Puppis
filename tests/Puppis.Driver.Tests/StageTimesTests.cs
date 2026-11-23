using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;

namespace Puppis.Driver.Tests;

public sealed class StageTimesTests
{
    private static (StageTimes Stages, SteppedClock Clock) Started()
    {
        var clock = new SteppedClock(UsbTestSupport.Start);

        return (new StageTimes(clock), clock);
    }

    private static DevicePacing PacingOn(SteppedClock clock) => new(clock, clock.Advance);

    [Fact]
    public void EachStageTakesTheTimeSinceTheStageBeforeIt()
    {
        (StageTimes stages, SteppedClock clock) = Started();

        clock.Advance(TimeSpan.FromMilliseconds(12.5));
        stages.Mark("frontend");
        clock.Advance(TimeSpan.FromMilliseconds(300));
        stages.Mark("lock", "31 polls");

        Assert.Equal("312.5 ms in all (frontend 12.5 ms, lock 300.0 ms/31 polls)", stages.ToString());
    }

    [Fact]
    public void AStageMarkedAgainAddsToWhatItAlreadyTook()
    {
        (StageTimes stages, SteppedClock clock) = Started();

        clock.Advance(TimeSpan.FromMilliseconds(10));
        stages.Mark("temperature");
        clock.Advance(TimeSpan.FromMilliseconds(5));
        stages.Mark("PLL");
        clock.Advance(TimeSpan.FromMilliseconds(20));
        stages.Mark("temperature");

        Assert.Equal("35.0 ms in all (temperature 30.0 ms, PLL 5.0 ms)", stages.ToString());
    }

    [Fact]
    public void RequestsAndSleepsAreCountedPerStageOnceThereIsSomethingToCount()
    {
        (StageTimes stages, SteppedClock clock) = Started();
        DevicePacing pacing = PacingOn(clock);
        long requests = 40;

        clock.Advance(TimeSpan.FromMilliseconds(1));
        stages.Mark("before");
        stages.Count(() => requests, pacing);
        requests += 7;
        pacing.SleepMilliseconds(20);
        pacing.SleepMilliseconds(100);
        stages.Mark("registers");
        requests += 3;
        stages.Mark("lock", "1 looks");

        Assert.Equal(
            "121.0 ms in all (before 1.0 ms, registers 120.0 ms/7 requests/120.0 ms in 2 sleeps, lock 0.0 ms/3 requests/0.0 ms in 0 sleeps/1 looks)",
            stages.ToString()
        );
    }

    [Fact]
    public void TheTotalStopsAtTheLastStageSoTheStagesAddUpToIt()
    {
        (StageTimes stages, SteppedClock clock) = Started();

        clock.Advance(TimeSpan.FromMilliseconds(4));
        stages.Mark("tune");
        clock.Advance(TimeSpan.FromSeconds(9));

        Assert.Equal(TimeSpan.FromMilliseconds(4), stages.Total);
    }

    [Fact]
    public void ANoteFollowsTheStages()
    {
        (StageTimes stages, SteppedClock clock) = Started();

        clock.Advance(TimeSpan.FromMilliseconds(2));
        stages.Mark("search");
        stages.Note("the first search failed at sync");

        Assert.Equal("2.0 ms in all (search 2.0 ms); the first search failed at sync", stages.ToString());
    }
}

public sealed class DevicePacingCountTests
{
    [Fact]
    public void EverySleepIsCountedAndStillSlept()
    {
        (DevicePacing pacing, _, List<TimeSpan> slept) = UsbTestSupport.Pacing();

        pacing.SleepMilliseconds(20);
        pacing.Sleep(TimeSpan.FromMilliseconds(100));

        Assert.Equal([TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100)], slept);
        Assert.Equal(2, pacing.Sleeps);
        Assert.Equal(TimeSpan.FromMilliseconds(120), pacing.Slept);
    }

    [Fact]
    public void APacingCountedOnItsOwnSleepsTheSameWayAndKeepsItsOwnCount()
    {
        (DevicePacing pacing, ManualTimeProvider clock, List<TimeSpan> slept) = UsbTestSupport.Pacing();
        DevicePacing own = pacing.CountedOnItsOwn();

        pacing.SleepMilliseconds(5);
        own.SleepMilliseconds(30);

        Assert.Equal([TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(30)], slept);
        Assert.Equal(1, pacing.Sleeps);
        Assert.Equal(1, own.Sleeps);
        Assert.Equal(TimeSpan.FromMilliseconds(30), own.Slept);
        Assert.Equal(UsbTestSupport.Start + TimeSpan.FromMilliseconds(35), own.Now);
        Assert.Same(clock, own.Clock);
    }
}

public sealed class TunerOpeningDescriptionTests
{
    [Fact]
    public void ATerrestrialOpeningNamesTheDeviceTheHardwareTheKindThePurposeAndThePhysicalChannel()
    {
        var opening = new TunerOpening("tuner-a", DeviceKind.Terrestrial, DvbChannel.Terrestrial(27), SessionPurpose.Live);

        Assert.Equal("devices['tuner-a'] (DVB, terrestrial) for live on physical channel 27", opening.On("DVB"));
    }

    [Fact]
    public void ASatelliteOpeningNamesTheSlotAndTheTransportStream()
    {
        var broadcast = new TunerOpening("tuner-b", DeviceKind.Satellite, DvbChannel.BroadcastSatellite(15, 16625), SessionPurpose.Recording);
        var communication = new TunerOpening("tuner-b", DeviceKind.Satellite, DvbChannel.CommunicationSatellite(12), SessionPurpose.SurveyNow);

        Assert.Equal("devices['tuner-b'] (DVB, satellite) for recording on BS slot 15 transport stream 16625", broadcast.On("DVB"));
        Assert.Equal("devices['tuner-b'] (PIX-DT295, satellite) for surveyNow on CS slot 12", communication.On("PIX-DT295"));
    }

    [Fact]
    public void AnOpeningNobodyGaveAPurposeSaysSo()
    {
        var opening = new TunerOpening("tuner-a", DeviceKind.Terrestrial, DvbChannel.Terrestrial(22), null);

        Assert.Equal("devices['tuner-a'] (CECH-ZD1, terrestrial) for an unstated purpose on physical channel 22", opening.On("CECH-ZD1"));
    }
}
