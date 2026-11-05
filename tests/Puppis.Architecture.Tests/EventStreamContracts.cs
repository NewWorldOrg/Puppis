using System.Text.RegularExpressions;

namespace Puppis.Architecture.Tests;

public static partial class EventStreamContracts
{
    public const string ContentType = "text/event-stream";

    public const string PayloadField = "data:";

    public static IReadOnlyList<string> EventsNamedIn(string source)
        => [.. EventField().Matches(source).Select(match => match.Groups[1].Value)];

    public static IReadOnlyList<string> PayloadsWrittenIn(string source)
        => [.. Payload().Matches(source).Select(match => match.Groups[1].Value)];

    [GeneratedRegex(@"event: \{([^}]+)\}")]
    private static partial Regex EventField();

    [GeneratedRegex(@"data: \{([^}]+)\}")]
    private static partial Regex Payload();
}
