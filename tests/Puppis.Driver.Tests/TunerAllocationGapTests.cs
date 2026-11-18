using Microsoft.Extensions.Logging.Abstractions;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Sessions;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.CechZd1;

namespace Puppis.Driver.Tests;

public sealed class TunerAllocationGapTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 8, 13, 21, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Deadlock = TimeSpan.FromSeconds(30);

    private static readonly TuningKey Here = new(TunerKind.Terrestrial, 55, 0);

    private static readonly TuningKey Elsewhere = new(TunerKind.Terrestrial, 57, 0);

    private readonly string root = Directory.CreateTempSubdirectory("puppis-gaps-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    private DriverConfiguration Configuration(params string[] devices) =>
        new(
            "/run/carina/driver.sock",
            [new OutputRootSettings("primary", root)],
            6,
            new TunerSettings(TunerBackend.Fake),
            [.. devices.Select(id => new DeviceSettings(id, DeviceKind.Terrestrial))]
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

    [Fact]
    public void ATakeOverTheLoserDoesNotLetGoOfLeavesTheLoserOnTheTunerAndNeverOpensItTwice()
    {
        var factory = new StubbornDeviceFactory();
        var manager = new TunerSessionManager(
            Configuration("adapter0"),
            factory,
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance,
            handOverLimit: TimeSpan.FromMilliseconds(200)
        );

        Assert.True(manager.Begin(Request("survey", SessionPurpose.Survey)).TryGetSession(out TunerSession? loser));
        factory.AwaitReading();

        SessionStart refused = manager.Begin(Request("rec-1", SessionPurpose.Recording, 57));

        Assert.Equal(SessionRefusal.DeviceBusy, refused.Refusal);
        Assert.True(manager.IsClaimed("adapter0"));
        TunerAllocationView kept = Assert.Single(manager.Allocations());
        Assert.Equal(TunerAllocationState.InUse, kept.State);
        Assert.Equal(SessionId.Parse("survey"), kept.Holder);
        Assert.True(kept.DeviceOpen);

        SessionStart again = manager.Begin(Request("rec-2", SessionPurpose.Recording, 57));

        Assert.Equal(SessionRefusal.DeviceBusy, again.Refusal);
        Assert.Equal(1, factory.MostOpenAtOnce);
        Assert.False(factory.ClosedWhileRead);

        factory.Release();
        loser.WaitForEnd(Deadlock);
    }

    [Fact]
    public async Task ClosingADeviceDoesNotHoldUpTheTableForAnotherTuner()
    {
        var pool = new TunerPool(TimeProvider.System, TimeSpan.FromMinutes(5));
        var slow = new SlowToCloseDevice();
        string[] both = ["adapter0", "adapter1"];

        PoolGrant first = pool.Acquire(new PoolRequest(SessionId.Parse("s-1"), SessionPurpose.Live, Here, null, both));
        pool.Tuned(first.DeviceId, first.Holder, slow);
        pool.Ready(first.DeviceId, first.Holder);
        pool.Leave(first.Holder);

        Thread retuning = new(() =>
        {
            PoolGrant second = pool.Acquire(new PoolRequest(SessionId.Parse("s-2"), SessionPurpose.Live, Elsewhere, "adapter0", both));
            pool.Tuned(second.DeviceId, second.Holder, new FakeTunerDevice(57));
            pool.Ready(second.DeviceId, second.Holder);
            pool.Leave(second.Holder);
            pool.Acquire(new PoolRequest(SessionId.Parse("s-3"), SessionPurpose.Live, Here, "adapter0", both));
        })
        { IsBackground = true };

        retuning.Start();

        Assert.True(slow.Closing.Wait(Deadlock));

        Task<PoolGrant> other = Task.Run(() =>
            pool.Acquire(new PoolRequest(SessionId.Parse("s-4"), SessionPurpose.Live, Elsewhere, "adapter1", both)));

        bool answered = await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(5))) == other;
        slow.Let();

        Assert.True(answered, "Closing one tuner held up a request for another.");
        Assert.True(retuning.Join(Deadlock));
    }

    [Fact]
    public void ARiderPastTheLimitIsTurnedAwayBeforeItIsCountedOnTheTuner()
    {
        var pool = new TunerPool(TimeProvider.System);
        string[] one = ["adapter0"];

        PoolGrant holder = pool.Acquire(new PoolRequest(SessionId.Parse("h"), SessionPurpose.Survey, Here, null, one));
        pool.Tuned(holder.DeviceId, holder.Holder, new FakeTunerDevice(55));
        pool.Ready(holder.DeviceId, holder.Holder);

        for (int rider = 0; rider < SessionBroadcaster.DefaultSubscriberLimit; rider++)
        {
            Assert.Equal(
                PoolVerdict.Shared,
                pool.Acquire(new PoolRequest(SessionId.Parse($"r-{rider}"), SessionPurpose.Survey, Here, null, one)).Verdict
            );
        }

        PoolGrant tooMany = pool.Acquire(new PoolRequest(SessionId.Parse("late"), SessionPurpose.Live, Here, null, one));

        Assert.Equal(PoolVerdict.DeviceBusy, tooMany.Verdict);
        Assert.DoesNotContain(SessionId.Parse("late"), pool.SinksOn("adapter0"));
        Assert.Equal(
            PoolVerdict.Granted,
            pool.Acquire(new PoolRequest(SessionId.Parse("rec"), SessionPurpose.Live, Elsewhere, null, one)).Verdict
        );
    }

    [Fact]
    public async Task ATakeOverWaitsForTheLoserWithoutHoldingTheCallerAndStartsOnceItHasGone()
    {
        var factory = new StubbornDeviceFactory();
        var manager = new TunerSessionManager(
            Configuration("adapter0"),
            factory,
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance,
            handOverLimit: Deadlock
        );

        SessionStart first = await manager.BeginAsync(Request("survey", SessionPurpose.Survey));
        SessionStart second = await manager.BeginAsync(Request("rider", SessionPurpose.Survey));
        Assert.True(first.TryGetSession(out _));
        Assert.True(second.TryGetSession(out _));
        factory.AwaitReading();

        Task<SessionStart> taking = manager.BeginAsync(Request("rec", SessionPurpose.Recording, 57));

        Assert.False(taking.IsCompleted);
        Assert.Equal(TunerAllocationState.HandingOver, Assert.Single(manager.Allocations()).State);

        factory.Release();
        SessionStart taken = await taking.WaitAsync(Deadlock);

        Assert.Equal(SessionRefusal.None, taken.Refusal);
        Assert.Equal(1, factory.MostOpenAtOnce);
        Assert.False(factory.ClosedWhileRead);

        await manager.StopAsync(SessionId.Parse("rec"), "done", CancellationToken.None);
    }

    [Fact]
    public void AnUnpluggedTunerIsNeverOfferedAndAPluggedOneIsOfferedAgain()
    {
        var factory = new OpeningFactory();
        DriverConfiguration configuration = Configuration("adapter0", "adapter1");
        var manager = new TunerSessionManager(
            configuration,
            factory,
            TimeProvider.System,
            NullLogger<TunerSessionManager>.Instance
        );

        manager.Looked(configuration.Devices![0], false);

        Assert.True(manager.Begin(Request("live", SessionPurpose.Live)).TryGetSession(out TunerSession? live));
        Assert.Equal("adapter1", live.DeviceId);

        SessionStart named = manager.Begin(Request("named", SessionPurpose.Live, 57) with { DeviceId = "adapter0" });

        Assert.Equal(SessionRefusal.DeviceUnavailable, named.Refusal);
        Assert.DoesNotContain("adapter0", factory.Opened);

        manager.Looked(configuration.Devices![0], true);

        Assert.True(
            manager.Begin(Request("again", SessionPurpose.Live, 57) with { DeviceId = "adapter0" }).TryGetSession(out TunerSession? again)
        );
        Assert.Equal("adapter0", again.DeviceId);

        live.Stop();
        again.Stop();
        live.WaitForEnd(Deadlock);
        again.WaitForEnd(Deadlock);
    }

    [Fact]
    public async Task ATunerFaultedOrTurnedOffWhileRequestsArriveIsNeverLeftWithAnOwner()
    {
        for (int round = 0; round < 200; round++)
        {
            var pool = new TunerPool(TimeProvider.System, declared: [("adapter0", true)]);
            using var start = new Barrier(3);

            Task asking = Task.Run(() =>
            {
                start.SignalAndWait();

                for (int request = 0; request < 20; request++)
                {
                    pool.Acquire(new PoolRequest(SessionId.Parse($"s-{request}"), SessionPurpose.Live, Here, null, ["adapter0"]));
                }
            });
            Task faulting = Task.Run(() =>
            {
                start.SignalAndWait();
                pool.Fault("adapter0", new TunerFault("broke", null, DeviceFaultKind.Failed));
            });

            start.SignalAndWait();
            await Task.WhenAll(asking, faulting).WaitAsync(Deadlock);

            TunerAllocationView after = Assert.Single(pool.Snapshot());
            Assert.Equal(TunerAllocationState.Faulted, after.State);
            Assert.Empty(after.Sinks);

            pool.Turn("adapter0", false);
            pool.Restore("adapter0");

            Assert.Equal(
                PoolVerdict.DisabledDevice,
                pool.Acquire(new PoolRequest(SessionId.Parse("late"), SessionPurpose.Live, Here, "adapter0", ["adapter0"])).Verdict
            );
        }
    }

    [Fact]
    public void TwoUsbTunersKeepTheirOwnSleepingDevicesAndOneLetGoOfLeavesTheOther()
    {
        (FakeChip firstDemodulator, FakeChip firstTuner) = UsbTestSupport.LockingChips();
        (FakeChip secondDemodulator, FakeChip secondTuner) = UsbTestSupport.LockingChips();
        FakeBridge first = UsbTestSupport.BridgeWith(firstDemodulator, firstTuner);
        FakeBridge second = UsbTestSupport.BridgeWith(secondDemodulator, secondTuner);
        var host = new TwoBridgeHost(new Dictionary<string, FakeBridge>
        {
            ["/dev/bus/usb/001/002"] = first,
            ["/dev/bus/usb/001/003"] = second,
        });
        DeviceSettings one = new("one", DeviceKind.Terrestrial, "/dev/bus/usb/001/002");
        DeviceSettings other = new("other", DeviceKind.Terrestrial, "/dev/bus/usb/001/003");
        (DevicePacing pacing, ManualTimeProvider clock, _) = UsbTestSupport.Pacing();
        first.Idle = clock.Advance;
        second.Idle = clock.Advance;
        using TunerDeviceFactory factory = TunerDeviceFactory.Using(
            new DriverConfiguration(
                OutputRoots: [new OutputRootSettings("primary", "/srv/recordings")],
                Tuner: new TunerSettings(TunerBackend.Usb),
                Devices: [one, other]
            ),
            host,
            pacing
        );
        TuningRequest channel = new(TunerKind.Terrestrial, 22);

        factory.Create(one, channel, null, HeadHold.EveryProgramme).Dispose();
        factory.Create(other, channel, null, HeadHold.EveryProgramme).Dispose();

        Assert.False(first.Disposed);
        Assert.False(second.Disposed);

        factory.Create(one, channel, null, HeadHold.EveryProgramme).Dispose();

        Assert.Equal(1, host.Opened["/dev/bus/usb/001/002"]);

        factory.LetGo(one);

        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
    }

    private sealed class OpeningFactory : ITunerDeviceFactory
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> opened = new();

        public IReadOnlyCollection<string> Opened => [.. opened];

        public ITunerDevice Create(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold)
        {
            opened.Enqueue(device.Id!);

            return new ScriptedTunerDevice();
        }
    }

    private sealed class TwoBridgeHost(Dictionary<string, FakeBridge> bridges) : IUsbHost
    {
        public Dictionary<string, int> Opened { get; } = [];

        public IReadOnlyList<UsbDeviceNode> List() => [];

        public UsbModel ModelAt(string path) => CechZd1TunerDevice.Model;

        public IUsbConnection Open(string path, byte interfaceNumber)
        {
            Opened[path] = Opened.GetValueOrDefault(path) + 1;

            return bridges[path];
        }
    }

    private sealed class SlowToCloseDevice : ITunerDevice
    {
        private readonly ManualResetEventSlim closed = new(false);

        public ManualResetEventSlim Closing { get; } = new(false);

        public long Overflows => 0;

        public byte[] Read(int count, CancellationToken cancellationToken) => new byte[count];

        public void Let() => closed.Set();

        public void Dispose()
        {
            Closing.Set();
            closed.Wait(Deadlock);
        }
    }

    private sealed class StubbornDeviceFactory : ITunerDeviceFactory
    {
        private readonly ManualResetEventSlim released = new(false);
        private readonly ManualResetEventSlim reading = new(false);
        private readonly Lock gate = new();
        private int open;
        private int most;
        private bool closedWhileRead;

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

        public bool ClosedWhileRead
        {
            get
            {
                lock (gate)
                {
                    return closedWhileRead;
                }
            }
        }

        public void AwaitReading() => Assert.True(reading.Wait(Deadlock));

        public void Release() => released.Set();

        public ITunerDevice Create(DeviceSettings device, TuningRequest tuning, TuneParams? tune, HeadHold hold)
        {
            lock (gate)
            {
                open++;
                most = Math.Max(most, open);
            }

            return new Stubborn(this);
        }

        private sealed class Stubborn(StubbornDeviceFactory factory) : ITunerDevice
        {
            private int inRead;

            public long Overflows => 0;

            public byte[] Read(int count, CancellationToken cancellationToken)
            {
                Interlocked.Exchange(ref inRead, 1);
                factory.reading.Set();
                factory.released.Wait(Deadlock);
                Interlocked.Exchange(ref inRead, 0);

                throw new OperationCanceledException(cancellationToken);
            }

            public void Dispose()
            {
                lock (factory.gate)
                {
                    factory.open--;
                    factory.closedWhileRead |= Volatile.Read(ref inRead) is 1;
                }
            }
        }
    }
}
