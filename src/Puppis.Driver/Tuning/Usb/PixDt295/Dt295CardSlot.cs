using Puppis.Driver.Descrambling;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

/// <summary>
/// The card slot of an open PIX-DT295, offered as a smart-card service with one reader. The card is powered when it
/// is connected to and stays powered until the tuner closes; disposing a connection or the service leaves it as it is.
/// </summary>
public sealed class Dt295CardSlot(CardUart uart)
{
    public const string ReaderName = "PIX-DT295 card slot";

    public const byte KnownSpeed = 0x12;

    private readonly Lock gate = new();
    private bool closed;

    public bool Powered { get; private set; }

    public ISmartCardService Service() => new SlotService(this);

    /// <summary>
    /// Switches the card off, once the tuner is closing, and refuses the slot from then on.
    /// </summary>
    public void Close()
    {
        lock (gate)
        {
            closed = true;
            Powered = false;
            uart.SwitchOff();
        }
    }

    private CardSlot Look()
    {
        lock (gate)
        {
            return !closed && uart.CardIsIn() ? CardSlot.Present : CardSlot.Empty;
        }
    }

    private T1Protocol Connect()
    {
        lock (gate)
        {
            Usable();
            Powered = true;
            uart.SwitchOn();
            AnswerToReset answer = AnswerToReset.Read(uart.Receive(CardUart.AnswerToResetPatience, _ => false))
                ?? throw new SmartCardException(
                    $"{uart.Path}: the card in the slot gave no answer to reset that could be read.",
                    SmartCardCodes.Timeout
                );

            if (answer.SpeedByte is not KnownSpeed || !answer.OffersT1)
            {
                throw new SmartCardException(
                    $"{uart.Path}: the card in the slot asks for a speed or protocol this driver does not speak.",
                    SmartCardCodes.CommunicationLost
                );
            }

            uart.SpeakFaster();
            var protocol = new T1Protocol(ExchangeBlock, answer.InformationFieldSize);
            protocol.AskForBiggerBlocks();

            return protocol;
        }
    }

    private byte[] Transmit(T1Protocol protocol, ReadOnlySpan<byte> command)
    {
        lock (gate)
        {
            Usable();

            return protocol.Transmit(command);
        }
    }

    private void SwitchOff()
    {
        lock (gate)
        {
            Usable();
            Powered = false;
            uart.SwitchOff();
        }
    }

    private void Usable()
    {
        ObjectDisposedException.ThrowIf(closed, this);

        if (!uart.CardIsIn())
        {
            throw CardUart.NoCard(uart.Path);
        }
    }

    private byte[] ExchangeBlock(byte[] block)
    {
        uart.Send(block);

        return uart.Receive(
            CardUart.ReplyPatience,
            received => T1Protocol.LengthOf(received) is { } length && received.Count >= length
        );
    }

    private sealed class SlotService(Dt295CardSlot slot) : ISmartCardService
    {
        public IReadOnlyList<string> Readers() => [ReaderName];

        public IReadOnlyList<CardSlot> Slots(IReadOnlyList<string> readers) => [.. readers.Select(_ => slot.Look())];

        public ISmartCardConnection Connect(string reader) =>
            reader is ReaderName
                ? new SlotConnection(slot, slot.Connect())
                : throw new SmartCardException($"There is no reader named '{reader}' on this tuner.", SmartCardCodes.NoReadersAvailable);

        public void Dispose() { }
    }

    private sealed class SlotConnection(Dt295CardSlot slot, T1Protocol protocol) : ISmartCardConnection
    {
        private T1Protocol protocol = protocol;

        public byte[] Transmit(ReadOnlySpan<byte> command) => slot.Transmit(protocol, command);

        public void Reconnect()
        {
            slot.SwitchOff();
            protocol = slot.Connect();
        }

        public void Dispose() { }
    }
}
