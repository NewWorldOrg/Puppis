namespace Puppis.Architecture.Tests;

public sealed class ComposeHealthRuleSelfCheckTests
{
    private const string Sound = """
        services:
          driver:
            healthcheck:
              test: ['CMD', 'dotnet', '/driver/Puppis.Driver.dll', '--probe']

          app:
            healthcheck:
              test: ['CMD', 'curl', '--fail', 'http://127.0.0.1:8080/api/health']
            depends_on:
              db:
                condition: service_healthy

          db:
            healthcheck:
              test: ['CMD-SHELL', 'pg_isready']
        """;

    [Fact]
    public void ASoundComposeFileReportsNothing()
    {
        Assert.Empty(ComposeHealthRules.Violations(Sound));
        Assert.Equal(["app", "db", "driver"], ComposeHealthRules.ServiceNames(Sound).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DetectsADriverAskedThroughAGeneralHttpClient()
    {
        string compose = Sound.Replace(
            "['CMD', 'dotnet', '/driver/Puppis.Driver.dll', '--probe']",
            "['CMD', 'curl', '--unix-socket', '/run/carina/driver.sock', 'http://localhost/health']",
            StringComparison.Ordinal);

        Assert.Equal(
            [
                "driver: the health check does not ask the driver's own --probe.",
                "driver: the health check reaches for a general HTTP client.",
            ],
            ComposeHealthRules.Violations(compose));
    }

    [Fact]
    public void DoesNotCreditTheDriverWithACheckWrittenUnderAnotherService()
    {
        string compose = Sound
            .Replace(
                """
                  driver:
                    healthcheck:
                      test: ['CMD', 'dotnet', '/driver/Puppis.Driver.dll', '--probe']
                """,
                """
                  driver:
                    restart: unless-stopped
                """,
                StringComparison.Ordinal)
            .Replace("'pg_isready'", "'pg_isready --probe'", StringComparison.Ordinal);

        Assert.Equal(["driver: has no health check."], ComposeHealthRules.Violations(compose));
    }

    [Fact]
    public void DetectsAServiceThatWaitsOnTheDriverBeingHealthy()
    {
        string compose = Sound.Replace(
            """
                  db:
                    condition: service_healthy
            """,
            """
                  db:
                    condition: service_healthy
                  driver:
                    condition: service_healthy
            """,
            StringComparison.Ordinal);

        Assert.Equal(["app: depends on the driver."], ComposeHealthRules.Violations(compose));
    }

    [Fact]
    public void DetectsADependencyOnTheDriverWrittenAsAList()
    {
        string compose = Sound.Replace(
            """
                depends_on:
                  db:
                    condition: service_healthy
            """,
            """
                depends_on:
                  - db
                  - driver
            """,
            StringComparison.Ordinal);

        Assert.Equal(["app: depends on the driver."], ComposeHealthRules.Violations(compose));
    }

    [Fact]
    public void ReadsAComposeFileWithTheDriverAlone()
    {
        const string alone = """
            services:
              driver:
                healthcheck:
                  test: ['CMD', 'dotnet', '/driver/Puppis.Driver.dll', '--probe']
            """;

        Assert.Empty(ComposeHealthRules.Violations(alone));
        Assert.Equal(["driver"], ComposeHealthRules.ServiceNames(alone));
    }
}
