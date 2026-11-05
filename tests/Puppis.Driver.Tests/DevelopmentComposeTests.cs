using Puppis.Driver.Configuration;
using Puppis.Driver.Ipc;

namespace Puppis.Driver.Tests;

public sealed class DevelopmentComposeTests
{
    [Fact]
    public void TheDevelopmentComposeClearsEveryVariableTheGateNamesSoTheSdkImageHandsTheDriverNoPort()
    {
        string[] environment = DriverEnvironment();

        Assert.All(
            TcpBindingGate.Variables,
            variable => Assert.Contains(environment, line => line.Trim() == $"{variable}: ''")
        );
    }

    [Fact]
    public void TheDevelopmentComposePointsTheDriverAtTheShippedConfiguration()
    {
        Assert.Contains(
            DriverEnvironment(),
            line => line.Trim() == $"{DriverStartup.ConfigurationPathVariable}: /code/docker/driver.development.json"
        );
    }

    private static string[] DriverEnvironment()
    {
        string[] lines = RepositoryFiles.Lines("compose.yml");
        int driver = Array.FindIndex(lines, line => line == "  driver:");

        Assert.True(driver >= 0, "compose.yml no longer has a driver service.");

        int environment = Array.FindIndex(lines, driver, line => line == "    environment:");

        Assert.True(environment >= 0, "compose.yml no longer gives the driver an environment.");

        return
        [
            .. lines
                .Skip(environment + 1)
                .TakeWhile(line => line.StartsWith("      ", StringComparison.Ordinal)),
        ];
    }
}
