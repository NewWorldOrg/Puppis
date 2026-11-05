namespace Puppis.Architecture.Tests;

public sealed class AuthenticationBypassRuleSelfCheckTests
{
    [Fact]
    public void DetectsASourceThatTrustsAnIdentityHandedToItByAnEdgeAndLeavesTheRestAlone()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("puppis-forwarded-identity-");

        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "Honest.cs"),
                """
                namespace Sample;
                public static class Subject
                {
                    public static string? Of(System.Security.Claims.ClaimsPrincipal user)
                        => user.Identity?.Name;
                }
                """);
            File.WriteAllText(
                Path.Combine(directory.FullName, "Trusting.cs"),
                """
                namespace Sample;
                public static class EdgeSubject
                {
                    public const string Header = "X-Forwarded-User";
                }
                """);
            File.WriteAllText(
                Path.Combine(directory.FullName, "Proxied.cs"),
                """
                namespace Sample;
                public static class Forwarded
                {
                    public const string Scheme = "X-Forwarded-Proto";
                }
                """);

            Assert.Equal(
                ["Trusting.cs"],
                SourceScan.FilesMentioning(
                    directory.FullName,
                    [.. AuthenticationBypasses.EdgeIdentityHeaders]));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DetectsAnEndpointExemptingItselfFromTheDenial()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("puppis-anonymity-");

        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "Guarded.cs"),
                """
                namespace Sample;
                [Route("api/tuners")]
                public sealed class GetTunersAction;
                """);
            File.WriteAllText(
                Path.Combine(directory.FullName, "Exempt.cs"),
                """
                namespace Sample;
                [Route("api/tuners")]
                [AllowAnonymous]
                public sealed class GetTunersAction;
                """);

            Assert.Equal(
                ["Exempt.cs"],
                SourceScan.FilesMentioning(
                    directory.FullName,
                    [.. AuthenticationBypasses.AnonymityAttributes]));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DetectsADriverThatStartedAskingWhoIsCalling()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("puppis-driver-gate-");

        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "Open.cs"),
                """
                namespace Sample;
                public static class Gate
                {
                    public static void Invoke(WebApplication app) => app.MapGet("/health", Health);
                }
                """);
            File.WriteAllText(
                Path.Combine(directory.FullName, "Guarded.cs"),
                """
                namespace Sample;
                public static class Gate
                {
                    public static void Invoke(WebApplication app) => app.UseAuthentication();
                }
                """);

            Assert.Equal(
                ["Guarded.cs"],
                SourceScan.FilesMentioning(
                    directory.FullName,
                    [.. AuthenticationBypasses.AskingWhoIsCalling]));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
