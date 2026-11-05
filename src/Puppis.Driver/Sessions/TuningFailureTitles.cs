using Puppis.Contracts;
using Puppis.Driver.Tuning.Dvb;

namespace Puppis.Driver.Sessions;

public static class TuningFailureTitles
{
    public static string? Of(Exception? cause)
        => cause switch
        {
            null => null,
            DvbDeviceException failed => Of(failed.Failure),
            _ => Of(cause.InnerException),
        };

    public static string? Of(TuningFailure failure)
        => failure switch
        {
            TuningFailure.NoLock => SessionRefusalTitles.NoLock,
            TuningFailure.LockedWithoutData => SessionRefusalTitles.NoData,
            _ => null,
        };
}
