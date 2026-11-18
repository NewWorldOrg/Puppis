using Microsoft.Extensions.Logging.Abstractions;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;
using Puppis.Driver.Sessions;
using Puppis.Driver.Tuning;

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
