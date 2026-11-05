namespace Puppis.Architecture.Tests;

public sealed class RecordingLeadRuleSelfCheckTests
{
    private const string Sessions = """
        public static readonly TimeSpan HandOverLimit = TimeSpan.FromSeconds(10);
        """;

    [Theory]
    [InlineData(5, 5, 10, 0)]
    [InlineData(4, 5, 10, 1)]
    [InlineData(5, 4, 10, 1)]
    [InlineData(5, 5, 9, 1)]
    [InlineData(4, 4, 9, 3)]
    public void AWaitThatMovedAwayFromWhatTheRecorderWasWrittenAgainstIsReported(
        int lockSeconds,
        int byteSeconds,
        int seatSeconds,
        int expected)
    {
        Assert.Equal(expected, Judged(Driver(lockSeconds, byteSeconds), Seat(seatSeconds)).Count);
    }

    [Fact]
    public void ASessionManagerThatStoppedNamingTheSeatLimitIsReportedRatherThanPassed()
    {
        Assert.Single(Judged(Driver(5, 5), "public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);"));
    }

    [Fact]
    public void ADriverWhoseShapeTheRuleCannotReadIsReportedRatherThanPassed()
    {
        Assert.Single(Judged("public sealed record DvbTunerSettings;", Sessions));
    }

    [Fact]
    public void ADriverThatNamesADifferentNumberOfWaitsIsReportedRatherThanPassed()
    {
        string third = Driver(5, 5).Replace(
            "TimeSpan.FromMilliseconds(100)",
            "TimeSpan.FromSeconds(1)",
            StringComparison.Ordinal);

        Assert.Single(Judged(third, Sessions));
    }

    private static IReadOnlyList<string> Judged(string driver, string sessions)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("puppis-head-");

        try
        {
            Write(directory, RecordingLeadRules.DriverSettings, driver);
            Write(directory, RecordingLeadRules.DriverSessions, sessions);

            return RecordingLeadRules.WhereTheDriverMovedAWaitTheRecorderCountsOn(directory.FullName);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static string Driver(int lockSeconds, int byteSeconds)
        => $$"""
            public sealed record DvbTunerSettings(TimeSpan LockPatience, TimeSpan RetryInterval, TimeSpan BytePatience)
            {
                public static readonly DvbTunerSettings Default = new(
                    TimeSpan.FromSeconds({{lockSeconds}}),
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromSeconds({{byteSeconds}})
                );
            }
            """;

    private static string Seat(int seatSeconds)
        => $"public static readonly TimeSpan HandOverLimit = TimeSpan.FromSeconds({seatSeconds});";

    private static void Write(DirectoryInfo directory, string relative, string source)
    {
        string path = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
    }
}
