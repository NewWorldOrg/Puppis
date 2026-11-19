using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.Bridge;

namespace Puppis.Driver.Tests;

public sealed class HeldTurnTests
{
    private const int RequestsTogether = 7;

    private const int Rounds = 200;

    private static (TurnTakingConnection Turns, WatchedConnection Watched) Connection()
    {
        var watched = new WatchedConnection(new FakeBridge());

        return (new TurnTakingConnection(watched), watched);
    }

    private static UsbSetup At(uint address) => BridgeMemory.SetupFor(BridgeMemory.ReadRequestType, address);

    private static void Ask(TurnTakingConnection turns, uint address) => turns.ControlIn(At(address), new byte[4], TimeSpan.Zero);

    private static Thread Started(Action work)
    {
        Thread thread = new(() => work()) { IsBackground = true };
        thread.Start();

        return thread;
    }

    private static bool Queued(Thread thread) =>
        SpinWait.SpinUntil(() => thread.ThreadState.HasFlag(ThreadState.WaitSleepJoin), WatchedConnection.Patience);

    private static void AskTogether(TurnTakingConnection turns)
    {
        using HeldTurn turn = turns.TakeTurn();

        for (int request = 0; request < RequestsTogether; request++)
        {
            Ask(turns, 0x1000);
        }
    }

    private static void Repeat(Action work)
    {
        for (int round = 0; round < Rounds; round++)
        {
            work();
        }
    }

    [Fact]
    public void RequestsMadeInOneTurnAreNotMixedWithAnotherCallers()
    {
        (TurnTakingConnection turns, WatchedConnection watched) = Connection();

        Thread[] callers = [.. Enumerable.Range(0, 4).Select(_ => Started(() => Repeat(() => AskTogether(turns))))];
        Thread single = Started(() => Repeat(() => Ask(turns, 0x2000)));

        Assert.All([.. callers, single], caller => Assert.True(caller.Join(WatchedConnection.Patience)));
        Assert.Equal(1, watched.MostAtOnce);
        Assert.Equal((4 * RequestsTogether * Rounds) + Rounds, watched.Requests.Count);
        Assert.All(
            watched.Runs().Where(run => run.Thread != single.ManagedThreadId),
            run => Assert.Equal(0, run.Count % RequestsTogether)
        );
    }

    [Fact]
    public void ATurnTakenInsideATurnIsTheSameTurn()
    {
        (TurnTakingConnection turns, WatchedConnection watched) = Connection();
        Thread? other = null;

        Thread nesting = Started(() =>
        {
            using HeldTurn outer = turns.TakeTurn();
            AskTogether(turns);
            turns.Together(() => turns.ControlIn(At(0x1000), new byte[4], TimeSpan.Zero));
            other = Started(() => Ask(turns, 0x2000));
            Queued(other);
            Ask(turns, 0x3000);
        });

        Assert.True(nesting.Join(WatchedConnection.Patience));
        Assert.True(other!.Join(WatchedConnection.Patience));
        Assert.Equal(
            [.. Enumerable.Repeat(0x1000u, RequestsTogether + 1), 0x3000u, 0x2000u],
            watched.Requests.Select(request => request.Address)
        );
    }

    [Fact]
    public void AFailureInsideATurnGivesTheTurnBack()
    {
        (TurnTakingConnection turns, WatchedConnection watched) = Connection();
        watched.FailAt(request => request.Address is 0x2000);

        Assert.Throws<DvbDeviceException>(() => Ask(turns, 0x2000));
        Assert.Throws<DvbDeviceException>(() => turns.ControlOut(At(0x2000), new byte[4], TimeSpan.Zero));
        Assert.Throws<DvbDeviceException>(() =>
        {
            using HeldTurn turn = turns.TakeTurn();
            Ask(turns, 0x1000);
            Ask(turns, 0x2000);
        });
        Assert.Throws<InvalidOperationException>(() => turns.Together<int>(() => throw new InvalidOperationException()));

        Thread after = Started(() => AskTogether(turns));

        Assert.True(after.Join(WatchedConnection.Patience));
        Assert.Equal(1, watched.MostAtOnce);
    }

    [Fact]
    public void ATurnGivenBackTwiceLetsOnlyOneCallerIn()
    {
        (TurnTakingConnection turns, WatchedConnection watched) = Connection();
        HeldTurn turn = turns.TakeTurn();
        HeldTurn copy = turn;
        watched.HoldAt(request => request.Address is 0x2000);
        Thread second = Started(() => Ask(turns, 0x2000));
        Assert.True(Queued(second));
        Thread third = Started(() => Ask(turns, 0x3000));
        Assert.True(Queued(third));

        turn.Dispose();
        Assert.True(watched.WaitUntilHolding());
        copy.Dispose();

        Assert.False(SpinWait.SpinUntil(() => watched.Requests.Count > 1, TimeSpan.FromMilliseconds(300)));
        watched.LetGo();
        Assert.All([second, third], caller => Assert.True(caller.Join(WatchedConnection.Patience)));
        Assert.Equal(1, watched.MostAtOnce);
        Assert.Equal([0x2000u, 0x3000u], watched.Requests.Select(request => request.Address));
    }

    [Fact]
    public void ATurnGivenBackTwiceDoesNotEndTheTurnTakenAfterIt()
    {
        (TurnTakingConnection turns, WatchedConnection watched) = Connection();
        HeldTurn turn = turns.TakeTurn();
        HeldTurn copy = turn;
        turn.Dispose();
        HeldTurn next = turns.TakeTurn();
        Thread other = Started(() => Ask(turns, 0x2000));
        Assert.True(Queued(other));

        copy.Dispose();
        Ask(turns, 0x1000);

        Assert.False(SpinWait.SpinUntil(() => watched.Requests.Count > 1, TimeSpan.FromMilliseconds(300)));
        next.Dispose();
        Assert.True(other.Join(WatchedConnection.Patience));
        Assert.Equal(1, watched.MostAtOnce);
        Assert.Equal([0x1000u, 0x2000u], watched.Requests.Select(request => request.Address));
    }

    [Fact]
    public void ClosingWhileCallersWaitLeavesNobodyWaiting()
    {
        (TurnTakingConnection turns, WatchedConnection watched) = Connection();
        watched.HoldAt(request => request.Address is 0x1000);
        Thread holder = Started(() => Ask(turns, 0x1000));
        Assert.True(watched.WaitUntilHolding());
        Thread second = Started(() => Ask(turns, 0x2000));
        Assert.True(Queued(second));
        Thread third = Started(() => AskTogether(turns));
        Assert.True(Queued(third));

        turns.Dispose();
        watched.LetGo();

        Assert.True(watched.Disposed);
        Assert.All([holder, second, third], caller => Assert.True(caller.Join(WatchedConnection.Patience)));
        Assert.Equal(1, watched.MostAtOnce);
        Assert.Equal(
            [0x1000u, 0x2000u, .. Enumerable.Repeat(0x1000u, RequestsTogether)],
            watched.Requests.Select(request => request.Address)
        );
    }
}
