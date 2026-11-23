using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Tuning;

public sealed record TunerOpening(string DeviceId, DeviceKind Kind, DvbChannel Channel, SessionPurpose? Purpose)
{
    public string On(string hardware) =>
        $"devices['{DeviceId}'] ({hardware}, {KindName(Kind)}) for {PurposeName(Purpose)} on {ChannelName(Channel)}";

    public static string ChannelName(DvbChannel channel) =>
        channel switch
        {
            TerrestrialChannel terrestrial => $"physical channel {terrestrial.PhysicalChannel}",
            BroadcastSatelliteChannel broadcast =>
                $"BS slot {broadcast.Slot} transport stream {broadcast.TransportStreamId}",
            CommunicationSatelliteChannel communication => $"CS slot {communication.Slot}",
            _ => "a channel of no known kind",
        };

    private static string KindName(DeviceKind kind) =>
        kind switch
        {
            DeviceKind.Terrestrial => "terrestrial",
            DeviceKind.Satellite => "satellite",
            _ => "unspecified",
        };

    private static string PurposeName(SessionPurpose? purpose) =>
        purpose is { } stated ? SessionPurposeConverter.WireName(stated) : "an unstated purpose";
}
