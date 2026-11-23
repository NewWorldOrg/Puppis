using Puppis.Contracts;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.CechZd1;

namespace Puppis.Driver.Tests;

public sealed class CechZd1RememberedChannelTests
{
    private const string Device = "/dev/bus/usb/001/002";

    private const int SearchSequence = 355;

    private const int SearchState = 1284;

    private const ushort Synchronised = 0x14FE;

    private static readonly long Channel22 = BroadcastStandards.TerrestrialCentreHz(22);

    private static readonly long Channel27 = BroadcastStandards.TerrestrialCentreHz(27);

    private static readonly (int Register, ushort Value) LoopValuesBeforeTheLock = (32, 0x6680);

    private static readonly int[] WrittenOnlyToStartASearch = [6, 7, 8, 11, 12, 13, 14, 15, 16, 355, 356, 357];

    private static (CechZd1Frontend Frontend, FakeChip Demodulator, ManualTimeProvider Clock) Initialised()
    {
        (FakeChip demodulator, FakeChip tuner) = UsbTestSupport.LockingChips();
        (DevicePacing pacing, ManualTimeProvider clock, _) = UsbTestSupport.Pacing();
        var frontend = new CechZd1Frontend(demodulator, tuner, pacing, Device);
        frontend.Initialise();

        return (frontend, demodulator, clock);
    }

    private static void Forget(FakeChip chip)
    {
        chip.Written.Clear();
        chip.ReadFrom.Clear();
    }

    private static bool Searched(FakeChip demodulator) => demodulator.WritesTo(SearchSequence).Count > 0;

    private static IReadOnlyList<(int Register, ushort Value)> FromTheConfiguration(FakeChip demodulator) =>
        [
            .. demodulator.Written
                .SkipWhile(write => write != LoopValuesBeforeTheLock)
                .Where(write => !Dib8000.TablesOfAChannel.Contains(write.Register)),
        ];

    private static void SynchroniseOnlyAfterASearch(FakeChip demodulator, List<DateTimeOffset>? askedAt = null, TimeProvider? clock = null) =>
        demodulator.Answers[Dib8000.Status] = () =>
        {
            if (Searched(demodulator))
            {
                return Synchronised;
            }

            askedAt?.Add(clock!.GetUtcNow());

            return 0;
        };

