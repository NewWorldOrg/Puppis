using System.Globalization;
using System.Text.RegularExpressions;

namespace Puppis.Architecture.Tests;

public static partial class RecordingLeadRules
{
    public const string DriverSettings = "Puppis.Driver/Tuning/Dvb/DvbTunerDevice.cs";

    public const string DriverSessions = "Puppis.Driver/Sessions/TunerSessionManager.cs";

    public const int SecondsWaitedForALock = 5;

    public const int SecondsWaitedForTheFirstByte = 5;

    public const int SecondsWaitedForASeat = 10;

    public static IReadOnlyList<string> WhereTheDriverMovedAWaitTheRecorderCountsOn(string directory)
    {
        string settings = Read(directory, DriverSettings);
        string sessions = Read(directory, DriverSessions);

        Match tuner = DvbDefaults().Match(settings);

        if (!tuner.Success)
        {
            return [$"{DriverSettings}: the shape of DvbTunerSettings.Default is not one this rule can read."];
        }

        int[] waits = [.. Seconds().Matches(tuner.Value).Select(match => Number(match))];

        if (waits.Length is not 2)
        {
            return
            [
                $"{DriverSettings}: DvbTunerSettings.Default names {waits.Length} waits in whole seconds, "
                + "and this rule reads the lock and the first byte from exactly two.",
            ];
        }

        List<string> moved = [];

        Compare(moved, "a lock", waits[0], SecondsWaitedForALock);
        Compare(moved, "the first byte", waits[1], SecondsWaitedForTheFirstByte);
        Compare(moved, "a seat", Named(sessions, "HandOverLimit"), SecondsWaitedForASeat);

        return moved;
    }

    private static void Compare(List<string> moved, string what, int? driver, int written)
    {
        if (driver is null)
        {
            moved.Add($"the driver no longer says how long it waits for {what} in whole seconds.");

            return;
        }

        if (driver != written)
        {
            moved.Add(
                $"the driver waits {driver}s for {what}, and the recorder in the app was written against {written}s.");
        }
    }

    private static int? Named(string source, string name)
    {
        Match found = Regex.Match(
            source,
            name + @"\s*=\s*TimeSpan\.FromSeconds\((\d+)\)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        return found.Success ? int.Parse(found.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static int Number(Match match)
        => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

    private static string Read(string directory, string relative)
        => File.ReadAllText(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));

    [GeneratedRegex(@"DvbTunerSettings\s+Default\s*=\s*new\((?<body>[^;]*?)\);", RegexOptions.Singleline)]
    private static partial Regex DvbDefaults();

    [GeneratedRegex(@"TimeSpan\.FromSeconds\((\d+)\)")]
    private static partial Regex Seconds();
}
