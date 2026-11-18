using Microsoft.Extensions.Logging.Abstractions;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Sessions;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tests;

public sealed class TunerOpeningTests : IDisposable
{
    private static readonly TimeSpan Deadlock = TimeSpan.FromSeconds(30);

    private static readonly TuningKey Here = new(TunerKind.Terrestrial, 55, 0);

    private static readonly TuningKey Elsewhere = new(TunerKind.Terrestrial, 57, 0);

    private readonly string root = Directory.CreateTempSubdirectory("puppis-opening-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    private DriverConfiguration Configuration(params string[] devices) =>
        new(
            "/run/carina/driver.sock",
            [new OutputRootSettings("primary", root)],
            6,
            new TunerSettings(TunerBackend.Fake),
            [.. devices.Select(id => new DeviceSettings(id, DeviceKind.Terrestrial))]
        );

    private TunerSessionManager Manager(ITunerDeviceFactory factory, TimeSpan? grace = null) =>
        new(
            Configuration("adapter0"),
            factory,
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance,
            tunerGrace: grace,
            handOverLimit: Deadlock
        );

    private static StartSessionRequest Request(string sessionId, SessionPurpose purpose, int channel = 55) =>
        new()
        {
            SessionId = SessionId.Parse(sessionId),
            Purpose = purpose,
            Tuning = new TuningRequest(TunerKind.Terrestrial, channel, 50001),
            OutputRoot = purpose is SessionPurpose.Recording ? "primary" : null,
            EndsAt = purpose is SessionPurpose.Recording ? DateTimeOffset.UtcNow.AddHours(1) : null,
            RecordingId = purpose is SessionPurpose.Recording ? $"k-{sessionId}" : null,
        };

    private static async Task<TunerSession> StartedAsync(Task<SessionStart> starting)
    {
        SessionStart start = await starting.WaitAsync(Deadlock);

        Assert.True(start.TryGetSession(out TunerSession? session), $"{start.Refusal}: {start.Detail}");

        return session;
    }

    private static async Task StopAsync(params TunerSession[] sessions)
    {
        foreach (TunerSession session in sessions)
        {
            session.Stop();
            await session.Completion.WaitAsync(Deadlock);
        }
    }

    [Fact]
    public async Task ARecordingThatTookTheSeatKeepsTheOpenTheViewerStartedWhenTheViewerLeavesMidOpen()
    {
        var factory = new GatedFactory();
        TunerSessionManager manager = Manager(factory);
        using var leaving = new CancellationTokenSource();

        Task<SessionStart> live = manager.BeginAsync(Request("live", SessionPurpose.Live), leaving.Token);
        factory.AwaitOpening();
        Task<SessionStart> recording = manager.BeginAsync(Request("rec", SessionPurpose.Recording));

        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live.WaitAsync(Deadlock));
        Assert.Equal(TunerAllocationState.Tuning, Assert.Single(manager.Allocations()).State);

        factory.Open();
        TunerSession taken = await StartedAsync(recording);

        Assert.Equal("adapter0", taken.DeviceId);
        Assert.Null(taken.RidesOn);
        Assert.Equal(1, factory.Opens);
        Assert.Equal(1, factory.MostOpenAtOnce);

        await StopAsync(taken);
    }

    [Fact]
    public async Task ARiderKeepsTheOpenTheViewerStartedWhenTheViewerLeavesMidOpen()
    {
        var factory = new GatedFactory();
        TunerSessionManager manager = Manager(factory);
        using var leaving = new CancellationTokenSource();

        Task<SessionStart> live = manager.BeginAsync(Request("live", SessionPurpose.Live), leaving.Token);
        factory.AwaitOpening();
        Task<SessionStart> rider = manager.BeginAsync(Request("rider", SessionPurpose.Live));

        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live.WaitAsync(Deadlock));

        factory.Open();
        TunerSession kept = await StartedAsync(rider);

        Assert.Null(kept.RidesOn);
        Assert.Equal(1, factory.Opens);

