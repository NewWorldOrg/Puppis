using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Sessions;
using Puppis.Driver.Tuning;

namespace Puppis.Driver.Tests;

public sealed class TunerHandBackTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 8, 13, 21, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Deadlock = TimeSpan.FromSeconds(30);

    private static readonly TuningKey Here = new(TunerKind.Terrestrial, 55, 0);

    private static readonly TuningKey Elsewhere = new(TunerKind.Terrestrial, 57, 0);

    private readonly string root = Directory.CreateTempSubdirectory("puppis-hand-back-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    private DriverConfiguration Configuration(params string[] devices) =>
        new(
            "/run/carina/driver.sock",
            [new OutputRootSettings("primary", root)],
            6,
            new TunerSettings(TunerBackend.Fake),
            [.. devices.Select(id => new DeviceSettings(id, DeviceKind.Terrestrial))]
        );

    private static StartSessionRequest Request(string sessionId, SessionPurpose purpose, int channel = 55, string? deviceId = null) =>
        new()
        {
            SessionId = SessionId.Parse(sessionId),
            Purpose = purpose,
            Tuning = new TuningRequest(TunerKind.Terrestrial, channel, 50001),
            DeviceId = deviceId,
            OutputRoot = purpose is SessionPurpose.Recording ? "primary" : null,
            EndsAt = purpose is SessionPurpose.Recording ? DateTimeOffset.UtcNow.AddHours(1) : null,
            RecordingId = purpose is SessionPurpose.Recording ? $"k-{sessionId}" : null,
        };

    [Fact]
    public void AFailedTuneHoldsBackOnlyARetryOfTheSameChannel()
    {
        var clock = new ManualTimeProvider(Start);
        var pool = new TunerPool(clock, TimeSpan.FromSeconds(5), [("adapter0", true)]);
        string[] one = ["adapter0"];

        PoolGrant failed = pool.Acquire(new PoolRequest(SessionId.Parse("s-1"), SessionPurpose.Live, Here, null, one));
        pool.TuningFailed(failed.DeviceId, failed.Holder, new IOException("the frontend would not lock"));
        clock.Advance(TimeSpan.FromSeconds(2));

        PoolGrant retry = pool.Acquire(new PoolRequest(SessionId.Parse("s-2"), SessionPurpose.Live, Here, null, one));

        Assert.Equal(PoolVerdict.NoDeviceFree, retry.Verdict);
        Assert.Contains("would not lock", retry.Detail, StringComparison.Ordinal);

        PoolGrant recording = pool.Acquire(new PoolRequest(SessionId.Parse("rec"), SessionPurpose.Recording, Elsewhere, null, one));

        Assert.Equal(PoolVerdict.Granted, recording.Verdict);
        Assert.True(recording.NeedsTuning);
    }

    [Fact]
    public void ATunerSeenAbsentIsLookedAtAgainBeforeARequestIsTurnedAway()
    {
        bool plugged = false;
        DriverConfiguration configuration = Configuration("adapter0");
        var manager = new TunerSessionManager(
            configuration,
            new TunerDeviceFactory(configuration, TimeProvider.System),
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance,
            presence: _ => Volatile.Read(ref plugged)
        );

        manager.Looked(configuration.Devices![0], false);

        Assert.Equal(SessionRefusal.DeviceUnavailable, manager.Begin(Request("early", SessionPurpose.Live, deviceId: "adapter0")).Refusal);

        Volatile.Write(ref plugged, true);

        Assert.True(manager.Begin(Request("named", SessionPurpose.Live, deviceId: "adapter0")).TryGetSession(out TunerSession? named));
        named.Stop();
        named.WaitForEnd(Deadlock);
    }

    [Fact]
    public void WhenEveryTunerIsUnpluggedTheRequestHearsTheTunerIsUnavailableAsBefore()
    {
        DriverConfiguration configuration = Configuration("adapter0");
        var manager = new TunerSessionManager(
            configuration,
            new TunerDeviceFactory(configuration, TimeProvider.System),
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance,
            presence: _ => false
        );

        manager.Looked(configuration.Devices![0], false);

        SessionStart refused = manager.Begin(Request("live", SessionPurpose.Live));

        Assert.Equal(SessionRefusal.DeviceUnavailable, refused.Refusal);
        Assert.Contains("not plugged in", refused.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ATunerPluggedBackInIsUsedRatherThanTakingAnotherFromItsSession()
    {
        bool plugged = false;
        DriverConfiguration configuration = Configuration("adapter0", "adapter1");
        var manager = new TunerSessionManager(
            configuration,
            new TunerDeviceFactory(configuration, TimeProvider.System),
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance,
            presence: device => device.Id is "adapter1" || Volatile.Read(ref plugged)
        );

        manager.Looked(configuration.Devices![0], false);
        Assert.True(manager.Begin(Request("survey", SessionPurpose.Survey)).TryGetSession(out TunerSession? survey));
        Assert.Equal("adapter1", survey.DeviceId);

        Volatile.Write(ref plugged, true);

        Assert.True(manager.Begin(Request("rec", SessionPurpose.Recording, 57)).TryGetSession(out TunerSession? recording));
        Assert.Equal("adapter0", recording.DeviceId);
        Assert.Equal(SessionState.Active, survey.State);

        survey.Stop();
        recording.Stop();
        survey.WaitForEnd(Deadlock);
        recording.WaitForEnd(Deadlock);
    }

    [Fact]
    public async Task AStartThatFailsAfterTheTunerWasOpenedGivesItBackClosed()
    {
        var factory = new CountingFactory();
        DriverConfiguration configuration = Configuration("adapter0") with { LiveSessionMinutes = 0 };
        var manager = new TunerSessionManager(
            configuration,
            factory,
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance
        );

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.BeginAsync(Request("live", SessionPurpose.Live)));

        TunerAllocationView after = Assert.Single(manager.Allocations());
        Assert.Equal(TunerAllocationState.Free, after.State);
        Assert.Empty(after.Sinks);
        Assert.Equal(0, factory.Open);
    }

    [Fact]
    public async Task AStartThatFailsWhileCuttingOffTheLoserGivesTheTunerBackToIt()
    {
        var factory = new CountingFactory { ThrowWhenCut = true };
        var manager = new TunerSessionManager(
            Configuration("adapter0"),
            factory,
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance,
            handOverLimit: Deadlock
        );

        Assert.True((await manager.BeginAsync(Request("survey", SessionPurpose.Survey))).TryGetSession(out TunerSession? loser));
        factory.AwaitReading();

        await Assert.ThrowsAnyAsync<Exception>(() => manager.BeginAsync(Request("rec", SessionPurpose.Recording, 57)));

        TunerAllocationView after = Assert.Single(manager.Allocations());
        Assert.Equal(TunerAllocationState.InUse, after.State);
        Assert.Equal([SessionId.Parse("survey")], after.Sinks);
        Assert.Equal(1, factory.Open);

        factory.Release();
        loser.WaitForEnd(Deadlock);
    }

    [Fact]
    public async Task AStartThatFailsBeforeCuttingOffTheLoserGivesTheTunerBackToIt()
    {
        var factory = new CountingFactory();
        var manager = new TunerSessionManager(
            Configuration("adapter0"),
            factory,
            TimeProvider.System,
            new ThrowingWhenCuttingOff(),
            handOverLimit: Deadlock
        );

        Assert.True((await manager.BeginAsync(Request("survey", SessionPurpose.Survey))).TryGetSession(out TunerSession? loser));
        factory.AwaitReading();

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.BeginAsync(Request("rec", SessionPurpose.Recording, 57)));

        TunerAllocationView after = Assert.Single(manager.Allocations());
        Assert.Equal(TunerAllocationState.InUse, after.State);
        Assert.Equal([SessionId.Parse("survey")], after.Sinks);
        Assert.Equal(1, factory.Open);

        factory.Release();
        loser.Stop();
        loser.WaitForEnd(Deadlock);
    }

    [Fact]
    public async Task ACallerThatGoesAwayWhileTheLoserIsStoppingGetsNothingAndTheTunerGoesBack()
    {
        var factory = new CountingFactory();
        var manager = new TunerSessionManager(
            Configuration("adapter0"),
            factory,
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance,
            handOverLimit: Deadlock
        );

        Assert.True((await manager.BeginAsync(Request("survey", SessionPurpose.Survey))).TryGetSession(out TunerSession? loser));
        factory.AwaitReading();
        using var leaving = new CancellationTokenSource();

        Task<SessionStart> taking = manager.BeginAsync(Request("rec", SessionPurpose.Recording, 57), leaving.Token);
        await leaving.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taking.WaitAsync(Deadlock));

        TunerAllocationView after = Assert.Single(manager.Allocations());
        Assert.Equal(TunerAllocationState.InUse, after.State);
        Assert.DoesNotContain(SessionId.Parse("rec"), after.Sinks);
        Assert.Equal(1, factory.Open);

        factory.Release();
        loser.WaitForEnd(Deadlock);
    }

    [Fact]
    public async Task AClaimantDisplacedBeforeItOpensIsNoLongerConfirmed()
    {
        var pool = new TunerPool(TimeProvider.System, declared: [("adapter0", true)]);
        string[] one = ["adapter0"];

        PoolGrant survey = pool.Acquire(new PoolRequest(SessionId.Parse("survey"), SessionPurpose.Survey, Here, null, one));
        pool.Tuned(survey.DeviceId, survey.Holder, new FakeTunerDevice(55));
        pool.Ready(survey.DeviceId, survey.Holder);
        PoolGrant live = pool.Acquire(new PoolRequest(SessionId.Parse("live"), SessionPurpose.Live, Elsewhere, null, one));
        pool.Leave(survey.Holder);

        Assert.True(await live.Cleared.WaitAsync(Deadlock));
        Assert.True(pool.Confirm("adapter0", live.Holder));

        pool.Acquire(new PoolRequest(SessionId.Parse("rec"), SessionPurpose.Recording, new TuningKey(TunerKind.Terrestrial, 59, 0), null, one));

        Assert.False(pool.Confirm("adapter0", live.Holder));
    }

    [Fact]
    public void AFaultedTunerIsNotQuietWhileTheOneOpeningItHasNotReported()
    {
        var pool = new TunerPool(TimeProvider.System, declared: [("adapter0", true)]);
        PoolGrant opening = pool.Acquire(new PoolRequest(SessionId.Parse("live"), SessionPurpose.Live, Here, null, ["adapter0"]));

        pool.Fault("adapter0", new TunerFault("broke", null, DeviceFaultKind.Failed));

        Assert.False(pool.IsQuiet("adapter0"));

        var late = new FakeTunerDevice(55);
        Assert.False(pool.Tuned(opening.DeviceId, opening.Holder, late));
        Assert.True(pool.IsQuiet("adapter0"));
    }

    private sealed class ThrowingWhenCuttingOff : ILogger<TunerSessionManager>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (formatter(state, exception).Contains("is being cut off", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("the log could not be written");
            }
        }
    }

    private sealed class CountingFactory : ITunerDeviceFactory
    {
        private readonly ManualResetEventSlim released = new(false);
        private readonly ManualResetEventSlim reading = new(false);
        private int open;

        public bool ThrowWhenCut { get; init; }

        public int Open => Volatile.Read(ref open);

        public void AwaitReading() => Assert.True(reading.Wait(Deadlock));

        public void Release() => released.Set();

        public ITunerDevice Create(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold)
        {
            Interlocked.Increment(ref open);

            return new Held(this);
        }

        private sealed class Held(CountingFactory factory) : ITunerDevice
        {
            private int closed;

            public long Overflows => 0;

            public byte[] Read(int count, CancellationToken cancellationToken)
            {
                if (factory.ThrowWhenCut)
                {
                    cancellationToken.Register(() => throw new InvalidOperationException("the read would not be cut short"));
                }

                factory.reading.Set();
                factory.released.Wait(Deadlock);

                throw new OperationCanceledException(cancellationToken);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref closed, 1) is 0)
                {
                    Interlocked.Decrement(ref factory.open);
                }
            }
        }
    }
}
