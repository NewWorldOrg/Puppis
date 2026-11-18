using Puppis.Driver.Sessions;

namespace Puppis.Driver.Reception;

public sealed class ReceptionBook
{
    public const int Window = 20;

    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

    private readonly Lock gate = new();
    private readonly Dictionary<string, Dictionary<ReceptionChannel, List<ReceptionResult>>> results = new(StringComparer.Ordinal);
    private readonly Dictionary<ReceptionChannel, TuningKey> known = [];
    private readonly Dictionary<(string DeviceId, ReceptionChannel Channel), DateTimeOffset> thrownAway = [];

    public event Action? Changed;

    public void Know(TuningKey tuning)
    {
        ReceptionChannel channel = ReceptionChannel.Of(tuning);

        lock (gate)
        {
            if (known.TryGetValue(channel, out TuningKey? seen) && seen == tuning)
            {
                return;
            }

            known[channel] = tuning;
        }

        Changed?.Invoke();
    }

    public IReadOnlyList<TuningKey> Known()
    {
        lock (gate)
        {
            return [.. Ordered(known).Select(pair => pair.Value)];
        }
    }

    public bool Note(string deviceId, ReceptionChannel channel, ReceptionResult result, Func<bool> receivedElsewhere)
    {
        if (!result.Locked && !receivedElsewhere())
        {
            lock (gate)
            {
                thrownAway[(deviceId, channel)] = result.At;
            }

            return false;
        }

        lock (gate)
        {
            List<ReceptionResult> kept = ResultsOf(deviceId, channel);

            kept.Add(result);
            Trim(kept);
            thrownAway.Remove((deviceId, channel));
        }

        Changed?.Invoke();

        return true;
    }

    public ReceptionStanding StandingOf(string deviceId, ReceptionChannel channel)
    {
        lock (gate)
        {
            return results.TryGetValue(deviceId, out Dictionary<ReceptionChannel, List<ReceptionResult>>? channels)
                && channels.TryGetValue(channel, out List<ReceptionResult>? kept)
                ? StandingOf(kept)
                : ReceptionStanding.Unmeasured;
        }
    }

    public bool IsDue(string deviceId, ReceptionChannel channel, DateTimeOffset now)
    {
        ReceptionStanding standing = StandingOf(deviceId, channel);

        lock (gate)
        {
            if (thrownAway.TryGetValue((deviceId, channel), out DateTimeOffset at) && now - at < RetryAfter)
            {
                return false;
            }
        }

        return standing.NewestAt is not { } newest || now - newest > StaleAfter;
    }

    public ReceptionRecords Records()
    {
        lock (gate)
        {
            return new ReceptionRecords(
                [.. Ordered(known).Select(pair => pair.Value)],
                [
                    .. results
                        .OrderBy(tuner => tuner.Key, StringComparer.Ordinal)
                        .Select(tuner => new TunerRecords(
                            tuner.Key,
                            [
                                .. Ordered(tuner.Value).Select(channel => new ChannelRecords(
                                    channel.Key.Kind,
                                    channel.Key.PhysicalChannel,
                                    [.. channel.Value]
                                )),
                            ]
                        )),
                ]
            );
        }
    }

    public void Restore(ReceptionRecords records, IReadOnlyCollection<string> configured)
    {
        lock (gate)
        {
            foreach (TuningKey tuning in (records.Channels ?? []).OfType<TuningKey>())
            {
                known[ReceptionChannel.Of(tuning)] = tuning;
            }

            foreach (TunerRecords tuner in (records.Tuners ?? []).OfType<TunerRecords>().Where(tuner => configured.Contains(tuner.DeviceId)))
            {
                RestoreTuner(tuner);
            }
        }
    }

    private void RestoreTuner(TunerRecords tuner)
    {
        foreach (ChannelRecords channel in (tuner.Channels ?? []).OfType<ChannelRecords>())
        {
            List<ReceptionResult> kept = ResultsOf(tuner.DeviceId, new ReceptionChannel(channel.Kind, channel.PhysicalChannel));

            kept.AddRange((channel.Results ?? []).OfType<ReceptionResult>().OrderBy(result => result.At));
            Trim(kept);
        }
    }

    private List<ReceptionResult> ResultsOf(string deviceId, ReceptionChannel channel)
    {
        if (!results.TryGetValue(deviceId, out Dictionary<ReceptionChannel, List<ReceptionResult>>? channels))
        {
            channels = [];
            results[deviceId] = channels;
        }

        if (!channels.TryGetValue(channel, out List<ReceptionResult>? kept))
        {
            kept = [];
            channels[channel] = kept;
        }

        return kept;
    }

    private static void Trim(List<ReceptionResult> kept)
    {
        if (kept.Count > Window)
        {
            kept.RemoveRange(0, kept.Count - Window);
        }
    }

    private static ReceptionStanding StandingOf(List<ReceptionResult> kept) =>
        kept.Count is 0
            ? ReceptionStanding.Unmeasured
            : new ReceptionStanding(
                kept.Count,
                kept.Count(result => result.Locked),
                Median.Of(kept.Where(result => result.Locked).Select(result => result.CarrierToNoise).OfType<double>()),
                kept.Max(result => result.At)
            );

    private static IEnumerable<KeyValuePair<ReceptionChannel, T>> Ordered<T>(Dictionary<ReceptionChannel, T> channels) =>
        channels.OrderBy(pair => pair.Key.Kind).ThenBy(pair => pair.Key.PhysicalChannel);
}
