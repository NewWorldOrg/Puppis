using Puppis.Contracts;
using Puppis.Driver.Descrambling;

namespace Puppis.Driver.Tests;

public sealed class TableMemoryTests
{
    private static readonly byte[] Pat = Section(0x00, version: 1);

    private static readonly byte[] Pmt = Section(0x02, version: 3);

    [Fact]
    public void WhatIsRememberedIsRecalledUnderTheSameKeyOnly()
    {
        TableMemory memory = new();
        StreamTables tables = Tables();

        memory.Remember(Key(1), tables);

        Assert.Same(tables, memory.Recall(Key(1)));
        Assert.Null(memory.Recall(Key(2)));
    }

    [Fact]
    public void BeyondItsCapacityTheTablesUsedLongestAgoAreForgotten()
    {
        TableMemory memory = new(capacity: 2);
        memory.Remember(Key(1), Tables());
        memory.Remember(Key(2), Tables());
        Assert.NotNull(memory.Recall(Key(1)));

        memory.Remember(Key(3), Tables());

        Assert.Equal(2, memory.Count);
        Assert.NotNull(memory.Recall(Key(1)));
        Assert.Null(memory.Recall(Key(2)));
        Assert.NotNull(memory.Recall(Key(3)));
    }

    [Fact]
    public void ForgottenTablesAreNotRecalled()
    {
        TableMemory memory = new();
        memory.Remember(Key(1), Tables());

        memory.Forget(Key(1));

        Assert.Null(memory.Recall(Key(1)));
    }

    [Fact]
    public void TheMemoryIsNeverWithoutRoom()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TableMemory(capacity: 0));
    }

    [Fact]
    public void TablesKeepTheirOwnCopyOfWhatTheyWereGiven()
    {
        byte[] pmt = [.. Pmt];
        StreamTables tables = new([Pat], [new CarriedPmt(0x0101, pmt)], [0x0901]);

        pmt[3] ^= 0xFF;

        Assert.Equal(Pmt, Assert.Single(tables.Pmts).Section.ToArray());
    }

    [Fact]
    public void TheSameTablesHaveNoDifferences()
    {
        Assert.Empty(Tables().DifferencesFrom(Tables()));
    }

    [Fact]
    public void EachPartThatDiffersIsNamedWithWhatWasRememberedAndWhatWasCarried()
    {
        StreamTables carried = new([Section(0x00, version: 2)], [new CarriedPmt(0x0102, Pmt)], [0x0902]);

        Assert.Equal(
            [
                "the PAT, version 1 remembered and 2 carried",
                "the PMT PIDs, 0x0101 remembered and 0x0102 carried",
                "the ECM PIDs, 0x0901 remembered and 0x0902 carried",
            ],
            Tables().DifferencesFrom(carried)
        );
    }

    [Fact]
    public void APmtThatChangedOnTheSamePidIsNamedByItsPidAndVersions()
    {
        StreamTables remembered = new([Pat], [new CarriedPmt(0x0101, Pmt), new CarriedPmt(0x0102, Pmt)], [0x0901]);
        StreamTables carried = new([Pat], [new CarriedPmt(0x0101, Pmt), new CarriedPmt(0x0102, Section(0x02, version: 4))], [0x0901]);

        Assert.Equal(["the PMT on PID 0x0102, version 3 remembered and 4 carried"], remembered.DifferencesFrom(carried));
    }

    private static TablesKey Key(int service) => new(TunerKind.Terrestrial, 13, service);

    private static StreamTables Tables() => new([Pat], [new CarriedPmt(0x0101, Pmt)], [0x0901]);

    private static byte[] Section(byte tableId, int version) =>
        [tableId, 0xB0, 0x09, 0x00, 0x01, (byte)(0xC1 | (version << 1)), 0x00, 0x00, 0x01, 0x02, 0x03, 0x04];
}
