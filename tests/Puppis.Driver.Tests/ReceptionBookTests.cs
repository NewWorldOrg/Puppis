using Puppis.Contracts;
using Puppis.Driver.Reception;
using Puppis.Driver.Sessions;

namespace Puppis.Driver.Tests;

public sealed class ReceptionBookTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 13, 21, 0, 0, TimeSpan.Zero);

    private static readonly ReceptionChannel Here = new(TunerKind.Terrestrial, 31);

    private static readonly ReceptionChannel There = new(TunerKind.Terrestrial, 33);

    private static ReceptionResult Locked(double? decibels, int minute = 0) =>
        new(Start.AddMinutes(minute), true, decibels);

    private static ReceptionResult NoLock(int minute = 0) => new(Start.AddMinutes(minute), false, null);

    private static bool Receiving() => true;

    private static bool NobodyReceiving() => false;

    [Fact]
    public void ACombinationNobodyUsedHasNoStanding()
    {
        var book = new ReceptionBook();

        Assert.Equal(ReceptionStanding.Unmeasured, book.StandingOf("north", Here));
    }

    [Fact]
    public void OnlyTheLastTwentyResultsAreKept()
    {
        var book = new ReceptionBook();

        book.Note("north", Here, NoLock(), Receiving);

        for (int use = 1; use <= ReceptionBook.Window; use++)
        {
            book.Note("north", Here, Locked(20, use), NobodyReceiving);
        }

        ReceptionStanding standing = book.StandingOf("north", Here);

        Assert.Equal(20, standing.Kept);
        Assert.Equal(20, standing.Locked);
        Assert.Equal(Start.AddMinutes(20), standing.NewestAt);
    }

    [Theory]
    [InlineData(new[] { 18.0, 22.0, 20.0 }, 20.0)]
    [InlineData(new[] { 18.0, 22.0, 20.0, 30.0 }, 21.0)]
    [InlineData(new[] { 25.5 }, 25.5)]
    public void TheCarrierToNoiseOfACombinationIsTheMedianOfWhatItsUsesRead(double[] read, double expected)
    {
        var book = new ReceptionBook();

        foreach (double decibels in read)
        {
            book.Note("north", Here, Locked(decibels), NobodyReceiving);
        }

        Assert.Equal(expected, book.StandingOf("north", Here).CarrierToNoise);
    }

    [Fact]
    public void AUseThatReadNoCarrierToNoiseCountsAsLockedAndLeavesTheMedianAlone()
    {
        var book = new ReceptionBook();

        book.Note("north", Here, Locked(19), NobodyReceiving);
        book.Note("north", Here, Locked(null), NobodyReceiving);
        book.Note("north", Here, NoLock(), Receiving);

        ReceptionStanding standing = book.StandingOf("north", Here);

        Assert.Equal(3, standing.Kept);
        Assert.Equal(2, standing.Locked);
        Assert.Equal(19, standing.CarrierToNoise);
    }

    [Fact]
    public void AUseThatDidNotLockIsKeptWhileAnotherTunerReceivesTheChannel()
    {
        var book = new ReceptionBook();

        Assert.True(book.Note("north", Here, NoLock(), Receiving));

        Assert.Equal(new ReceptionStanding(1, 0, null, Start), book.StandingOf("north", Here));
    }

    [Fact]
    public void AUseThatDidNotLockIsThrownAwayWhenNoOtherTunerReceivesTheChannel()
    {
        var book = new ReceptionBook();

        Assert.False(book.Note("north", Here, NoLock(), NobodyReceiving));

        Assert.Equal(ReceptionStanding.Unmeasured, book.StandingOf("north", Here));
    }

    [Fact]
    public void WhetherAnotherTunerReceivesTheChannelIsNotAskedForAUseThatLocked()
    {
        var book = new ReceptionBook();

        Assert.True(book.Note("north", Here, Locked(20), () => throw new InvalidOperationException("asked")));
    }

    [Fact]
    public void ACombinationWithNoResultIsDueToBeMeasured()
    {
        var book = new ReceptionBook();

        Assert.True(book.IsDue("north", Here, Start));
    }

    [Fact]
    public void ACombinationIsDueAgainOnceItsNewestResultIsThirtyDaysOld()
    {
        var book = new ReceptionBook();

        book.Note("north", Here, Locked(20), NobodyReceiving);

        Assert.False(book.IsDue("north", Here, Start + ReceptionBook.StaleAfter));
        Assert.True(book.IsDue("north", Here, Start + ReceptionBook.StaleAfter + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ACombinationWhoseResultWasThrownAwayIsNotDueForSixHours()
    {
        var book = new ReceptionBook();

        book.Note("north", Here, NoLock(), NobodyReceiving);

        Assert.False(book.IsDue("north", Here, Start + ReceptionBook.RetryAfter - TimeSpan.FromSeconds(1)));
        Assert.True(book.IsDue("north", Here, Start + ReceptionBook.RetryAfter));
        Assert.True(book.IsDue("north", There, Start));
        Assert.True(book.IsDue("south", Here, Start));
    }

    [Fact]
    public void AChannelIsKnownOnceWithTheStreamItWasLastAskedFor()
    {
        var book = new ReceptionBook();

        book.Know(new TuningKey(TunerKind.Satellite, 15, 16625));
        book.Know(new TuningKey(TunerKind.Terrestrial, 31, 0));
        book.Know(new TuningKey(TunerKind.Satellite, 15, 16626));

        Assert.Equal(
            [new TuningKey(TunerKind.Terrestrial, 31, 0), new TuningKey(TunerKind.Satellite, 15, 16626)],
            book.Known()
        );
    }

    [Fact]
    public void AChangeIsAnnouncedOnceForEachThingThatChanged()
    {
        var book = new ReceptionBook();
        int changes = 0;
        book.Changed += () => changes++;

        book.Know(new TuningKey(TunerKind.Terrestrial, 31, 0));
        book.Know(new TuningKey(TunerKind.Terrestrial, 31, 0));
        book.Note("north", Here, Locked(20), NobodyReceiving);
        book.Note("north", Here, NoLock(), NobodyReceiving);

        Assert.Equal(2, changes);
    }

    [Fact]
    public void WhatIsCopiedOutCanBePutBackIntoAnotherBook()
    {
        var book = new ReceptionBook();
        book.Know(new TuningKey(TunerKind.Terrestrial, 31, 0));
        book.Note("north", Here, Locked(20), NobodyReceiving);
        book.Note("north", There, NoLock(1), Receiving);
        book.Note("south", Here, Locked(24.5, 2), NobodyReceiving);

        var restored = new ReceptionBook();
        restored.Restore(book.Records(), ["north", "south"]);

        Assert.Equal(book.Known(), restored.Known());
        Assert.Equal(book.StandingOf("north", Here), restored.StandingOf("north", Here));
        Assert.Equal(book.StandingOf("north", There), restored.StandingOf("north", There));
        Assert.Equal(book.StandingOf("south", Here), restored.StandingOf("south", Here));
    }

    [Fact]
    public void TheRecordsOfATunerThatIsNoLongerConfiguredAreDropped()
    {
        var book = new ReceptionBook();
        book.Note("north", Here, Locked(20), NobodyReceiving);
        book.Note("gone", Here, Locked(22), NobodyReceiving);

        var restored = new ReceptionBook();
        restored.Restore(book.Records(), ["north"]);

        Assert.Equal(["north"], restored.Records().Tuners.Select(tuner => tuner.DeviceId));
        Assert.Equal(ReceptionStanding.Unmeasured, restored.StandingOf("gone", Here));
    }

    [Fact]
    public void MoreResultsThanTheWindowArePutBackAsTheNewestTwenty()
    {
        ReceptionResult[] many = [.. Enumerable.Range(0, 25).Select(minute => Locked(20, minute))];
        var records = new ReceptionRecords(
            [],
            [new TunerRecords("north", [new ChannelRecords(Here.Kind, Here.PhysicalChannel, many)])]
        );
        var book = new ReceptionBook();

        book.Restore(records, ["north"]);

        ReceptionStanding standing = book.StandingOf("north", Here);

        Assert.Equal(20, standing.Kept);
        Assert.Equal(Start.AddMinutes(24), standing.NewestAt);
    }
}
