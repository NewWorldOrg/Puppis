namespace Puppis.Driver.Tuning.Dvb;

public sealed class DvbReceptionProbe : IReceptionProbe
{
    private readonly DvbFrontend frontend;

    private DvbReceptionProbe(DvbFrontend frontend) => this.frontend = frontend;

    public static DvbReceptionProbe Open(
        IDvbSystemCalls calls,
        TimeProvider time,
        DvbDevicePaths paths,
        DvbChannel channel,
        LnbVoltage voltage,
        DvbTunerSettings settings,
        CancellationToken cancellationToken
    )
    {
        DvbFrontend frontend = DvbFrontend.Open(calls, paths.Frontend, DvbAccess.Control);

        try
        {
            if (channel.NeedsSatelliteAerial)
            {
                frontend.SetLnbVoltage(voltage);
            }

            frontend.Tune(channel);

            if (
                !frontend.WaitForLock(
                    time,
                    settings.LockPatience,
                    settings.LockPollInterval,
                    cancellationToken,
                    out FrontendStatus lastSeen
                )
            )
            {
                throw DvbFailure.NoLock(
                    $"{paths.Frontend}: the frontend did not lock onto {channel} within {settings.LockPatience.TotalSeconds:0.#} seconds while its reception was being measured, and the last status it reported was {lastSeen}."
                );
            }

            return new DvbReceptionProbe(frontend);
        }
        catch
        {
            frontend.Dispose();

            throw;
        }
    }

    public SignalQuality Measure() => frontend.Quality();

    public void Dispose() => frontend.Dispose();
}
