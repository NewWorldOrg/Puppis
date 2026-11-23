using Microsoft.Extensions.Logging;

using Puppis.Driver.Configuration;

namespace Puppis.Driver.Tuning.Dvb;

public sealed record DvbTunerSettings(
    TimeSpan LockPatience,
    TimeSpan LockPollInterval,
    TimeSpan RetryInterval,
    TimeSpan BytePatience,
    int DemuxBufferBytes,
    bool ReadsFromTheLock = false
)
{
    public static readonly DvbTunerSettings Default = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromSeconds(5),
        16 * 1024 * 1024
    );
}

public static class LnbPower
{
    public static LnbVoltage For(DeviceKind kind, bool enabledInTheLedger) =>
        kind is DeviceKind.Satellite && enabledInTheLedger
            ? LnbVoltage.Eighteen
            : LnbVoltage.Off;
}

public sealed class DvbTunerDevice : ITunerDevice, ISignalQualitySource
{
    private const int SpillBytes = 188 * 1024;

    private const int MostSpills = 1024;

    private const string Hardware = "DVB";

    private readonly IDvbSystemCalls calls;
    private readonly TimeProvider time;
    private readonly DvbDevicePaths paths;
    private readonly DvbTunerSettings settings;
    private readonly DvbChannel channel;
    private readonly DvbFrontend frontend;
    private readonly int demux;
    private readonly int dvr;

    private long overflows;
    private bool closed;

    private DvbTunerDevice(
        IDvbSystemCalls calls,
        TimeProvider time,
        DvbDevicePaths paths,
        DvbTunerSettings settings,
        DvbChannel channel,
        DvbFrontend frontend,
        int demux,
        int dvr
    )
    {
        this.calls = calls;
        this.time = time;
        this.paths = paths;
        this.settings = settings;
        this.channel = channel;
        this.frontend = frontend;
        this.demux = demux;
        this.dvr = dvr;
    }

    public long Overflows => Interlocked.Read(ref overflows);

    public ISignalQualitySource? Quality => this;

    public SignalQuality Measure() => frontend.Quality();

    public static DvbTunerDevice Open(
        IDvbSystemCalls calls,
        TimeProvider time,
        DvbDevicePaths paths,
        DvbChannel channel,
        LnbVoltage voltage,
        DvbTunerSettings settings,
        CancellationToken cancellationToken,
        ILogger? logger = null,
        TunerOpening? opening = null
    )
    {
        var stages = new StageTimes(time);
        string what = opening?.On(Hardware) ?? $"{paths.Frontend} on {TunerOpening.ChannelName(channel)}";
        DvbFrontend frontend = DvbFrontend.Open(calls, paths.Frontend, DvbAccess.Control);
        stages.Mark("frontend");
        int demux = -1;
        int dvr = -1;

        try
        {
            if (settings.ReadsFromTheLock)
            {
                (demux, dvr) = OpenTheStream(calls, paths, settings);
                stages.Mark("stream");
            }

            if (channel.NeedsSatelliteAerial)
            {
                frontend.SetLnbVoltage(voltage);
                stages.Mark("aerial supply");
            }

            frontend.Tune(channel);
            stages.Mark("tune");
            bool locked = frontend.WaitForLock(
                time,
                settings.LockPatience,
                settings.LockPollInterval,
                cancellationToken,
                out FrontendStatus lastSeen,
                out int polls
            );
            stages.Mark("lock", $"{polls} polls");

            if (!locked)
            {
                logger?.LogInformation("{Opening}: gave up without a lock after {Stages}.", what, stages);

                throw DvbFailure.NoLock(
                    $"{paths.Frontend}: the frontend did not lock onto {channel} within {settings.LockPatience.TotalSeconds:0.#} seconds, and the last status it reported while waiting was {lastSeen}. Nothing was received, so no bytes will follow."
                );
            }

            if (settings.ReadsFromTheLock)
            {
                long discarded = ThrowAwayWhatCameBeforeTheLock(calls, dvr);
                stages.Mark("discard", $"{discarded} bytes");
            }
            else
            {
                (demux, dvr) = OpenTheStream(calls, paths, settings);
                stages.Mark("stream");
            }

            logger?.LogInformation("{Opening}: opened and locked in {Stages}.", what, stages);

            return new DvbTunerDevice(calls, time, paths, settings, channel, frontend, demux, dvr);
        }
        catch
        {
            CloseQuietly(calls, dvr);
            CloseQuietly(calls, demux);
            frontend.Dispose();

            throw;
        }
    }

    public static void OpenAndClose(IDvbSystemCalls calls, DvbDevicePaths paths)
    {
        using DvbFrontend frontend = DvbFrontend.Open(calls, paths.Frontend, DvbAccess.Control);
        SyscallOutcome opened = calls.Open(paths.Dvr, DvbAccess.Stream);

        if (opened.Refused)
        {
            throw DvbFailure.AtDevice(
                paths.Dvr,
                "opening the transport stream reader",
                opened.Error,
                "The frontend opened, but the transport stream cannot be taken off this adapter."
            );
        }

        calls.Close(opened.Value);
    }