        await StopAsync(kept);
    }

    [Fact]
    public async Task ARecordingThatTookTheSeatDuringAHandOverOpensTheTunerItselfWhenTheClaimantLeavesFirst()
    {
        var factory = new GatedFactory { OpenAtOnce = true };
        TunerSessionManager manager = Manager(factory);
        TunerSession scan = await StartedAsync(manager.BeginAsync(Request("scan", SessionPurpose.Scan)));
        factory.HoldReads();
        factory.AwaitHeldRead();
        using var leaving = new CancellationTokenSource();

        Task<SessionStart> live = manager.BeginAsync(Request("live", SessionPurpose.Live, 57), leaving.Token);
        Task<SessionStart> recording = manager.BeginAsync(Request("rec", SessionPurpose.Recording, 57));

        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live.WaitAsync(Deadlock));
        Assert.Equal(TunerAllocationState.HandingOver, Assert.Single(manager.Allocations()).State);

        factory.ReleaseReads();
        TunerSession taken = await StartedAsync(recording);

        Assert.Equal(2, factory.Opens);
        Assert.Equal(1, factory.MostOpenAtOnce);
        Assert.Equal(SessionStopReason.Preempted, scan.StopReason);

        await StopAsync(taken);
    }

    [Fact]
    public async Task ARecordingThatTookTheSeatOpensTheTunerItselfWhenTheViewerLeavesBeforeTheOpenStarted()
    {
        var factory = new GatedFactory { OpenAtOnce = true };
        TunerSessionManager manager = Manager(factory, TimeSpan.FromMilliseconds(100));
        TunerSession first = await StartedAsync(manager.BeginAsync(Request("first", SessionPurpose.Live, 57)));
        factory.SlowClose();
        await StopAsync(first);
        factory.AwaitClosing();
        using var leaving = new CancellationTokenSource();

        Task<SessionStart> live = manager.BeginAsync(Request("live", SessionPurpose.Live), leaving.Token);

        Assert.False(live.IsCompleted);
        Assert.Equal(TunerAllocationState.Tuning, Assert.Single(manager.Allocations()).State);

        Task<SessionStart> recording = manager.BeginAsync(Request("rec", SessionPurpose.Recording));

        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live.WaitAsync(Deadlock));

        factory.FinishClosing();
        TunerSession taken = await StartedAsync(recording);

        Assert.Equal(2, factory.Opens);
        Assert.Equal(1, factory.MostOpenAtOnce);

        await StopAsync(taken);
    }

    [Fact]
    public async Task AChannelThatDoesNotLockRefusesEveryoneWaitingOnTheOpenWithTheSameAnswer()
    {
        var factory = new GatedFactory { Failure = TuningFailure.NoLock };
        TunerSessionManager manager = Manager(factory);

        Task<SessionStart> live = manager.BeginAsync(Request("live", SessionPurpose.Live));
        factory.AwaitOpening();
        Task<SessionStart> recording = manager.BeginAsync(Request("rec", SessionPurpose.Recording));
        Task<SessionStart> rider = manager.BeginAsync(Request("rider", SessionPurpose.Survey));

        factory.Open();
        SessionStart[] answers = await Task.WhenAll(live, recording, rider).WaitAsync(Deadlock);

        Assert.All(answers, answer => Assert.Equal(SessionRefusal.NoLock, answer.Refusal));
        Assert.Single(answers.Select(answer => answer.Detail).Distinct());
    }

    [Fact]
    public async Task ATunerWhoseOpenIsStillUnderWayIsNotHandedToAnotherUntilTheOpenReports()
    {
        var factory = new GatedFactory();
        TunerSessionManager manager = Manager(factory);
        using var leaving = new CancellationTokenSource();

        Task<SessionStart> live = manager.BeginAsync(Request("live", SessionPurpose.Live), leaving.Token);
        factory.AwaitOpening();
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live.WaitAsync(Deadlock));

        TunerAllocationView settling = Assert.Single(manager.Allocations());
        Assert.Equal(TunerAllocationState.Tuning, settling.State);
        Assert.Empty(settling.Sinks);
        Assert.True(settling.OpeningInFlight);

        Task<SessionStart> recording = manager.BeginAsync(Request("rec", SessionPurpose.Recording, 57));

        Assert.False(recording.IsCompleted);

        factory.OpenAtOnce = true;
        factory.Open();
        TunerSession taken = await StartedAsync(recording);

        Assert.Equal(1, factory.MostOpenAtOnce);
        Assert.Equal(2, factory.Opens);

        await StopAsync(taken);
    }

    [Fact]
    public async Task AReaderAlreadyTurnedAwayHearsWhyAtOnceRatherThanWaiting()
    {
        var pool = new TunerPool(TimeProvider.System, declared: [("adapter0", true)]);
        string[] one = ["adapter0"];

        PoolGrant opener = pool.Acquire(new PoolRequest(SessionId.Parse("live"), SessionPurpose.Live, Here, null, one));
        pool.Acquire(new PoolRequest(SessionId.Parse("rider"), SessionPurpose.Survey, Here, null, one));
        pool.Acquire(new PoolRequest(SessionId.Parse("rec"), SessionPurpose.Recording, Elsewhere, null, one));
        pool.Leave(opener.Holder);
        pool.Leave(SessionId.Parse("rec"));

        Task<TunerReadiness> late = pool.AwaitReadyAsync("adapter0", Deadlock, SessionId.Parse("rider"));

        Assert.True(late.IsCompleted);
        Assert.False((await late).Ready);
    }

    private sealed class GatedFactory : ITunerDeviceFactory
    {
        private readonly SemaphoreSlim opening = new(0);
        private readonly SemaphoreSlim opened = new(0);
        private readonly ManualResetEventSlim reads = new(true);
        private readonly ManualResetEventSlim heldRead = new(false);
        private readonly ManualResetEventSlim closing = new(false);
        private readonly ManualResetEventSlim closed = new(true);
        private readonly Lock gate = new();
        private int open;
        private int most;
        private int opens;

        public bool OpenAtOnce { get; set; }

        public TuningFailure? Failure { get; init; }

        public int Opens => Volatile.Read(ref opens);

        public int MostOpenAtOnce
        {
            get
            {
                lock (gate)
                {
                    return most;
                }
            }
        }

        public void AwaitOpening() => Assert.True(opening.Wait(Deadlock));

        public void Open() => opened.Release();

        public void HoldReads() => reads.Reset();

        public void ReleaseReads() => reads.Set();

        public void AwaitHeldRead() => Assert.True(heldRead.Wait(Deadlock));

        public void SlowClose() => closed.Reset();

        public void AwaitClosing() => Assert.True(closing.Wait(Deadlock));

        public void FinishClosing() => closed.Set();

        public ITunerDevice Create(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold)
        {
            lock (gate)
            {
                open++;
                most = Math.Max(most, open);
            }

            Interlocked.Increment(ref opens);
            opening.Release();

            if (!OpenAtOnce)
            {
                Assert.True(opened.Wait(Deadlock));
            }

            if (Failure is { } failure)
            {
                Closed();

                throw new DvbDeviceException("the frontend would not lock", failure);
            }

            return new Device(this, tuning.PhysicalChannel);
        }

        private void Closed()
        {
            lock (gate)
            {
                open--;
            }
        }

        private sealed class Device(GatedFactory factory, int channel) : ITunerDevice
        {
            private readonly FakeTunerDevice inner = new(channel);
            private int disposed;

            public long Overflows => 0;

            public byte[] Read(int count, CancellationToken cancellationToken)
            {
                if (!factory.reads.IsSet)
                {
                    factory.heldRead.Set();
                }

                factory.reads.Wait(Deadlock);
                Thread.Sleep(1);

                return inner.Read(count, cancellationToken);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) is 1)
                {
                    return;
                }

                factory.closing.Set();
                factory.closed.Wait(Deadlock);
                factory.Closed();
            }
        }
    }
}
