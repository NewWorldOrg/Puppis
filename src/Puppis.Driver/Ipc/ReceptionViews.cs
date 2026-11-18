using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Reception;
using Puppis.Driver.Sessions;

namespace Puppis.Driver.Ipc;

public sealed record KnownChannelView(TunerKind Kind, int PhysicalChannel, int TransportStreamId);

public sealed record ChannelReceptionView(
    TunerKind Kind,
    int PhysicalChannel,
    int Results,
    int Locked,
    double? CarrierToNoiseDecibels,
    DateTimeOffset? NewestAt
);

public sealed record TunerReceptionView(string DeviceId, IReadOnlyList<ChannelReceptionView> Channels);

public sealed record ReceptionView(IReadOnlyList<KnownChannelView> KnownChannels, IReadOnlyList<TunerReceptionView> Tuners);

public static class ReceptionViews
{
    public const string Path = $"{DriverEndpoints.Tuners}/reception";

    public static ReceptionView Of(DriverConfiguration configuration, ReceptionBook book)
    {
        ReceptionRecords records = book.Records();

        return new ReceptionView(
            [.. records.Channels.Select(channel => new KnownChannelView(channel.Kind, channel.PhysicalChannel, channel.TransportStreamId))],
            [
                .. (configuration.Devices ?? [])
                    .Select(device => device?.Id)
                    .OfType<string>()
                    .Select(deviceId => new TunerReceptionView(deviceId, ChannelsOf(deviceId, records, book))),
            ]
        );
    }

    private static ChannelReceptionView[] ChannelsOf(string deviceId, ReceptionRecords records, ReceptionBook book) =>
        [
            .. records
                .Tuners.Where(tuner => tuner.DeviceId == deviceId)
                .SelectMany(tuner => tuner.Channels)
                .Select(channel => Of(channel, book.StandingOf(deviceId, new ReceptionChannel(channel.Kind, channel.PhysicalChannel)))),
        ];

    private static ChannelReceptionView Of(ChannelRecords channel, ReceptionStanding standing) =>
        new(channel.Kind, channel.PhysicalChannel, standing.Kept, standing.Locked, standing.CarrierToNoise, standing.NewestAt);
}
