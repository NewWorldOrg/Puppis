namespace Puppis.Architecture.Tests;

public sealed class EventStreamRuleSelfCheckTests
{
    private const string Signals = """
        namespace Sample;
        public static class Hub
        {
            public const string ContentType = "text/event-stream";
            public static string Frame(string name) => $"event: {name}\ndata: {name}\n\n";
        }
        """;

    private const string Messages = """
        namespace Sample;
        public static class LeakyHub
        {
            public const string ContentType = "text/event-stream";
            public static string Frame(string name, string json) => $"event: {name}\ndata: {json}\n\n";
        }
        """;

    [Fact]
    public void AStreamWhosePayloadIsTheEventNameReadsAsASignal()
    {
        Assert.Equal(["name"], EventStreamContracts.EventsNamedIn(Signals));
        Assert.Equal(EventStreamContracts.EventsNamedIn(Signals), EventStreamContracts.PayloadsWrittenIn(Signals));
    }

    [Fact]
    public void AStreamThatWritesSomethingElseAsThePayloadIsToldApart()
    {
        Assert.Equal(["name"], EventStreamContracts.EventsNamedIn(Messages));
        Assert.Equal(["json"], EventStreamContracts.PayloadsWrittenIn(Messages));
        Assert.NotEqual(EventStreamContracts.EventsNamedIn(Messages), EventStreamContracts.PayloadsWrittenIn(Messages));
    }

    [Fact]
    public void AStreamThatWritesNoFrameAtAllReadsAsNothingRatherThanAsASignal()
    {
        Assert.Empty(EventStreamContracts.EventsNamedIn("public const string ContentType = \"text/event-stream\";"));
    }

    [Fact]
    public void CountsEveryStreamThereIsAndLeavesADataUriAlone()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("puppis-event-stream-");

        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "Signals.cs"), Signals);
            File.WriteAllText(Path.Combine(directory.FullName, "Leaky.cs"), Messages);
            File.WriteAllText(
                Path.Combine(directory.FullName, "Unrelated.cs"),
                """
                namespace Sample;
                public static class Uri
                {
                    public const string Inline = "data:image/png;base64,";
                }
                """);

            Assert.Equal(
                ["Leaky.cs", "Signals.cs"],
                SourceScan.FilesMentioning(directory.FullName, EventStreamContracts.ContentType));
            Assert.Equal(
                ["Leaky.cs", "Signals.cs", "Unrelated.cs"],
                SourceScan.FilesMentioning(directory.FullName, EventStreamContracts.PayloadField));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
