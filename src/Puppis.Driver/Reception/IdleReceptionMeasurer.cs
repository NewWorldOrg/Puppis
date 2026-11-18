using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Sessions;
using Puppis.Driver.Tuning;
using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Reception;

public sealed class IdleReceptionMeasurer(
    DriverConfiguration configuration,
    TunerSessionManager manager,
    ReceptionBook book,
    IReceptionProbeFactory probes,
    TimeProvider clock,
    ILogger<IdleReceptionMeasurer> logger,
    TimeSpan? betweenReadings = null,
    TimeSpan? restBeforeMeasuring = null,
    TimeSpan? betweenMeasurements = null,
    TimeSpan? looksEvery = null
) : IHostedService, IDisposable
{
    public const int Readings = 5;

    public static readonly TimeSpan RestBeforeMeasuring = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan BetweenMeasurements = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan BetweenReadings = TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan LooksEvery = TimeSpan.FromSeconds(5);

    private readonly bool enabled = configuration.Tuner?.MeasureWhileIdle ?? TunerSettings.DefaultMeasureWhileIdle;
    private readonly TimeSpan readingGap = betweenReadings ?? BetweenReadings;
    private readonly TimeSpan rest = restBeforeMeasuring ?? RestBeforeMeasuring;
    private readonly TimeSpan gap = betweenMeasurements ?? BetweenMeasurements;
    private readonly TimeSpan looking = looksEvery ?? LooksEvery;
    private readonly Lock gate = new();
    private ITimer? timer;
    private Task running = Task.CompletedTask;
    private DateTimeOffset? lastEndedAt;
    private bool stopping;
    private long interrupted;
    private long finished;

    public long Interrupted => Interlocked.Read(ref interrupted);

    public long Finished => Interlocked.Read(ref finished);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            logger.LogInformation("tuner.measureWhileIdle is off, so no tuner is measured while it is not in use.");

            return Task.CompletedTask;
        }

        timer = clock.CreateTimer(_ => Look(), null, looking, looking);

        return Task.CompletedTask;
    }

    private void Look()
    {
        lock (gate)
        {
            if (stopping || !running.IsCompleted)
            {
                return;
            }

            running = Task.Factory.StartNew(
                MeasureQuietly,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
        }
    }

    private void MeasureQuietly()
    {
        try
        {
            MeasureIfDue();
        }
        catch (Exception failed)
        {
            logger.LogError(failed, "Measuring an idle tuner failed in a way nothing foresaw; the next look tries again.");
        }
    }

    public IdleMeasurement? MeasureIfDue()
    {
        DateTimeOffset now = clock.GetUtcNow();

        if (!MayMeasure(now))
        {
            return null;
        }

        foreach ((DeviceSettings device, TuningKey channel, TuneParams tune) in Due(now))
        {
            if (manager.BeginMeasuring(device.Id!, channel) is { } ticket)
            {
                return Measure(device, channel, tune, ticket);
            }
        }

        return null;
    }

    private bool MayMeasure(DateTimeOffset now)
    {
        lock (gate)
        {
            return enabled && !stopping && (lastEndedAt is not { } ended || now - ended >= gap);
        }
    }

    private IEnumerable<(DeviceSettings Device, TuningKey Channel, TuneParams Tune)> Due(DateTimeOffset now)
    {
        IReadOnlyList<TuningKey> known = book.Known();

        foreach (RestingTuner resting in manager.RestingTuners().Where(tuner => now - tuner.Since >= rest))
        {
            if (DeviceNamed(resting.DeviceId) is not { } device)
            {
                continue;
            }

            foreach (TuningKey channel in known)
            {
                if (
                    Receives(device, channel)
                    && channel.AsTune() is { } tune
                    && book.IsDue(resting.DeviceId, ReceptionChannel.Of(channel), now)
                )
                {
                    yield return (device, channel, tune);

                    break;
                }
            }
        }
    }

    private DeviceSettings? DeviceNamed(string deviceId) =>
        (configuration.Devices ?? []).FirstOrDefault(device => string.Equals(device?.Id, deviceId, StringComparison.Ordinal));

    private static bool Receives(DeviceSettings device, TuningKey channel) =>
        (device.Kind, channel.Kind) is (DeviceKind.Terrestrial, TunerKind.Terrestrial) or (DeviceKind.Satellite, TunerKind.Satellite);

    private IdleMeasurement Measure(DeviceSettings device, TuningKey channel, TuneParams tune, MeasuringTicket ticket)
    {
        string deviceId = device.Id!;
        Reading reading;

        try
        {
            reading = Read(device, tune, ticket.Stopping);
        }
        finally
        {
            bool undisturbed = manager.EndMeasuring(ticket);

            lock (gate)
            {
                lastEndedAt = clock.GetUtcNow();
            }

            if (!undisturbed)
            {
                reading = Reading.Interrupted;
            }
        }

        IdleMeasurement measurement = Conclude(deviceId, channel, reading);

        Interlocked.Increment(ref measurement.End is IdleMeasurementEnd.Interrupted ? ref interrupted : ref finished);
        Say(measurement);

        return measurement;
    }

    private sealed record Reading(IdleMeasurementEnd End, bool Locked = false, double? CarrierToNoise = null, Exception? Cause = null)
    {
        public static readonly Reading Interrupted = new(IdleMeasurementEnd.Interrupted);
    }

    private Reading Read(DeviceSettings device, TuneParams tune, CancellationToken stopping)
    {
        try
        {
            stopping.ThrowIfCancellationRequested();

            using IReceptionProbe probe = probes.Tune(device, tune, stopping);

            return ReadTheSignal(probe, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return Reading.Interrupted;
        }
        catch (DvbDeviceException channel) when (channel.Failure is TuningFailure.NoLock or TuningFailure.LockedWithoutData)
        {
            return new Reading(IdleMeasurementEnd.Kept);
        }
        catch (DvbDeviceException refused) when (refused.Failure is TuningFailure.Unspecified)
        {
            return new Reading(IdleMeasurementEnd.Refused, Cause: refused);
        }
        catch (Exception failed) when (failed is not OutOfMemoryException)
        {
            return stopping.IsCancellationRequested
                ? Reading.Interrupted
                : new Reading(IdleMeasurementEnd.DeviceFailed, Cause: failed);
        }
    }

    private Reading ReadTheSignal(IReceptionProbe probe, CancellationToken stopping)
    {
        List<double> read = [];
        bool locked = false;

        for (int reading = 0; reading < Readings; reading++)
        {
            stopping.ThrowIfCancellationRequested();

            SignalQuality quality = probe.Measure();

            locked |= quality.HasLock;

            if (quality.HasLock && quality.CarrierToNoise.TryGetDecibels(out double decibels))
            {
                read.Add(decibels);
            }

            if (reading < Readings - 1 && stopping.WaitHandle.WaitOne(readingGap))
            {
                return Reading.Interrupted;
            }
        }

        return new Reading(IdleMeasurementEnd.Kept, locked, locked ? Median.Of(read) : null);
    }

    private IdleMeasurement Conclude(string deviceId, TuningKey channel, Reading reading)
    {
        ReceptionChannel on = ReceptionChannel.Of(channel);
        DateTimeOffset now = clock.GetUtcNow();

        switch (reading.End)
        {
            case IdleMeasurementEnd.Kept:
                bool kept = book.Note(
                    deviceId,
                    on,
                    new ReceptionResult(now, reading.Locked, reading.CarrierToNoise),
                    () => manager.IsReceivedOnAnotherTuner(on, deviceId)
                );

                return new IdleMeasurement(
                    deviceId,
                    channel,
                    kept ? IdleMeasurementEnd.Kept : IdleMeasurementEnd.ThrownAway,
                    reading.CarrierToNoise
                );
            case IdleMeasurementEnd.Refused:
                book.Note(deviceId, on, new ReceptionResult(now, false, null), () => false);
                logger.LogWarning(
                    "The tuner {DeviceId} could not be measured on {Channel}, and is not tried on it again for {Hours} hours: {Why}",
                    deviceId,
                    channel,
                    ReceptionBook.RetryAfter.TotalHours,
                    reading.Cause?.Message
                );

                break;
            case IdleMeasurementEnd.DeviceFailed:
                manager.FaultAfterMeasuring(deviceId, reading.Cause!);

                break;
        }

        return new IdleMeasurement(deviceId, channel, reading.End);
    }

    private void Say(IdleMeasurement measurement)
    {
        if (measurement.End is IdleMeasurementEnd.Kept)
        {
            logger.LogInformation(
                "The tuner {DeviceId} was measured on {Channel} while nobody was on it, and its record there is now: {Standing}.",
                measurement.DeviceId,
                measurement.Channel,
                book.StandingOf(measurement.DeviceId, ReceptionChannel.Of(measurement.Channel))
            );

            return;
        }

        logger.LogDebug(
            "Measuring the tuner {DeviceId} on {Channel} while nobody was on it ended as {End}.",
            measurement.DeviceId,
            measurement.Channel,
            measurement.End
        );
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task last;

        lock (gate)
        {
            stopping = true;
            timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            last = running;
        }

        try
        {
            await last.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("The measurement of an idle tuner had not stopped when the driver gave up waiting for it.");
        }
    }

    public void Dispose() => timer?.Dispose();
}
