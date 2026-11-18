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
        Assert.DoesNotContain(log.Lines, line => line.Contains("only being measured", StringComparison.Ordinal));

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

    [Fact]
    public async Task TheRequestIsRefusedOnlyWhenTheMeasurementDoesNotLandWithinTheLimitOfAnOpen()
    {
        var limited = new TunerSessionManager(
            Configured("north"),
            hardware,
            TimeProvider.System,
            log,
            handOverLimit: Deadlock,
            deviceCheck: hardware,
            openLimit: TimeSpan.FromMilliseconds(200),
            reception: book
        );
        var stuck = new IdleReceptionMeasurer(Configured("north"), limited, book, hardware, new ManualTimeProvider(DateTimeOffset.UtcNow.AddMinutes(2)), NullLogger<IdleReceptionMeasurer>.Instance, TimeSpan.Zero);
        using var opening = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        hardware.WhileOpening = _ =>
        {
            opening.Set();
            release.Wait(Deadlock);
        };
        Task<IdleMeasurement?> measuring = Task.Run(stuck.MeasureIfDue);
        Assert.True(opening.Wait(Deadlock));

        Ask(limited);
        SessionStart refused = await asked!.WaitAsync(Deadlock);

        Assert.Equal(SessionRefusal.DeviceBusy, refused.Refusal);
        Assert.Contains(log.Lines, line => line.Contains("the measurement under way on it had not stopped", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, line => line.Contains("only being measured", StringComparison.Ordinal));
        Assert.Equal(["probe opened north 31"], hardware.Happened);

        release.Set();
        Assert.Equal(IdleMeasurementEnd.Interrupted, (await measuring.WaitAsync(Deadlock))?.End);
        Assert.Equal(TunerAllocationState.Free, Assert.Single(limited.Allocations()).State);
        Assert.Equal(0, Assert.Single(limited.Allocations()).InFlight);
    }
}
