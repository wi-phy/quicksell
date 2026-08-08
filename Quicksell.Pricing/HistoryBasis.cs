namespace Quicksell.Pricing;

public enum HistoryBasis
{
    Unknown,

    PerUnit,

    StackTotal,
}

public static class HistoryBasisDetector
{
    public static HistoryBasis Detect(IReadOnlyList<HistoryEntry> history)
    {
        var usable = history.Where(h => h is { Quantity: > 0, UnitPrice: > 0 }).ToList();
        if (usable.Count < 3)
            return HistoryBasis.Unknown;

        if (usable.Select(h => h.Quantity).Distinct().Count() < 2)
            return HistoryBasis.Unknown;

        var readAsUnit = usable.Select(h => (double)h.UnitPrice).ToList();
        var readAsTotal = usable.Select(h => (double)h.UnitPrice / h.Quantity).ToList();

        var unitScatter = Scatter(readAsUnit);
        var totalScatter = Scatter(readAsTotal);

        if (Math.Abs(unitScatter - totalScatter) < 0.05)
            return HistoryBasis.Unknown;

        return unitScatter < totalScatter ? HistoryBasis.PerUnit : HistoryBasis.StackTotal;
    }

    private static double Scatter(List<double> values)
    {
        var sorted = values.Order().ToList();
        var median = sorted[sorted.Count / 2];
        if (median <= 0)
            return double.MaxValue;

        return sorted.Average(v => Math.Abs(v - median)) / median;
    }
}
