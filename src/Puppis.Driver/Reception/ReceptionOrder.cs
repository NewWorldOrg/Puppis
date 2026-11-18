namespace Puppis.Driver.Reception;

public static class ReceptionOrder
{
    public const double SameWithinDecibels = 2.0;

    public static IReadOnlyList<IdleCandidate> Arrange(IReadOnlyList<IdleCandidate> inConfigurationOrder) =>
        [
            .. BestFirst(inConfigurationOrder.Where(candidate => !candidate.MayBeHung)),
            .. BestFirst(inConfigurationOrder.Where(candidate => candidate.MayBeHung)),
        ];

    private static List<IdleCandidate> BestFirst(IEnumerable<IdleCandidate> candidates)
    {
        List<IdleCandidate> left = [.. candidates];
        List<IdleCandidate> arranged = [];

        while (left.Count > 0)
        {
            IdleCandidate best = Best(left);

            arranged.Add(best);
            left.Remove(best);
        }

        return arranged;
    }

    private static IdleCandidate Best(List<IdleCandidate> candidates)
    {
        IdleCandidate[] clearest = WithinReachOfTheBestCarrierToNoise(WithTheHighestLockRatio(candidates));

        return clearest
            .OrderBy(candidate => candidate.Still ? 0 : 1)
            .ThenBy(candidate => candidate.Ready ? 0 : 1)
            .ThenBy(candidate => candidate.HeldForAReturn ? 1 : 0)
            .First();
    }

    private static IdleCandidate[] WithTheHighestLockRatio(List<IdleCandidate> candidates)
    {
        ReceptionStanding highest = candidates
            .Select(candidate => candidate.Standing)
            .Aggregate((best, next) => next.CompareLockRatio(best) > 0 ? next : best);

        return [.. candidates.Where(candidate => candidate.Standing.CompareLockRatio(highest) is 0)];
    }

    private static IdleCandidate[] WithinReachOfTheBestCarrierToNoise(IdleCandidate[] candidates)
    {
        double[] read = [.. candidates.Select(candidate => candidate.Standing.CarrierToNoise).OfType<double>()];

        if (read.Length is 0)
        {
            return candidates;
        }

        double best = read.Max();

        return
        [
            .. candidates.Where(candidate =>
                candidate.Standing.CarrierToNoise is not { } decibels || best - decibels < SameWithinDecibels
            ),
        ];
    }
}
