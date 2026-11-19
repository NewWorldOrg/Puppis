using System.Collections.Concurrent;

using Puppis.Contracts;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.Bridge;
using Puppis.Driver.Tuning.Usb.CechZd1;

namespace Puppis.Driver.Tests;

public sealed class CechZd1TurnsTests
{
    private const string Device = "/dev/bus/usb/001/002";

    private const int FewestSessions = 6;

    private static readonly long Channel22 = BroadcastStandards.TerrestrialCentreHz(22);

    private static readonly DevicePacing Pacing = new(TimeProvider.System, static _ => { });

    private readonly ConcurrentQueue<Exception> failures = new();
    private long sessions;
    private long readings;
    private long checks;
    private long probes;
    private long stoppedProbes;
    private long letGoes;
    private long lastLetGo;

    private static (ClaimingUsbHost Host, WatchedConnection Watched) Tuner()
    {
        (FakeChip demodulator, FakeChip tuner) = UsbTestSupport.LockingChips();
        var watched = new WatchedConnection(UsbTestSupport.BridgeWith(demodulator, tuner));

        return (new ClaimingUsbHost(watched), watched);
    }

    private static CechZd1TunerDevice Open(ClaimingUsbHost host, CechZd1Standby? standby = null) =>
        CechZd1TunerDevice.Open(host, Device, Channel22, Pacing, TimeSpan.FromSeconds(5), null, standby);

    private Thread Started(Action work)
    {
        Thread thread = new(() => Watched(work)) { IsBackground = true };
        thread.Start();

        return thread;
    }

    private void Watched(Action work)
    {
        try
        {
            work();
        }
        catch (Exception failed)
        {
            failures.Enqueue(failed);
        }
    }

    private static bool Queued(Thread thread) =>
        SpinWait.SpinUntil(() => thread.ThreadState.HasFlag(ThreadState.WaitSleepJoin), WatchedConnection.Patience);

    private static int RequestsOf(WatchedConnection watched, Action work)
    {
        int before = watched.Requests.Count;
        work();

        return watched.Requests.Count - before;
    }

    private static string Outcome(Func<SignalQuality> measure)
    {
        try
        {
            return measure().HasLock ? "locked" : "not locked";
        }
        catch (ObjectDisposedException)
        {
            return "closed";
        }
    }

    private void MeasureUntilClosed(ISignalQualitySource source)
    {
        while (Outcome(source.Measure) is not "closed")
        {
            Interlocked.Increment(ref readings);
        }
    }

    [Fact]
    public void ASignalReadingIsNotMixedWithTheClose()
    {
        (ClaimingUsbHost host, WatchedConnection watched) = Tuner();
        CechZd1TunerDevice device = Open(host);
        int reading = RequestsOf(watched, () => device.Measure());
        int opened = watched.Requests.Count;

        Thread sampler = Started(() => MeasureUntilClosed(device));
        Assert.True(SpinWait.SpinUntil(() => watched.Requests.Count >= opened + (3 * reading), WatchedConnection.Patience));
        Thread closer = Started(device.Dispose);

        Assert.All([sampler, closer], thread => Assert.True(thread.Join(WatchedConnection.Patience)));
        Assert.Empty(failures);
        Assert.Equal(1, watched.MostAtOnce);
        Assert.True(watched.BulkClosed);
        Assert.Single(watched.Runs(), run => run.Thread == closer.ManagedThreadId);
        Assert.All(
            watched.Runs().Where(run => run.Thread == sampler.ManagedThreadId),
            run => Assert.Equal(0, run.Count % reading)
        );
    }

