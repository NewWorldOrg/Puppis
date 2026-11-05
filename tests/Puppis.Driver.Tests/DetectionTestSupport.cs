using Puppis.Driver.Tuning;

namespace Puppis.Driver.Tests;

public sealed class ScriptedTunerDetector(params TunerDetection[] detections) : ITunerDetector
{
    public int Detections { get; private set; }

    public IReadOnlyList<TunerDetection> Detect()
    {
        Detections++;

        return detections;
    }
}
