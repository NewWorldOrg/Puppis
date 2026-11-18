using Puppis.Driver.Reception;

namespace Puppis.Driver.Tests;

public sealed class ReceptionStandingTests
{
    private static ReceptionStanding Of(int kept, int locked) => new(kept, locked, null, null);

    [Theory]
    [InlineData(20, 20, 10, 10, 0)]
    [InlineData(20, 19, 20, 18, 1)]
    [InlineData(3, 1, 20, 7, -1)]
    [InlineData(2, 1, 20, 10, 0)]
    [InlineData(0, 0, 20, 20, 0)]
    [InlineData(0, 0, 20, 19, 1)]
    [InlineData(1, 0, 0, 0, -1)]
    public void LockRatiosAreComparedAsFractionsAndAnUnmeasuredOneCountsAsWhole(
        int kept,
        int locked,
        int otherKept,
        int otherLocked,
        int expected
    )
    {
        Assert.Equal(expected, Math.Sign(Of(kept, locked).CompareLockRatio(Of(otherKept, otherLocked))));
    }

    [Fact]
    public void AStandingSaysItsRatioAndItsCarrierToNoise()
    {
        Assert.Equal("locked 19 of 20, C/N 21.4 dB", new ReceptionStanding(20, 19, 21.44, null).ToString());
        Assert.Equal("locked 2 of 2, C/N not read", new ReceptionStanding(2, 2, null, null).ToString());
        Assert.Equal("not measured", ReceptionStanding.Unmeasured.ToString());
    }
}