    [Fact]
    public void AProbesReadingIsNotMixedWithItsClose()
    {
        (ClaimingUsbHost host, WatchedConnection watched) = Tuner();
        using var standby = new CechZd1Standby();
        Open(host, standby).Dispose();
        CechZd1ReceptionProbe probe = CechZd1ReceptionProbe.Open(host, Device, Channel22, Pacing, null, standby);
        int reading = RequestsOf(watched, () => probe.Measure());
        int opened = watched.Requests.Count;

        Thread sampler = Started(() => MeasureUntilClosed(new ProbeAsSource(probe)));
        Assert.True(SpinWait.SpinUntil(() => watched.Requests.Count >= opened + (3 * reading), WatchedConnection.Patience));
        Thread closer = Started(probe.Dispose);

        Assert.All([sampler, closer], thread => Assert.True(thread.Join(WatchedConnection.Patience)));
        Assert.Empty(failures);
        Assert.Equal(1, watched.MostAtOnce);
        Assert.True(standby.Holds(Device));
        Assert.Single(watched.Runs(), run => run.Thread == closer.ManagedThreadId);
        Assert.All(
            watched.Runs().Where(run => run.Thread == sampler.ManagedThreadId),
            run => Assert.Equal(0, run.Count % reading)
        );
    }

    [Fact]
    public void TheStreamIsReadWhileAnotherCallerIsInItsTurn()
    {
        (ClaimingUsbHost host, WatchedConnection watched) = Tuner();
        CechZd1TunerDevice device = Open(host);
        watched.HoldAt(_ => true);
        Thread sampler = Started(() => device.Measure());
        Assert.True(watched.WaitUntilHolding());

        byte[] read = device.Read(188 * 10, CancellationToken.None);

        Assert.Equal(188 * 10, read.Length);
        Assert.True(sampler.IsAlive);
        watched.LetGo();
        Assert.True(sampler.Join(WatchedConnection.Patience));
        Assert.Empty(failures);
        device.Dispose();
    }

    [Fact]
    public void ClosingWhileSignalReadingsWaitLeavesNobodyWaitingAndServesThemInOrder()
    {
        (ClaimingUsbHost host, WatchedConnection watched) = Tuner();
        using var standby = new CechZd1Standby();
        CechZd1TunerDevice device = Open(host, standby);
        string[] outcomes = new string[3];
        watched.HoldAt(_ => true);

        Thread first = Started(() => outcomes[0] = Outcome(device.Measure));
        Assert.True(watched.WaitUntilHolding());
        Thread second = Started(() => outcomes[1] = Outcome(device.Measure));
        Assert.True(Queued(second));
        Thread closer = Started(device.Dispose);
        Assert.True(Queued(closer));
        Thread late = Started(() => outcomes[2] = Outcome(device.Measure));
        Assert.True(Queued(late));
        watched.LetGo();

        Assert.All([first, second, closer, late], thread => Assert.True(thread.Join(WatchedConnection.Patience)));
        Assert.Empty(failures);
        Assert.Equal(["locked", "locked", "closed"], outcomes);
        Assert.Equal(1, watched.MostAtOnce);
        Assert.True(standby.Holds(Device));
    }

    [Fact]
    public void AFailureWhileTheSignalIsReadGivesTheTurnBack()
    {
        (ClaimingUsbHost host, WatchedConnection watched) = Tuner();
        CechZd1TunerDevice device = Open(host);
        watched.FailAt(request => !request.Writes && request.Address is BridgeI2c.Data);

        Assert.Throws<DvbDeviceException>(() => device.Measure());
        watched.FailAt(null);
        Thread sampler = Started(() => device.Measure());
        Assert.True(sampler.Join(WatchedConnection.Patience));
        Thread closer = Started(device.Dispose);

        Assert.True(closer.Join(WatchedConnection.Patience));
        Assert.Empty(failures);
        Assert.Equal("closed", Outcome(device.Measure));
    }

    [Fact]
    public void EverythingThatTalksToTheTunerTakesTurns()
    {
        (ClaimingUsbHost host, WatchedConnection watched) = Tuner();
        using var standby = new CechZd1Standby();
        using var stop = new CancellationTokenSource();

        Thread checking = Started(() => Until(stop.Token, () => Check(host, standby)));
        Thread probing = Started(() => Until(stop.Token, () => Probe(host, standby, stopped: false)));
        Thread stopping = Started(() => Until(stop.Token, () => Probe(host, standby, stopped: true)));
        Thread lettingGo = Started(() => Until(stop.Token, () => LetGo(standby)));
        Thread streaming = Started(() => Sessions(host, standby));

        Assert.True(streaming.Join(WatchedConnection.Patience * 4));
        stop.Cancel();
        Assert.All([checking, probing, stopping, lettingGo], thread => Assert.True(thread.Join(WatchedConnection.Patience)));
        Assert.Empty(failures);
        Assert.Equal(1, watched.MostAtOnce);
        Assert.Equal(0, host.UsedAfterClosing);
        Assert.True(watched.BulkTakes > 0);
        Assert.All(
            (long[])[sessions - FewestSessions + 1, readings, checks, probes, stoppedProbes, letGoes, host.Opened - 1],
            count => Assert.True(count > 0)
        );
    }

