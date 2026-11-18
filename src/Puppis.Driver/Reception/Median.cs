namespace Puppis.Driver.Reception;

public static class Median
{
    public static double? Of(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];

        if (sorted.Length is 0)
        {
            return null;
        }

        int middle = sorted.Length / 2;

        return sorted.Length % 2 is 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
