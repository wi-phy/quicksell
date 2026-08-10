using Quicksell.Pricing;
using Xunit;

namespace Quicksell.Pricing.Tests;

public class PricingEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly RetainerSet Mine =
        new([1001UL, 1002UL], ["Bibi", "Koko"]);

    private static PricingConfig Config() => new();

    private static List<HistoryEntry> History(long unitPrice, int count = 5, bool isHq = false) =>
        [.. Enumerable.Range(0, count).Select(i =>
            new HistoryEntry(unitPrice, 1, isHq, Now.AddHours(-i - 1)))];

    private static ItemContext Item(
        long myPrice,
        IEnumerable<Listing>? offerings = null,
        IEnumerable<HistoryEntry>? history = null,
        bool isHq = false,
        uint myQuantity = 10) => new()
        {
            ItemId = 5057,
            ItemName = "Test Item",
            IsHq = isHq,
            MyUnitPrice = myPrice,
            MyQuantity = myQuantity,
            Offerings = [.. offerings ?? []],
            History = [.. history ?? []],
            MyRetainers = Mine,
        };

    [Fact]
    public void Undercuts_the_cheapest_competitor()
    {
        var ctx = Item(9000, [new Listing(5000, 1, false, 2001, "Rival")], History(5000));

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(PriceAction.SetPrice, decision.Action);
        Assert.Equal(4999, decision.TargetPrice);
        Assert.Equal(PriceReason.UndercutCompetitor, decision.Reason);
    }

    [Fact]
    public void Skips_when_already_one_gil_below_the_second_cheapest()
    {
        var ctx = Item(4999,
        [
            new Listing(4999, 10, false, 1001, "Bibi"),
            new Listing(5000, 3, false, 2001, "Rival"),
        ], History(5000));

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(PriceAction.Skip, decision.Action);
        Assert.Equal(PriceReason.AlreadyOptimal, decision.Reason);
    }

    [Fact]
    public void Raises_price_when_sitting_far_below_the_cheapest_competitor()
    {
        var ctx = Item(5000, [new Listing(10000, 5, false, 2001, "Rival")], History(9000));

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(PriceAction.SetPrice, decision.Action);
        Assert.Equal(9999, decision.TargetPrice);
        Assert.Equal(PriceReason.RaisedTowardCompetitor, decision.Reason);
    }

    [Fact]
    public void Respects_a_custom_undercut_amount()
    {
        var cfg = Config();
        cfg.UndercutAmount = 100;
        var ctx = Item(9000, [new Listing(5000, 1, false, 2001, "Rival")], History(5000));

        Assert.Equal(4900, PricingEngine.Decide(ctx, cfg, Now).TargetPrice);
    }

    [Fact]
    public void Never_undercuts_our_own_retainer()
    {
        var ctx = Item(4000, [new Listing(4000, 10, false, 1001, "Bibi")], History(5000));

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(PriceReason.HistoryFallback, decision.Reason);
        Assert.Equal(5000, decision.TargetPrice);
        Assert.NotEqual(3999, decision.TargetPrice);
    }

    [Fact]
    public void Two_of_our_own_retainers_do_not_fight_each_other()
    {
        var offerings = new[]
        {
            new Listing(4999, 10, false, 1001, "Bibi"),
            new Listing(6000, 10, false, 1002, "Koko"),
            new Listing(7000, 5, false, 2001, "Rival"),
        };

        var bibi = PricingEngine.Decide(Item(4999, offerings, History(7000)), Config(), Now);
        var koko = PricingEngine.Decide(Item(6000, offerings, History(7000)), Config(), Now);

        Assert.Equal(6999, bibi.TargetPrice);
        Assert.Equal(6999, koko.TargetPrice);
    }

    [Fact]
    public void Matches_our_own_listing_by_name_when_the_retainer_id_is_missing()
    {
        var ctx = Item(4000, [new Listing(4000, 10, false, 0, "bibi")], History(5000));

        Assert.Equal(PriceReason.HistoryFallback, PricingEngine.Decide(ctx, Config(), Now).Reason);
    }

    [Fact]
    public void Ignores_an_aggressive_undercut()
    {
        var ctx = Item(6000,
        [
            new Listing(100, 1, false, 2002, "Idiot"),
            new Listing(5000, 20, false, 2001, "Rival"),
        ], History(5000));

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(4999, decision.TargetPrice);
        Assert.Equal(1, decision.IgnoredOutliers);
        Assert.False(decision.CrashGuardTripped);
    }

    [Fact]
    public void Ignores_it_however_big_the_stack_behind_it_is()
    {
        var ctx = Item(6000,
        [
            new Listing(100, 99, false, 2002, "Dumper"),
            new Listing(5000, 20, false, 2001, "Rival"),
        ], History(5000));

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(4999, decision.TargetPrice);
        Assert.Equal(1, decision.IgnoredOutliers);
    }

    [Fact]
    public void Reads_the_going_rate_off_the_board_rather_than_the_history()
    {
        var ctx = Item(5000,
        [
            new Listing(4, 2, false, 2001, "Silly"),
            new Listing(1800, 1, false, 2002, "Liaam"),
            new Listing(2084, 2, false, 2003, "Aelirenn"),
            new Listing(2084, 1, false, 2004, "Hafla"),
            new Listing(2094, 1, false, 2005, "Amasaki"),
        ]);

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(1799, decision.TargetPrice);
        Assert.Equal(1, decision.IgnoredOutliers);
        Assert.False(decision.CrashGuardTripped);
        Assert.Null(decision.ReferencePrice);
    }

    [Fact]
    public void Ignores_a_whole_huddle_of_silly_sellers()
    {
        var ctx = Item(5000,
        [
            new Listing(4, 2, false, 2001, "Silly"),
            new Listing(5, 1, false, 2002, "Sillier"),
            new Listing(1800, 1, false, 2003, "Liaam"),
            new Listing(2084, 2, false, 2004, "Aelirenn"),
            new Listing(2094, 1, false, 2005, "Amasaki"),
        ]);

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(1799, decision.TargetPrice);
        Assert.Equal(2, decision.IgnoredOutliers);
    }

    [Fact]
    public void A_pair_of_dreamers_at_the_top_does_not_lift_the_going_rate()
    {
        var ctx = Item(5000,
        [
            new Listing(4, 2, false, 2001, "Silly"),
            new Listing(1800, 1, false, 2002, "Liaam"),
            new Listing(999_999, 1, false, 2003, "Dreamer"),
            new Listing(999_999, 1, false, 2004, "Dreamer"),
        ], History(1800));

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(1799, decision.TargetPrice);
        Assert.Equal(1, decision.IgnoredOutliers);
    }

    [Fact]
    public void Would_rather_ignore_nobody_than_follow_the_dreamers_with_no_history()
    {
        var ctx = Item(5000,
        [
            new Listing(1800, 1, false, 2001, "Liaam"),
            new Listing(999_999, 1, false, 2002, "Dreamer"),
            new Listing(999_999, 1, false, 2003, "Dreamer"),
        ]);

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(1799, decision.TargetPrice);
        Assert.Equal(0, decision.IgnoredOutliers);
    }

    [Fact]
    public void Takes_a_uniformly_collapsed_market_at_face_value()
    {
        var ctx = Item(5000,
        [
            new Listing(100, 1, false, 2002, "A"),
            new Listing(110, 2, false, 2003, "B"),
            new Listing(120, 1, false, 2004, "C"),
        ], History(5000));

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(99, decision.TargetPrice);
        Assert.Equal(0, decision.IgnoredOutliers);
        Assert.False(decision.CrashGuardTripped);
    }

    [Fact]
    public void Cannot_judge_a_lone_rival_without_history_to_lean_on()
    {
        var thinHistory = History(5000, count: 2);
        var ctx = Item(6000,
        [
            new Listing(100, 1, false, 2002, "Idiot"),
            new Listing(5000, 20, false, 2001, "Rival"),
        ], thinHistory);

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(99, decision.TargetPrice);
        Assert.Null(decision.ReferencePrice);
    }

    [Fact]
    public void Prices_from_history_when_nobody_else_is_selling()
    {
        var history = new[]
        {
            new HistoryEntry(1000, 1, false, Now.AddHours(-1)),
            new HistoryEntry(2000, 1, false, Now.AddHours(-2)),
            new HistoryEntry(3000, 1, false, Now.AddHours(-3)),
            new HistoryEntry(4000, 1, false, Now.AddHours(-4)),
        };
        var ctx = Item(500, offerings: [], history: history);

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(PriceReason.HistoryFallback, decision.Reason);
        Assert.Equal(3000, decision.TargetPrice);
    }

    [Fact]
    public void History_strategy_is_configurable()
    {
        var history = new[]
        {
            new HistoryEntry(1000, 1, false, Now.AddHours(-1)),
            new HistoryEntry(2000, 1, false, Now.AddHours(-2)),
            new HistoryEntry(3000, 1, false, Now.AddHours(-3)),
            new HistoryEntry(90000, 1, false, Now.AddHours(-4)),
        };
        var ctx = Item(500, offerings: [], history: history);

        var median = Config();
        median.NoCompetitionStrategy = NoCompetitionStrategy.Median;
        Assert.Equal(2500, PricingEngine.Decide(ctx, median, Now).TargetPrice);

        var max = Config();
        max.NoCompetitionStrategy = NoCompetitionStrategy.Max;
        Assert.Equal(90000, PricingEngine.Decide(ctx, max, Now).TargetPrice);
    }

    [Fact]
    public void Ignores_sales_older_than_the_configured_window()
    {
        var stale = Enumerable.Range(0, 5)
            .Select(i => new HistoryEntry(5000, 1, false, Now.AddDays(-30 - i)))
            .ToList();
        var ctx = Item(500, offerings: [], history: stale);

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(PriceAction.Skip, decision.Action);
        Assert.Equal(PriceReason.NoData, decision.Reason);
    }

    [Fact]
    public void Skips_when_there_is_nothing_to_price_from()
    {
        var decision = PricingEngine.Decide(Item(5000), Config(), Now);

        Assert.Equal(PriceAction.Skip, decision.Action);
        Assert.Equal(PriceReason.NoData, decision.Reason);
        Assert.Equal(5000, decision.TargetPrice);
    }

    [Fact]
    public void Hq_listings_are_priced_against_hq_offers_only()
    {
        var ctx = Item(20000,
        [
            new Listing(5000, 10, false, 2001, "NqSeller"),
            new Listing(15000, 2, true, 2002, "HqSeller"),
        ], History(15000, isHq: true), isHq: true);

        Assert.Equal(14999, PricingEngine.Decide(ctx, Config(), Now).TargetPrice);
    }

    [Fact]
    public void Nq_listings_must_undercut_cheaper_hq_offers_too()
    {
        var ctx = Item(20000,
        [
            new Listing(1000, 2, true, 2002, "HqSeller"),
            new Listing(5000, 10, false, 2001, "NqSeller"),
        ], History(1200));

        Assert.Equal(999, PricingEngine.Decide(ctx, Config(), Now).TargetPrice);
    }

    [Fact]
    public void Hq_listings_may_sit_above_the_nq_offers()
    {
        var ctx = Item(20000,
        [
            new Listing(1000, 10, false, 2001, "NqSeller"),
            new Listing(15000, 2, true, 2002, "HqSeller"),
        ], History(15000, isHq: true), isHq: true);

        Assert.Equal(14999, PricingEngine.Decide(ctx, Config(), Now).TargetPrice);
    }

    [Fact]
    public void Nq_beats_the_cheapest_offer_on_the_board_whatever_its_quality()
    {
        var ctx = Item(9957,
        [
            new Listing(9948, 2, true, 2001, "Catluggage"),
            new Listing(9956, 2, true, 2002, "Dragonladyhq"),
            new Listing(9957, 2, false, 1001, "Bibi"),
            new Listing(10000, 1, false, 2003, "Regen"),
            new Listing(16000, 2, true, 2004, "Urizan"),
        ], History(10000));

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(PriceAction.SetPrice, decision.Action);
        Assert.Equal(9947, decision.TargetPrice);
    }

    [Fact]
    public void Hq_history_does_not_leak_into_nq_pricing()
    {
        var history = new[]
        {
            new HistoryEntry(50000, 1, true, Now.AddHours(-1)),
            new HistoryEntry(50000, 1, true, Now.AddHours(-2)),
            new HistoryEntry(50000, 1, true, Now.AddHours(-3)),
        };
        var ctx = Item(500, offerings: [], history: history);

        Assert.Equal(PriceReason.NoData, PricingEngine.Decide(ctx, Config(), Now).Reason);
    }

    [Fact]
    public void Pulls_the_item_when_the_target_falls_below_the_floor()
    {
        var ctx = Item(5000, [new Listing(150, 20, false, 2001, "Rival")], History(150));

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(PriceAction.ReturnToInventory, decision.Action);
        Assert.Equal(PriceReason.BelowFloor, decision.Reason);
    }

    [Fact]
    public void Pulls_the_item_even_when_it_already_sits_below_the_floor()
    {
        var ctx = Item(149, [new Listing(150, 20, false, 2001, "Rival")], History(150));

        Assert.Equal(PriceAction.ReturnToInventory, PricingEngine.Decide(ctx, Config(), Now).Action);
    }

    [Fact]
    public void Floor_is_configurable()
    {
        var cfg = Config();
        cfg.MinPrice = 100;
        var ctx = Item(5000, [new Listing(150, 20, false, 2001, "Rival")], History(150));

        var decision = PricingEngine.Decide(ctx, cfg, Now);

        Assert.Equal(PriceAction.SetPrice, decision.Action);
        Assert.Equal(149, decision.TargetPrice);
    }

    [Fact]
    public void Never_prices_below_one_gil()
    {
        var cfg = Config();
        cfg.MinPrice = 1;
        var ctx = Item(5000, [new Listing(1, 99, false, 2001, "Rival")], History(1));

        Assert.Equal(1, PricingEngine.Decide(ctx, cfg, Now).TargetPrice);
    }

    [Fact]
    public void Clamps_to_the_game_maximum()
    {
        var history = History(2_000_000_000);
        var ctx = Item(500, offerings: [], history: history);

        Assert.Equal(999_999_999, PricingEngine.Decide(ctx, Config(), Now).TargetPrice);
    }

    [Fact]
    public void History_from_stack_totals_is_converted_to_unit_prices()
    {
        var entry = HistoryEntry.FromTotal(total: 50000, quantity: 10, isHq: false, purchaseTime: Now);

        Assert.Equal(5000, entry.UnitPrice);
    }

    [Fact]
    public void Detects_prices_recorded_per_unit()
    {
        List<HistoryEntry> history =
        [
            new(6449, 1, false, Now),
            new(6400, 1, false, Now),
            new(6449, 90, false, Now),
            new(6500, 12, false, Now),
        ];

        Assert.Equal(HistoryBasis.PerUnit, HistoryBasisDetector.Detect(history));
    }

    [Fact]
    public void Detects_prices_recorded_as_stack_totals()
    {
        List<HistoryEntry> history =
        [
            new(6449, 1, false, Now),
            new(6400, 1, false, Now),
            new(580410, 90, false, Now),
            new(77388, 12, false, Now),
        ];

        Assert.Equal(HistoryBasis.StackTotal, HistoryBasisDetector.Detect(history));
    }

    [Fact]
    public void Detects_per_unit_prices_with_no_single_unit_sale_to_anchor_on()
    {
        List<HistoryEntry> history =
        [
            new(5802, 20, false, Now),
            new(5500, 25, false, Now),
            new(5000, 15, false, Now),
            new(4999, 20, false, Now),
            .. Enumerable.Repeat(new HistoryEntry(6000, 10, false, Now), 15),
        ];

        Assert.Equal(HistoryBasis.PerUnit, HistoryBasisDetector.Detect(history));
    }

    [Fact]
    public void Cannot_tell_the_basis_when_every_sale_is_the_same_size()
    {
        List<HistoryEntry> history =
        [
            new(6449, 10, false, Now),
            new(6400, 10, false, Now),
            new(6500, 10, false, Now),
        ];

        Assert.Equal(HistoryBasis.Unknown, HistoryBasisDetector.Detect(history));
    }

    [Fact]
    public void Says_what_it_looked_at_when_it_finds_nothing_to_price_from()
    {
        var ctx = Item(9000,
            [new Listing(5000, 1, false, 2001, "Rival")],
            History(5000, count: 8),
            isHq: true);

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(PriceReason.NoData, decision.Reason);
        Assert.Contains("board has 1 offer(s), 0 ours, 0 HQ", decision.Explanation);
        Assert.Contains("history has 8 sale(s), 0 HQ", decision.Explanation);
    }

    [Fact]
    public void Says_when_the_history_was_the_right_quality_but_too_old()
    {
        List<HistoryEntry> stale =
            [.. Enumerable.Range(0, 6).Select(i => new HistoryEntry(5000, 1, false, Now.AddDays(-60 - i)))];

        var ctx = Item(9000, offerings: [], history: stale);

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(PriceReason.NoData, decision.Reason);
        Assert.Contains("history has 6 sale(s), 6 NQ, 0 under 30 day(s) old", decision.Explanation);
        Assert.Contains("newest NQ sale 2026-06-07 12:00 (60 day(s) ago)", decision.Explanation);
    }

    [Fact]
    public void Stamps_the_newest_sale_of_our_own_quality_not_of_the_other_one()
    {
        List<HistoryEntry> mixed =
        [
            new HistoryEntry(4000, 1, false, Now.AddHours(-2)),
            .. Enumerable.Range(0, 6).Select(i => new HistoryEntry(9000, 1, true, Now.AddDays(-60 - i))),
        ];

        var ctx = Item(9000, offerings: [], history: mixed, isHq: true);

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(PriceReason.NoData, decision.Reason);
        Assert.Contains("newest HQ sale 2026-06-07 12:00 (60 day(s) ago)", decision.Explanation);
    }

    [Fact]
    public void Follows_the_low_prices_once_too_many_sellers_are_down_there()
    {
        List<Listing> offerings =
        [
            .. Enumerable.Range(0, 13).Select(i => new Listing(1000 + i, 1, false, (ulong)(2001 + i), $"Rival{i}")),
            new Listing(9000, 1, false, 3001, "Holdout"),
        ];

        var ctx = Item(9000, offerings, History(5000));

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(PriceAction.SetPrice, decision.Action);
        Assert.Equal(999, decision.TargetPrice);
        Assert.False(decision.CrashGuardTripped);
        Assert.Equal(0, decision.IgnoredOutliers);
    }

    [Fact]
    public void Still_ignores_a_couple_of_aggressive_undercuts()
    {
        var ctx = Item(9000,
        [
            new Listing(100, 1, false, 2001, "Bradeur"),
            new Listing(120, 1, false, 2002, "Bradeur"),
            new Listing(5000, 3, false, 2003, "Rival"),
        ], History(5000));

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(4999, decision.TargetPrice);
        Assert.Equal(2, decision.IgnoredOutliers);
        Assert.False(decision.CrashGuardTripped);
    }

    [Fact]
    public void Follows_them_as_soon_as_there_is_one_more_than_allowed()
    {
        var ctx = Item(20000,
        [
            new Listing(300, 1, false, 2001, "Bradeur"),
            new Listing(1200, 1, false, 2002, "Bradeur"),
            new Listing(4500, 1, false, 2003, "Bradeur"),
            new Listing(18000, 3, false, 2004, "Rival"),
            new Listing(18000, 3, false, 2005, "Rival"),
            new Listing(18000, 3, false, 2006, "Rival"),
        ]);

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(299, decision.TargetPrice);
        Assert.Equal(0, decision.IgnoredOutliers);
        Assert.True(decision.CrashGuardTripped);
    }

    [Fact]
    public void Says_when_the_cheaper_offer_it_sat_above_was_an_aggressive_undercut()
    {
        var ctx = Item(9000,
        [
            new Listing(900, 1, false, 2001, "Bradeur"),
            new Listing(5000, 3, false, 2002, "Rival"),
        ], History(5000));

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(4999, decision.TargetPrice);
        Assert.Contains("cheaper offer at 900 passed over (treated as an aggressive undercut)",
            decision.Explanation);
    }

    [Fact]
    public void Says_when_the_cheaper_offer_it_sat_above_was_our_own()
    {
        var ctx = Item(9000,
        [
            new Listing(4000, 1, false, 1002, "Koko"),
            new Listing(5000, 3, false, 2001, "Rival"),
        ], History(5000));

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(4999, decision.TargetPrice);
        Assert.Contains("passed over (ours)", decision.Explanation);
    }

    [Fact]
    public void Says_when_the_cheaper_offer_it_sat_above_was_nq_and_ours_is_hq()
    {
        var ctx = Item(9000,
        [
            new Listing(4000, 1, false, 2001, "Rival"),
            new Listing(5000, 3, true, 2002, "Rival HQ"),
        ], History(5000, isHq: true), isHq: true);

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.Equal(4999, decision.TargetPrice);
        Assert.Contains("passed over (it is NQ and ours is HQ)", decision.Explanation);
    }

    [Fact]
    public void Says_nothing_extra_when_it_really_is_the_cheapest()
    {
        var ctx = Item(9000, [new Listing(5000, 3, false, 2001, "Rival")], History(5000));

        var decision = PricingEngine.Decide(ctx, Config(), Now);

        Assert.DoesNotContain("passed over", decision.Explanation);
    }

    [Fact]
    public void Cannot_tell_the_basis_from_too_few_sales()
    {
        List<HistoryEntry> history = [new(6449, 1, false, Now), new(6400, 10, false, Now)];

        Assert.Equal(HistoryBasis.Unknown, HistoryBasisDetector.Detect(history));
    }
}
