namespace Puppis.Driver.Descrambling;

public sealed class SmartCardException(string message, uint code) : IOException(message)
{
    public uint Code { get; } = code;

    public bool CardWasReset => Code is SmartCardCodes.ResetCard or SmartCardCodes.RemovedCard;
}

public static class SmartCardCodes
{
    public const uint Success = 0x00000000;

    public const uint InsufficientBuffer = 0x80100008;

    public const uint SharingViolation = 0x8010000B;

    public const uint Timeout = 0x8010000A;

    public const uint NoSmartCard = 0x8010000C;

    public const uint NoService = 0x8010001D;

    public const uint NoReadersAvailable = 0x8010002E;

    public const uint ResetCard = 0x80100068;

    public const uint RemovedCard = 0x80100069;

    public const uint CommunicationLost = 0x8010002F;

    public static string Describe(uint code) =>
        code switch
        {
            Success => "success",
            InsufficientBuffer => "the answer did not fit",
            SharingViolation => "another program holds the card exclusively",
            NoSmartCard => "no card is in the reader",
            NoService => "the smart-card service is not running",
            NoReadersAvailable => "no reader is attached",
            ResetCard => "the card was reset",
            RemovedCard => "the card was removed",
            CommunicationLost => "what the card sent was lost on the way",
            _ => "an error the service did not name",
        } + $" (0x{code:X8})";
}

public interface ISmartCardConnection : IDisposable
{
    byte[] Transmit(ReadOnlySpan<byte> command);

    void Reconnect();
}

public enum CardSlot
{
    Unknown = 0,

    Empty = 1,

    Present = 2,

    Mute = 3,
}

public interface ISmartCardService : IDisposable
{
    IReadOnlyList<string> Readers();

    IReadOnlyList<CardSlot> Slots(IReadOnlyList<string> readers);

    ISmartCardConnection Connect(string reader);
}
