using System.Globalization;

using Puppis.Driver.Reception;

namespace Puppis.Driver.Tests;

public sealed class ReceptionOrderTests
{
    public static TheoryData<string, string, string> Cases() =>
        new()
        {
            { "the higher lock ratio comes first whatever the carrier to noise", "a 10/20 30 free still; b 19/20 15 free still", "b a" },
            { "a ready tuner does not come before a higher lock ratio", "a 20/20 - free still; b 19/20 - ready still", "a b" },
            { "a tuner held for a return comes after a free one that is the same", "a 20/20 20.0 held still; b 20/20 20.5 free still", "b a" },
            { "a tuner held for a return comes before a free one that receives worse", "a 20/20 23.0 free still; b 20/20 25.0 held still; c 19/20 30 free still", "b a c" },
            { "a ready tuner comes before one held for a return", "a - - held still; b - - free still; c - - ready still", "c b a" },
            { "an unmeasured tuner counts as one that always locked", "a 19/20 25 free still; b - - free still", "b a" },
            { "an unmeasured tuner ties with one that always locked", "a 20/20 25 free still; b - - free still", "a b" },
            { "the same ratio from different counts is a tie", "a 1/2 - free still; b 10/20 - free still", "a b" },
            { "the higher carrier to noise comes first at two decibels apart", "a 20/20 20.0 free still; b 20/20 22.0 free still", "b a" },
            { "under two decibels apart counts as the same", "a 20/20 20.0 free still; b 20/20 21.9 free still", "a b" },
            { "carrier to noise is compared only when both have one", "a 20/20 - free still; b 20/20 30 free still", "a b" },
            { "a ready tuner comes before a free one that is the same", "a 20/20 21.5 free still; b 20/20 20.0 ready still", "b a" },
            { "a ready tuner does not come before one two decibels better", "a 20/20 22.0 free still; b 20/20 20.0 ready still", "a b" },
            { "a ready tuner comes before an unmeasured free one", "a - - free still; b - - ready still", "b a" },
            { "what is left is the order of the configuration", "a 5/5 20 free still; b 5/5 20 free still; c 5/5 20 free still", "a b c" },
            { "two ready tuners keep the order of the configuration", "a - - free still; b - - ready still; c - - ready still", "b c a" },
            { "a tuner with a poor record is used when it is the only one", "a 0/20 - free still", "a" },
            { "a tuner with an operation in flight comes after every one without", "a 20/20 30 ready busy; b 1/20 10 free still", "b a" },
            { "tuners with an operation in flight are ordered among themselves", "a 10/20 - free busy; b 20/20 - free busy; c 0/20 - free still", "c b a" },
            { "one without a carrier to noise stays beside the best", "a 20/20 20.0 free still; b 20/20 - ready still; c 20/20 23.0 free still", "b c a" },
        };

    [Theory]
    [MemberData(nameof(Cases))]
    public void IdleTunersAreArrangedByHowWellTheyReceiveTheChannel(string rule, string given, string expected)
    {
        IdleCandidate[] candidates = [.. given.Split("; ").Select(Parse)];

        IReadOnlyList<IdleCandidate> arranged = ReceptionOrder.Arrange(candidates);

        Assert.True(
            expected.Split(' ').SequenceEqual(arranged.Select(candidate => candidate.DeviceId)),
            $"{rule}: expected {expected}, got {string.Join(' ', arranged.Select(candidate => candidate.DeviceId))}"
        );
    }

    [Fact]
    public void NoCandidatesArrangeToNone()
    {
        Assert.Empty(ReceptionOrder.Arrange([]));
    }

    private static IdleCandidate Parse(string written)
    {
        string[] parts = written.Split(' ');

        return new IdleCandidate(
            parts[0],
            parts[4] is "still",
            parts[3] is "ready",
            Standing(parts[1], parts[2]),
            parts[3] is "held"
        );
    }

    private static ReceptionStanding Standing(string ratio, string decibels)
    {
        if (ratio is "-")
        {
            return ReceptionStanding.Unmeasured;
        }

        string[] counts = ratio.Split('/');

        return new ReceptionStanding(
            int.Parse(counts[1], CultureInfo.InvariantCulture),
            int.Parse(counts[0], CultureInfo.InvariantCulture),
            decibels is "-" ? null : double.Parse(decibels, CultureInfo.InvariantCulture),
            null
        );
    }
}
