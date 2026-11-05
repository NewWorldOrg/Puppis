using System.Globalization;

using Puppis.Driver.Configuration;

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
