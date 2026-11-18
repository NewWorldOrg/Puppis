using Microsoft.Extensions.Logging.Abstractions;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Ipc;
using Puppis.Driver.Reception;
using Puppis.Driver.Sessions;
using Puppis.Driver.Tuning;

namespace Puppis.Driver.Tests;

public sealed class IdleReceptionMeasurerTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 13, 21, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Deadlock = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Rested = IdleReceptionMeasurer.RestBeforeMeasuring;

    private static readonly TimeSpan Gap = IdleReceptionMeasurer.BetweenMeasurements;

    private static readonly TuningKey Here = new(TunerKind.Terrestrial, 31, 0);

    private static readonly TuningKey There = new(TunerKind.Terrestrial, 33, 0);

    private static readonly TuningKey Yonder = new(TunerKind.Terrestrial, 35, 0);

    private readonly ManualTimeProvider clock = new(Start);
    private readonly ReceptionBook book = new();
    private readonly MeasuredHardware hardware = new();
    private readonly CapturingLogger<IdleReceptionMeasurer> log = new();
    private DriverConfiguration configuration = Configured(true, ("north", DeviceKind.Terrestrial), ("south", DeviceKind.Terrestrial));
    private TunerSessionManager? made;

    private static DriverConfiguration Configured(bool measureWhileIdle, params (string Id, DeviceKind Kind)[] devices) =>
        new(
            "/run/carina/driver.sock",
            [new OutputRootSettings("primary", Path.GetTempPath())],
            6,
            new TunerSettings(TunerBackend.Fake, MeasureWhileIdle: measureWhileIdle),
            [.. devices.Select(device => new DeviceSettings(device.Id, device.Kind))]
        );

    private TunerSessionManager Manager(TimeSpan? grace = null) =>
        made ??= new TunerSessionManager(
            configuration,
            hardware,
            clock,
            NullLogger<TunerSessionManager>.Instance,
            tunerGrace: grace,
            deviceCheck: hardware,
            reception: book
        );

    private IdleReceptionMeasurer Measurer(TimeProvider? timers = null) =>
        new(configuration, Manager(), book, hardware, timers ?? clock, log, TimeSpan.Zero);

    private static ReceptionChannel On(TuningKey channel) => ReceptionChannel.Of(channel);

    private void Know(params TuningKey[] channels)
    {
        foreach (TuningKey channel in channels)
        {
            book.Know(channel);
        }
    }

    private void Measured(string deviceId, TuningKey channel, double decibels = 20) =>
        book.Note(deviceId, On(channel), new ReceptionResult(clock.GetUtcNow(), true, decibels), () => false);

    private static StartSessionRequest Live(string sessionId, TuningKey channel, string? deviceId = null) =>
        new()
        {
            SessionId = SessionId.Parse(sessionId),
            Purpose = SessionPurpose.Live,
            Tuning = new TuningRequest(TunerKind.Terrestrial, channel.PhysicalChannel, 50001),
            DeviceId = deviceId,
        };

    private static async Task<TunerSession> StartedAsync(Task<SessionStart> starting)
    {
        SessionStart start = await starting.WaitAsync(Deadlock);

        Assert.True(start.TryGetSession(out TunerSession? session), $"{start.Refusal}: {start.Detail}");

        return session;
    }

    private static async Task StopAsync(TunerSession session)
    {
        session.Stop();
        await session.Completion.WaitAsync(Deadlock);
    }

    private TunerAllocationView Table(string deviceId) =>
        Manager().Allocations().Single(tuner => tuner.DeviceId == deviceId);

    [Fact]
    public void NothingIsMeasuredUntilATunerHasRestedForAMinute()
    {
        Know(Here);
        IdleReceptionMeasurer measurer = Measurer();

        Assert.Null(measurer.MeasureIfDue());

        clock.Advance(Rested - TimeSpan.FromSeconds(1));

        Assert.Null(measurer.MeasureIfDue());
        Assert.Empty(hardware.Happened);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(new IdleMeasurement("north", Here, IdleMeasurementEnd.Kept, 20), measurer.MeasureIfDue());
    }

    [Fact]
    public void TenSecondsPassBetweenTwoMeasurements()
    {
        Know(Here);
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);

        Assert.Equal("north", measurer.MeasureIfDue()?.DeviceId);
        Assert.Null(measurer.MeasureIfDue());

        clock.Advance(Gap - TimeSpan.FromSeconds(1));

        Assert.Null(measurer.MeasureIfDue());

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("south", measurer.MeasureIfDue()?.DeviceId);
    }

    [Fact]
    public void OnlyOneTunerIsMeasuredAtATime()
    {
        Know(Here);
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);
        IdleMeasurement? meanwhile = new("nobody", Here, IdleMeasurementEnd.Kept);
        hardware.WhileOpening = _ =>
        {
            clock.Advance(Gap);
            meanwhile = measurer.MeasureIfDue();
        };

        IdleMeasurement? first = measurer.MeasureIfDue();

        Assert.Equal("north", first?.DeviceId);
        Assert.Null(meanwhile);
        Assert.Equal(["probe opened north 31", "probe closed north"], hardware.Happened);

        hardware.WhileOpening = null;
        clock.Advance(Gap);

        Assert.Equal("south", measurer.MeasureIfDue()?.DeviceId);
    }

    [Fact]
    public void OnlyACombinationWithNoResultOrAnOldOneIsMeasured()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        Know(Here, There);
        Measured("north", Here);
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);

        Assert.Equal(There, measurer.MeasureIfDue()?.Channel);

        clock.Advance(Gap);

        Assert.Null(measurer.MeasureIfDue());

        clock.Advance(ReceptionBook.StaleAfter);

        Assert.Equal(Here, measurer.MeasureIfDue()?.Channel);
        Assert.Equal(2, book.StandingOf("north", On(Here)).Kept);
    }

    [Fact]
    public void ATunerSeenForTheFirstTimeIsMeasuredOnEveryKnownChannelOnce()
    {
        Know(Yonder, Here, There);
        Measured("north", Here);
        Measured("north", There);
        Measured("north", Yonder);
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);
        List<IdleMeasurement?> measured = [];

        for (int look = 0; look < 5; look++)
        {
            measured.Add(measurer.MeasureIfDue());
            clock.Advance(Gap);
        }

        Assert.Equal(
            [("south", Here), ("south", There), ("south", Yonder)],
            measured.OfType<IdleMeasurement>().Select(measurement => (measurement.DeviceId, measurement.Channel))
        );
        Assert.Equal(3, measurer.Finished);
    }

    [Fact]
    public void ATunerIsMeasuredOnlyOnChannelsItCanReceive()
    {
        configuration = Configured(true, ("dish", DeviceKind.Satellite), ("aerial", DeviceKind.Terrestrial));
        Know(Here, new TuningKey(TunerKind.Satellite, 15, 16625), new TuningKey(TunerKind.Satellite, 13, 0), new TuningKey(TunerKind.Satellite, 4, 0));
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);
        List<IdleMeasurement?> measured = [];

        for (int look = 0; look < 5; look++)
        {
            measured.Add(measurer.MeasureIfDue());
            clock.Advance(Gap);
        }

        Assert.Equal(
            ["dish 4", "dish 15", "aerial 31"],
            measured.OfType<IdleMeasurement>().Select(measurement => $"{measurement.DeviceId} {measurement.Channel.PhysicalChannel}")
        );
    }

    [Fact]
    public void AMeasurementKeepsTheMedianOfWhatItRead()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        hardware.Signals[("north", 31)] = [20, 22, 21, 23, 19];
        Know(Here);
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);

        IdleMeasurement? measured = measurer.MeasureIfDue();

        Assert.Equal(new IdleMeasurement("north", Here, IdleMeasurementEnd.Kept, 21), measured);
        Assert.Equal(new ReceptionStanding(1, 1, 21, clock.GetUtcNow()), book.StandingOf("north", On(Here)));
    }

    [Fact]
    public void AReadingWithoutLockIsLeftOutAndOneWithLockIsEnoughToCountAsLocked()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        hardware.Signals[("north", 31)] = [double.NaN, 18, double.NaN, double.NaN, double.NaN];
        Know(Here);
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);

        Assert.Equal(new IdleMeasurement("north", Here, IdleMeasurementEnd.Kept, 18), measurer.MeasureIfDue());
    }

    [Fact]
    public void AMeasurementLeavesAFreeTunerClosedAndFree()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        Know(Here);
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);

        measurer.MeasureIfDue();

        Assert.Equal(["probe opened north 31", "probe closed north"], hardware.Happened);
        Assert.Equal(TunerAllocationState.Free, Table("north").State);
        Assert.False(Table("north").DeviceOpen);
        Assert.Equal(0, Table("north").InFlight);
    }

    [Fact]
    public async Task ATunerHeldForWhoeverComesBackIsNotMeasuredAndStaysHeld()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        TunerSessionManager manager = Manager(TimeSpan.FromMinutes(10));
        Know(There);
        IdleReceptionMeasurer measurer = Measurer();
        await StopAsync(await StartedAsync(manager.BeginAsync(Live("viewer", Here))));
        clock.Advance(Rested + Rested);

        Assert.Null(measurer.MeasureIfDue());
        Assert.Equal(TunerAllocationState.Ready, Table("north").State);
        Assert.True(Table("north").DeviceOpen);

        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(There, measurer.MeasureIfDue()?.Channel);
        Assert.Equal(TunerAllocationState.Free, Table("north").State);
    }

    [Fact]
    public async Task ATunerThatWasJustUsedRestsForAMinuteAgainBeforeItIsMeasured()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        TunerSessionManager manager = Manager(TimeSpan.Zero);
        Know(There);
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);
        await StopAsync(await StartedAsync(manager.BeginAsync(Live("viewer", Here))));

        Assert.Null(measurer.MeasureIfDue());

        clock.Advance(Rested);

        Assert.Equal(There, measurer.MeasureIfDue()?.Channel);
    }

    [Fact]
    public void AChannelThatDoesNotLockIsThrownAwayAndTriedAgainNoSoonerThanSixHoursLater()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        hardware.Dead.Add(("north", 31));
        Know(Here);
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);

        Assert.Equal(new IdleMeasurement("north", Here, IdleMeasurementEnd.ThrownAway), measurer.MeasureIfDue());
        Assert.Equal(ReceptionStanding.Unmeasured, book.StandingOf("north", On(Here)));

        clock.Advance(ReceptionBook.RetryAfter - TimeSpan.FromSeconds(1));

        Assert.Null(measurer.MeasureIfDue());

        clock.Advance(TimeSpan.FromSeconds(1));
        hardware.Dead.Clear();

        Assert.Equal(IdleMeasurementEnd.Kept, measurer.MeasureIfDue()?.End);
    }

    [Fact]
    public async Task AChannelThatDoesNotLockIsKeptWhenAnotherTunerIsReceivingItWithLock()
    {
        hardware.Dead.Add(("south", 31));
        Know(Here);
        Measured("north", Here);
        TunerSessionManager manager = Manager();
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);
        TunerSession receiving = await StartedAsync(manager.BeginAsync(Live("viewer", Here, "north")));
        await WaitUntilAsync(() => receiving.Quality?.HasLock is true);

        Assert.Equal(new IdleMeasurement("south", Here, IdleMeasurementEnd.Kept), measurer.MeasureIfDue());
        Assert.Equal(new ReceptionStanding(1, 0, null, clock.GetUtcNow()), book.StandingOf("south", On(Here)));

        await StopAsync(receiving);
    }

    [Fact]
    public async Task ChannelsThatDoNotLockNeitherFaultTheTunerNorHoldItBack()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        hardware.Dead.Add(("north", 31));
        hardware.Dead.Add(("north", 33));
        Know(Here, There);
        TunerSessionManager manager = Manager();
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);

        Assert.Equal(IdleMeasurementEnd.ThrownAway, measurer.MeasureIfDue()?.End);
        clock.Advance(Gap);
        Assert.Equal(IdleMeasurementEnd.ThrownAway, measurer.MeasureIfDue()?.End);

        Assert.False(manager.IsFaulted("north", out _));
        Assert.False(manager.IsFailingToTune("north"));

        hardware.Dead.Clear();
        TunerSession session = await StartedAsync(manager.BeginAsync(Live("viewer", There)));

        Assert.Equal("north", session.DeviceId);

        await StopAsync(session);
    }

    [Fact]
    public void ADeviceThatFailsWhileItsSignalIsReadIsFaultedLikeAnyDeviceThatFailsInUse()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        hardware.BreaksAtRead["north"] = 2;
        Know(Here);
        TunerSessionManager manager = Manager();
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);

        Assert.Equal(new IdleMeasurement("north", Here, IdleMeasurementEnd.DeviceFailed), measurer.MeasureIfDue());

        Assert.True(manager.IsFaulted("north", out string? detail));
        Assert.Contains("measured", detail, StringComparison.Ordinal);
        Assert.Equal(TunerFaultKind.DeviceFailed, manager.FaultKindOf("north")?.Kind);
        Assert.Equal(ReceptionStanding.Unmeasured, book.StandingOf("north", On(Here)));
        Assert.Empty(hardware.Overlaps);
        Assert.Equal(["probe opened north 31", "probe closed north", "let go north"], hardware.Happened);

        clock.Advance(Gap);

        Assert.Null(measurer.MeasureIfDue());
    }

    [Fact]
    public void AStatisticsReadThatFailsPartWayIsLeftOutAndTheTunerIsNotFaulted()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        hardware.Signals[("north", 31)] = [20, 22, 99, 24, 26];
        hardware.Unreadable.Add(("north", 2));
        Know(Here);
        TunerSessionManager manager = Manager();
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);

        Assert.Equal(new IdleMeasurement("north", Here, IdleMeasurementEnd.Kept, 23), measurer.MeasureIfDue());

        Assert.False(manager.IsFaulted("north", out _));
        Assert.Equal(TunerAllocationState.Free, Table("north").State);
        Assert.Equal(["probe opened north 31", "probe closed north"], hardware.Happened);
    }

    [Fact]
    public void ATunerWhoseStatisticsCannotBeReadAtAllIsKeptAsLockedWithNoCarrierToNoise()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        hardware.Unreadable.UnionWith(Enumerable.Range(0, IdleReceptionMeasurer.Readings).Select(read => ("north", read)));
        Know(Here);
        TunerSessionManager manager = Manager();
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);

        Assert.Equal(new IdleMeasurement("north", Here, IdleMeasurementEnd.Kept), measurer.MeasureIfDue());

        Assert.Equal(new ReceptionStanding(1, 1, null, clock.GetUtcNow()), book.StandingOf("north", On(Here)));
        Assert.False(manager.IsFaulted("north", out _));
    }

    [Fact]
    public async Task AFailureFoundJustAsARequestTakesTheTunerIsNotHeldAgainstTheRequest()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        hardware.BreaksAtRead["north"] = 1;
        Know(Here);
        TunerSessionManager manager = Manager();
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);
        Task<SessionStart>? asked = null;
        hardware.WhileReading = (_, read) => asked ??= read is 1 ? manager.BeginAsync(Live("viewer", There)) : null;

        Assert.Equal(new IdleMeasurement("north", Here, IdleMeasurementEnd.Interrupted), measurer.MeasureIfDue());

        TunerSession session = await StartedAsync(asked!);

        Assert.Equal("north", session.DeviceId);
        Assert.False(manager.IsFaulted("north", out _));
        Assert.Empty(hardware.Overlaps);

        await StopAsync(session);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATunerTakenOutOfServiceWhileItIsMeasuredIsLetGoOfOnceAndOnlyAfterTheMeasurementHasLanded(bool faulted)
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        Know(Here);
        TunerSessionManager manager = Manager();
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);
        hardware.WhileTuning = _ =>
        {
            if (faulted)
            {
                manager.Fault(new TunerContradiction("north", DeviceKind.Terrestrial, [DeviceKind.Satellite]));
            }
            else
            {
                manager.Turn("north", disabled: true);
            }
        };

        Assert.Equal(IdleMeasurementEnd.Interrupted, measurer.MeasureIfDue()?.End);

        Assert.Equal(["probe opened north 31", "probe closed north", "let go north"], hardware.Happened);
        Assert.Empty(hardware.Overlaps);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATunerThatCannotBeOpenedToBeMeasuredIsNotFaultedAndIsLeftAloneForSixHours(bool refuses)
    {
        (refuses ? hardware.Refusing : hardware.Broken).Add("north");
        Know(Here, There);
        Measured("south", Here);
        Measured("south", There);
        TunerSessionManager manager = Manager();
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);

        Assert.Equal(new IdleMeasurement("north", Here, IdleMeasurementEnd.NotOpened), measurer.MeasureIfDue());

        Assert.False(manager.IsFaulted("north", out _));
        Assert.False(manager.IsFailingToTune("north"));
        Assert.Equal(TunerAllocationState.Free, Table("north").State);
        Assert.Equal(ReceptionStanding.Unmeasured, book.StandingOf("north", On(Here)));
        Assert.Empty(hardware.Overlaps);

        clock.Advance(ReceptionBook.RetryAfter - TimeSpan.FromSeconds(1));

        Assert.Null(measurer.MeasureIfDue());

        clock.Advance(TimeSpan.FromSeconds(1));
        hardware.Refusing.Clear();
        hardware.Broken.Clear();

        Assert.Equal(new IdleMeasurement("north", Here, IdleMeasurementEnd.Kept, 20), measurer.MeasureIfDue());
    }

    [Fact]
    public void ATunerThatCannotBeOpenedIsLookedForSoThatOnePulledOutIsSeenToBeGone()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        hardware.Broken.Add("north");
        Know(Here);
        List<string> lookedFor = [];
        var manager = new TunerSessionManager(
            configuration,
            hardware,
            clock,
            NullLogger<TunerSessionManager>.Instance,
            reception: book,
            presence: device =>
            {
                lookedFor.Add(device.Id!);
                made!.Looked(device, false);

                return false;
            }
        );
        made = manager;
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);

        measurer.MeasureIfDue();

        Assert.Equal(["north"], lookedFor);
        Assert.Equal(TunerAllocationState.Absent, Table("north").State);
        Assert.False(manager.IsFaulted("north", out _));
    }

    [Fact]
    public void ATunerBeingMeasuredLooksIdleInTheListingOfTuners()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        Know(Here);
        TunerSessionManager manager = Manager();
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);
        TunerSnapshot? during = null;
        TunerAllocationState? state = null;
        bool? claimed = null;
        hardware.WhileTuning = _ =>
        {
            during = Assert.Single(SessionViews.Tuners(configuration, manager));
            state = Table("north").State;
            claimed = manager.IsClaimed("north");
        };

        measurer.MeasureIfDue();

        Assert.Equal(TunerAllocationState.Measuring, state);
        Assert.False(claimed);
        Assert.NotNull(during);
        Assert.Equal(TunerState.Idle, during.State);
        Assert.Null(during.CurrentSession);
        Assert.True(during.SessionId.IsUnset);
        Assert.Null(during.SignalQuality);
        Assert.Equal(TunerHealthLevel.Healthy, during.Health?.Level);
        Assert.Empty(manager.Sessions);
    }

    [Fact]
    public void NothingIsMeasuredWhenTheSettingTurnsItOff()
    {
        configuration = Configured(false, ("north", DeviceKind.Terrestrial));
        Know(Here);
        var timers = new SteppedTimeProvider(Start);
        IdleReceptionMeasurer measurer = Measurer(timers);

        measurer.StartAsync(CancellationToken.None);
        clock.Advance(Rested + Rested);
        timers.Advance(Rested + Rested);

        Assert.Equal(0, timers.Waiting);
        Assert.Null(measurer.MeasureIfDue());
        Assert.Empty(hardware.Happened);
        Assert.Contains("tuner.measureWhileIdle", Assert.Single(log.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void MeasuringIsOnUnlessTheConfigurationSaysOtherwise()
    {
        Assert.True(new TunerSettings(TunerBackend.Fake).MeasureWhileIdle);
        Assert.True(Read(""" "backend": "fake" """).Tuner!.MeasureWhileIdle);
        Assert.True(Read(""" "backend": "fake", "measureWhileIdle": true """).Tuner!.MeasureWhileIdle);
        Assert.False(Read(""" "backend": "fake", "measureWhileIdle": false """).Tuner!.MeasureWhileIdle);
    }

    [Fact]
    public void TheSettingSurvivesTheDriverWritingItsConfigurationBack()
    {
        DriverConfiguration turnedOff = Read(""" "backend": "fake", "measureWhileIdle": false """);

        DriverConfigurationResult back = DriverConfigurationReader.Read(DriverConfigurationWriter.Serialize(turnedOff));

        Assert.True(back.TryGetConfiguration(out DriverConfiguration again, out IReadOnlyList<string> problems), string.Join(" ", problems));
        Assert.False(again.Tuner!.MeasureWhileIdle);
    }

    private static DriverConfiguration Read(string tuner)
    {
        DriverConfigurationResult read = DriverConfigurationReader.Read(
            $$"""
            {
              "socketPath": "/run/carina/driver.sock",
              "outputRoots": [{ "name": "primary", "path": "/srv/recordings" }],
              "tuner": { {{tuner}} },
              "devices": [{ "id": "fake-terrestrial", "kind": "terrestrial" }]
            }
            """
        );

        Assert.True(read.TryGetConfiguration(out DriverConfiguration configuration, out IReadOnlyList<string> problems), string.Join(" ", problems));

        return configuration;
    }

    [Fact]
    public async Task TheDriverLooksForSomethingToMeasureByItself()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        Know(Here);
        var timers = new SteppedTimeProvider(Start);
        using IdleReceptionMeasurer measurer = Measurer(timers);

        await measurer.StartAsync(CancellationToken.None);
        timers.Advance(Rested);
        await WaitUntilAsync(() => measurer.Finished is 1);

        Assert.Equal(1, book.StandingOf("north", On(Here)).Kept);

        await measurer.StopAsync(CancellationToken.None);
        book.Know(There);
        timers.Advance(Rested);

        Assert.Equal(1, measurer.Finished);
    }

    [Fact]
    public async Task StoppingWaitsForTheMeasurementUnderWayToLand()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        Know(Here);
        var timers = new SteppedTimeProvider(Start);
        using IdleReceptionMeasurer measurer = Measurer(timers);
        using var opening = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        hardware.WhileOpening = _ =>
        {
            opening.Set();
            release.Wait(Deadlock);
        };
        await measurer.StartAsync(CancellationToken.None);
        timers.Advance(Rested);
        await Task.Run(() => Assert.True(opening.Wait(Deadlock)));

        Task stopping = measurer.StopAsync(CancellationToken.None);
        await Task.Delay(50);

        Assert.False(stopping.IsCompleted);
        Assert.True(hardware.LastStopping.IsCancellationRequested);

        release.Set();
        await stopping.WaitAsync(Deadlock);

        Assert.Equal("probe closed north", hardware.Happened[^1]);
        Assert.Equal(1, measurer.Interrupted);
        Assert.Equal(ReceptionStanding.Unmeasured, book.StandingOf("north", On(Here)));
    }

    [Fact]
    public async Task StoppingTellsTheMeasurementUnderWayToStop()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        Know(Here);
        var timers = new SteppedTimeProvider(Start);
        using IdleReceptionMeasurer measurer = Measurer(timers);
        using var reading = new ManualResetEventSlim(false);
        int reads = 0;
        hardware.WhileReading = (_, _) =>
        {
            reads++;
            reading.Set();
            hardware.LastStopping.WaitHandle.WaitOne(Deadlock);
        };
        await measurer.StartAsync(CancellationToken.None);
        timers.Advance(Rested);
        await Task.Run(() => Assert.True(reading.Wait(Deadlock)));

        await measurer.StopAsync(CancellationToken.None).WaitAsync(Deadlock);

        Assert.Equal(1, reads);
        Assert.Equal(1, measurer.Interrupted);
        Assert.Equal(TunerAllocationState.Free, Table("north").State);
    }

    [Fact]
    public async Task NoThreadIsStartedForALookThatFindsNothingDue()
    {
        configuration = Configured(true, ("north", DeviceKind.Terrestrial));
        var timers = new SteppedTimeProvider(Start);
        using IdleReceptionMeasurer measurer = Measurer(timers);
        await measurer.StartAsync(CancellationToken.None);

        for (int look = 0; look < 20; look++)
        {
            timers.Advance(Rested);
        }

        Assert.Equal(0, measurer.StartedByItself);

        Know(Here);
        timers.Advance(Rested);
        await WaitUntilAsync(() => measurer.Finished is 1);

        for (int look = 0; look < 20; look++)
        {
            timers.Advance(Rested);
        }

        Assert.Equal(1, measurer.StartedByItself);
    }

    [Fact]
    public void NothingIsMeasuredOnceTheDriverIsShuttingDown()
    {
        Know(Here);
        TunerSessionManager manager = Manager();
        IdleReceptionMeasurer measurer = Measurer();
        clock.Advance(Rested);

        manager.EnterDraining();

        Assert.Null(measurer.MeasureIfDue());
        Assert.Empty(hardware.Happened);
    }

    private static async Task WaitUntilAsync(Func<bool> met)
    {
        DateTime giveUp = DateTime.UtcNow + Deadlock;

        while (!met())
        {
            Assert.True(DateTime.UtcNow < giveUp, "What the test waited for never happened.");
            await Task.Delay(5);
        }
    }
}
