using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

public enum Dt295Side
{
    Neither = 0,

    Terrestrial = 1,

    Satellite = 2,
}

/// <summary>
/// The demodulator and the two tuners once they are set up: tuning either side and reading how the signal stands.
/// </summary>
public sealed class Dt295Frontend(Dt295Chips chips, DevicePacing pacing)
{
    public const byte DemodulatorIdentity = 0x77;

    public const byte TunerIdentity = 0x03;

    public static readonly TimeSpan TerrestrialLockPatience = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan SatelliteLockPatience = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan BetweenLooks = TimeSpan.FromMilliseconds(100);

    private const int TerrestrialStatus = 0x80;
    private const int TerrestrialLayers = 0xB0;
    private const int TerrestrialNoise = 0x8B;
    private const int SatelliteStatus = 0xC3;
    private const int SatelliteStream = 0xE6;
    private const int SatelliteCandidates = 0xCE;
    private const int SatelliteNoise = 0xBC;

    public Dt295Side Tuned { get; private set; }

    public Dt295Chips Chips => chips;

    public void Identify(string path)
    {
        byte demodulator = chips.ReadT(0x0E, 1)[0];

        if (demodulator is not DemodulatorIdentity)
        {
            throw new DvbDeviceException(
                $"{path}: the demodulator answered 0x{demodulator:X2} where 0x{DemodulatorIdentity:X2} was expected after its reset.",
                TuningFailure.DeviceUnusable
            );
        }

        byte tuner = chips.ReadRT(0x18);

        if (tuner is not TunerIdentity)
        {
            throw new DvbDeviceException(
                $"{path}: the terrestrial tuner answered 0x{tuner:X2} where 0x{TunerIdentity:X2} was expected.",
                TuningFailure.DeviceUnusable
            );
        }
    }

    public void Initialise()
    {
        Dt295Initialisation.Run(chips, pacing);
        Tuned = Dt295Side.Neither;
    }

    public static ushort TerrestrialDivider(long hertz) => (ushort)(hertz * 64 / 1_000_000);

    /// <summary>
    /// Tunes the terrestrial side and answers whether it locked within its patience.
    /// </summary>
    public bool TuneTerrestrial(long hertz)
    {
        if (Tuned is Dt295Side.Satellite)
        {
            SleepTheSatelliteSide();
        }

        ushort divider = TerrestrialDivider(hertz);
        chips.WriteRT(0x0B, 0x01);
        chips.WriteRT(0x12, 0x01);
        pacing.SleepMilliseconds(20);
        chips.WriteT(0x03, 0x00);
        chips.WriteT(0x1D, 0xAA);
        chips.WriteT(0x1E, 0xAA);
        chips.WriteT(0x1F, 0xAA);
        chips.WriteT(0x25, 0x00);
        chips.WriteT(0x23, 0x4D);
        chips.ModifyRT(0x0D, 0xFE, 0x00);
        chips.WriteRT(0x0F, 0x20);
        chips.WriteRT(0x10, (byte)divider);
        chips.WriteRT(0x11, (byte)(divider >> 8));
        chips.WriteRT(0x00, 0x01);
        chips.WriteRT(0x31, 0x81);
        chips.WriteRT(0x00, 0x00);
        chips.WriteRT(0x7C, 0x1F);
        chips.WriteRT(0x12, 0x00);
        chips.ModifyRT(0x6E, 0xFF, 0x80);
        chips.ModifyRT(0x14, 0xDF, 0x00);
        chips.WriteRT(0x12, 0x01);
        pacing.SleepMilliseconds(20);
        chips.ModifyRT(0x6E, 0x7F, 0x00);
        byte mode = chips.ReadT(0x0E, 1)[0];
        chips.WriteT(0x0E, (byte)((mode & 0x70) | 0x01));
        chips.WriteT(0x0F, 0x10);
        chips.WriteT(0x01, 0x40);
        chips.WriteT(0x23, 0x4C);
        pacing.SleepMilliseconds(100);
        chips.WriteT(0x1D, 0x02);
        chips.WriteT(0x1E, 0x2A);
        chips.WriteT(0x1F, 0x8A);
        Tuned = Dt295Side.Terrestrial;

        return Await(TerrestrialLockPatience, TerrestrialLocked);
    }

