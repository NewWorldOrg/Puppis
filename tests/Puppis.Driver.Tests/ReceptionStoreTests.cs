using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Reception;
using Puppis.Driver.Sessions;

namespace Puppis.Driver.Tests;

public sealed class ReceptionStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 8, 13, 21, 0, 0, TimeSpan.Zero);

    private static readonly ReceptionChannel Here = new(TunerKind.Terrestrial, 31);

    private static readonly string[] Configured = ["north", "south"];

    private readonly string root = Directory.CreateTempSubdirectory("puppis-reception-").FullName;
    private readonly SteppedTimeProvider clock = new(Start);
    private readonly CapturingLogger<ReceptionStore> log = new();

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Saved => Path.Combine(root, ReceptionStore.FileName);

    private (ReceptionBook Book, ReceptionStore Store) Open(string? directory)
    {
        var book = new ReceptionBook();
        var store = new ReceptionStore(book, directory, clock, log);

        store.Load(Configured);

        return (book, store);
    }

    private static void Use(ReceptionBook book, string deviceId, double decibels = 21.5) =>
        book.Note(deviceId, Here, new ReceptionResult(Start, true, decibels), () => false);

    [Fact]
    public void WhatWasSavedIsReadBackAfterARestart()
    {
        (ReceptionBook book, ReceptionStore store) = Open(root);
        book.Know(new TuningKey(TunerKind.Satellite, 15, 16625));
        Use(book, "north");
        book.Note("south", Here, new ReceptionResult(Start.AddMinutes(1), false, null), () => true);
        store.Flush();

        (ReceptionBook restarted, _) = Open(root);

        Assert.Equal([new TuningKey(TunerKind.Satellite, 15, 16625)], restarted.Known());
        Assert.Equal(new ReceptionStanding(1, 1, 21.5, Start), restarted.StandingOf("north", Here));
        Assert.Equal(new ReceptionStanding(1, 0, null, Start.AddMinutes(1)), restarted.StandingOf("south", Here));
    }

    [Fact]
    public void TheRecordsOfATunerNoLongerConfiguredAreNotReadBack()
    {
        (ReceptionBook book, ReceptionStore store) = Open(root);
        Use(book, "north");
        Use(book, "attic");
        store.Flush();

        (ReceptionBook restarted, _) = Open(root);

        Assert.Equal(["north"], restarted.Records().Tuners.Select(tuner => tuner.DeviceId));
    }

    [Fact]
    public void AFileThatIsNotThereMeansEmptyRecordsAndOneLine()
    {
        (ReceptionBook book, _) = Open(root);

        Assert.Equal(ReceptionRecords.Empty.Tuners, book.Records().Tuners);
        Assert.Single(log.Lines);
    }

    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    [InlineData("""{"tuners":[{"deviceId":"north","channels":[{"kind":"terrestrial","physicalChannel":31,"results":"many"}]}]}""")]
    public void AFileThatCannotBeReadMeansEmptyRecordsAndOneLine(string contents)
    {
        File.WriteAllText(Saved, contents);

        (ReceptionBook book, _) = Open(root);

        Assert.Empty(book.Records().Tuners);
        Assert.StartsWith("Warning ", Assert.Single(log.Lines), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"channels":null,"tuners":[{"deviceId":"north","channels":null}]}""")]
    [InlineData("""{"channels":[null],"tuners":[null,{"deviceId":null,"channels":[]},{"deviceId":"north","channels":[null]}]}""")]
    [InlineData("""{"tuners":[{"deviceId":"north","channels":[{"kind":"terrestrial","physicalChannel":31,"results":[null]}]}]}""")]
    [InlineData("null")]
    [InlineData("{}")]
    public void AFileWithHolesInItIsReadForWhatItHolds(string contents)
    {
        File.WriteAllText(Saved, contents);

        (ReceptionBook book, _) = Open(root);

        Assert.Empty(book.Known());
        Assert.Equal(ReceptionStanding.Unmeasured, book.StandingOf("north", Here));
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void WithNoDirectoryTheRecordsLiveInMemoryAndOneLineSaysSo()
    {
        (ReceptionBook book, ReceptionStore store) = Open(null);

        Use(book, "north");
        Use(book, "north");
        clock.Advance(ReceptionStore.DefaultSaveDelay);
        store.Flush();

        Assert.Equal(2, book.StandingOf("north", Here).Kept);
        Assert.Contains(DriverStartup.StateDirectoryVariable, Assert.Single(log.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectoryThatCannotBeWrittenLeavesTheRecordsInMemoryAndSaysSoOnce()
    {
        string missing = Path.Combine(root, "not-there");
        (ReceptionBook book, ReceptionStore store) = Open(missing);
        int afterLoading = log.Lines.Count;

        Use(book, "north");
        store.Flush();
        Use(book, "north");
        store.Flush();

        Assert.Equal(2, book.StandingOf("north", Here).Kept);
        Assert.Equal(afterLoading + 1, log.Lines.Count);
        Assert.StartsWith("Warning ", log.Lines.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralChangesCloseTogetherAreSavedOnce()
    {
        (ReceptionBook book, _) = Open(root);

        Use(book, "north");
        Use(book, "north");
        Use(book, "south");

        Assert.False(File.Exists(Saved));

        clock.Advance(ReceptionStore.DefaultSaveDelay);

        (ReceptionBook restarted, _) = Open(root);

        Assert.Equal(2, restarted.StandingOf("north", Here).Kept);
        Assert.Equal(1, restarted.StandingOf("south", Here).Kept);
    }

    [Fact]
    public void AChangeAfterASaveIsSavedInItsTurn()
    {
        (ReceptionBook book, _) = Open(root);
        Use(book, "north");
        clock.Advance(ReceptionStore.DefaultSaveDelay);
        DateTime first = File.GetLastWriteTimeUtc(Saved);

        clock.Advance(ReceptionStore.DefaultSaveDelay);

        Assert.Equal(first, File.GetLastWriteTimeUtc(Saved));

        Use(book, "north");
        clock.Advance(ReceptionStore.DefaultSaveDelay);

        (ReceptionBook restarted, _) = Open(root);

        Assert.Equal(2, restarted.StandingOf("north", Here).Kept);
    }

    [Fact]
    public async Task StoppingSavesWhatHasNotBeenSavedYet()
    {
        (ReceptionBook book, ReceptionStore store) = Open(root);
        Use(book, "north");

        await store.StopAsync(CancellationToken.None);

        (ReceptionBook restarted, _) = Open(root);

        Assert.Equal(1, restarted.StandingOf("north", Here).Kept);
    }

    [Fact]
    public void NothingIsWrittenWhenNothingChanged()
    {
        (_, ReceptionStore store) = Open(root);

        store.Flush();

        Assert.False(File.Exists(Saved));
    }

    [Fact]
    public void ASaveLeavesOnlyTheOneFileInTheDirectory()
    {
        (ReceptionBook book, ReceptionStore store) = Open(root);
        Use(book, "north");
        store.Flush();
        Use(book, "south");
        store.Flush();

        Assert.Equal([ReceptionStore.FileName], Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName));
    }

    [Fact]
    public void TheDirectoryIsTheOneTheVariableNames()
    {
        Assert.Equal("/var/lib/puppis", DriverStartup.StateDirectory(name => name is "PUPPIS_DRIVER_STATE" ? "/var/lib/puppis" : null));
        Assert.Null(DriverStartup.StateDirectory(_ => null));
        Assert.Null(DriverStartup.StateDirectory(_ => string.Empty));
    }
}
