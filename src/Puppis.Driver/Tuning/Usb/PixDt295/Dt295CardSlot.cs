using Puppis.Driver.Descrambling;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

public sealed class Dt295CardSlot(CardUart uart)
{
    public const string ReaderName = "PIX-DT295 card slot";

    public const byte KnownSpeed = 0x12;

    private readonly Lock gate = new();
    private bool closed;
    private T1Protocol? speaking;
    private int session;

    public bool Powered { get; private set; }

    public bool IsOpen
    {
        get
        {
            lock (gate)
            {
                return !closed;
            }
        }
    }

    public ISmartCardService Service()
    {
        lock (gate)
        {
            return Service(session);
        }
    }

    public ISmartCardService Service(int ofSession) => new SlotService(this, ofSession);

    public int BeginSession()
    {
        lock (gate)
        {
            session++;
            speaking = null;
            Powered = false;

            return session;
        }
    }

    public bool CardIsIn()
    {
        lock (gate)
        {
            return !closed && uart.CardIsIn();
        }
    }

    public void Rest()
    {
        lock (gate)
        {
            session++;
            speaking = null;

            if (closed || !Powered)
            {
                return;
            }

            Powered = false;
            speaking = null;
            uart.SwitchOff();
        }
    }

    public void Close()
    {
        lock (gate)
        {
            closed = true;
            Powered = false;
            speaking = null;
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

    private T1Protocol Connect(int ofSession)
    {
        lock (gate)
        {
            Usable(ofSession);

            if (Powered && speaking is not null)
            {
                return speaking;
            }

            Powered = true;
            speaking = null;
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
            speaking = protocol;

            return protocol;
        }
    }

    private byte[] Transmit(int ofSession, T1Protocol protocol, ReadOnlySpan<byte> command)
    {
        lock (gate)
        {
            Usable(ofSession);

            try
            {
                return protocol.Transmit(command);
            }
            catch (SmartCardException)
            {
                speaking = null;
                Powered = false;

                throw;
            }
        }
    }

    private void SwitchOff(int ofSession)
    {
        lock (gate)
        {
            Usable(ofSession);
            Powered = false;
            speaking = null;
            uart.SwitchOff();
        }
    }

    private void Usable(int ofSession)
    {
        ObjectDisposedException.ThrowIf(closed, this);

        if (ofSession != session)
        {
            throw new SmartCardException(
                $"{uart.Path}: the session that asked for the card is over, so the card is not spoken to on its behalf.",
                SmartCardCodes.CommunicationLost
            );
        }

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

    private sealed class SlotService(Dt295CardSlot slot, int ofSession) : ISmartCardService
    {
        public IReadOnlyList<string> Readers() => [ReaderName];

        public IReadOnlyList<CardSlot> Slots(IReadOnlyList<string> readers) => [.. readers.Select(_ => slot.Look())];

        public ISmartCardConnection Connect(string reader) =>
            reader is ReaderName
                ? new SlotConnection(slot, ofSession, slot.Connect(ofSession))
                : throw new SmartCardException($"There is no reader named '{reader}' on this tuner.", SmartCardCodes.NoReadersAvailable);

        public void Dispose() { }
    }

    private sealed class SlotConnection(Dt295CardSlot slot, int ofSession, T1Protocol protocol) : ISmartCardConnection
    {
        private T1Protocol protocol = protocol;

        public byte[] Transmit(ReadOnlySpan<byte> command) => slot.Transmit(ofSession, protocol, command);

        public void Reconnect()
        {
            slot.SwitchOff(ofSession);
            protocol = slot.Connect(ofSession);
        }

        public void Dispose() { }
    }
}
