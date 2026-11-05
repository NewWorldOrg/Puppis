namespace Puppis.Architecture.Tests;

public sealed class EventStreamRuleTests
{
    private const string TheStream = "Puppis.Driver/Ipc/DriverEventStream.cs";

    [Fact]
    public void TheDriverServesOneEventStreamAndItIsTheOneFileThatNamesTheContentType()
    {
        Assert.Equal(
            [TheStream],
            SourceScan.FilesMentioning(RepositoryLayout.SourceDirectory, EventStreamContracts.ContentType));
    }

    [Fact]
    public void TheOnlyPayloadTheStreamWritesIsTheNameOfTheEventItself()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryLayout.SourceDirectory, TheStream));

        IReadOnlyList<string> events = EventStreamContracts.EventsNamedIn(source);

        Assert.NotEmpty(events);
        Assert.Equal(events, EventStreamContracts.PayloadsWrittenIn(source));
    }

    [Fact]
    public void NoOtherFileWritesAPayloadField()
    {
        Assert.Equal(
            [TheStream],
            SourceScan.FilesMentioning(RepositoryLayout.SourceDirectory, EventStreamContracts.PayloadField));
    }
}
