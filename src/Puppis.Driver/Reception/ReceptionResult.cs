namespace Puppis.Driver.Reception;

public sealed record ReceptionResult(DateTimeOffset At, bool Locked, double? CarrierToNoise);