    [Fact]
    public void AChannelThatLockedBeforeIsConfiguredWithoutSearchingForIt()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, _) = Initialised();
        frontend.Tune(Channel22);
        Forget(demodulator);

        SearchOutcome outcome = frontend.Tune(Channel22);

        Assert.Equal(SearchOutcome.DataFlowing, outcome);
        Assert.DoesNotContain(demodulator.Written, write => WrittenOnlyToStartASearch.Contains(write.Register));
        Assert.DoesNotContain(SearchState, demodulator.ReadFrom);
        Assert.Equal([(ushort)0], demodulator.WritesTo(10));
        Assert.Equal(48, demodulator.Written.Count(write => write.Register is >= 117 and <= 140));
    }

    [Fact]
    public void ApartFromTheTablesTheRememberedChannelIsWrittenAsTheSearchWroteItOnceItHadFoundIt()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, _) = Initialised();
        frontend.Tune(Channel22);
        IReadOnlyList<(int Register, ushort Value)> afterTheSearch = FromTheConfiguration(demodulator);
        ChannelParameters found = frontend.Channel;
        Forget(demodulator);

        frontend.Tune(Channel22);

        Assert.NotEmpty(afterTheSearch);
        Assert.Equal(afterTheSearch, FromTheConfiguration(demodulator));
        Assert.Equal(found, frontend.Channel);
    }

    [Fact]
    public void TheDemodulatorIsStillPreparedBeforeTheRememberedChannelIsWritten()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, _) = Initialised();
        frontend.Tune(Channel22);
        Forget(demodulator);

        frontend.Tune(Channel22);

        IReadOnlyList<int> before = [.. demodulator.Written.TakeWhile(write => write != LoopValuesBeforeTheLock).Select(write => write.Register)];

        Assert.Contains(771, before);
        Assert.Contains(285, before);
        Assert.Contains(27, before);
        Assert.Contains(29, before);
    }

    [Fact]
    public void AChannelIsRememberedUnderItsOwnFrequency()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, _) = Initialised();
        frontend.Tune(Channel22);
        Forget(demodulator);

        frontend.Tune(Channel27);

        Assert.True(Searched(demodulator));
        Forget(demodulator);

        frontend.Tune(Channel22);

        Assert.False(Searched(demodulator));
        Forget(demodulator);

        frontend.Tune(Channel27);

        Assert.False(Searched(demodulator));
    }

    [Fact]
    public void ARememberedChannelThatDoesNotSynchroniseIsSearchedForAfterAll()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, _) = Initialised();
        frontend.Tune(Channel22);
        Forget(demodulator);
        SynchroniseOnlyAfterASearch(demodulator);

        SearchOutcome outcome = frontend.Tune(Channel22);

        Assert.Equal(SearchOutcome.DataFlowing, outcome);
        Assert.True(Searched(demodulator));
        Assert.Equal(2, demodulator.WritesTo(10).Count(sequence => sequence is 0));
        Assert.Contains((ushort)256, demodulator.WritesTo(10));
    }

    [Fact]
    public void TheChannelFoundByThatSearchIsRememberedInItsTurn()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, _) = Initialised();
        frontend.Tune(Channel22);
        SynchroniseOnlyAfterASearch(demodulator);
        Forget(demodulator);
        frontend.Tune(Channel22);
        demodulator.Answer(Dib8000.Status, Synchronised);
        Forget(demodulator);

        SearchOutcome outcome = frontend.Tune(Channel22);

        Assert.Equal(SearchOutcome.DataFlowing, outcome);
        Assert.False(Searched(demodulator));
    }

    [Fact]
    public void AChannelThatDidNotLockIsNotRemembered()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, _) = Initialised();
        frontend.Tune(Channel22);
        demodulator.Answer(Dib8000.Status, 0);
        demodulator.Answer(SearchState, 0x0001);

        Assert.Equal(SearchOutcome.Failed, frontend.Tune(Channel22));

        demodulator.Answer(Dib8000.Status, Synchronised);
        demodulator.Answer(SearchState, 0x0002);
        Forget(demodulator);

        Assert.Equal(SearchOutcome.DataFlowing, frontend.Tune(Channel22));
        Assert.True(Searched(demodulator));
    }

    [Fact]
    public void ARememberedChannelIsGivenItsOwnPatienceToSynchroniseAndNoMore()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, ManualTimeProvider clock) = Initialised();
        frontend.Tune(Channel22);
        Forget(demodulator);
        var askedAt = new List<DateTimeOffset>();
        SynchroniseOnlyAfterASearch(demodulator, askedAt, clock);

        frontend.Tune(Channel22);

        TimeSpan waited = askedAt[^1] - askedAt[0];

        Assert.True(waited >= DemodulatorSearch.RememberedPatience, $"waited {waited.TotalMilliseconds} ms");
        Assert.True(waited < DemodulatorSearch.RememberedPatience + TimeSpan.FromMilliseconds(30), $"waited {waited.TotalMilliseconds} ms");
    }

    [Fact]
    public void TheWholeTuneStillEndsWithinTheSearchsPatienceWhenTheRememberedChannelFails()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, ManualTimeProvider clock) = Initialised();
        frontend.Tune(Channel22);
        demodulator.Answer(Dib8000.Status, 0);
        demodulator.Answer(SearchState, 0);
        DateTimeOffset began = clock.GetUtcNow();

        DvbDeviceException refused = UsbTestSupport.Refusal(() => frontend.Tune(Channel22));

        Assert.Equal(TuningFailure.Unspecified, refused.Failure);
        Assert.True(clock.GetUtcNow() - began < DemodulatorSearch.Patience + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ARememberedChannelCarriesNoDifferentialSegmentsOverFromTheChannelBeforeIt()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, _) = Initialised();
        demodulator.Answer(505, 0);
        Differential(demodulator, segments: 1);
        frontend.Tune(Channel22);
        ushort found = demodulator.Last(352);
        Differential(demodulator, segments: 13);
        frontend.Tune(Channel27);
        Forget(demodulator);

        frontend.Tune(Channel22);

        Assert.False(Searched(demodulator));
        Assert.NotEqual(0, found);
        Assert.Equal(found, demodulator.Last(352));
    }

    [Fact]
    public void TheStagesOfATuneOnARememberedChannelNameIt()
    {
        (CechZd1Frontend frontend, _, ManualTimeProvider clock) = Initialised();
        frontend.Tune(Channel22);
        var stages = new StageTimes(clock);

        frontend.Tune(Channel22, stages);

        string listed = stages.ToString();

        Assert.Contains("remembered channel begin ", listed, StringComparison.Ordinal);
        Assert.Contains("remembered channel configuration ", listed, StringComparison.Ordinal);
        Assert.Contains("remembered channel sync ", listed, StringComparison.Ordinal);
        Assert.Contains("remembered channel lock ", listed, StringComparison.Ordinal);
        Assert.Contains("remembered channel data ", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("first search", listed, StringComparison.Ordinal);
    }

    [Fact]
    public void ARememberedChannelThatFailsIsNamedBeforeTheSearchThatFollowsIt()
    {
        (CechZd1Frontend frontend, FakeChip demodulator, ManualTimeProvider clock) = Initialised();
        frontend.Tune(Channel22);
        Forget(demodulator);
        SynchroniseOnlyAfterASearch(demodulator);
        var stages = new StageTimes(clock);

        frontend.Tune(Channel22, stages);

        string listed = stages.ToString();

        Assert.Contains(
            $"; the remembered channel failed while waiting for sync, D[1284] not read, D[{Dib8000.Status}] 0x0000",
            listed,
            StringComparison.Ordinal
        );
        Assert.True(
            listed.IndexOf("remembered channel sync ", StringComparison.Ordinal)
                < listed.IndexOf("first search begin ", StringComparison.Ordinal)
        );
    }

    private static void Differential(FakeChip demodulator, ushort segments)
    {
        demodulator.Answer(487, 0);
        demodulator.Answer(493, segments);
        demodulator.Answer(494, (ushort)(13 - segments));
    }
}
