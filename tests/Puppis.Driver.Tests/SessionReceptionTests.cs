using Microsoft.Extensions.Logging.Abstractions;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Reception;
using Puppis.Driver.Sessions;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tests;

public sealed class SessionReceptionTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 13, 21, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Deadlock = TimeSpan.FromSeconds(30);

    private static readonly ReceptionChannel Here = new(TunerKind.Terrestrial, 31);

    private readonly ManualTimeProvider clock = new(Start);
    private readonly ReceptionBook book = new();
    private readonly Devices devices = new();

    private TunerSessionManager Manager() =>
        new(
            new DriverConfiguration(
                "/run/carina/driver.sock",
                [new OutputRootSettings("primary", Path.GetTempPath())],
                6,
                new TunerSettings(TunerBackend.Fake),
                [new DeviceSettings("north", DeviceKind.Terrestrial), new DeviceSettings("south", DeviceKind.Terrestrial)]
            ),
            devices,
            clock,
            NullLogger<TunerSessionManager>.Instance,
            reception: book
        );

    private static StartSessionRequest Live(string sessionId, string? deviceId = null, int channel = 31) =>
        new()
        {
            SessionId = SessionId.Parse(sessionId),
            Purpose = SessionPurpose.Live,
            Tuning = new TuningRequest(TunerKind.Terrestrial, channel, 50001),
            DeviceId = deviceId,
        };

    private static TunerSession Begin(TunerSessionManager manager, string sessionId, string? deviceId = null, int channel = 31)
    {
        SessionStart start = manager.Begin(Live(sessionId, deviceId, channel));

        Assert.True(start.TryGetSession(out TunerSession? session), $"{start.Refusal}: {start.Detail}");

        return session;
    }

    private void ReadOnce(string deviceId)
    {
        PacedTunerDevice device = devices.Opened[deviceId];
        long read = device.Reads;

        device.Allow(1);
        device.AwaitParkedBefore((int)read + 2);
    }

    private static void Stop(TunerSession session)
    {
        session.Stop();
        Assert.True(session.Completion.Wait(Deadlock));
    }

    [Fact]
    public void ASessionThatEndsLeavesTheMedianOfTheCarrierToNoiseItRead()
    {
        devices.Signals["north"] = new ScriptedQualitySource().Answer(
            Readings.Measured(18),
            Readings.Measured(22),
            Readings.Measured(20)
        );
        TunerSessionManager manager = Manager();
        TunerSession session = Begin(manager, "viewer", "north");

        ReadOnce("north");
        clock.Advance(TimeSpan.FromSeconds(10));
        ReadOnce("north");
        clock.Advance(TimeSpan.FromSeconds(10));
        ReadOnce("north");
        Stop(session);

        Assert.Equal(new ReceptionStanding(1, 1, 20, clock.GetUtcNow()), book.StandingOf("north", Here));
        Assert.Equal(ReceptionStanding.Unmeasured, book.StandingOf("south", Here));
    }

    [Fact]
    public void AReadingWithoutLockIsLeftOutOfTheMedian()
    {
        devices.Signals["north"] = new ScriptedQualitySource().Answer(
            Readings.Measured(24),
            Readings.WithoutLock(),
            Readings.WithoutCarrierToNoise()
        );
        TunerSessionManager manager = Manager();
        TunerSession session = Begin(manager, "viewer", "north");

        ReadOnce("north");
        clock.Advance(TimeSpan.FromSeconds(10));
        ReadOnce("north");
        clock.Advance(TimeSpan.FromSeconds(10));
        ReadOnce("north");
        Stop(session);

        Assert.Equal(24, book.StandingOf("north", Here).CarrierToNoise);
    }

    [Fact]
    public void ASessionThatReadNoCarrierToNoiseStillCountsAsOneThatLocked()
    {
        TunerSessionManager manager = Manager();

        Stop(Begin(manager, "viewer", "north"));

        ReceptionStanding standing = book.StandingOf("north", Here);

        Assert.Equal((1, 1, null), (standing.Kept, standing.Locked, standing.CarrierToNoise));
    }

    [Fact]
    public void ASessionThatReadTheStreamThroughAnotherLeavesNoResultOfItsOwn()
    {
        TunerSessionManager manager = Manager();
        TunerSession holder = Begin(manager, "holder", "north");
        TunerSession rider = Begin(manager, "rider");

        Assert.NotNull(rider.RidesOn);

        devices.Opened["north"].Allow(1000);
        Stop(rider);

        Assert.Equal(ReceptionStanding.Unmeasured, book.StandingOf("north", Here));

        Stop(holder);

        Assert.Equal(1, book.StandingOf("north", Here).Kept);
    }

    [Fact]
    public void AnOpenThatDidNotLockIsThrownAwayWhenNoOtherTunerReceivesTheChannel()
    {
        devices.Dead.Add(("north", 31));
        TunerSessionManager manager = Manager();

        SessionStart start = manager.Begin(Live("viewer", "north"));

        Assert.Equal(SessionRefusal.NoLock, start.Refusal);
        Assert.Equal(ReceptionStanding.Unmeasured, book.StandingOf("north", Here));
        Assert.False(book.IsDue("north", Here, clock.GetUtcNow()));
    }

    [Fact]
    public void AnOpenThatDidNotLockIsKeptWhenAnotherTunerIsReceivingTheChannelWithLock()
    {
        devices.Signals["south"] = new ScriptedQualitySource();
        devices.Dead.Add(("north", 31));
        TunerSessionManager manager = Manager();
        TunerSession receiving = Begin(manager, "receiving", "south");
        ReadOnce("south");

        SessionStart start = manager.Begin(Live("viewer", "north"));

        Assert.Equal(SessionRefusal.NoLock, start.Refusal);
        Assert.Equal(new ReceptionStanding(1, 0, null, clock.GetUtcNow()), book.StandingOf("north", Here));

        devices.Opened["south"].Allow(1000);
        Stop(receiving);
    }

    [Fact]
    public void AnotherTunerThatHasLostItsLockDoesNotCountAsReceiving()
    {
        devices.Signals["south"] = new ScriptedQualitySource().Answer(Readings.WithoutLock());
        devices.Dead.Add(("north", 31));
        TunerSessionManager manager = Manager();
        TunerSession receiving = Begin(manager, "receiving", "south");
        ReadOnce("south");

        manager.Begin(Live("viewer", "north"));

        Assert.Equal(ReceptionStanding.Unmeasured, book.StandingOf("north", Here));

        devices.Opened["south"].Allow(1000);
        Stop(receiving);
    }

    [Fact]
    public void AnotherTunerOnADifferentChannelDoesNotCountAsReceiving()
    {
        devices.Signals["south"] = new ScriptedQualitySource();
        devices.Dead.Add(("north", 31));
        TunerSessionManager manager = Manager();
        TunerSession receiving = Begin(manager, "receiving", "south", 33);
        ReadOnce("south");

        manager.Begin(Live("viewer", "north"));

        Assert.Equal(ReceptionStanding.Unmeasured, book.StandingOf("north", Here));

        devices.Opened["south"].Allow(1000);
        Stop(receiving);
    }

    [Fact]
    public async Task ASessionThatLostTheChannelPartWayIsKeptAsNotLockedOnlyWhileAnotherTunerReceivesIt()
    {
        devices.Signals["south"] = new ScriptedQualitySource();
        devices.LosesTheChannel.Add("north");
        TunerSessionManager manager = Manager();
        TunerSession receiving = Begin(manager, "receiving", "south");
        ReadOnce("south");
        TunerSession losing = Begin(manager, "losing", "north");

        devices.Opened["north"].Allow(1);
        await losing.Completion.WaitAsync(Deadlock);

        Assert.Equal(new ReceptionStanding(1, 0, null, clock.GetUtcNow()), book.StandingOf("north", Here));

        devices.Opened["south"].Allow(1000);
        Stop(receiving);

        TunerSession alone = Begin(manager, "alone", "north");
        devices.Opened["north"].Allow(1);
        await alone.Completion.WaitAsync(Deadlock);

        Assert.Equal(1, book.StandingOf("north", Here).Kept);
    }

    [Fact]
    public async Task ASessionWhoseDeviceFailedLeavesNoResult()
    {
        devices.Breaks.Add("north");
        TunerSessionManager manager = Manager();
        TunerSession session = Begin(manager, "viewer", "north");

        devices.Opened["north"].Allow(1);
        await session.Completion.WaitAsync(Deadlock);

        Assert.Equal(SessionStopReason.DeviceFailed, session.StopReason);
        Assert.Equal(ReceptionStanding.Unmeasured, book.StandingOf("north", Here));
    }

    [Fact]
    public void AChannelIsKnownOnceASessionHasStartedOnIt()
    {
        devices.Dead.Add(("north", 35));
        TunerSessionManager manager = Manager();

        manager.Begin(Live("dead", "north", 35));

        Assert.Empty(book.Known());

        Stop(Begin(manager, "viewer", "north"));

        Assert.Equal([new TuningKey(TunerKind.Terrestrial, 31, 0)], book.Known());
    }

    [Fact]
    public void TheNextRequestGoesToTheTunerTheSessionsFoundBetter()
    {
        devices.Signals["north"] = new ScriptedQualitySource { Standing = Readings.Measured(17) };
        devices.Signals["south"] = new ScriptedQualitySource { Standing = Readings.Measured(25) };
        TunerSessionManager manager = Manager();
        TunerSession first = Begin(manager, "first", "north");
        ReadOnce("north");
        Stop(first);
        TunerSession second = Begin(manager, "second", "south");
        ReadOnce("south");
        Stop(second);
        clock.Advance(TimeSpan.FromSeconds(10));

        TunerSession third = Begin(manager, "third");

        Assert.Equal("south", third.DeviceId);

        Stop(third);
    }

    private sealed class Devices : ITunerDeviceFactory
    {
        public Dictionary<string, ScriptedQualitySource> Signals { get; } = [];

        public HashSet<(string DeviceId, int Channel)> Dead { get; } = [];

        public HashSet<string> LosesTheChannel { get; } = [];

        public HashSet<string> Breaks { get; } = [];

        public Dictionary<string, PacedTunerDevice> Opened { get; } = [];

        public ITunerDevice Create(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold)
        {
            string deviceId = device.Id!;

            if (Dead.Contains((deviceId, tuning.PhysicalChannel)))
            {
                throw DvbFailure.NoLock($"{deviceId} did not lock.");
            }

            var paced = new PacedTunerDevice { Signal = Signals.GetValueOrDefault(deviceId) };

            Opened[deviceId] = paced;

            if (LosesTheChannel.Contains(deviceId))
            {
                return new Failing(paced, DvbFailure.NoLock($"{deviceId} is no longer locked."));
            }

            return Breaks.Contains(deviceId) ? new Failing(paced, new IOException($"{deviceId} stopped answering.")) : paced;
        }

        private sealed class Failing(PacedTunerDevice inner, Exception failure) : ITunerDevice
        {
            public long Overflows => 0;

            public ISignalQualitySource? Quality => inner.Quality;

            public byte[] Read(int count, CancellationToken cancellationToken)
            {
                inner.Read(count, cancellationToken);

                throw failure;
            }

            public void Dispose() => inner.Dispose();
        }
    }
}
