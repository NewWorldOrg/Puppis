using Puppis.Driver.Descrambling;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

/// <summary>
/// The card slot of an open PIX-DT295, offered as a smart-card service with one reader. The card is powered when it
/// is first connected to and stays powered until its session ends or the tuner closes, so that later connections in a
/// session speak to it as it is; disposing a connection or the service leaves it as it is. A card that stops answering
/// is powered afresh by the next connection, and so is every card at the start of a session, since one may have been
/// swapped in the meantime without the slot saying so.
/// </summary>
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

    /// <summary>
    /// The slot for the session going on now, refusing everything once that session is over.
    /// </summary>
    public ISmartCardService Service()
    {
        lock (gate)
        {
            return Service(session);
        }
    }

    /// <summary>
    /// The slot for session <paramref name="ofSession"/>, refusing everything once that session is over.
    /// </summary>
    public ISmartCardService Service(int ofSession) => new SlotService(this, ofSession);

    /// <summary>
    /// Starts a session on the slot, whose first connection powers the card on and reads its answer to reset afresh,
    /// since a card may have been swapped without the slot saying so. Answers the session's number.
    /// </summary>
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

    /// <summary>
    /// Whether a card is in the slot now, asked of the bridge.
    /// </summary>
    public bool CardIsIn()
    {
        lock (gate)
        {
            return !closed && uart.CardIsIn();
        }
    }

    /// <summary>
    /// Ends the session going on, so that nothing it still has in hand reaches the card, and switches the card off,
    /// when it is on.
    /// </summary>
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

    /// <summary>
    /// Switches the card off, once the tuner is closing, and refuses the slot from then on.
    /// </summary>
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
