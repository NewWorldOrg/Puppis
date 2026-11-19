using Microsoft.Extensions.Logging;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Descrambling;

namespace Puppis.Driver.Tuning.Usb.PixDt295;

public sealed class SlotCards(ILogger? logger, TimeProvider? clock = null) : IDescramblerFactory, ICardReaderReport
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);

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

    public static SlotCards? For(DriverConfiguration configuration, bool borrows, ILogger? logger, TimeProvider? clock) =>
        borrows || configuration.Tuner?.Backend is not TunerBackend.Usb ? null : new SlotCards(logger, clock);

    public IDescrambler? Open(HeadHold hold) => null;

    public void Present(string deviceId, bool? card)
    {
        lock (gate)
        {
            Slot slot = SlotOf(deviceId);
            slot.Present = true;
            slot.Card = card ?? slot.Card;
        }
    }

    public bool Knows(string deviceId)
    {
        lock (gate)
        {
            return slots.TryGetValue(deviceId, out Slot? slot) && slot.Present && slot.Card is not null;
        }
    }

    public void Absent(string deviceId)
    {
        lock (gate)
        {
            slots.Remove(deviceId);
        }
    }

    public IDescramblerFactory Attach(string deviceId, Dt295Bridge tuner, Func<ISmartCardService> session)
    {
        Descramblers cards = Descramblers.Over(session, logger, clock);
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
                InUse = slot.HoldsACard && slot.Tuner?.InSession is true,
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
