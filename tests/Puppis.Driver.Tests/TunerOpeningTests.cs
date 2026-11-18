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

    private TunerSessionManager Manager(
        ITunerDeviceFactory factory,
        TimeSpan? grace = null,
        TimeSpan? openLimit = null,
        DriverConfiguration? configuration = null
    ) =>
        new(
            configuration ?? Configuration("adapter0"),
            factory,
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance,
            tunerGrace: grace,
            handOverLimit: Deadlock,
            openLimit: openLimit
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
        Assert.Equal(HeadHold.EveryProgramme, factory.LastHold);

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
        TunerReadiness heard = await late;
        Assert.False(heard.Ready);
        Assert.NotNull(heard.Refusal);
    }

    [Fact]
    public async Task ARequestForTheSameChannelTakesOverAnOpenNobodyIsWaitingForAnyMore()
    {
        var factory = new GatedFactory();
        TunerSessionManager manager = Manager(factory);
        using var leaving = new CancellationTokenSource();

        Task<SessionStart> live = manager.BeginAsync(Request("live", SessionPurpose.Live), leaving.Token);
        factory.AwaitOpening();
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live.WaitAsync(Deadlock));

        Task<SessionStart> again = manager.BeginAsync(Request("again", SessionPurpose.Live));

        Assert.Equal([SessionId.Parse("again")], Assert.Single(manager.Allocations()).Sinks);

        factory.Open();
        TunerSession taken = await StartedAsync(again);

        Assert.Equal(1, factory.Opens);

        await StopAsync(taken);
    }

    [Fact]
    public async Task WhenTheOpenFailsAfterTheOpenerLeftEveryoneWaitingHearsTheSame()
    {
        var factory = new GatedFactory { Failure = TuningFailure.LockedWithoutData };
        TunerSessionManager manager = Manager(factory);
        using var leaving = new CancellationTokenSource();

        Task<SessionStart> live = manager.BeginAsync(Request("live", SessionPurpose.Live), leaving.Token);
        factory.AwaitOpening();
        Task<SessionStart> recording = manager.BeginAsync(Request("rec", SessionPurpose.Recording));
        Task<SessionStart> rider = manager.BeginAsync(Request("rider", SessionPurpose.Survey));

        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live.WaitAsync(Deadlock));

        factory.Open();
        SessionStart[] answers = await Task.WhenAll(recording, rider).WaitAsync(Deadlock);

        Assert.All(answers, answer => Assert.Equal(SessionRefusal.NoData, answer.Refusal));
        Assert.Single(answers.Select(answer => answer.Detail).Distinct());
    }

    [Fact]
    public async Task WhenTheOpenerFailsAfterTheTunerOpenedTheOneWaitingUsesTheSameDevice()
    {
        var factory = new GatedFactory { OpenAtOnce = true };
        TunerSessionManager manager = Manager(factory, configuration: Configuration("adapter0") with { LiveSessionMinutes = 0 });
        factory.HoldOpens();

        Task<SessionStart> live = manager.BeginAsync(Request("live", SessionPurpose.Live));
        factory.AwaitOpening();
        Task<SessionStart> recording = manager.BeginAsync(Request("rec", SessionPurpose.Recording));
        factory.Open();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => live.WaitAsync(Deadlock));
        TunerSession taken = await StartedAsync(recording);

        Assert.Equal(1, factory.Opens);

        await StopAsync(taken);
    }

    [Fact]
    public async Task AnOpenThatNeverReturnsRefusesThoseWaitingWithItsOwnReason()
    {
        var factory = new GatedFactory();
        TunerSessionManager manager = Manager(factory, openLimit: TimeSpan.FromMilliseconds(300));
        using var leaving = new CancellationTokenSource();

        Task<SessionStart> live = manager.BeginAsync(Request("live", SessionPurpose.Live), leaving.Token);
        factory.AwaitOpening();
        Task<SessionStart> recording = manager.BeginAsync(Request("rec", SessionPurpose.Recording));
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live.WaitAsync(Deadlock));

        SessionStart refused = await recording.WaitAsync(Deadlock);

        Assert.Equal(SessionRefusal.DeviceUnavailable, refused.Refusal);
        Assert.Contains("did not open", refused.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("let go", refused.Detail, StringComparison.Ordinal);
        Assert.Equal(TunerAllocationState.Tuning, Assert.Single(manager.Allocations()).State);

        factory.Open();
    }

    private TunerSessionManager Stuck(GatedFactory factory, TimeSpan handOver, params string[] devices) =>
        new(
            Configuration(devices),
            factory,
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance,
            handOverLimit: handOver,
            openLimit: TimeSpan.FromMilliseconds(300)
        );

    private static async Task LeaveAnOpenStuckAsync(TunerSessionManager manager, GatedFactory factory)
    {
        using var leaving = new CancellationTokenSource();

        Task<SessionStart> live = manager.BeginAsync(Request("live", SessionPurpose.Live), leaving.Token);
        factory.AwaitOpening();
        factory.OpenAtOnce = true;
        Task<SessionStart> waiting = manager.BeginAsync(Request("rec-1", SessionPurpose.Recording));
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live.WaitAsync(Deadlock));

        Assert.Equal(SessionRefusal.DeviceUnavailable, (await waiting.WaitAsync(Deadlock)).Refusal);
    }

    [Fact]
    public async Task AnOpenPastItsLimitIsNotTakenOverWhileAnotherTunerIsFree()
    {
        var factory = new GatedFactory();
        TunerSessionManager manager = Stuck(factory, Deadlock, "adapter0", "adapter1");

        await LeaveAnOpenStuckAsync(manager, factory);

        TunerSession served = await StartedAsync(manager.BeginAsync(Request("rec-2", SessionPurpose.Recording)));

        Assert.Equal("adapter1", served.DeviceId);

        factory.Open();
        await StopAsync(served);
    }

    [Fact]
    public async Task WithOneTunerARequestWaitsForTheStuckOpenAndIsServedOnceItHasReturned()
    {
        var factory = new GatedFactory();
        TunerSessionManager manager = Stuck(factory, TimeSpan.FromMilliseconds(500), "adapter0");

        await LeaveAnOpenStuckAsync(manager, factory);

        SessionStart refused = await manager.BeginAsync(Request("rec-2", SessionPurpose.Recording)).WaitAsync(Deadlock);

        Assert.Equal(SessionRefusal.DeviceBusy, refused.Refusal);

        factory.Open();
        await WaitUntilAsync(() => manager.Allocations()[0].State is TunerAllocationState.Free);

        TunerSession served = await StartedAsync(manager.BeginAsync(Request("rec-3", SessionPurpose.Recording)));

        Assert.Equal("adapter0", served.DeviceId);
        Assert.Equal(1, factory.MostOpenAtOnce);

        await StopAsync(served);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime giveUp = DateTime.UtcNow + Deadlock;

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < giveUp, "The tuner never settled.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task AFreeTunerWithNothingUnderWayIsPreferredToOneStillClosingItsDevice()
    {
        var factory = new GatedFactory { OpenAtOnce = true };
        TunerSessionManager manager = new(
            Configuration("adapter0", "adapter1"),
            factory,
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance,
            tunerGrace: TimeSpan.FromMilliseconds(100),
            handOverLimit: Deadlock
        );
        TunerSession first = await StartedAsync(manager.BeginAsync(Request("first", SessionPurpose.Live, 57)));
        Assert.Equal("adapter0", first.DeviceId);
        factory.SlowClose();
        await StopAsync(first);
        factory.AwaitClosing();

        TunerSession next = await StartedAsync(manager.BeginAsync(Request("next", SessionPurpose.Live)));

        Assert.Equal("adapter1", next.DeviceId);

        factory.FinishClosing();
        await StopAsync(next);
    }

    [Fact]
    public void AnOpenAbandonedMidWayIsClosedWhenItLandsRatherThanKept()
    {
        var pool = new TunerPool(TimeProvider.System, declared: [("adapter0", true)]);
        PoolGrant opener = pool.Acquire(new PoolRequest(SessionId.Parse("live"), SessionPurpose.Live, Here, null, ["adapter0"]));
        OpenTicket ticket = pool.StartOpening(opener.DeviceId, opener.Holder)!;

        pool.Abandon(opener.DeviceId, opener.Holder);
        var device = new ClosableTunerDevice();
        pool.Opened(ticket, device);

        Assert.True(device.Disposed);
        Assert.Equal(TunerAllocationState.Free, pool.StateOf("adapter0"));
    }

    [Fact]
    public void AStepThatFailsWhileATunerIsHandedOutDoesNotLoseTheGrant()
    {
        var pool = new TunerPool(
            TimeProvider.System,
            TimeSpan.FromMinutes(5),
            [("adapter0", true)],
            _ => throw new InvalidOperationException("the kept device would not let go")
        );
        string[] one = ["adapter0"];
        PoolGrant first = pool.Acquire(new PoolRequest(SessionId.Parse("live"), SessionPurpose.Live, Here, null, one));
        pool.Tuned(first.DeviceId, first.Holder, new ThrowingOnClose());
        pool.Ready(first.DeviceId, first.Holder);
        pool.Leave(first.Holder);

        PoolGrant second = pool.Acquire(new PoolRequest(SessionId.Parse("next"), SessionPurpose.Live, Elsewhere, null, one));

        Assert.Equal(PoolVerdict.Granted, second.Verdict);
        Assert.Equal([SessionId.Parse("next")], pool.SinksOn("adapter0"));

        pool.Leave(second.Holder);

        Assert.Empty(pool.SinksOn("adapter0"));
    }

    private sealed class ThrowingOnClose : ITunerDevice
    {
        public long Overflows => 0;

        public byte[] Read(int count, CancellationToken cancellationToken) => new byte[count];

        public void Dispose() => throw new InvalidOperationException("the device would not close");
    }

    [Fact]
    public void EveryActionAfterAChangeRunsEvenWhenOneOfThemThrows()
    {
        var pool = new TunerPool(
            TimeProvider.System,
            declared: [("adapter0", true)],
            letGo: _ => throw new InvalidOperationException("the kept device would not let go")
        );
        PoolGrant opener = pool.Acquire(new PoolRequest(SessionId.Parse("live"), SessionPurpose.Live, Here, null, ["adapter0"]));
        OpenTicket ticket = pool.StartOpening(opener.DeviceId, opener.Holder)!;
        Assert.Throws<InvalidOperationException>(() => pool.Turn("adapter0", false));
        pool.Leave(opener.Holder);
        var device = new ClosableTunerDevice();

        Assert.Throws<InvalidOperationException>(() => pool.Opened(ticket, device));

        Assert.True(device.Disposed);
        Assert.True(ticket.Outcome.IsCompleted);
        Assert.True(pool.IsQuiet("adapter0"));
        Assert.Equal(TunerAllocationState.Disabled, pool.StateOf("adapter0"));
    }

    [Fact]
    public async Task ATunerTakenOverAfterItsOpenWasGivenUpServesTheRiderThatJoinsIt()
    {
        var pool = new TunerPool(TimeProvider.System, declared: [("adapter0", true)]);
        string[] one = ["adapter0"];
        PoolGrant opener = pool.Acquire(new PoolRequest(SessionId.Parse("live"), SessionPurpose.Live, Here, null, one));
        OpenTicket ticket = pool.StartOpening(opener.DeviceId, opener.Holder)!;
        pool.GiveBack(opener.DeviceId, opener.Holder, new TunerRefusal(SessionRefusal.DeviceBusy, "gave up"));

        PoolGrant adopted = pool.Acquire(new PoolRequest(SessionId.Parse("again"), SessionPurpose.Live, Here, null, one));
        pool.Acquire(new PoolRequest(SessionId.Parse("rider"), SessionPurpose.Survey, Here, null, one));
        Task<TunerReadiness> riding = pool.AwaitReadyAsync("adapter0", Deadlock, SessionId.Parse("rider"));

        Assert.False(riding.IsCompleted);

        pool.Opened(ticket, new FakeTunerDevice(55));
        pool.Ready(adopted.DeviceId, adopted.Holder);

        Assert.True((await riding.WaitAsync(Deadlock)).Ready);
    }

    [Fact]
    public void ARiderWhoseWaitRunsOutJustAsTheTunerBecomesReadyIsServed()
    {
        (TunerPool pool, SteppedTimeProvider clock, PoolGrant opener) = ARiderWaiting();

        TunerReadiness heard = AtTheDeadline(clock, () => pool.AwaitReadyAsync("adapter0", TimeSpan.FromSeconds(5), SessionId.Parse("rider")), () => pool.Ready(opener.DeviceId, opener.Holder));

        Assert.True(heard.Ready);
    }

    [Fact]
    public void ARiderWhoseWaitRunsOutJustAsItIsMadeTheOpenerIsToldToOpen()
    {
        (TunerPool pool, SteppedTimeProvider clock, PoolGrant opener) = ARiderWaiting();

        TunerReadiness heard = AtTheDeadline(clock, () => pool.AwaitReadyAsync("adapter0", TimeSpan.FromSeconds(5), SessionId.Parse("rider")), () => pool.Leave(opener.Holder));

        Assert.True(heard.Leads);
    }

    private static (TunerPool Pool, SteppedTimeProvider Clock, PoolGrant Opener) ARiderWaiting()
    {
        var clock = new SteppedTimeProvider(new DateTimeOffset(2026, 8, 13, 21, 0, 0, TimeSpan.Zero));
        var pool = new TunerPool(clock, declared: [("adapter0", true)]);
        string[] one = ["adapter0"];
        PoolGrant opener = pool.Acquire(new PoolRequest(SessionId.Parse("live"), SessionPurpose.Live, Here, null, one));
        pool.Acquire(new PoolRequest(SessionId.Parse("rider"), SessionPurpose.Survey, Here, null, one));

        return (pool, clock, opener);
    }

    private static TunerReadiness AtTheDeadline(SteppedTimeProvider clock, Func<Task<TunerReadiness>> wait, Action meanwhile)
    {
        SynchronizationContext? before = SynchronizationContext.Current;
        var queued = new QueuedContext();

        SynchronizationContext.SetSynchronizationContext(queued);

        try
        {
            Task<TunerReadiness> waiting = wait();

            var advancing = new Thread(() => clock.Advance(TimeSpan.FromSeconds(5)));
            advancing.Start();
            Assert.True(advancing.Join(Deadlock));
            meanwhile();
            queued.RunAll();

            Assert.True(waiting.IsCompleted);

            return waiting.Result;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(before);
        }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (queue)
            {
                queue.Enqueue((d, state));
            }
        }

        public void RunAll()
        {
            DateTime giveUp = DateTime.UtcNow + Deadlock;

            while (DateTime.UtcNow < giveUp)
            {
                (SendOrPostCallback Callback, object? State) next;

                lock (queue)
                {
                    if (queue.Count is 0)
                    {
                        return;
                    }

                    next = queue.Dequeue();
                }

                next.Callback(next.State);
            }
        }
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

        public HeadHold? LastHold { get; private set; }

        public void HoldOpens() => OpenAtOnce = false;

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
            LastHold = hold;
            bool waits = !OpenAtOnce;
            opening.Release();

            if (waits)
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
