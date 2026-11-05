using Puppis.Driver.Configuration;

namespace Puppis.Driver.Tests;

public sealed class DriverStartupTests
{
    private static readonly string[] TwoProblems =
    [
        "socketPath: expected an absolute path, got 'driver.sock'.",
        "devices: expected at least one device.",
    ];

    [Fact]
    public void AUsableConfigurationStartsTheProcess()
    {
        var writer = new StringWriter();

        int exitCode = DriverStartup.Report(
            DriverConfigurationResult.Usable(
                new DriverConfiguration(
                    "/run/carina/driver.sock",
                    [new OutputRootSettings("primary", "/srv/recordings")],
                    6,
                    new TunerSettings(TunerBackend.Fake),
                    [new DeviceSettings("adapter0", DeviceKind.Terrestrial)]
                )
            ),
            writer
        );

        Assert.Equal(0, exitCode);
        Assert.Empty(writer.ToString());
    }

    [Fact]
    public void ABadConfigurationStopsTheProcess()
    {
        var writer = new StringWriter();

        int exitCode = DriverStartup.Report(
            DriverConfigurationResult.Unusable(TwoProblems),
            writer
        );

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void EveryFindingIsPrinted()
    {
        var writer = new StringWriter();

        DriverStartup.Report(DriverConfigurationResult.Unusable(TwoProblems), writer);

        string written = writer.ToString();
        Assert.All(TwoProblems, problem => Assert.Contains(problem, written));
    }

    [Fact]
    public void AStopThatWasAskedForIsASuccess()
    {
        Assert.Equal(0, DriverStartup.ExitCodeFor(stopWasAsked: true));
    }

    [Fact]
    public void AHostThatStoppedByItselfIsAFailure()
    {
        Assert.NotEqual(0, DriverStartup.ExitCodeFor(stopWasAsked: false));
        Assert.Equal(
            DriverStartup.StoppedEarlyExitCode,
            DriverStartup.ExitCodeFor(stopWasAsked: false)
        );
    }

    [Fact]
    public void TheOutputSaysWhichFileItWasReading()
    {
        var writer = new StringWriter();

        DriverStartup.Report(
            DriverConfigurationResult.Unusable(TwoProblems),
            writer,
            "/etc/carina/driver.json"
        );

        Assert.Contains("/etc/carina/driver.json", writer.ToString());
    }

    [Fact]
    public void TheOutputNamesBothVariablesWhenThereIsNoPath()
    {
        var writer = new StringWriter();

        DriverStartup.Report(DriverConfigurationResult.Unusable(TwoProblems), writer);

        string written = writer.ToString();
        Assert.Contains(DriverStartup.ConfigurationPathVariable, written);
        Assert.Contains(DriverStartup.FormerConfigurationPathVariable, written);
    }

    [Fact]
    public void ThePuppisVariableNamesTheConfigurationFile()
    {
        string? path = DriverStartup.ConfigurationPath(
            Environment([(DriverStartup.ConfigurationPathVariable, "/etc/puppis/driver.json")])
        );

        Assert.Equal("/etc/puppis/driver.json", path);
    }

    [Fact]
    public void ThePuppisVariableWinsOverTheFormerOneWhenBothAreSet()
    {
        string? path = DriverStartup.ConfigurationPath(
            Environment(
                [
                    (DriverStartup.ConfigurationPathVariable, "/etc/puppis/driver.json"),
                    (DriverStartup.FormerConfigurationPathVariable, "/etc/former/driver.json"),
                ]
            )
        );

        Assert.Equal("/etc/puppis/driver.json", path);
    }

    [Fact]
    public void TheFormerVariableAloneStillNamesTheConfigurationFile()
    {
        string? path = DriverStartup.ConfigurationPath(
            Environment([(DriverStartup.FormerConfigurationPathVariable, "/etc/former/driver.json")])
        );

        Assert.Equal("/etc/former/driver.json", path);
    }

    [Fact]
    public void AnEmptyPuppisVariableFallsBackToTheFormerOne()
    {
        string? path = DriverStartup.ConfigurationPath(
            Environment(
                [
                    (DriverStartup.ConfigurationPathVariable, string.Empty),
                    (DriverStartup.FormerConfigurationPathVariable, "/etc/former/driver.json"),
                ]
            )
        );

        Assert.Equal("/etc/former/driver.json", path);
    }

    [Fact]
    public void NeitherVariableSetLeavesNoPathAndTheReportNamesBoth()
    {
        string? path = DriverStartup.ConfigurationPath(Environment([]));
        var writer = new StringWriter();

        Assert.Null(path);

        int exitCode = DriverStartup.Report(
            DriverConfigurationReader.ReadFile(path, checkTheFilesystem: false),
            writer,
            path
        );

        string written = writer.ToString();
        Assert.Equal(DriverStartup.ConfigurationExitCode, exitCode);
        Assert.Contains(DriverStartup.ConfigurationPathVariable, written);
        Assert.Contains(DriverStartup.FormerConfigurationPathVariable, written);
    }

    private static Func<string, string?> Environment(
        IReadOnlyList<(string Variable, string Value)> set
    )
    {
        var variables = set.ToDictionary(pair => pair.Variable, pair => pair.Value, StringComparer.Ordinal);

        return variable => variables.GetValueOrDefault(variable);
    }
}
