using Puppis.Contracts;
using Puppis.Driver.Sessions;

namespace Puppis.Driver.Reception;

public sealed record ChannelRecords(TunerKind Kind, int PhysicalChannel, IReadOnlyList<ReceptionResult> Results);

public sealed record TunerRecords(string DeviceId, IReadOnlyList<ChannelRecords> Channels);

public sealed record ReceptionRecords(IReadOnlyList<TuningKey> Channels, IReadOnlyList<TunerRecords> Tuners)
{
    public static readonly ReceptionRecords Empty = new([], []);
}
