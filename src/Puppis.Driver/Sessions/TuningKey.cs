using Puppis.Contracts;

namespace Puppis.Driver.Sessions;

public sealed record TuningKey(TunerKind Kind, int PhysicalChannel, int TransportStreamId)
{
    public const int WholeStream = 0;

    public static TuningKey Of(StartSessionRequest request) =>
        request.Tune is { } tune
            ? Of(tune)
            : new TuningKey(request.Tuning.Kind, request.Tuning.PhysicalChannel, WholeStream);

    public static TuningKey Of(TuneParams tune) =>
        tune.System switch
        {
            TuneSystem.IsdbT => new TuningKey(
                TunerKind.Terrestrial,
                tune.IsdbT?.PhysicalChannel ?? 0,
                WholeStream
            ),
            TuneSystem.IsdbSBs => new TuningKey(
                TunerKind.Satellite,
                tune.IsdbSBs?.BsChannel ?? 0,
                tune.IsdbSBs?.Tsid ?? WholeStream
            ),
            TuneSystem.IsdbSCs110 => new TuningKey(
                TunerKind.Satellite,
                tune.IsdbSCs110?.CsChannel ?? 0,
                WholeStream
            ),
            _ => new TuningKey(TunerKind.Unspecified, 0, WholeStream),
        };

    public TuneParams? AsTune() =>
        Kind switch
        {
            TunerKind.Terrestrial when BroadcastStandards.IsTerrestrialChannel(PhysicalChannel) =>
                TuneParams.Terrestrial(PhysicalChannel),
            TunerKind.Satellite when BroadcastStandards.IsCs110Channel(PhysicalChannel) =>
                TuneParams.Cs110(PhysicalChannel),
            TunerKind.Satellite when BroadcastStandards.IsBsChannel(PhysicalChannel) && TransportStreamId is not WholeStream =>
                TuneParams.Bs(PhysicalChannel, TransportStreamId),
            _ => null,
        };

    public override string ToString() =>
        TransportStreamId is WholeStream
            ? $"{TunerKindConverter.WireName(Kind)} channel {PhysicalChannel}"
            : $"{TunerKindConverter.WireName(Kind)} channel {PhysicalChannel} stream {TransportStreamId}";
}
