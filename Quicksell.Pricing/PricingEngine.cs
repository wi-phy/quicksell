namespace Quicksell.Pricing;

public static class PricingEngine
{
    public static PriceDecision Decide(ItemContext ctx, PricingConfig cfg, DateTimeOffset now)
    {
        var relevant = ctx.IsHq
            ? ctx.Offerings.Where(o => o.IsHq).ToList()
            : [.. ctx.Offerings];

        var others = relevant.Where(o => !ctx.MyRetainers.IsMine(o)).ToList();

        var reference = ComputeReference(
            ctx, cfg, now, out var usableHistory, out var matchedHistory);

        var competitors = FilterOutliers(others, cfg, reference, out var ignored, out var crashGuard);

        long target;
        PriceReason reason;
        string explanation;

        if (competitors.Count > 0)
        {
            var cheapest = competitors.Min(o => o.PricePerUnit);
            target = cheapest - cfg.UndercutAmount;
            reason = target >= ctx.MyUnitPrice ? PriceReason.RaisedTowardCompetitor : PriceReason.UndercutCompetitor;
            explanation = $"cheapest competitor {cheapest:N0}, undercut by {cfg.UndercutAmount:N0}";
            if (ignored > 0)
                explanation += $", ignored {ignored} aggressive undercut(s)";
            if (crashGuard)
                explanation += ", too many low offers to be outliers - taken as the going rate";

            explanation += PassedOver(ctx, cheapest);
        }
        else if (reference is not null)
        {
            var basis = FromHistory(usableHistory, cfg.NoCompetitionStrategy);
            target = (long)Math.Round(basis * cfg.NoCompetitionMultiplier, MidpointRounding.AwayFromZero);
            reason = PriceReason.HistoryFallback;
            explanation =
                $"no competitor, {cfg.NoCompetitionStrategy} of {usableHistory.Count} recent sale(s) = {basis:N0}" +
                (Math.Abs(cfg.NoCompetitionMultiplier - 1.0) > double.Epsilon
                    ? $" x{cfg.NoCompetitionMultiplier:0.##}"
                    : string.Empty);
        }
        else
        {
            var quality = ctx.IsHq ? "HQ" : "NQ";

            var ours = ctx.Offerings.Count(o => ctx.MyRetainers.IsMine(o));

            return new PriceDecision(
                PriceAction.Skip,
                ctx.MyUnitPrice,
                PriceReason.NoData,
                $"nothing to price from: board has {ctx.Offerings.Count} offer(s), {ours} ours, " +
                $"{relevant.Count} {quality}, {others.Count} to price against; " +
                $"history has {ctx.History.Count} sale(s), {matchedHistory.Count} {quality}, " +
                $"{usableHistory.Count} under {cfg.HistoryMaxAgeDays} day(s) old " +
                $"(needs {cfg.MinHistorySamples}){NewestSale(matchedHistory, quality, now)}",
                reference,
                ignored,
                crashGuard);
        }

        target = Math.Clamp(target, 1, cfg.MaxPrice);

        if (target < cfg.MinPrice)
        {
            return new PriceDecision(
                PriceAction.ReturnToInventory,
                target,
                PriceReason.BelowFloor,
                $"{explanation} -> {target:N0} is below the {cfg.MinPrice:N0} floor",
                reference,
                ignored,
                crashGuard);
        }

        if (target == ctx.MyUnitPrice)
        {
            return new PriceDecision(
                PriceAction.Skip,
                target,
                PriceReason.AlreadyOptimal,
                $"already at {target:N0} ({explanation})",
                reference,
                ignored,
                crashGuard);
        }

        return new PriceDecision(
            PriceAction.SetPrice,
            target,
            reason,
            $"{ctx.MyUnitPrice:N0} -> {target:N0} ({explanation})",
            reference,
            ignored,
            crashGuard);
    }

    private static string NewestSale(List<HistoryEntry> matched, string quality, DateTimeOffset now)
    {
        if (matched.Count == 0)
            return string.Empty;

        var newest = matched.Max(h => h.PurchaseTime);
        return $", newest {quality} sale {newest:yyyy-MM-dd HH:mm} " +
               $"({(now - newest).TotalDays:0.#} day(s) ago)";
    }

    private static string PassedOver(ItemContext ctx, long cheapest)
    {
        var cheaper = ctx.Offerings.Where(o => o.PricePerUnit < cheapest).ToList();
        if (cheaper.Count == 0)
            return string.Empty;

        var lowest = cheaper.Min(o => o.PricePerUnit);
        var holder = cheaper.First(o => o.PricePerUnit == lowest);

        var why =
            ctx.MyRetainers.IsMine(holder) ? "ours"
            : ctx.IsHq && !holder.IsHq ? "it is NQ and ours is HQ"
            : "treated as an aggressive undercut";

        return $", cheaper offer at {lowest:N0} passed over ({why})";
    }

    private static long? ComputeReference(
        ItemContext ctx,
        PricingConfig cfg,
        DateTimeOffset now,
        out List<long> usableHistory,
        out List<HistoryEntry> matched)
    {
        matched = ctx.History.Where(h => h.IsHq == ctx.IsHq && h.UnitPrice > 0).ToList();

        usableHistory =
        [
            .. matched
                 .Where(h => (now - h.PurchaseTime).TotalDays <= cfg.HistoryMaxAgeDays)
                 .Select(h => h.UnitPrice)
                 .Order(),
        ];

        return usableHistory.Count >= cfg.MinHistorySamples ? Median(usableHistory) : null;
    }

    private static List<Listing> FilterOutliers(
        List<Listing> others,
        PricingConfig cfg,
        long? history,
        out int ignored,
        out bool crashGuard)
    {
        ignored = 0;
        crashGuard = false;

        var sorted = others.OrderBy(o => o.PricePerUnit).ToList();
        var dropped = 0;

        while (sorted.Count - dropped > 1)
        {
            var candidate = sorted[dropped].PricePerUnit;

            var above = sorted
                .GetRange(dropped + 1, sorted.Count - dropped - 1)
                .Select(o => o.PricePerUnit)
                .ToList();

            if ((GoingRate(above, cfg.OutlierRatio, history is null ? 3 : 2) ?? history) is not { } rate)
                break;

            if (candidate >= rate * cfg.OutlierRatio)
                break;

            if (history is { } sold && candidate >= sold * cfg.OutlierRatio)
                break;

            dropped++;

            if (dropped > cfg.MaxAggressiveUndercuts)
            {
                crashGuard = true;
                return others;
            }
        }

        if (dropped == 0)
            return others;

        ignored = dropped;
        return sorted.GetRange(dropped, sorted.Count - dropped);
    }

    private static long? GoingRate(List<long> above, double ratio, int minSellers)
    {
        List<List<long>> runs = [];

        foreach (var price in above)
        {
            if (runs.Count == 0 || price * ratio > runs[^1][^1])
                runs.Add([]);

            runs[^1].Add(price);
        }

        var biggest = runs.Max(run => run.Count);
        var market = runs.First(run => run.Count * 2 >= biggest);

        return market.Count >= minSellers ? Median(market) : null;
    }

    private static long FromHistory(List<long> sortedPrices, NoCompetitionStrategy strategy) => strategy switch
    {
        NoCompetitionStrategy.Median => Median(sortedPrices),
        NoCompetitionStrategy.Max => sortedPrices[^1],
        _ => Percentile(sortedPrices, 0.75),
    };

    private static long Median(List<long> sorted) =>
        sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;

    private static long Percentile(List<long> sorted, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }
}
