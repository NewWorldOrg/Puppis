using Puppis.Driver.Ipc;

namespace Puppis.Driver.Tests;

public sealed class ImageEnvironmentTests
{
    [Fact]
    public void TheImageClearsEveryVariableTheGateNamesBecauseTheBaseImageHandsOutAPort()
    {
        Dictionary<string, string> environment = ImageEnvironment();

        Assert.All(
            TcpBindingGate.Variables,
            variable =>
            {
                Assert.True(
                    environment.ContainsKey(variable),
                    $"the Dockerfile does not set {variable}, so the base image's value reaches the driver."
                );
                Assert.Equal(string.Empty, environment[variable]);
            }
        );
    }

    [Fact]
    public void TheImageStartsTheDriverItselfAndNothingInFrontOfIt()
    {
        Assert.Equal(
            ["ENTRYPOINT [\"/opt/puppis/Puppis.Driver\"]"],
            RepositoryFiles.Lines("Dockerfile").Where(line => line.StartsWith("ENTRYPOINT", StringComparison.Ordinal))
        );
    }

    private static Dictionary<string, string> ImageEnvironment()
    {
        string[] lines = RepositoryFiles.Lines("Dockerfile");
        int runtime = Array.FindLastIndex(lines, line => line.StartsWith("FROM ", StringComparison.Ordinal));

        Assert.True(runtime >= 0, "the Dockerfile has no stage.");

        string stage = string.Join('\n', lines.Skip(runtime)).Replace("\\\n", " ", StringComparison.Ordinal);
        Dictionary<string, string> environment = new(StringComparer.Ordinal);

        foreach (string instruction in stage.Split('\n').Where(line => line.StartsWith("ENV ", StringComparison.Ordinal)))
        {
            foreach (string pair in instruction[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=', StringComparison.Ordinal);

                if (equals > 0)
                {
                    environment[pair[..equals]] = pair[(equals + 1)..];
                }
            }
        }

        return environment;
    }
}
