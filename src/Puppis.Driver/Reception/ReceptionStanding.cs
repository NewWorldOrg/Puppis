using System.Globalization;

namespace Puppis.Driver.Reception;

public readonly record struct ReceptionStanding(int Kept, int Locked, double? CarrierToNoise, DateTimeOffset? NewestAt)
{
    public static readonly ReceptionStanding Unmeasured = new(0, 0, null, null);

    public bool IsMeasured => Kept > 0;

    private (long Locked, long Kept) Ratio => IsMeasured ? (Locked, Kept) : (1, 1);

    public int CompareLockRatio(ReceptionStanding other)
    {
        (long locked, long kept) = Ratio;
        (long otherLocked, long otherKept) = other.Ratio;

        return (locked * otherKept).CompareTo(otherLocked * kept);
    }

    public override string ToString()
    {
        if (!IsMeasured)
        {
            return "not measured";
        }

        string ratio = CarrierToNoise is { } decibels
            ? $"C/N {decibels.ToString("0.0", CultureInfo.InvariantCulture)} dB"
            : "C/N not read";

        return $"locked {Locked} of {Kept}, {ratio}";
    }
}
