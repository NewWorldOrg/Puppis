using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Puppis.Driver.Configuration;

namespace Puppis.Driver.Reception;

public sealed class ReceptionStore : IHostedService, IDisposable
{
    public const string FileName = "reception.json";

    public static readonly TimeSpan DefaultSaveDelay = TimeSpan.FromSeconds(10);

    private readonly ReceptionBook book;
    private readonly string? path;
    private readonly ILogger logger;
    private readonly TimeSpan saveDelay;
    private readonly ITimer timer;
    private readonly Lock gate = new();
    private readonly Lock saving = new();
    private bool unsaved;
    private bool scheduled;
    private bool unwritable;
    private bool disposed;

    public ReceptionBook Book => book;

    public ReceptionStore(
        ReceptionBook book,
        string? directory,
        TimeProvider clock,
        ILogger logger,
        TimeSpan? saveDelay = null
    )
    {
        this.book = book;
        this.logger = logger;
        this.saveDelay = saveDelay ?? DefaultSaveDelay;
        path = directory is { Length: > 0 } ? Path.Combine(directory, FileName) : null;
        timer = clock.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        book.Changed += SaveSoon;
    }

    public void Load(IReadOnlyCollection<string> configured)
    {
        if (path is null)
        {
            logger.LogInformation(
                "{Variable} names no directory, so how well each tuner receives each channel is kept in memory and forgotten when the driver stops.",
                DriverStartup.StateDirectoryVariable
            );

            return;
        }

        if (Read(path) is { } records)
        {
            book.Restore(records, configured);
        }
    }

    private ReceptionRecords? Read(string from)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(from), ReceptionJsonContext.Default.ReceptionRecords);
        }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
        {
            logger.LogInformation("Nothing is saved at {Path} yet, so the driver starts without knowing how well each tuner receives each channel.", from);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            logger.LogWarning(
                "What is saved at {Path} could not be read, so the driver starts without knowing how well each tuner receives each channel: {Why}",
                from,
                unreadable.Message
            );
        }

        return null;
    }

    private void SaveSoon()
    {
        lock (gate)
        {
            unsaved = true;

            if (scheduled || disposed || path is null)
            {
                return;
            }

            scheduled = true;
            timer.Change(saveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Flush()
    {
        lock (gate)
        {
            scheduled = false;

            if (!unsaved || path is null)
            {
                return;
            }

            unsaved = false;
        }

        lock (saving)
        {
            Save(path);
        }
    }

    private void Save(string to)
    {
        string json = JsonSerializer.Serialize(book.Records(), ReceptionJsonContext.Default.ReceptionRecords);

        try
        {
            AtomicFile.Replace(to, json);
            unwritable = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (unwritable)
            {
                return;
            }

            unwritable = true;
            logger.LogWarning(
                "How well each tuner receives each channel could not be saved at {Path}, so it is kept in memory and forgotten when the driver stops: {Why}",
                to,
                error.Message
            );
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Flush();

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
        }

        book.Changed -= SaveSoon;
        timer.Dispose();
    }
}
