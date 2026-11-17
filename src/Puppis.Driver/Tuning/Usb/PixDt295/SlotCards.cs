using Microsoft.Extensions.Logging;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

/// <summary>
/// The cards in the slots of the PIX-DT295 tuners a usb driver drives, for a driver that has no other card. What it
/// says is worked out from what is known now each time it is asked: which of those tuners are plugged in, as the
/// driver's presence checks last found, and whether each slot held a card when the slot was last spoken to. Answering
/// asks no device anything.
/// </summary>
public sealed class SlotCards(ILogger? logger, TimeProvider? clock = null) : IDescramblerFactory, ICardReaderReport
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a PIX-DT295 is plugged in now with a card in its slot.
    /// </summary>
    public bool Unscrambles
    {
        get
        {
            lock (gate)
            {
                return slots.Values.Any(slot => slot.HoldsACard);
            }
        }
    }

    public CardIdentity? CardInHand
    {
        get
        {
            lock (gate)
            {
                return slots.Values
                    .Where(slot => slot.Present)
                    .OrderByDescending(slot => slot.AnsweredAt)
                    .Select(slot => slot.Cards?.CardInHand)
                    .FirstOrDefault(identity => identity is not null);
            }
        }
    }

    /// <summary>
    /// The slot cards of a usb driver that borrows no card, or null for any other driver.
    /// </summary>
    public static SlotCards? For(DriverConfiguration configuration, bool borrows, ILogger? logger, TimeProvider? clock) =>
        borrows || configuration.Tuner?.Backend is not TunerBackend.Usb ? null : new SlotCards(logger, clock);

    /// <summary>
    /// Nothing is asked for without a tuner to reach the slot through.
    /// </summary>
    public IDescrambler? Open(HeadHold hold) => null;

    /// <summary>
    /// Writes down that the PIX-DT295 declared as <paramref name="deviceId"/> is plugged in, and whether its slot was
    /// seen to hold a card, when that was looked at.
    /// </summary>
    public void Present(string deviceId, bool? card)
    {
        lock (gate)
        {
            Slot slot = SlotOf(deviceId);
            slot.Present = true;
            slot.Card = card ?? slot.Card;
        }
    }

    /// <summary>
    /// Whether the slot of <paramref name="deviceId"/> has been seen, with or without a card, since it was last plugged in.
    /// </summary>
    public bool Knows(string deviceId)
    {
        lock (gate)
        {
            return slots.TryGetValue(deviceId, out Slot? slot) && slot.Present && slot.Card is not null;
        }
    }

    /// <summary>
    /// Writes down that <paramref name="deviceId"/> is not plugged in, or is not a PIX-DT295, so its slot no longer
    /// counts.
    /// </summary>
    public void Absent(string deviceId)
    {
        lock (gate)
        {
            slots.Remove(deviceId);
        }
    }

    /// <summary>
    /// The cards of <paramref name="tuner"/>'s slot, for the session about to start on it; the slot is looked at once
    /// on the way, while the tuner is open for the session anyway.
    /// </summary>
    public IDescramblerFactory Attach(string deviceId, Dt295Bridge tuner)
    {
        Descramblers cards = Descramblers.Over(tuner.CardService, logger, clock);
        bool card = tuner.Slot.CardIsIn();

        lock (gate)
        {
            Slot slot = SlotOf(deviceId);
            slot.Present = true;
            slot.Card = card;
            slot.Cards = cards;
            slot.Tuner = tuner;
        }

        return new Noted(cards, () => Answered(deviceId));
    }

    public CardReaderDto Inspect()
    {
        DateTimeOffset now = clock.GetUtcNow();
        Slot[] present;

        lock (gate)
        {
            present = [.. slots.Values.Where(slot => slot.Present).Select(slot => slot.Copy())];
        }

        if (present.Length is 0)
        {
            return new CardReaderDto { Use = CardReaderUse.None, State = CardState.NotUsed, CheckedAt = now };
        }

        CardReaderSlotDto[] readers =
        [
            .. present.Select(slot => new CardReaderSlotDto
            {
                Name = Dt295CardSlot.ReaderName,
                State = StateOf(slot),
                InUse = slot.HoldsACard && slot.Tuner?.IsOpen is true,
            }),
        ];

        return new CardReaderDto
        {
            Use = CardReaderUse.Local,
            State = readers.Any(reader => reader.State is CardState.Inserted) ? CardState.Inserted
                : readers.All(reader => reader.State is CardState.Empty) ? CardState.Empty
                : CardState.Unspecified,
            Readers = readers,
            LastAnsweredAt = present.Max(slot => slot.AnsweredAt),
            CheckedAt = now,
        };
    }

    private static CardState StateOf(Slot slot) =>
        slot.Card switch
        {
            true => CardState.Inserted,
            false => CardState.Empty,
            null => CardState.Unspecified,
        };

    private void Answered(string deviceId)
    {
        lock (gate)
        {
            if (slots.TryGetValue(deviceId, out Slot? slot))
            {
                slot.AnsweredAt = clock.GetUtcNow();
                slot.Card = true;
            }
        }
    }

    private Slot SlotOf(string deviceId)
    {
        if (!slots.TryGetValue(deviceId, out Slot? slot))
        {
            slot = new Slot();
            slots[deviceId] = slot;
        }

        return slot;
    }

    private sealed class Slot
    {
        public bool Present { get; set; }

        public bool? Card { get; set; }

        public Descramblers? Cards { get; set; }

        public Dt295Bridge? Tuner { get; set; }

        public DateTimeOffset? AnsweredAt { get; set; }

        public bool HoldsACard => Present && Card is true;

        public Slot Copy() =>
            new()
            {
                Present = Present,
                Card = Card,
                Cards = Cards,
                Tuner = Tuner,
                AnsweredAt = AnsweredAt,
            };
    }

    /// <summary>
    /// Passes a slot's cards through, writing down each time one is in hand.
    /// </summary>
    private sealed class Noted(IDescramblerFactory cards, Action answered) : IDescramblerFactory
    {
        public bool Unscrambles => cards.Unscrambles;

        public CardIdentity? CardInHand => cards.CardInHand;

        public IDescrambler? Open(HeadHold hold)
        {
            IDescrambler? opened = cards.Open(hold);

            if (opened is not null)
            {
                answered();
            }

            return opened;
        }
    }
}
