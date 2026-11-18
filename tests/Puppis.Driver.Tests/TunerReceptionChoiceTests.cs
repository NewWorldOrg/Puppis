using Microsoft.Extensions.Logging;

using Puppis.Contracts;
using Puppis.Driver.Reception;
using Puppis.Driver.Sessions;
using Puppis.Driver.Tuning;

namespace Puppis.Driver.Tests;

public sealed class TunerReceptionChoiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 13, 21, 0, 0, TimeSpan.Zero);

    private static readonly string[] Tuners = ["north", "south", "west"];

    private static readonly TuningKey Here = new(TunerKind.Terrestrial, 31, 0);

    private static readonly TuningKey There = new(TunerKind.Terrestrial, 33, 0);

    private readonly ManualTimeProvider clock = new(Start);
    private readonly ReceptionBook book = new();
    private readonly CapturingLogger<TunerPool> log = new();
    private readonly HashSet<string> keptOpen = [];

    private TunerPool Pool() =>
        new(
            clock,
            TimeSpan.FromSeconds(5),
            [.. Tuners.Select(id => (id, true))],
            logger: log,
            reception: (deviceId, tuning) => book.StandingOf(deviceId, ReceptionChannel.Of(tuning)),
            keptOpen: keptOpen.Contains
        );

    private void Used(string deviceId, TuningKey tuning, bool locked, double? decibels = null, int times = 1)
    {
        for (int use = 0; use < times; use++)
        {
            book.Note(deviceId, ReceptionChannel.Of(tuning), new ReceptionResult(Start, locked, decibels), () => true);
        }
    }

    private static PoolGrant Ask(
        TunerPool pool,
        string sessionId,
        TuningKey tuning,
        SessionPurpose purpose = SessionPurpose.Live,
        string? deviceId = null
    ) =>
        pool.Acquire(new PoolRequest(SessionId.Parse(sessionId), purpose, tuning, deviceId, Tuners));

    private static PoolGrant Settle(TunerPool pool, PoolGrant grant)
    {
        pool.Tuned(grant.DeviceId, grant.Holder, new FakeTunerDevice(31));
        pool.Ready(grant.DeviceId, grant.Holder);

        return grant;
    }

    private IEnumerable<string> Choices => log.Lines.Where(line => line.Contains("was chosen", StringComparison.Ordinal));

    [Fact]
    public void WithNoRecordsTheFirstTunerInTheConfigurationIsUsed()
    {
        TunerPool pool = Pool();

        Assert.Equal("north", Ask(pool, "s-1", Here).DeviceId);
    }

    [Fact]
    public void TheTunerThatLocksMoreOftenOnTheChannelIsUsed()
    {
        Used("north", Here, locked: false, times: 3);
        Used("north", Here, locked: true, 30, times: 3);
        Used("south", Here, locked: true, 18, times: 4);
        TunerPool pool = Pool();

        Assert.Equal("south", Ask(pool, "s-1", Here).DeviceId);
    }

    [Fact]
    public void TheRecordOfAnotherChannelDoesNotCount()
    {
        Used("north", Here, locked: false, times: 3);
        Used("south", Here, locked: true, 18);
        TunerPool pool = Pool();

        Assert.Equal("north", Ask(pool, "s-1", There).DeviceId);
    }

    [Fact]
    public void TheTunerWithTheClearerSignalIsUsedWhenBothAlwaysLock()
    {
        Used("north", Here, locked: true, 19);
        Used("south", Here, locked: true, 21);
        Used("west", Here, locked: true, 24.5);
        TunerPool pool = Pool();

        Assert.Equal("west", Ask(pool, "s-1", Here).DeviceId);
    }

    [Fact]
    public void ASignalLessThanTwoDecibelsClearerDoesNotChangeTheOrder()
    {
        Used("north", Here, locked: true, 20);
        Used("south", Here, locked: true, 21.9);
        TunerPool pool = Pool();

        Assert.Equal("north", Ask(pool, "s-1", Here).DeviceId);
    }

    [Fact]
    public void ATunerKeptOpenIsUsedBeforeAClosedOneThatReceivesTheSame()
    {
        Used("north", Here, locked: true, 21);
        Used("west", Here, locked: true, 20);
        keptOpen.Add("west");
        TunerPool pool = Pool();

        Assert.Equal("west", Ask(pool, "s-1", Here).DeviceId);
    }

    [Fact]
    public void ATunerKeptOpenIsNotUsedBeforeAClosedOneThatReceivesBetter()
    {
        Used("north", Here, locked: true, 22);
        Used("west", Here, locked: true, 20);
        keptOpen.Add("west");
        TunerPool pool = Pool();

        Assert.Equal("north", Ask(pool, "s-1", Here).DeviceId);
    }

    [Fact]
    public void ATunerHeldForWhoeverComesBackIsLeftAloneWhenAFreeOneReceivesTheSame()
    {
        TunerPool pool = Pool();
        Settle(pool, Ask(pool, "first", There));
        pool.Leave(SessionId.Parse("first"));

        PoolGrant grant = Ask(pool, "s-1", Here);

        Assert.Equal("south", grant.DeviceId);
        Assert.Equal(TunerAllocationState.Ready, pool.StateOf("north"));
    }

    [Fact]
    public void ATunerHeldForWhoeverComesBackIsUsedWhenItReceivesTheChannelBetter()
    {
        Used("north", Here, locked: true, 25);
        Used("south", Here, locked: true, 19);
        Used("west", Here, locked: true, 19);
        TunerPool pool = Pool();
        Settle(pool, Ask(pool, "first", There));
        pool.Leave(SessionId.Parse("first"));

        PoolGrant grant = Ask(pool, "s-1", Here);

        Assert.Equal("north", grant.DeviceId);
        Assert.True(grant.NeedsTuning);
    }

    [Fact]
    public void ATunerWithAPoorRecordIsStillUsedWhenItIsTheOnlyOneFree()
    {
        Used("west", Here, locked: false, times: 20);
        TunerPool pool = Pool();
        Settle(pool, Ask(pool, "first", There));
        Settle(pool, Ask(pool, "second", new TuningKey(TunerKind.Terrestrial, 35, 0)));

        PoolGrant grant = Ask(pool, "s-1", Here);

        Assert.Equal(PoolVerdict.Granted, grant.Verdict);
        Assert.Equal("west", grant.DeviceId);
        Assert.Empty(grant.Displaced);
    }

    [Fact]
    public void RidingAlongStillComesBeforeABetterTunerThatIsFree()
    {
        Used("north", Here, locked: false, times: 5);
        Used("north", Here, locked: true, 15);
        Used("south", Here, locked: true, 30, times: 5);
        TunerPool pool = Pool();
        Settle(pool, Ask(pool, "first", Here, deviceId: "north"));

        PoolGrant grant = Ask(pool, "s-1", Here);

        Assert.Equal(PoolVerdict.Shared, grant.Verdict);
        Assert.Equal("north", grant.DeviceId);
    }

    [Fact]
    public void ARequestThatNamesATunerGetsThatTunerWhateverItsRecord()
    {
        Used("north", Here, locked: false, times: 20);
        Used("south", Here, locked: true, 30, times: 20);
        TunerPool pool = Pool();

        Assert.Equal("north", Ask(pool, "s-1", Here, deviceId: "north").DeviceId);
    }

    [Fact]
    public void ATunerInUseIsNotTakenBecauseItReceivesBetter()
    {
        Used("north", Here, locked: true, 30, times: 20);
        Used("south", Here, locked: false, times: 20);
        Used("west", Here, locked: false, times: 19);
        Used("west", Here, locked: true, 12);
        TunerPool pool = Pool();
        Settle(pool, Ask(pool, "survey", There, SessionPurpose.Survey, "north"));

        PoolGrant grant = Ask(pool, "rec", Here, SessionPurpose.Recording);

        Assert.Equal("west", grant.DeviceId);
        Assert.Empty(grant.Displaced);
        Assert.Equal(TunerAllocationState.InUse, pool.StateOf("north"));
    }

    [Fact]
    public void WithNothingFreeTheLeastImportantUseIsTakenOverAsBefore()
    {
        Used("west", Here, locked: true, 30, times: 20);
        Used("north", Here, locked: false, times: 20);
        TunerPool pool = Pool();
        Settle(pool, Ask(pool, "logo", There, SessionPurpose.Logo, "north"));
        Settle(pool, Ask(pool, "scan", new TuningKey(TunerKind.Terrestrial, 35, 0), SessionPurpose.Scan, "south"));
        Settle(pool, Ask(pool, "survey", new TuningKey(TunerKind.Terrestrial, 37, 0), SessionPurpose.Survey, "west"));

        PoolGrant grant = Ask(pool, "rec", Here, SessionPurpose.Recording);

        Assert.Equal("north", grant.DeviceId);
        Assert.Equal([SessionId.Parse("logo")], grant.Displaced);
    }

    [Fact]
    public void AChoiceAmongSeveralIsLoggedOnceWithEveryCandidatesRecord()
    {
        Used("north", Here, locked: true, 19.04, times: 2);
        Used("south", Here, locked: true, 23.5, times: 3);
        TunerPool pool = Pool();

        Ask(pool, "s-1", Here);

        string line = Assert.Single(Choices);

        Assert.StartsWith("Information ", line, StringComparison.Ordinal);
        Assert.Contains("The tuner south was chosen for terrestrial channel 31 among 3", line, StringComparison.Ordinal);
        Assert.Contains("'north' locked 2 of 2, C/N 19.0 dB", line, StringComparison.Ordinal);
        Assert.Contains("'south' locked 3 of 3, C/N 23.5 dB", line, StringComparison.Ordinal);
        Assert.Contains("'west' not measured", line, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsLoggedWhenThereWasOnlyOneCandidate()
    {
        TunerPool pool = Pool();
        Settle(pool, Ask(pool, "first", There));
        Settle(pool, Ask(pool, "second", new TuningKey(TunerKind.Terrestrial, 35, 0)));
        int before = Choices.Count();

        Ask(pool, "s-1", Here);
        Ask(pool, "s-2", Here);
        Ask(pool, "s-3", Here, deviceId: "west");

        Assert.Equal(before, Choices.Count());
    }
}
