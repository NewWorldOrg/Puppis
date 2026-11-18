using Microsoft.Extensions.Logging.Abstractions;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Reception;
using Puppis.Driver.Sessions;

namespace Puppis.Driver.Tests;

public sealed class IdleMeasurementYieldsTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 13, 21, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Deadlock = TimeSpan.FromSeconds(30);

    private static readonly TuningKey Measured = new(TunerKind.Terrestrial, 31, 0);

    private static readonly TuningKey Wanted = new(TunerKind.Terrestrial, 33, 0);

    private readonly ManualTimeProvider clock = new(Start);
    private readonly ReceptionBook book = new();
    private readonly MeasuredHardware hardware = new();
    private readonly CapturingLogger<TunerSessionManager> log = new();
    private readonly TunerSessionManager manager;
    private readonly IdleReceptionMeasurer measurer;
    private Task<SessionStart>? asked;

    public IdleMeasurementYieldsTests()
    {
        manager = Manager("north");
        measurer = MeasurerFor(manager, "north");
        book.Know(Measured);
        clock.Advance(IdleReceptionMeasurer.RestBeforeMeasuring);
    }

    private static DriverConfiguration Configured(params string[] devices) =>
        new(
            "/run/carina/driver.sock",
            [new OutputRootSettings("primary", Path.GetTempPath())],
            6,
            new TunerSettings(TunerBackend.Fake),
            [.. devices.Select(id => new DeviceSettings(id, DeviceKind.Terrestrial))]
        );

    private TunerSessionManager Manager(params string[] devices) =>
        new(Configured(devices), hardware, clock, log, handOverLimit: Deadlock, deviceCheck: hardware, reception: book);

    private IdleReceptionMeasurer MeasurerFor(TunerSessionManager measured, params string[] devices) =>
        new(Configured(devices), measured, book, hardware, clock, NullLogger<IdleReceptionMeasurer>.Instance, TimeSpan.Zero);

    private void Ask(TunerSessionManager from, SessionPurpose purpose = SessionPurpose.Logo) =>
        asked = from.BeginAsync(
            new StartSessionRequest
            {
                SessionId = SessionId.Parse("asked"),
                Purpose = purpose,
                Tuning = new TuningRequest(TunerKind.Terrestrial, Wanted.PhysicalChannel, 50001),
            }
        );

    private async Task<TunerSession> ServedAsync()
    {
        Assert.NotNull(asked);

        SessionStart start = await asked.WaitAsync(Deadlock);

        Assert.True(start.TryGetSession(out TunerSession? session), $"{start.Refusal}: {start.Detail}");

        return session;
    }

    private async Task TheRequestWasServedOnTheMeasuredTunerAsync(IdleMeasurement? measurement, params string[] happened)
    {
        TunerSession session = await ServedAsync();

        Assert.Equal(new IdleMeasurement("north", Measured, IdleMeasurementEnd.Interrupted), measurement);
        Assert.Equal("north", session.DeviceId);
        Assert.Equal(happened, hardware.Happened);
        Assert.Empty(hardware.Overlaps);
        Assert.Equal(ReceptionStanding.Unmeasured, book.StandingOf("north", ReceptionChannel.Of(Measured)));
        Assert.True(book.IsDue("north", ReceptionChannel.Of(Measured), clock.GetUtcNow()));
        Assert.Equal(1, measurer.Interrupted);
        Assert.Equal(0, measurer.Finished);
        Assert.DoesNotContain(log.Lines, line => line.Contains(TunerPool.MeasurementUnderWay, StringComparison.Ordinal));
        Assert.StartsWith(
            "Information The measurement of north on terrestrial channel 31 was stopped for session asked, which then waited ",
            Assert.Single(log.Lines, line => line.Contains("was stopped for session", StringComparison.Ordinal)),
            StringComparison.Ordinal
        );

        session.Stop();
        await session.Completion.WaitAsync(Deadlock);
    }

    [Fact]
    public async Task ARequestThatArrivesBeforeTheOpenGetsTheTunerAndNothingIsOpenedForTheMeasurement()
    {
        hardware.BeforeOpening = _ => Ask(manager);

        IdleMeasurement? measurement = measurer.MeasureIfDue();

        await TheRequestWasServedOnTheMeasuredTunerAsync(measurement, "opened north 33");
    }

    [Fact]
    public async Task ARequestThatArrivesDuringTheOpenWaitsForItToLandAndThenGetsTheTuner()
    {
        hardware.WhileOpening = _ =>
        {
            Ask(manager);
            Assert.False(asked!.IsCompleted);
            Assert.Equal(TunerAllocationState.Tuning, Assert.Single(manager.Allocations()).State);
        };

        IdleMeasurement? measurement = measurer.MeasureIfDue();

        await TheRequestWasServedOnTheMeasuredTunerAsync(
            measurement,
            "probe opened north 31",
            "probe closed north",
            "opened north 33"
        );
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARequestThatArrivesDuringTheTuneGetsTheTunerOnceTheTuneHasStopped(bool tuneStopsWhenTold)
    {
        hardware.TuneStopsWhenTold = tuneStopsWhenTold;
        hardware.WhileTuning = _ => Ask(manager);

        IdleMeasurement? measurement = measurer.MeasureIfDue();

        await TheRequestWasServedOnTheMeasuredTunerAsync(
            measurement,
            "probe opened north 31",
            "probe closed north",
            "opened north 33"
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ARequestThatArrivesWhileTheSignalIsReadGetsTheTunerAndTheReadingStops(int duringRead)
    {
        int reads = 0;
        hardware.WhileReading = (_, read) =>
        {
            reads++;

            if (read == duringRead)
            {
                Ask(manager);
            }
        };

        IdleMeasurement? measurement = measurer.MeasureIfDue();

        Assert.Equal(duringRead + 1, reads);
        await TheRequestWasServedOnTheMeasuredTunerAsync(
            measurement,
            "probe opened north 31",
            "probe closed north",
            "opened north 33"
        );
    }

    [Fact]
    public async Task TheDeviceIsNotOpenedForTheRequestWhileTheMeasurementStillHasIt()
    {
        using var opening = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        hardware.WhileOpening = _ =>
        {
            opening.Set();
            release.Wait(Deadlock);
        };
        Task<IdleMeasurement?> measuring = Task.Run(measurer.MeasureIfDue);
        Assert.True(opening.Wait(Deadlock));

        Ask(manager);
        await Task.Delay(50);

        Assert.False(asked!.IsCompleted);
        Assert.Equal(["probe opened north 31"], hardware.Happened);

        release.Set();

        await TheRequestWasServedOnTheMeasuredTunerAsync(
            await measuring.WaitAsync(Deadlock),
            "probe opened north 31",
            "probe closed north",
            "opened north 33"
        );
    }

    [Fact]
    public async Task ARequestGoesToATunerNobodyIsMeasuringAndTheMeasurementCarriesOn()
    {
        TunerSessionManager two = Manager("north", "south");
        IdleReceptionMeasurer measuringTwo = MeasurerFor(two, "north", "south");
        clock.Advance(IdleReceptionMeasurer.RestBeforeMeasuring);
        hardware.WhileTuning = _ => Ask(two);

        IdleMeasurement? measurement = measuringTwo.MeasureIfDue();
        TunerSession session = await ServedAsync();

        Assert.Equal(new IdleMeasurement("north", Measured, IdleMeasurementEnd.Kept, 20), measurement);
        Assert.Equal("south", session.DeviceId);
        Assert.Equal(0, measuringTwo.Interrupted);
        Assert.Empty(hardware.Overlaps);

        session.Stop();
        await session.Completion.WaitAsync(Deadlock);
    }

    private static readonly TimeSpan ShortLimit = TimeSpan.FromMilliseconds(300);

    private (TunerSessionManager Manager, IdleReceptionMeasurer Measurer) OnTheWallClock(params string[] devices)
    {
        var limited = new TunerSessionManager(
            Configured(devices),
            hardware,
            TimeProvider.System,
            log,
            tunerGrace: TimeSpan.Zero,
            handOverLimit: ShortLimit,
            deviceCheck: hardware,
            openLimit: ShortLimit,
            reception: book
        );

        return (
            limited,
            new IdleReceptionMeasurer(
                Configured(devices),
                limited,
                book,
                hardware,
                new ManualTimeProvider(DateTimeOffset.UtcNow.AddMinutes(2)),
                NullLogger<IdleReceptionMeasurer>.Instance,
                TimeSpan.Zero
            )
        );
    }

    private (ManualResetEventSlim Reached, ManualResetEventSlim Release) HoldTheProbeOpen()
    {
        var reached = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);

        hardware.WhileOpening = _ =>
        {
            reached.Set();
            release.Wait(Deadlock);
        };

        return (reached, release);
    }

    private void Prefer(string deviceId)
    {
        foreach (string tuner in new[] { "north", "south" })
        {
            book.Note(
                tuner,
                ReceptionChannel.Of(Wanted),
                new ReceptionResult(DateTimeOffset.UtcNow, true, tuner == deviceId ? 30 : 10),
                () => false
            );
        }
    }

    [Fact]
    public async Task ARequestForTheOnlyTunerIsRefusedWhenTheMeasurementDoesNotStopWithinTheLimitOfAnOpen()
    {
        (TunerSessionManager limited, IdleReceptionMeasurer stuck) = OnTheWallClock("north");
        (ManualResetEventSlim reached, ManualResetEventSlim release) = HoldTheProbeOpen();
        Task<IdleMeasurement?> measuring = Task.Run(stuck.MeasureIfDue);
        await Task.Run(() => Assert.True(reached.Wait(Deadlock)));

        Ask(limited);
        SessionStart refused = await asked!.WaitAsync(Deadlock);

        Assert.Equal(SessionRefusal.NoDeviceFree, refused.Refusal);
        Assert.Contains($"The tuner 'north' has {TunerPool.MeasurementUnderWay} that has not stopped.", refused.Detail, StringComparison.Ordinal);
        Assert.Contains(log.Lines, line => line.StartsWith("Error ", StringComparison.Ordinal) && line.Contains(TunerPool.MeasurementUnderWay, StringComparison.Ordinal));
        Assert.Equal(["probe opened north 31"], hardware.Happened);

        release.Set();
        Assert.Equal(IdleMeasurementEnd.Interrupted, (await measuring.WaitAsync(Deadlock))?.End);
        Assert.Equal(TunerAllocationState.Free, Assert.Single(limited.Allocations()).State);
        Assert.Equal(0, Assert.Single(limited.Allocations()).InFlight);
    }

    [Fact]
    public async Task ARequestThatNamesTheTunerIsRefusedWhenItsMeasurementDoesNotStopInTime()
    {
        (TunerSessionManager limited, IdleReceptionMeasurer stuck) = OnTheWallClock("north", "south");
        (ManualResetEventSlim reached, ManualResetEventSlim release) = HoldTheProbeOpen();
        Task<IdleMeasurement?> measuring = Task.Run(stuck.MeasureIfDue);
        await Task.Run(() => Assert.True(reached.Wait(Deadlock)));

        SessionStart refused = await limited
            .BeginAsync(
                new StartSessionRequest
                {
                    SessionId = SessionId.Parse("named"),
                    Purpose = SessionPurpose.Live,
                    Tuning = new TuningRequest(TunerKind.Terrestrial, Wanted.PhysicalChannel, 50001),
                    DeviceId = "north",
                }
            )
            .WaitAsync(Deadlock);

        Assert.Equal(SessionRefusal.DeviceBusy, refused.Refusal);
        Assert.Contains($"{TunerPool.MeasurementUnderWay} on it did not stop within", refused.Detail, StringComparison.Ordinal);
        Assert.Equal(TunerAllocationState.Free, limited.Allocations().Single(tuner => tuner.DeviceId is "south").State);

        release.Set();
        await measuring.WaitAsync(Deadlock);
    }

    [Fact]
    public async Task ARequestWhoseWaitForAMeasurementRunsOutGoesToAnotherIdleTunerInsteadOfBeingRefused()
    {
        Prefer("north");
        (TunerSessionManager limited, IdleReceptionMeasurer stuck) = OnTheWallClock("north", "south");
        (ManualResetEventSlim reached, ManualResetEventSlim release) = HoldTheProbeOpen();
        Task<IdleMeasurement?> measuring = Task.Run(stuck.MeasureIfDue);
        await Task.Run(() => Assert.True(reached.Wait(Deadlock)));

        Ask(limited);
        TunerSession session = await ServedAsync();

        Assert.Equal("south", session.DeviceId);
        Assert.Equal(["probe opened north 31", "opened south 33"], hardware.Happened);
        Assert.Empty(hardware.Overlaps);
        Assert.DoesNotContain(log.Lines, line => line.StartsWith("Error ", StringComparison.Ordinal));
        Assert.Contains(log.Lines, line => line.Contains("gave up waiting for the device north and goes to south instead", StringComparison.Ordinal));

        release.Set();
        Assert.Equal(IdleMeasurementEnd.Interrupted, (await measuring.WaitAsync(Deadlock))?.End);
        session.Stop();
        await session.Completion.WaitAsync(Deadlock);
    }

    [Fact]
    public async Task ARequestWhoseWaitForACloseRunsOutGoesToAnotherIdleTunerInsteadOfBeingRefused()
    {
        Prefer("north");
        (TunerSessionManager limited, _) = OnTheWallClock("north", "south");
        using var closing = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        TunerSession before = await StartedOnAsync(limited, "before", "north");
        hardware.WhileClosing = _ =>
        {
            closing.Set();
            release.Wait(Deadlock);
        };
        before.Stop();
        await Task.Run(() => Assert.True(closing.Wait(Deadlock)));

        Ask(limited);
        TunerSession session = await ServedAsync();

        Assert.Equal("south", session.DeviceId);
        Assert.Empty(hardware.Overlaps);

        hardware.WhileClosing = null;
        release.Set();
        await before.Completion.WaitAsync(Deadlock);
        session.Stop();
        await session.Completion.WaitAsync(Deadlock);
    }

    [Fact]
    public async Task ARequestThatGaveUpOnOneTunerTakesAnotherFromSomethingLessImportantWhenNoneIsIdle()
    {
        Prefer("north");
        (TunerSessionManager limited, IdleReceptionMeasurer stuck) = OnTheWallClock("north", "south");
        TunerSession logo = await StartedOnAsync(limited, "logo", "south", SessionPurpose.Logo, 35);
        (ManualResetEventSlim reached, ManualResetEventSlim release) = HoldTheProbeOpen();
        Task<IdleMeasurement?> measuring = Task.Run(stuck.MeasureIfDue);
        await Task.Run(() => Assert.True(reached.Wait(Deadlock)));

        Ask(limited, SessionPurpose.Live);
        TunerSession session = await ServedAsync();

        Assert.Equal("south", session.DeviceId);
        Assert.Equal(SessionStopReason.Preempted, logo.StopReason);
        Assert.Empty(hardware.Overlaps);

        release.Set();
        await measuring.WaitAsync(Deadlock);
        session.Stop();
        await session.Completion.WaitAsync(Deadlock);
    }

    [Fact]
    public async Task ARequestThatArrivesLateInASlowMeasurementStillGetsTheWholeLimitToWaitForIt()
    {
        (TunerSessionManager limited, IdleReceptionMeasurer slow) = OnTheWallClock("north");
        (ManualResetEventSlim reached, ManualResetEventSlim release) = HoldTheProbeOpen();
        Task<IdleMeasurement?> measuring = Task.Run(slow.MeasureIfDue);
        await Task.Run(() => Assert.True(reached.Wait(Deadlock)));
        await Task.Delay(ShortLimit + TimeSpan.FromMilliseconds(100));

        Ask(limited);
        await Task.Delay(30);
        release.Set();
        TunerSession session = await ServedAsync();

        Assert.Equal("north", session.DeviceId);
        Assert.Equal(IdleMeasurementEnd.Interrupted, (await measuring.WaitAsync(Deadlock))?.End);
        Assert.Equal(["probe opened north 31", "probe closed north", "opened north 33"], hardware.Happened);

        session.Stop();
        await session.Completion.WaitAsync(Deadlock);
    }

    private static async Task<TunerSession> StartedOnAsync(
        TunerSessionManager from,
        string sessionId,
        string deviceId,
        SessionPurpose purpose = SessionPurpose.Live,
        int channel = 35
    )
    {
        SessionStart start = await from
            .BeginAsync(
                new StartSessionRequest
                {
                    SessionId = SessionId.Parse(sessionId),
                    Purpose = purpose,
                    Tuning = new TuningRequest(TunerKind.Terrestrial, channel, 50001),
                    DeviceId = deviceId,
                }
            )
            .WaitAsync(Deadlock);

        Assert.True(start.TryGetSession(out TunerSession? session), $"{start.Refusal}: {start.Detail}");

        return session;
    }
}