    /// <summary>
    /// Tunes the satellite side to the intermediate frequency and answers whether it locked on the transport stream.
    /// Without a transport stream id, the first one the demodulator offers once it has a signal is taken.
    /// </summary>
    public bool TuneSatellite(long kilohertz, int? transportStreamId, string path)
    {
        if (Tuned is Dt295Side.Terrestrial)
        {
            LeaveTheTerrestrialSide();
        }

        SatellitePlan plan = SatellitePlan.For(kilohertz, path);
        chips.WriteS(0x1E, (byte)((chips.ReadS(0x1E, 1)[0] & 0xCF) | 0x20));
        chips.WriteRS(0x11, 0x00, 0xA7);
        chips.WriteRS(0x2A, 0xB9);
        chips.WriteRS(0x1C, 0xC0);
        chips.WriteRS(0x29, 0x71);
        chips.WriteS(0x17, 0x00);
        chips.WriteS(0x1E, 0x20);
        chips.WriteS(0x0A, 0xFF);
        chips.WriteS(0x03, 0x01);
        chips.WriteS(0x1C, 0xAA);
        chips.WriteS(0x1F, 0x2A);
        chips.WriteS(0x8F, 0x00);
        chips.WriteS(0x90, 0x00);
        chips.WriteS(0x1E, 0x28);
        chips.WriteS(0x0A, 0x00);
        chips.WriteS(0x03, 0x01);
        chips.WriteRS(0x00, plan.Pll);
        chips.WriteRS(0x09, plan.Band);
        chips.WriteRS(0x37, 0xAC);
        chips.WriteRS(0x05, 0x80);
        chips.WriteRS(0x2A, 0xBB);
        pacing.SleepMilliseconds(10);
        chips.WriteT(0x0E, 0x77);
        chips.WriteT(0x0F, 0x77);
        byte mode = chips.ReadS(0x07, 1)[0];
        chips.WriteS(0x07, (byte)((mode & 0x70) | 0x01));
        chips.WriteS(0x08, 0x10);
        byte[] tuner = chips.ReadRS(2);
        chips.WriteRS(0x50, (tuner[1] & 0x60) is 0x60 ? (byte)0xF1 : (byte)0xF0);
        pacing.SleepMilliseconds(40);
        chips.WriteS(0x1E, 0x20);
        chips.WriteS(0x0A, 0xFF);
        chips.WriteS(0x03, 0x01);
        chips.WriteS(0x03, 0x01);
        pacing.SleepMilliseconds(100);
        chips.WriteS(0x1C, 0x08);
        chips.WriteS(0x1F, 0x08);
        chips.WriteS(0xA5, (byte)(chips.ReadS(0xA5, 1)[0] | 0x40));
        chips.WriteS(0xA3, (byte)((chips.ReadS(0xA3, 1)[0] & 0x88) | 0x11));
        Tuned = Dt295Side.Satellite;
        long started = pacing.Timestamp;

        if (transportStreamId is not { } wanted)
        {
            if (!Await(SatelliteLockPatience, () => SatelliteLock(chips.ReadS(SatelliteStatus, 1)[0]))
                || FirstOffered() is not { } offered)
            {
                return false;
            }

            wanted = offered;
        }

        chips.WriteS(0x8F, (byte)(wanted >> 8));
        chips.WriteS(0x90, (byte)wanted);

        return Await(SatelliteLockPatience - pacing.Since(started), () => SatelliteLockedOn(wanted));
    }

    public bool Locked() =>
        Tuned switch
        {
            Dt295Side.Terrestrial => TerrestrialLocked(),
            Dt295Side.Satellite => SatelliteLock(chips.ReadS(SatelliteStatus, 1)[0]),
            _ => false,
        };

    public CarrierToNoise CarrierToNoise(bool locked)
    {
        if (!locked)
        {
            return Dvb.CarrierToNoise.WithoutLock;
        }

        if (Tuned is Dt295Side.Satellite)
        {
            return Dvb.CarrierToNoise.Measured(Dt295Signal.SatelliteDecibels(chips.ReadS(SatelliteNoise, 2)));
        }

        return Dt295Signal.TerrestrialDecibels(chips.ReadT(TerrestrialNoise, 3)) is { } decibels
            ? Dvb.CarrierToNoise.Measured(decibels)
            : Dvb.CarrierToNoise.Unavailable;
    }

