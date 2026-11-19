using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.Bridge;
using Puppis.Driver.Tuning.Usb.CechZd1;

namespace Puppis.Driver.Tests;

public sealed class BridgeTurnsTests
{
    private const int Rounds = 200;

    private static (TurnTakingConnection Turns, WatchedConnection Watched, FakeChip Demodulator, FakeChip Tuner) Bridge()
    {
        (FakeChip demodulator, FakeChip tuner) = UsbTestSupport.LockingChips();
        var watched = new WatchedConnection(UsbTestSupport.BridgeWith(demodulator, tuner));

        return (new TurnTakingConnection(watched), watched, demodulator, tuner);
    }

    private static Thread Started(Action work)
    {
        Thread thread = new(() => Repeat(work)) { IsBackground = true };
        thread.Start();

        return thread;
    }

    private static void Repeat(Action work)
    {
        for (int round = 0; round < Rounds; round++)
        {
            work();
        }
    }

    private static int RequestsOf(WatchedConnection watched, Action work)
    {
        int before = watched.Requests.Count;
        work();

        return watched.Requests.Count - before;
    }

    private static void AssertWholeRuns(WatchedConnection watched, Thread thread, int requestsEach) =>
        Assert.All(
            watched.Runs().Where(run => run.Thread == thread.ManagedThreadId),
            run => Assert.Equal(0, run.Count % requestsEach)
        );

    [Fact]
    public void ARegisterChangedInPlaceIsReadAndWrittenInOneTurn()
    {
        (TurnTakingConnection turns, WatchedConnection watched, _, _) = Bridge();
        var first = new BridgeMemory(turns);
        var second = new BridgeMemory(turns);

        Thread modifying = Started(() => first.Modify(0x1000, 0, 1));
        Thread halving = Started(() => second.WriteHalf(0x2000, 5));
        Thread reading = Started(() => second.Read(0x3000));

        Assert.All([modifying, halving, reading], thread => Assert.True(thread.Join(WatchedConnection.Patience)));
        Assert.Equal(1, watched.MostAtOnce);
        AssertWholeRuns(watched, modifying, 2);
        AssertWholeRuns(watched, halving, 2);
        Assert.Equal(Rounds * 2, first.Requests);
        Assert.Equal(Rounds * 3, second.Requests);
    }

    [Fact]
    public void AnI2cTransactionIsOneTurn()
    {
        (TurnTakingConnection turns, WatchedConnection watched, _, _) = Bridge();
        var memory = new BridgeMemory(turns);
        var i2c = new BridgeI2c(memory);
        var demodulator = new Dib8000Bus(i2c);
        var tuner = new Dib0090Bus(i2c);
        int writing = RequestsOf(watched, () => demodulator.Write(100, 7));
        int reading = RequestsOf(watched, () => tuner.Read(0x1A));

        Thread writer = Started(() => demodulator.Write(100, 7));
        Thread reader = Started(() => tuner.Read(0x1A));
        Thread modifying = Started(() => memory.Modify(0x1000, 0, 1));
        Thread plain = Started(() => memory.Read(0x3000));

        Assert.All([writer, reader, modifying, plain], thread => Assert.True(thread.Join(WatchedConnection.Patience)));
        Assert.Equal(1, watched.MostAtOnce);
        Assert.True(writing > 2 && reading > 2);
        AssertWholeRuns(watched, writer, writing);
        AssertWholeRuns(watched, reader, reading);
        AssertWholeRuns(watched, modifying, 2);
        Assert.Equal(watched.Requests.Count, memory.Requests);
    }

    [Fact]
    public void TwoMemoriesOnOneDeviceShareItsOneQueue()
    {
        (TurnTakingConnection turns, WatchedConnection watched, _, _) = Bridge();
        var first = new BridgeMemory(turns);
        var second = new BridgeMemory(turns);
        var demodulator = new Dib8000Bus(new BridgeI2c(first));
        int writing = RequestsOf(watched, () => demodulator.Write(100, 7));

        Thread writer = Started(() => demodulator.Write(100, 7));
        Thread modifying = Started(() => second.Modify(0x1000, 0, 1));
        Thread plain = Started(() => second.Read(0x3000));

        Assert.All([writer, modifying, plain], thread => Assert.True(thread.Join(WatchedConnection.Patience)));
        Assert.Equal(1, watched.MostAtOnce);
        AssertWholeRuns(watched, writer, writing);
        AssertWholeRuns(watched, modifying, 2);
        Assert.Equal(watched.Requests.Count, first.Requests + second.Requests);
    }

    [Fact]
    public void AMemoryIsBuiltOnlyOnAConnectionThatTakesTurns()
    {
        Assert.Equal(
            [typeof(TurnTakingConnection)],
            typeof(BridgeMemory).GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType)
        );
    }
}
