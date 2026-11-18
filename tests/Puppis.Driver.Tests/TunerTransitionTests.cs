using Puppis.Contracts;
using Puppis.Driver.Sessions;
using Puppis.Driver.Tuning;

using On = Puppis.Driver.Sessions.TunerAllocationEvent;
using State = Puppis.Driver.Sessions.TunerAllocationState;

namespace Puppis.Driver.Tests;

public sealed class TunerTransitionTests
{
    private const string Tuner = "adapter0";

    private static readonly DateTimeOffset Start = new(2026, 8, 13, 21, 0, 0, TimeSpan.Zero);

    private static readonly TuningKey Here = new(TunerKind.Terrestrial, 55, 0);

    private static readonly TuningKey There = new(TunerKind.Terrestrial, 57, 0);

    private static readonly TuningKey Yonder = new(TunerKind.Terrestrial, 59, 0);

    private static readonly TunerFault Broken = new("it broke", null, DeviceFaultKind.Failed);

    private sealed class Table
    {
        public ManualTimeProvider Clock { get; } = new(Start);

        public TunerPool Pool { get; }

        public Table() => Pool = new TunerPool(Clock, TimeSpan.FromSeconds(5), [(Tuner, true)]);

        public PoolGrant Ask(string sessionId, SessionPurpose purpose, TuningKey tuning) =>
            Pool.Acquire(new PoolRequest(SessionId.Parse(sessionId), purpose, tuning, null, [Tuner]));

        public void Tune(string sessionId) => Pool.Tuned(Tuner, SessionId.Parse(sessionId), new FakeTunerDevice(55));

        public void Settle(string sessionId) => Pool.Ready(Tuner, SessionId.Parse(sessionId));

        public void Leave(string sessionId) => Pool.Leave(SessionId.Parse(sessionId));

        public State Now => Pool.StateOf(Tuner);
    }

    private static Table Free() => new();

    private static Table Ready()
    {
        Table table = InUse();
        table.Leave("rider");
        table.Leave("holder");

        return table;
    }

    private static Table Tuning()
    {
        Table table = new();
        table.Ask("holder", SessionPurpose.Survey, Here);
        table.Ask("rider", SessionPurpose.Survey, Here);

        return table;
    }

    private static Table InUse()
    {
        Table table = Tuning();
        table.Tune("holder");
        table.Settle("holder");

        return table;
    }

    private static Table HandingOver()
    {
        Table table = InUse();
        table.Ask("claimant", SessionPurpose.Live, There);

        return table;
    }

    private static Table HandingOverFromTuning()
    {
        Table table = Tuning();
        table.Ask("claimant", SessionPurpose.Live, There);

        return table;
    }

    private static Table Absent()
    {
        Table table = new();
        table.Pool.Looked(Tuner, false);

        return table;
    }

    private static Table Disabled()
    {
        Table table = new();
        table.Pool.Turn(Tuner, false);

        return table;
    }

    private static Table Faulted()
    {
        Table table = new();
        table.Pool.Fault(Tuner, Broken);

        return table;
    }

    private static Table From(State state) =>
        state switch
        {
            State.Free => Free(),
            State.Ready => Ready(),
            State.Tuning => Tuning(),
            State.InUse => InUse(),
            State.HandingOver => HandingOver(),
            State.Absent => Absent(),
            State.Disabled => Disabled(),
            _ => Faulted(),
        };