    private bool EveryoneHadAGo() =>
        Interlocked.Read(ref sessions) >= FewestSessions
        && Interlocked.Read(ref readings) > 0
        && Interlocked.Read(ref checks) > 0
        && Interlocked.Read(ref probes) > 0
        && Interlocked.Read(ref stoppedProbes) > 0
        && Interlocked.Read(ref letGoes) > 0;

    private long Others() =>
        Interlocked.Read(ref checks) + Interlocked.Read(ref probes) + Interlocked.Read(ref stoppedProbes) + Interlocked.Read(ref letGoes);

    private static void Until(CancellationToken stop, Action work)
    {
        while (!stop.IsCancellationRequested)
        {
            work();
            Thread.Yield();
        }
    }

    private void Sessions(ClaimingUsbHost host, CechZd1Standby standby)
    {
        long deadline = Environment.TickCount64 + (long)(WatchedConnection.Patience * 3).TotalMilliseconds;

        while (!EveryoneHadAGo() && Environment.TickCount64 < deadline)
        {
            long others = Others();
            Session(host, standby);
            SpinWait.SpinUntil(() => Others() > others, TimeSpan.FromSeconds(1));
        }
    }

    private void Session(ClaimingUsbHost host, CechZd1Standby standby)
    {
        if (OpenWhenFree(host, standby) is not { } device)
        {
            return;
        }

        Thread sampler = Started(() => MeasureUntilClosed(device));
        Read(device, 8);
        Thread closer = Started(device.Dispose);
        Read(device, 8);
        closer.Join(WatchedConnection.Patience);
        sampler.Join(WatchedConnection.Patience);
        Interlocked.Increment(ref sessions);
    }

    private static CechZd1TunerDevice? OpenWhenFree(ClaimingUsbHost host, CechZd1Standby standby)
    {
        long deadline = Environment.TickCount64 + (long)WatchedConnection.Patience.TotalMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            try
            {
                return Open(host, standby);
            }
            catch (DvbDeviceException)
            {
                Thread.Yield();
            }
        }

        return null;
    }

    private static void Read(CechZd1TunerDevice device, int times)
    {
        for (int read = 0; read < times; read++)
        {
            Assert.NotEmpty(device.Read(188 * 64, CancellationToken.None));
        }
    }

    private void Check(ClaimingUsbHost host, CechZd1Standby standby)
    {
        bool kept = standby.Holds(Device);

        try
        {
            CechZd1TunerDevice.OpenAndClose(host, Device, standby);
            Interlocked.Add(ref checks, kept ? 1 : 0);
        }
        catch (Exception refused) when (refused is DvbDeviceException or InvalidOperationException)
        {
            Thread.Yield();
        }
    }

    private void Probe(ClaimingUsbHost host, CechZd1Standby standby, bool stopped)
    {
        using var stopping = new CancellationTokenSource();

        if (stopped)
        {
            stopping.CancelAfter(TimeSpan.FromMilliseconds(1));
        }

        try
        {
            using CechZd1ReceptionProbe probe = CechZd1ReceptionProbe.Open(host, Device, Channel22, Pacing, null, standby, stopping.Token);
            stopping.Token.ThrowIfCancellationRequested();
            probe.Measure();
            Interlocked.Increment(ref probes);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref stoppedProbes);
        }
        catch (DvbDeviceException)
        {
            Thread.Yield();
        }
    }

    private void LetGo(CechZd1Standby standby)
    {
        long seen = Interlocked.Read(ref sessions);

        if (seen < lastLetGo + 2 || !standby.HoldsATuner)
        {
            return;
        }

        standby.Release();
        lastLetGo = seen;
        Interlocked.Increment(ref letGoes);
    }
}
