using Puppis.Contracts;
using Puppis.Driver.Sessions;
using Puppis.Driver.Tuning;

namespace Puppis.Driver.Tests;

public sealed class TunerMeasuringTableTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 13, 21, 0, 0, TimeSpan.Zero);

    private static readonly string[] Tuners = ["north", "south"];

    private static readonly TuningKey Here = new(TunerKind.Terrestrial, 31, 0);

    private static readonly TuningKey There = new(TunerKind.Terrestrial, 33, 0);

    private readonly ManualTimeProvider clock = new(Start);
    private readonly List<string> letGo = [];
    private readonly CapturingLogger<TunerPool> log = new();
    private string[] candidates = Tuners;

    private TunerPool Pool(params (string DeviceId, bool Enabled)[] declared)
    {
        candidates = declared.Length is 0 ? Tuners : [.. declared.Select(tuner => tuner.DeviceId)];

        return new TunerPool(clock, TimeSpan.FromSeconds(5), [.. candidates.Select(id => (id, declared.Length is 0 || declared.First(tuner => tuner.DeviceId == id).Enabled))], letGo.Add, log);
    }

    private PoolGrant Ask(
        TunerPool pool,
        string sessionId,
        TuningKey tuning,
        SessionPurpose purpose = SessionPurpose.Logo,
        string? deviceId = null
    ) =>
        pool.Acquire(new PoolRequest(SessionId.Parse(sessionId), purpose, tuning, deviceId, candidates));

    private static MeasuringTicket Measure(TunerPool pool, string deviceId = "north")
    {
        MeasuringTicket? ticket = pool.BeginMeasuring(deviceId, There);

        Assert.NotNull(ticket);

        return ticket;
    }

    private static TunerAllocationView ViewOf(TunerPool pool, string deviceId) =>
        pool.Snapshot().Single(tuner => tuner.DeviceId == deviceId);

    [Fact]
    public void AFreeTunerCanBeMeasuredAndIsFreeAgainAfterwards()
    {
        TunerPool pool = Pool();

        MeasuringTicket ticket = Measure(pool);

        Assert.Equal(TunerAllocationState.Measuring, pool.StateOf("north"));
        Assert.False(ticket.Stopping.IsCancellationRequested);
        Assert.True(pool.EndMeasuring(ticket));
        Assert.Equal(TunerAllocationState.Free, pool.StateOf("north"));
        Assert.Equal(0, pool.RefusedTransitions);
    }

    [Fact]
    public void ATunerBeingMeasuredCountsAsOneOperationOnItsDeviceAndNobodyOnIt()
    {
        TunerPool pool = Pool();

        MeasuringTicket ticket = Measure(pool);
        TunerAllocationView during = ViewOf(pool, "north");

        Assert.True(during.Measuring);
        Assert.Equal(1, during.InFlight);
        Assert.False(during.DeviceOpen);
        Assert.Empty(during.Sinks);
        Assert.False(pool.IsQuiet("north"));
        Assert.False(pool.IsHeld("north"));
        Assert.False(pool.IsLingering("north"));
        Assert.Equal(["north"], pool.Occupied());

        pool.EndMeasuring(ticket);
        TunerAllocationView after = ViewOf(pool, "north");

        Assert.False(after.Measuring);
        Assert.Equal(0, after.InFlight);
        Assert.True(pool.IsQuiet("north"));
        Assert.Empty(pool.Occupied());
    }

    [Fact]
    public void OnlyAFreeTunerWithNothingInFlightCanBeMeasured()
    {
        TunerPool pool = Pool(("north", true), ("south", true), ("off", false));
        Ask(pool, "user", Here, deviceId: "north");
        pool.Fault("south", new TunerFault("it broke", null, DeviceFaultKind.Failed));

        Assert.Null(pool.BeginMeasuring("north", There));
        Assert.Null(pool.BeginMeasuring("south", There));
        Assert.Null(pool.BeginMeasuring("off", There));
        Assert.Null(pool.BeginMeasuring("nowhere", There));
        Assert.Equal(0, pool.RefusedTransitions);
    }

    [Fact]
    public void ATunerAlreadyBeingMeasuredIsNotMeasuredTwice()
    {
        TunerPool pool = Pool();
        Measure(pool);

        Assert.Null(pool.BeginMeasuring("north", Here));
    }

    [Fact]
    public void ATunerHeldForWhoeverComesBackIsNotMeasuredUntilTheHoldRunsOut()
    {
        TunerPool pool = Pool();
        PoolGrant grant = Ask(pool, "user", Here, deviceId: "north");
        pool.Tuned("north", grant.Holder, new FakeTunerDevice(31));
        pool.Ready("north", grant.Holder);
        pool.Leave(grant.Holder);

        Assert.Null(pool.BeginMeasuring("north", There));
        Assert.Equal(TunerAllocationState.Ready, pool.StateOf("north"));
        Assert.DoesNotContain(pool.Resting(), tuner => tuner.DeviceId is "north");

        clock.Advance(TimeSpan.FromSeconds(6));
        pool.Sweep();

        Assert.Contains(pool.Resting(), tuner => tuner.DeviceId is "north");
        Assert.NotNull(pool.BeginMeasuring("north", There));
    }

    [Fact]
    public async Task ATunerWhoseCloseHasNotLandedIsNotMeasured()
    {
        TunerPool pool = Pool();
        var closing = new SlowToClose();
        PoolGrant grant = Ask(pool, "user", Here, deviceId: "north");
        pool.Tuned("north", grant.Holder, closing);
        pool.Ready("north", grant.Holder);
        Task leaving = Task.Run(() => pool.Abandon("north", grant.Holder));
        Assert.True(closing.Closing.Wait(TimeSpan.FromSeconds(30)));

        Assert.Null(pool.BeginMeasuring("north", There));
        Assert.DoesNotContain(pool.Resting(), tuner => tuner.DeviceId is "north");

        closing.Finish();
        await leaving.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(pool.BeginMeasuring("north", There));
    }

    [Fact]
    public void ARequestTakesTheTunerBeingMeasuredAndTheMeasurementIsToldToStop()
    {
        TunerPool pool = Pool(("north", true));
        MeasuringTicket ticket = Measure(pool);

        PoolGrant grant = Ask(pool, "user", Here);

        Assert.Equal(PoolVerdict.Granted, grant.Verdict);
        Assert.Equal("north", grant.DeviceId);
        Assert.Empty(grant.Displaced);
        Assert.True(grant.NeedsTuning);
        Assert.True(ticket.Stopping.IsCancellationRequested);
        Assert.Equal(TunerAllocationState.Tuning, pool.StateOf("north"));
        Assert.Equal(0, pool.RefusedTransitions);
    }

    [Fact]
    public async Task TheRequestWaitsForTheMeasurementToLandBeforeItOpensTheDevice()
    {
        TunerPool pool = Pool(("north", true));
        MeasuringTicket ticket = Measure(pool);
        PoolGrant grant = Ask(pool, "user", Here);
        TunerClearance? clearance = pool.Clearance("north", grant.Holder);

        Assert.NotNull(clearance);
        Assert.True(clearance.AMeasurementIsStopping);
        Assert.Equal(Start, clearance.Since);
        Assert.False(grant.Cleared.IsCompleted);
        Assert.False(clearance.Cleared.IsCompleted);
        Assert.Equal(1, ViewOf(pool, "north").InFlight);

        Assert.False(pool.EndMeasuring(ticket));

        Assert.True(await grant.Cleared.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(await clearance.Cleared.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, ViewOf(pool, "north").InFlight);
        Assert.Equal(TunerAllocationState.Tuning, pool.StateOf("north"));
        Assert.NotNull(pool.StartOpening("north", grant.Holder));
    }

    [Fact]
    public void ATunerNobodyIsMeasuringIsUsedBeforeOneThatIsBeingMeasured()
    {
        TunerPool pool = Pool();
        MeasuringTicket ticket = Measure(pool);

        PoolGrant grant = Ask(pool, "user", Here);

        Assert.Equal("south", grant.DeviceId);
        Assert.False(ticket.Stopping.IsCancellationRequested);
        Assert.Equal(TunerAllocationState.Measuring, pool.StateOf("north"));
    }

    [Fact]
    public void TheLeastImportantRequestStillGetsATunerThatIsOnlyBeingMeasured()
    {
        TunerPool pool = Pool();
        Ask(pool, "recording", Here, SessionPurpose.Recording, "south");
        MeasuringTicket ticket = Measure(pool);

        PoolGrant grant = Ask(pool, "logo", There, SessionPurpose.Logo);

        Assert.Equal(PoolVerdict.Granted, grant.Verdict);
        Assert.Equal("north", grant.DeviceId);
        Assert.True(ticket.Stopping.IsCancellationRequested);
        Assert.DoesNotContain(log.Lines, line => line.Contains("only being measured", StringComparison.Ordinal));
    }

    [Fact]
    public void ATunerHeldBackAfterAFailedTuneIsStillHeldBackWhileItIsMeasuredAndThatIsNotBlamedOnTheMeasurement()
    {
        TunerPool pool = Pool(("north", true));
        PoolGrant failed = Ask(pool, "failed", Here);
        pool.TuningFailed("north", failed.Holder, new IOException("no lock"));
        Measure(pool);

        PoolGrant again = Ask(pool, "again", Here);
        PoolGrant other = Ask(pool, "other", There);

        Assert.Equal(PoolVerdict.NoDeviceFree, again.Verdict);
        Assert.Contains("held back", again.Detail, StringComparison.Ordinal);
        Assert.Equal(PoolVerdict.Granted, other.Verdict);
        Assert.DoesNotContain(log.Lines, line => line.Contains("only being measured", StringComparison.Ordinal));
    }

    [Fact]
    public void ARequestThatNamesTheTunerBeingMeasuredGetsIt()
    {
        TunerPool pool = Pool();
        Measure(pool);

        Assert.Equal("north", Ask(pool, "user", Here, deviceId: "north").DeviceId);
    }

    [Fact]
    public void AMeasurementLeavesNoHoldOnTheChannelAndNoTunerKeptForAReturn()
    {
        TunerPool pool = Pool(("north", true));
        MeasuringTicket ticket = pool.BeginMeasuring("north", Here)!;

        pool.EndMeasuring(ticket);

        Assert.False(pool.IsLingering("north"));

        PoolGrant grant = Ask(pool, "user", Here);

        Assert.Equal(PoolVerdict.Granted, grant.Verdict);
        Assert.True(grant.NeedsTuning);
    }

    [Fact]
    public async Task TheTunerIsFreeForTheNextRequestWhenTheOneThatInterruptedLeavesBeforeTheMeasurementLands()
    {
        TunerPool pool = Pool(("north", true));
        MeasuringTicket ticket = Measure(pool);
        PoolGrant first = Ask(pool, "first", Here);

        pool.Leave(first.Holder);

        Assert.Equal(TunerAllocationState.Free, pool.StateOf("north"));
        Assert.Null(pool.BeginMeasuring("north", There));

        PoolGrant second = Ask(pool, "second", There);

        Assert.Equal("north", second.DeviceId);
        Assert.False(second.Cleared.IsCompleted);
        Assert.True(pool.Clearance("north", second.Holder)!.AMeasurementIsStopping);

        pool.EndMeasuring(ticket);

        Assert.True(await second.Cleared.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(pool.Clearance("north", second.Holder)!.AMeasurementIsStopping);
    }

    [Fact]
    public void TurningTheTunerOffStopsTheMeasurementAndLetsGoOfTheDeviceAgainOnceItLands()
    {
        TunerPool pool = Pool();
        MeasuringTicket ticket = Measure(pool);

        pool.Turn("north", false);

        Assert.True(ticket.Stopping.IsCancellationRequested);
        Assert.Equal(TunerAllocationState.Disabled, pool.StateOf("north"));
        Assert.False(pool.IsQuiet("north"));
        Assert.Equal(["north"], letGo);

        Assert.False(pool.EndMeasuring(ticket));

        Assert.Equal(TunerAllocationState.Disabled, pool.StateOf("north"));
        Assert.True(pool.IsQuiet("north"));
        Assert.Equal(["north", "north"], letGo);
        Assert.Equal(0, pool.RefusedTransitions);
    }

    [Fact]
    public void AFaultStopsTheMeasurementAndTheTunerStaysFaultedWhenItLands()
    {
        TunerPool pool = Pool();
        MeasuringTicket ticket = Measure(pool);

        pool.Fault("north", new TunerFault("it broke", null, DeviceFaultKind.Failed));

        Assert.True(ticket.Stopping.IsCancellationRequested);
        Assert.Equal(TunerAllocationState.Faulted, pool.StateOf("north"));
        Assert.False(pool.EndMeasuring(ticket));
        Assert.Equal(TunerAllocationState.Faulted, pool.StateOf("north"));
        Assert.Equal(0, pool.RefusedTransitions);
    }

    [Fact]
    public void ATunerBeingMeasuredIsNotTakenAsUnpluggedByALookThatMissesIt()
    {
        TunerPool pool = Pool();
        MeasuringTicket ticket = Measure(pool);

        pool.Looked("north", false);

        Assert.Equal(TunerAllocationState.Measuring, pool.StateOf("north"));
        Assert.False(ticket.Stopping.IsCancellationRequested);
        Assert.Equal(0, pool.RefusedTransitions);
    }

    [Fact]
    public void ClosingThePoolStopsTheMeasurement()
    {
        TunerPool pool = Pool();
        MeasuringTicket ticket = Measure(pool);

        pool.Dispose();

        Assert.True(ticket.Stopping.IsCancellationRequested);
        Assert.Null(pool.BeginMeasuring("south", There));
        Assert.Empty(pool.Resting());
    }

    [Fact]
    public void ATunerHasRestedSinceItWasLastUsedAndAMeasurementDoesNotStartThatAgain()
    {
        TunerPool pool = Pool(("north", true));

        Assert.Equal(Start, Assert.Single(pool.Resting()).Since);

        clock.Advance(TimeSpan.FromMinutes(2));
        MeasuringTicket ticket = Measure(pool);

        Assert.Empty(pool.Resting());

        clock.Advance(TimeSpan.FromSeconds(3));
        pool.EndMeasuring(ticket);

        Assert.Equal(Start, Assert.Single(pool.Resting()).Since);

        PoolGrant grant = Ask(pool, "user", Here);
        clock.Advance(TimeSpan.FromMinutes(1));
        pool.Abandon("north", grant.Holder);

        Assert.Equal(Start.AddMinutes(3).AddSeconds(3), Assert.Single(pool.Resting()).Since);
    }

    [Fact]
    public void ATunerTurnedBackOnHasRestedOnlySinceThen()
    {
        TunerPool pool = Pool(("north", false));

        Assert.Empty(pool.Resting());

        clock.Advance(TimeSpan.FromMinutes(5));
        pool.Turn("north", true);

        Assert.Equal(Start.AddMinutes(5), Assert.Single(pool.Resting()).Since);
    }

    private sealed class SlowToClose : ITunerDevice
    {
        private readonly ManualResetEventSlim finish = new(false);

        public ManualResetEventSlim Closing { get; } = new(false);

        public long Overflows => 0;

        public byte[] Read(int count, CancellationToken cancellationToken) => new byte[count];

        public void Finish() => finish.Set();

        public void Dispose()
        {
            Closing.Set();
            finish.Wait(TimeSpan.FromSeconds(30));
        }
    }
}