    private static readonly Dictionary<(State, On), Func<Table>> Scenarios = new()
    {
        [(State.Free, On.Claim)] = () => Then(Free(), table => table.Ask("new", SessionPurpose.Live, Here)),
        [(State.Ready, On.Claim)] = () => Then(Ready(), table => table.Ask("new", SessionPurpose.Live, There)),
        [(State.Tuning, On.Ride)] = () => Then(Tuning(), table => table.Ask("more", SessionPurpose.Survey, Here)),
        [(State.InUse, On.Ride)] = () => Then(InUse(), table => table.Ask("more", SessionPurpose.Survey, Here)),
        [(State.HandingOver, On.Ride)] = () => Then(HandingOver(), table => table.Ask("more", SessionPurpose.Survey, There)),
        [(State.Tuning, On.TakeSeat)] = () => Then(Tuning(), table => table.Ask("rec", SessionPurpose.Recording, Here)),
        [(State.InUse, On.TakeSeat)] = () => Then(InUse(), table => table.Ask("rec", SessionPurpose.Recording, Here)),
        [(State.HandingOver, On.TakeSeat)] = () => Then(HandingOver(), table => table.Ask("rec", SessionPurpose.Recording, There)),
        [(State.Tuning, On.SeatSettled)] = () => Then(Tuning(), table =>
        {
            table.Ask("rec", SessionPurpose.Recording, Here);
            table.Pool.SeatTaken(Tuner, SessionId.Parse("rec"));
        }),
        [(State.InUse, On.SeatSettled)] = () => Then(InUse(), table =>
        {
            table.Ask("rec", SessionPurpose.Recording, Here);
            table.Pool.SeatTaken(Tuner, SessionId.Parse("rec"));
        }),
        [(State.InUse, On.PassOn)] = () => Then(InUse(), table =>
            Assert.True(table.Pool.HandTheTunerOn(SessionId.Parse("holder"), SessionId.Parse("rider")))),
        [(State.Tuning, On.TakeOver)] = () => Then(Tuning(), table => table.Ask("rec", SessionPurpose.Recording, There)),
        [(State.InUse, On.TakeOver)] = () => Then(InUse(), table => table.Ask("rec", SessionPurpose.Recording, There)),
        [(State.HandingOver, On.TakeOver)] = () => Then(HandingOver(), table => table.Ask("rec", SessionPurpose.Recording, Yonder)),
        [(State.Tuning, On.Opened)] = () => Then(Tuning(), table => table.Tune("holder")),
        [(State.Tuning, On.Established)] = () => Then(Tuning(), table => table.Settle("holder")),
        [(State.InUse, On.Established)] = () => Then(InUse(), table => table.Settle("rider")),
        [(State.Tuning, On.Released)] = () => Then(Tuning(), table => table.Leave("rider")),
        [(State.InUse, On.Released)] = () => Then(InUse(), table => table.Leave("rider")),
        [(State.HandingOver, On.Released)] = () => Then(HandingOver(), table =>
        {
            table.Ask("more", SessionPurpose.Survey, There);
            table.Leave("more");
        }),
        [(State.Tuning, On.Emptied)] = () => Then(Tuning(), table =>
        {
            table.Tune("holder");
            table.Leave("holder");
        }),
        [(State.InUse, On.Emptied)] = () => Then(InUse(), table => table.Leave("holder")),
        [(State.Tuning, On.EmptiedClosed)] = () => Then(Tuning(), table => table.Leave("holder")),
        [(State.InUse, On.EmptiedClosed)] = () => Then(Tuning(), table =>
        {
            table.Settle("holder");
            table.Leave("holder");
        }),
        [(State.Tuning, On.EmptiedDisabled)] = () => Then(Tuning(), table =>
        {
            table.Pool.Turn(Tuner, false);
            table.Leave("holder");
        }),
        [(State.InUse, On.EmptiedDisabled)] = () => Then(InUse(), table =>
        {
            table.Pool.Turn(Tuner, false);
            table.Leave("holder");
        }),
        [(State.Tuning, On.TuneFailed)] = () => Then(Tuning(), table =>
            table.Pool.TuningFailed(Tuner, SessionId.Parse("holder"), new IOException("no lock"))),
        [(State.HandingOver, On.PreviousGone)] = () => Then(HandingOver(), table => table.Leave("holder")),
        [(State.HandingOver, On.GaveBack)] = () => Then(HandingOver(), table =>
            Assert.True(table.Pool.GiveBack(Tuner, SessionId.Parse("claimant")))),
        [(State.HandingOver, On.GaveBackTuning)] = () => Then(HandingOverFromTuning(), table =>
            Assert.True(table.Pool.GiveBack(Tuner, SessionId.Parse("claimant")))),
        [(State.Ready, On.Expired)] = () => Then(Ready(), table =>
        {
            table.Clock.Advance(TimeSpan.FromSeconds(6));
            table.Pool.Sweep();
        }),
        [(State.Free, On.Unplugged)] = () => Then(Free(), table => table.Pool.Looked(Tuner, false)),
        [(State.Ready, On.Unplugged)] = () => Then(Ready(), table => table.Pool.Looked(Tuner, false)),
        [(State.Absent, On.PluggedIn)] = () => Then(Absent(), table => table.Pool.Looked(Tuner, true)),
        [(State.Disabled, On.Enabled)] = () => Then(Disabled(), table => table.Pool.Turn(Tuner, true)),
        [(State.Disabled, On.EnabledAbsent)] = () => Then(Absent(), table =>
        {
            table.Pool.Turn(Tuner, false);
            table.Pool.Turn(Tuner, true);
        }),
        [(State.Disabled, On.EnabledFaulted)] = () => Then(Faulted(), table =>
        {
            table.Pool.Turn(Tuner, false);
            table.Pool.Turn(Tuner, true);
        }),
        [(State.Tuning, On.Enabled)] = () => Then(Tuning(), Toggle),
        [(State.InUse, On.Enabled)] = () => Then(InUse(), Toggle),
        [(State.HandingOver, On.Enabled)] = () => Then(HandingOver(), Toggle),
        [(State.Faulted, On.Restored)] = () => Then(Faulted(), table => table.Pool.Restore(Tuner)),
        [(State.Faulted, On.RestoredAbsent)] = () => Then(Absent(), table =>
        {
            table.Pool.Fault(Tuner, Broken);
            table.Pool.Restore(Tuner);
        }),
        [(State.Disabled, On.Restored)] = () => Then(Faulted(), table =>
        {
            table.Pool.Turn(Tuner, false);
            table.Pool.Restore(Tuner);
        }),
        [(State.Disabled, On.Faulted)] = () => Then(Disabled(), table => table.Pool.Fault(Tuner, Broken)),
    };

