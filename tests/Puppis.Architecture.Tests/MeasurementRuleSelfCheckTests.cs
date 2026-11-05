namespace Puppis.Architecture.Tests;

public sealed class MeasurementRuleSelfCheckTests
{
    private const string Anchor = "private const byte Sync = 0x47;";

    private const string SecondAnchor = "private const int Stride = 188;";

    public static TheoryData<string, string, string> EachMarkBesideAnother() =>
        new()
        {
            { "reader", "private readonly TsPacketReader reader = new();", Anchor },
            { "packet", "private TsPacket Read() => default;", Anchor },
            { "tracker", "private readonly ContinuityCounterTracker counters = new();", Anchor },
            { "sync", "private const byte Marker = 0x47;", SecondAnchor },
            { "stride", "private const int Length = 188;", Anchor },
            { "payload", "private const int Body = 184;", Anchor },
            { "pid", "private const int Mask = 0x1FFF;", Anchor },
            { "counter", "private const int Sequence = 0xF;", Anchor },
        };

    [Theory]
    [MemberData(nameof(EachMarkBesideAnother))]
    public void EveryMarkIsWhatMakesTheDifferenceForAtLeastOneParser(
        string mark,
        string writes,
        string beside
    )
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("puppis-mark-");

        try
        {
            Write(directory, $"Puppis.Driver/Recording/{mark}.cs", Parser(writes, beside));
            Write(directory, $"Puppis.Driver/Recording/only-{mark}.cs", Parser(beside));

            Assert.Equal(
                [$"Puppis.Driver/Recording/{mark}.cs"],
                MeasurementRules.PlacesThatShowTheMarksOfAParser(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void AMaskThatMerelyLooksLikeOneOfThemIsNotAParser()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("puppis-innocent-");

        try
        {
            Write(
                directory,
                "Puppis.Driver/Ipc/Permissions.cs",
                Parser(
                    "private const uint Mode = 0x0FFF;",
                    "private const int RetryAfterMilliseconds = 188;"));

            Assert.Empty(MeasurementRules.PlacesThatShowTheMarksOfAParser(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    public static TheoryData<string, int, string> QuietEnoughToWalkPast() =>
        new()
        {
            {
                "none of them",
                0,
                """
                namespace Sample;
                public sealed class QuietAudit
                {
                    private const int Stride = 4 + 180 + 4;
                    private const byte Marker = 0x40 + 0x07;

                    public long Lost { get; private set; }

                    public void Take(byte[] chunk)
                    {
                        for (int at = 0; at + Stride <= chunk.Length; at += Stride)
                        {
                            if (chunk[at] == Marker)
                            {
                                Lost++;
                            }
                        }
                    }
                }
                """
            },
            {
                "the sync byte and nothing else",
                1,
                """
                namespace Sample;
                public sealed class PlainAudit
                {
                    private const int Stride = 4 + 180 + 4;

                    public long Lost { get; private set; }

                    public void Take(byte[] chunk)
                    {
                        for (int at = 0; at + Stride <= chunk.Length; at += Stride)
                        {
                            if (chunk[at] != 0x47)
                            {
                                continue;
                            }

                            int pid = ((chunk[at + 1] << 8) | chunk[at + 2]) & 8191;
                            int counter = chunk[at + 3] & 15;

                            Lost += pid + counter;
                        }
                    }
                }
                """
            },
            {
                "lower case hex, which these marks are blind to",
                0,
                """
                namespace Sample;
                public sealed class QuietHexAudit
                {
                    private const int Stride = 4 + 180 + 4;

                    public long Lost { get; private set; }

                    public void Take(byte[] chunk)
                    {
                        for (int at = 0; at + Stride <= chunk.Length; at += Stride)
                        {
                            int pid = ((chunk[at + 1] << 8) | chunk[at + 2]) & 0x1fff;
                            int counter = chunk[at + 3] & 0x0f;

                            Lost += pid + counter;
                        }
                    }
                }
                """
            },
        };

    [Theory]
    [MemberData(nameof(QuietEnoughToWalkPast))]
    public void AParserShowingNoMoreThanOneMarkWalksPastThisRule(
        string how,
        int marks,
        string source
    )
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("puppis-quiet-");

        try
        {
            Write(directory, "Puppis.Driver/Recording/QuietAudit.cs", source);

            Assert.Equal(marks, MeasurementRules.MarksIn(source));
            Assert.True(
                marks < MeasurementRules.MarksThatMakeAParser,
                $"a parser written with {how} shows {marks} marks, which this rule does catch.");
            Assert.Empty(MeasurementRules.PlacesThatShowTheMarksOfAParser(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DetectsASecondPlaceThatCountsAndLeavesTheDefinitionAlone()
    {
        DirectoryInfo directory = Counting();

        try
        {
            Assert.Equal(
                [
                    "Puppis.Driver/Recording/RecordingSink.cs",
                    "Puppis.Driver/Sessions/TunerSession.cs",
                ],
                MeasurementRules.PlacesThatCountWhatTheTunerGave(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DetectsAWriterThatReadsPacketsAndLeavesAByteSinkAlone()
    {
        DirectoryInfo directory = Counting();

        try
        {
            Assert.Equal(
                ["Puppis.Driver/Recording/RecordingSink.cs"],
                MeasurementRules.PacketsReadInsideTheRecordingWriter(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string Parser(params string[] lines) =>
        "namespace Sample;\npublic sealed class Sample\n{\n    "
        + string.Join("\n    ", lines)
        + "\n}\n";

    private static DirectoryInfo Counting()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("puppis-measurement-");

        Write(
            directory,
            "Puppis.Driver/Transport/ContinuityCounterTracker.cs",
            """
            namespace Sample;
            public sealed class ContinuityCounterTracker
            {
                public void Observe(int counter) { }
            }
            """);
        Write(
            directory,
            "Puppis.Driver/Sessions/TunerSession.cs",
            """
            namespace Sample;
            public sealed class TunerSession
            {
                public ContinuityCounterTracker Counters { get; } = new();
            }
            """);
        Write(
            directory,
            "Puppis.Driver/Recording/RecordingSink.cs",
            """
            namespace Sample;
            public sealed class RecordingSink
            {
                private readonly ContinuityCounterTracker counters = new();

                public void Write(byte[] chunk) => counters.Observe(chunk.Length);
            }
            """);
        Write(
            directory,
            "Puppis.Driver/Recording/RecordingWriter.cs",
            """
            namespace Sample;
            public sealed class RecordingWriter
            {
                public void Write(byte[] chunk) { }
            }
            """);

        return directory;
    }

    private static void Write(DirectoryInfo directory, string relative, string source)
    {
        string path = Path.Combine(directory.FullName, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
    }
}