    public byte[] Read(int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        DateTimeOffset deadline = time.GetUtcNow() + settings.BytePatience;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SyscallOutcome ready = calls.WaitForReadable(
                dvr,
                (int)settings.BytePatience.TotalMilliseconds
            );

            if (ready.Refused && ready.Error is not Errno.Interrupted)
            {
                throw DvbFailure.AtDevice(
                    paths.Dvr,
                    "waiting for transport stream bytes",
                    ready.Error,
                    "The session cannot continue without knowing whether the tuner is still delivering."
                );
            }

            if (!ready.Refused && ready.Value > 0)
            {
                SyscallOutcome read = calls.ReadBytes(dvr, buffer, count);

                if (!read.Refused)
                {
                    return Delivered(buffer, read.Value);
                }

                if (read.Error is Errno.Overflowed)
                {
                    Interlocked.Increment(ref overflows);
                }
                else if (read.Error is not (Errno.WouldBlock or Errno.Interrupted))
                {
                    throw DvbFailure.AtDevice(
                        paths.Dvr,
                        "reading transport stream bytes",
                        read.Error,
                        "The session cannot continue on a reader that has stopped answering."
                    );
                }
                else
                {
                    calls.Rest(settings.RetryInterval, cancellationToken);
                }
            }

            if (time.GetUtcNow() >= deadline)
            {
                throw NothingArrived();
            }
        }
    }

    private static byte[] Delivered(byte[] buffer, int read)
    {
        if (read is 0)
        {
            return [];
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }

    private DvbDeviceException NothingArrived()
    {
        string waited = $"{settings.BytePatience.TotalSeconds:0.#} seconds";

        if (!frontend.TryStatus(out FrontendStatus status))
        {
            return DvbFailure.Refused(
                $"{paths.Dvr}: no transport stream bytes arrived from {channel} within {waited}, and the frontend would not say whether it is still locked. This is left unclassified rather than recorded as a tuner that was locked and delivering nothing."
            );
        }

        if (!status.HasFlag(FrontendStatus.Lock))
        {
            return DvbFailure.NoLock(
                $"{paths.Dvr}: no transport stream bytes arrived from {channel} within {waited}, and the frontend is no longer locked; its status is now {status}."
            );
        }

        return DvbFailure.LockedWithoutData(
            $"{paths.Dvr}: the frontend is still locked onto {channel} ({status}) and no transport stream bytes arrived within {waited}. The tuner is synchronised and the demux is delivering nothing, which is a different fault from failing to lock."
        );
    }

    public void Dispose()
    {
        if (closed)
        {
            return;
        }

        closed = true;
        CloseQuietly(calls, dvr);
        calls.StopFilter(demux);
        CloseQuietly(calls, demux);
        frontend.Dispose();
    }

    private static int OpenNode(
        IDvbSystemCalls calls,
        string path,
        DvbAccess access,
        string what
    )
    {
        SyscallOutcome opened = calls.Open(path, access);

        if (opened.Refused)
        {
            throw DvbFailure.AtDevice(
                path,
                $"opening {what}",
                opened.Error,
                "The frontend locked, but the transport stream cannot be taken off this adapter."
            );
        }

        return opened.Value;
    }

    private static void SetBufferSize(
        IDvbSystemCalls calls,
        string path,
        int descriptor,
        int bytes
    )
    {
        SyscallOutcome sized = calls.SetBufferSize(descriptor, bytes);

        if (sized.Refused)
        {
            throw DvbFailure.AtDevice(
                path,
                $"asking for a {bytes} byte ring buffer",
                sized.Error,
                "The default buffer is small enough that a late reader loses bytes silently, so the driver will not proceed without the size it asked for."
            );
        }
    }

    private static void StartFilter(IDvbSystemCalls calls, string path, int descriptor)
    {
        SyscallOutcome filtered = calls.SetPesFilter(descriptor, DemuxFilter.EverythingFromTheFrontend());

        if (filtered.Refused)
        {
            throw DvbFailure.AtDevice(
                path,
                "routing every packet to the transport stream reader",
                filtered.Error,
                "Without the filter the reader would block forever on an adapter that is otherwise working."
            );
        }
    }

    private static (int Demux, int Dvr) OpenTheStream(IDvbSystemCalls calls, DvbDevicePaths paths, DvbTunerSettings settings)
    {
        int demux = OpenNode(calls, paths.Demux, DvbAccess.Control, "the demux");
        int dvr = -1;

        try
        {
            SetBufferSize(calls, paths.Demux, demux, settings.DemuxBufferBytes);
            StartFilter(calls, paths.Demux, demux);
            dvr = OpenNode(calls, paths.Dvr, DvbAccess.Stream, "the transport stream reader");

            return (demux, dvr);
        }
        catch
        {
            CloseQuietly(calls, demux);

            throw;
        }
    }

    private static long ThrowAwayWhatCameBeforeTheLock(IDvbSystemCalls calls, int dvr)
    {
        byte[] spilt = new byte[SpillBytes];
        long discarded = 0;

        for (int read = 0; read < MostSpills; read++)
        {
            SyscallOutcome taken = calls.ReadBytes(dvr, spilt, spilt.Length);

            if (taken.Refused ? taken.Error is not Errno.Overflowed : taken.Value is 0)
            {
                return discarded;
            }

            discarded += taken.Refused ? 0 : taken.Value;
        }

        return discarded;
    }

    private static void CloseQuietly(IDvbSystemCalls calls, int descriptor)
    {
        if (descriptor >= 0)
        {
            calls.Close(descriptor);
        }
    }
}
