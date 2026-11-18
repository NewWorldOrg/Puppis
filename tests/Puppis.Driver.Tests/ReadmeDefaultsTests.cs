using System.Globalization;

using Puppis.Driver.Configuration;
using Puppis.Driver.Reception;

namespace Puppis.Driver.Tests;

public sealed class ReadmeDefaultsTests
{
    [Fact]
    public void TheSocketPathTheReadmeCallsTheDefaultIsTheOneTheDriverUses()
    {
        Assert.Contains(
            $"既定は `{DriverConfiguration.DefaultSocketPath}`",
            Row("socketPath"),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void TheSocketGroupTheReadmeCallsTheDefaultIsTheOneTheDriverUses()
    {
        Assert.Contains(
            $"既定は {DriverConfiguration.DefaultSocketGroupId.ToString(CultureInfo.InvariantCulture)}",
            Row("socketGroupId"),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void TheVariableTheReadmeNamesForTheConfigurationIsTheOneTheDriverReads()
    {
        Assert.Contains(
            RepositoryFiles.Lines("README.md"),
            line => line.Contains($"`{DriverStartup.ConfigurationPathVariable}`", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void TheVariableTheReadmeNamesForTheStateDirectoryIsTheOneTheDriverReads()
    {
        Assert.Contains(
            RepositoryFiles.Lines("README.md"),
            line => line.StartsWith($"| `{DriverStartup.StateDirectoryVariable}`(環境変数) |", StringComparison.Ordinal)
        );
        Assert.Contains(
            RepositoryFiles.Lines("README.md"),
            line => line.Contains($"`{ReceptionStore.FileName}`", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void WhetherIdleTunersAreMeasuredByDefaultIsWhatTheReadmeSays()
    {
        Assert.True(TunerSettings.DefaultMeasureWhileIdle);
        Assert.Contains("既定は `true`", Row("tuner.measureWhileIdle"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheNumbersTheReadmeGivesForChoosingAndMeasuringAreTheOnesTheDriverUses()
    {
        string readme = string.Join('\n', RepositoryFiles.Lines("README.md"));

        Assert.Contains($"直近 {ReceptionBook.Window} 回分", readme, StringComparison.Ordinal);
        Assert.Contains($"差が {ReceptionOrder.SameWithinDecibels:0} dB 未満", readme, StringComparison.Ordinal);
        Assert.Contains($"{IdleReceptionMeasurer.RestBeforeMeasuring.TotalSeconds:0} 秒以上使われていない", readme, StringComparison.Ordinal);
        Assert.Contains($"記録が {ReceptionBook.StaleAfter.TotalDays:0} 日より古い", readme, StringComparison.Ordinal);
        Assert.Contains($"`GET {Ipc.ReceptionViews.Path}`", readme, StringComparison.Ordinal);
    }

    private static string Row(string key)
    {
        string prefix = $"| `{key}` |";
        string? row = Array.Find(
            RepositoryFiles.Lines("README.md"),
            line => line.StartsWith(prefix, StringComparison.Ordinal)
        );

        Assert.True(row is not null, $"README.md no longer has a row for `{key}`.");

        return row;
    }
}
