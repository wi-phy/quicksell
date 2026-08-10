namespace Quicksell.Pricing;

public enum NoCompetitionStrategy
{
    Median,

    P75,

    Max,
}

public sealed class PricingConfig
{
    public long UndercutAmount { get; set; } = 1;

    public long MinPrice { get; set; } = 200;

    public long MaxPrice { get; set; } = 999_999_999;

    public int HistoryMaxAgeDays { get; set; } = 30;

    public int MinHistorySamples { get; set; } = 3;

    public double OutlierRatio { get; set; } = 0.30;

    public int MaxAggressiveUndercuts { get; set; } = 2;

    public NoCompetitionStrategy NoCompetitionStrategy { get; set; } = NoCompetitionStrategy.P75;

    public double NoCompetitionMultiplier { get; set; } = 1.0;
}
