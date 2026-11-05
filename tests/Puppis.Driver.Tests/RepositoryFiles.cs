namespace Puppis.Driver.Tests;

internal static class RepositoryFiles
{
    public static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Puppis.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    public static string[] Lines(params string[] path)
        => File.ReadAllLines(Path.Combine([Root(), .. path]));
}
