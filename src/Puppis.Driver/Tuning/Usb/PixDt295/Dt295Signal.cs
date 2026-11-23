using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

public static class Dt295Signal
{
    public const FrontendStatus Locked =
        FrontendStatus.Signal | FrontendStatus.Carrier | FrontendStatus.Viterbi | FrontendStatus.Sync | FrontendStatus.Lock;

    private const double TerrestrialReference = 5_505_024;

    private const int SatelliteFloor = 3000;

    public static bool TerrestrialLock(ReadOnlySpan<byte> status, byte layers) =>
        (status[0] & 0x28) is 0 && (layers & 0x0F) >= 8;

    public static bool TerrestrialErrorFree(byte status) => (status & 0x2A) is 0;

    public static bool SatelliteLock(byte status) => (status & 0x10) is 0;

    public static FrontendStatus StatusOf(bool locked) => locked ? Locked : FrontendStatus.None;

    public static double? TerrestrialDecibels(ReadOnlySpan<byte> raw)
    {
        int value = (raw[0] << 16) | (raw[1] << 8) | raw[2];

        if (value is 0)
        {
            return null;
        }

        double p = 10 * Math.Log10(TerrestrialReference / value);

        return (0.000024 * Math.Pow(p, 4)) - (0.0016 * Math.Pow(p, 3)) + (0.0398 * p * p) + (0.5491 * p) + 3.0965;
    }

    public static double SatelliteDecibels(ReadOnlySpan<byte> raw)
    {
        int value = (raw[0] << 8) | raw[1];

        if (value < SatelliteFloor)
        {
            return 0;
        }

        double p = Math.Sqrt(value - SatelliteFloor) / 64;

        return (-1.6346 * Math.Pow(p, 5))
            + (14.341 * Math.Pow(p, 4))
            - (50.259 * Math.Pow(p, 3))
            + (88.977 * p * p)
            - (89.565 * p)
            + 58.857;
    }
}
