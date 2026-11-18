using Puppis.Contracts;
using Puppis.Driver.Sessions;

namespace Puppis.Driver.Reception;

public readonly record struct ReceptionChannel(TunerKind Kind, int PhysicalChannel)
{
    public static ReceptionChannel Of(TuningKey tuning) => new(tuning.Kind, tuning.PhysicalChannel);

    public override string ToString() => $"{TunerKindConverter.WireName(Kind)} channel {PhysicalChannel}";
}