    private static void Toggle(Table table)
    {
        table.Pool.Turn(Tuner, false);
        table.Pool.Turn(Tuner, true);
    }

    private static Table Then(Table table, Action<Table> act)
    {
        act(table);

        return table;
    }

    private static Func<Table> ScenarioFor(State from, On on) =>
        Scenarios.TryGetValue((from, on), out Func<Table>? scenario)
            ? scenario
            : on switch
            {
                On.Disabled => () => Then(From(from), table => table.Pool.Turn(Tuner, false)),
                On.Faulted => () => Then(From(from), table => table.Pool.Fault(Tuner, Broken)),
                _ => throw new InvalidOperationException($"No scenario reaches {from} x {on}."),
            };

    public static TheoryData<State, On, State> Listed()
    {
        TheoryData<State, On, State> rows = [];

        foreach ((State from, On on, State to) in TunerTransitions.All)
        {
            rows.Add(from, on, to);
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(Listed))]
    public void EveryListedTransitionIsReachedAndLandsWhereTheTableSays(State from, On on, State to)
    {
        Table before = ScenarioFor(from, on)();

        Assert.Equal(to, before.Now);
        Assert.Equal(0, before.Pool.RefusedTransitions);
    }

    [Fact]
    public void EveryPairTheTableDoesNotListIsRefused()
    {
        HashSet<(State, On)> listed = [.. TunerTransitions.All.Select(row => (row.From, row.On))];

        foreach (State from in Enum.GetValues<State>())
        {
            foreach (On on in Enum.GetValues<On>())
            {
                Assert.Equal(listed.Contains((from, on)), TunerTransitions.TryMove(from, on, out _));
            }
        }
    }

    [Fact]
    public void NoPairIsListedTwice()
    {
        Assert.Equal(
            TunerTransitions.All.Count,
            TunerTransitions.All.Select(row => (row.From, row.On)).Distinct().Count()
        );
    }

    [Fact]
    public void AnEventTheTableDoesNotListIsRefusedCountedAndChangesNothing()
    {
        Table table = HandingOver();

        table.Settle("claimant");

        Assert.Equal(State.HandingOver, table.Now);
        Assert.Equal(1, table.Pool.RefusedTransitions);
        Assert.False(table.Pool.AwaitReady(Tuner, TimeSpan.Zero));
    }

    [Fact]
    public void AReportFromSomeoneTheTunerIsNotForMovesNothing()
    {
        Table table = Free();

        table.Settle("stranger");
        table.Leave("stranger");

        Assert.Equal(State.Free, table.Now);
        Assert.Equal(0, table.Pool.RefusedTransitions);
    }
}
