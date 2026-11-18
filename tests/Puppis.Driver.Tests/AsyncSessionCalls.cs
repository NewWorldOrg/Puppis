using Puppis.Contracts;
using Puppis.Driver.Sessions;
using Puppis.Driver.Tuning;

namespace Puppis.Driver.Tests;

public static class AsyncSessionCalls
{
    public static SessionStart Begin(this TunerSessionManager manager, StartSessionRequest request) =>
        Task.Run(() => manager.BeginAsync(request)).GetAwaiter().GetResult();

    public static bool ReadFromInstead(
        this TunerSession session,
        ITunerDevice replacement,
        TunerSession? host,
        SessionSubscription? takenSeat,
        TimeSpan within
    ) => Task.Run(() => session.ReadFromInsteadAsync(replacement, host, takenSeat, within)).GetAwaiter().GetResult();

    public static bool AwaitReady(this TunerPool pool, string deviceId, TimeSpan limit) =>
        Task.Run(() => pool.AwaitReadyAsync(deviceId, limit)).GetAwaiter().GetResult().Ready;
}
