using Microsoft.Extensions.Logging;

using Puppis.Contracts;
using Puppis.Driver.CardLending;
using Puppis.Driver.Configuration;

namespace Puppis.Driver.Descrambling;

public sealed class Descramblers : IDescramblerFactory, ICardReaderReport
{
    private readonly Func<ISmartCardService> openService;

    private readonly ILogger? logger;

    private readonly TimeProvider clock;

    private readonly AnswerTimes times;

    private CardIdentity? cardInHand;

    private string? readerInUse;

    private Descramblers(Func<ISmartCardService> openService, ILogger? logger, TimeProvider? clock)
    {
        this.openService = openService;
        this.logger = logger;
        this.clock = clock ?? TimeProvider.System;
        times = new AnswerTimes(this.clock);
    }

    public static Descramblers Over(Func<ISmartCardService> openService, ILogger? logger, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(openService);

        return new Descramblers(openService, logger, clock);
    }

    public static IDescramblerFactory For(
        DriverConfiguration configuration,
        CardLoan? loan,
        ILogger? logger,
        TimeProvider? clock = null,
        IDescramblerFactory? slots = null
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);

        TunerBackend backend = configuration.Tuner?.Backend ?? TunerBackend.Unspecified;
        if (backend is not (TunerBackend.Dvb or TunerBackend.Usb))
        {
            return NoDescrambling.Instance;
        }

        if (loan is not null)
        {
            return BorrowedCards.From(loan, logger, clock);
        }

        return backend is TunerBackend.Dvb ? Probe(logger, clock) : slots ?? NoDescrambling.Instance;
    }

    public static Func<IConditionalAccess> CardsToLend(ILogger? logger)
    {
        PcscLibrary? library = PcscLibrary.Load(out string whyNot);
        if (library is null)
        {
            logger?.LogWarning("This driver has no card to lend: {Why}", whyNot);

            return () => throw new DescramblingException(whyNot);
        }

        return () => OpenCard(library.Open, settled: null);
    }

    public static IDescramblerFactory Probe(ILogger? logger, TimeProvider? clock = null)
    {
        PcscLibrary? library = PcscLibrary.Load(out string whyNot);
        if (library is null)
        {
            logger?.LogWarning(
                "This driver does not unscramble and does not offer to: {Why} What it records stays as the tuner gave it, and its scrambled-packet count says how much of that was scrambled.",
                whyNot
            );

            return NoDescrambling.Instance;
        }

        Descramblers descramblers = new(library.Open, logger, clock);

        try
        {
            descramblers.TheCard().Dispose();

            logger?.LogInformation(
                "A card answered the reader, so this driver unscrambles what it records and says so in its greeting."
            );
        }
        catch (DescramblingException error)
        {
            descramblers.Missed(error);
            logger?.LogWarning(
                "No card answered the reader when this driver started, so it asks again as each tuner opens and while one is being read: {Why}",
                error.Message
            );
        }

        return descramblers;
    }

    public bool Unscrambles => true;

    public CardIdentity? CardInHand => Volatile.Read(ref cardInHand);

    public IDescrambler? Open(HeadHold hold)
    {
        try
        {
            return new TransportStreamDescrambler(
                new NoticedCard(TheCard(), times.Answered),
                logger,
                hold: hold,
                time: clock,
                cardKind: "local"
            );
        }
        catch (DescramblingException error)
        {
            Missed(error);
            logger?.LogDebug(
                "The card was asked for and did not answer, so it is asked again: {Why}",
                error.Message
            );

            return null;
        }
    }

    public CardReaderDto Inspect()
    {
        DateTimeOffset now = clock.GetUtcNow();

        return Look() with
        {
            Use = CardReaderUse.Local,
            LastAnsweredAt = times.LastAnswered,
            CheckedAt = now,
        };
    }

    private CardReaderDto Look()
    {
        ISmartCardService service;

        try
        {
            service = openService();
        }
        catch (SmartCardException refused)
        {
            return Unresponsive(refused);
        }

        using (service)
        {
            try
            {
                return Looked(service);
            }
            catch (SmartCardException refused)
            {
                return Unresponsive(refused);
            }
        }
    }

    private CardReaderDto Looked(ISmartCardService service)
    {
        IReadOnlyList<string> readers = service.Readers();
        if (readers.Count is 0)
        {
            return new CardReaderDto { State = CardState.NoReader };
        }

        IReadOnlyList<CardSlot> slots = service.Slots(readers);
        bool silent = times.FailedSinceItAnswered;
        string? inUse = Volatile.Read(ref readerInUse);

        CardReaderSlotDto[] shown =
        [
            .. readers.Select((reader, index) => new CardReaderSlotDto
            {
                Name = CardReaderNames.Shown(reader),
                State = SlotState(index < slots.Count ? slots[index] : CardSlot.Unknown, silent),
                InUse = string.Equals(reader, inUse, StringComparison.Ordinal)
                    && index < slots.Count
                    && slots[index] is CardSlot.Present,
            }),
        ];

        if (shown.Any(slot => slot.State is CardState.Inserted))
        {
            return new CardReaderDto { State = CardState.Inserted, Readers = shown };
        }

        return shown.Any(slot => slot.State is CardState.Unresponsive)
            ? new CardReaderDto { State = CardState.Unresponsive, Problem = CardProblem.CardSilent, Readers = shown }
            : new CardReaderDto { State = CardState.Empty, Readers = shown };
    }

    private static CardState SlotState(CardSlot slot, bool silent) =>
        slot switch
        {
            CardSlot.Present => silent ? CardState.Unresponsive : CardState.Inserted,
            CardSlot.Mute => CardState.Unresponsive,
            _ => CardState.Empty,
        };

    private static CardReaderDto Unresponsive(SmartCardException refused) =>
        new()
        {
            State = CardState.Unresponsive,
            Problem = refused.Code is SmartCardCodes.NoService ? CardProblem.ServiceDown : null,
        };

    private void Missed(DescramblingException error)
    {
        if (error.Failure is DescramblingFailure.CardSilent)
        {
            times.Failed();
        }
    }

    private ConditionalAccessCard TheCard()
    {
        ConditionalAccessCard card = OpenCard(
            openService,
            identity =>
            {
                Volatile.Write(ref cardInHand, identity);
                times.Answered();
            }
        );
        Volatile.Write(ref readerInUse, card.Reader);

        return card;
    }

    private static ConditionalAccessCard OpenCard(Func<ISmartCardService> openService, Action<CardIdentity>? settled)
    {
        ISmartCardService service;

        try
        {
            service = openService();
        }
        catch (SmartCardException refused)
        {
            throw new DescramblingException(refused.Message);
        }

        return ConditionalAccessCard.Open(service, settled);
    }
}
