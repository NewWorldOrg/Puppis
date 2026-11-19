namespace Puppis.Driver.Descrambling;

public interface IConditionalAccess : IDisposable
{
    int CaSystemId { get; }

    EcmAnswer Answer(ReadOnlySpan<byte> ecmBody);
}