    public void LeaveTheTerrestrialSide()
    {
        chips.WriteT(0x25, 0x00);
        chips.WriteT(0x23, 0x4D);
        chips.WriteT(0x03, 0x90);
        chips.WriteRT(0x12, 0x00);
        chips.WriteRT(0x0B, 0x00);
        Tuned = Dt295Side.Neither;
    }

    public void SleepTheSatelliteSide()
    {
        chips.WriteS(0x1C, 0xAA);
        chips.WriteS(0x1F, 0x2A);
        chips.WriteS(0x1E, 0x28);
        chips.WriteS(0x0A, 0x00);
        chips.WriteS(0x03, 0x01);
        chips.WriteS(0x17, 0x01);
        chips.WriteS(0x1E, (byte)((chips.ReadS(0x1E, 1)[0] & 0xCF) | 0x30));
        chips.WriteRS(0x2A, 0xB9);
        chips.WriteRS(0x29, 0x70);
        chips.WriteRS(0x28, 0x3E);
        chips.WriteRS(0x2A, 0x19);
        chips.WriteRS(0x1C, 0x00);
        chips.WriteRS(0x11, 0xC0, 0xA7);
        Tuned = Dt295Side.Neither;
    }

    private bool TerrestrialLocked() =>
        Dt295Signal.TerrestrialLock(chips.ReadT(TerrestrialStatus, 4), chips.ReadT(TerrestrialLayers, 1)[0]);

    private bool SatelliteLockedOn(int transportStreamId)
    {
        byte status = chips.ReadS(SatelliteStatus, 1)[0];
        byte[] stream = chips.ReadS(SatelliteStream, 2);

        return SatelliteLock(status) && ((stream[0] << 8) | stream[1]) == transportStreamId;
    }

    private int? FirstOffered()
    {
        byte[] candidates = chips.ReadS(SatelliteCandidates, 16);

        for (int at = 0; at < candidates.Length; at += 2)
        {
            int candidate = (candidates[at] << 8) | candidates[at + 1];

            if (candidate is not (0 or 0xFFFF))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool SatelliteLock(byte status) => Dt295Signal.SatelliteLock(status);

    private bool Await(TimeSpan patience, Func<bool> locked)
    {
        long started = pacing.Timestamp;

        while (true)
        {
            pacing.Sleep(BetweenLooks);

            if (locked())
            {
                return true;
            }

            if (pacing.Since(started) >= patience)
            {
                return false;
            }
        }
    }
}

/// <summary>
/// The satellite tuner's synthesiser bytes and band byte for an intermediate frequency.
/// </summary>
public sealed record SatellitePlan(byte[] Pll, byte Band)
{
    private const long LowBandCeiling = 1_100_000;

    private static readonly (long Below, byte Band, byte Form)[] Bands =
    [
        (975_000, 0x1C, 1),
        (1_050_000, 0x18, 2),
        (1_150_000, 0x14, 2),
        (1_250_000, 0x10, 3),
        (1_350_000, 0x0C, 4),
        (1_450_000, 0x0A, 4),
        (1_600_000, 0x07, 5),
        (1_800_000, 0x04, 2),
        (2_000_000, 0x02, 1),
        (2_151_000, 0x00, 0),
    ];

    public static SatellitePlan For(long kilohertz, string path)
    {
        long rounded = (long)Math.Round(kilohertz / 1000.0, MidpointRounding.AwayFromZero) * 1000;
        bool low = rounded <= LowBandCeiling;
        long divider = ((rounded * (low ? 2 : 1)) + 500) / 1000;

        foreach ((long below, byte band, byte form) in Bands)
        {
            if (rounded < below)
            {
                return new SatellitePlan(
                    [(byte)(divider >> 7), (byte)((divider * 2) & 0xFF), 0, 0, low ? (byte)0x80 : (byte)0],
                    (byte)((form * 0x20) + band)
                );
            }
        }

        throw DvbFailure.Refused(
            $"{path}: {kilohertz} kHz is above the satellite tuner's highest band, so there is nothing to tune it to."
        );
    }
}
