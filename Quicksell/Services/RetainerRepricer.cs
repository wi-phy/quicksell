using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.UIHelpers.AddonMasterImplementations;
using Quicksell.Pricing;

namespace Quicksell.Services;

public sealed record RepriceOutcome(
    string Retainer, MarketListing Listing, PriceDecision? Decision, string? Failure = null);

public sealed class RetainerRepricer : IDisposable
{
    private const string SellListAddon = "RetainerSellList";
    private const string ContextMenuAddon = "ContextMenu";
    private const string SellAddon = "RetainerSell";
    private const string PromptAddon = "SelectYesno";

    private static readonly TaskManagerConfiguration Patient = new() { TimeLimitMS = 20_000, AbortOnTimeout = true };

    private static readonly TaskManagerConfiguration WaitingForData =
        new() { TimeLimitMS = 180_000, AbortOnTimeout = true };

    private readonly TaskManager tasks = new(new TaskManagerConfiguration
    {
        TimeLimitMS = 10_000,
        AbortOnTimeout = true,

        ShowError = true,
    });

    private readonly List<RepriceOutcome> outcomes = [];

    private const long NudgeAfterMs = 2_000;

    private const string HqGlyph = "";

    private sealed record PendingWrite(MarketListing Listing, long Target);

    private const long ContainerGraceMs = 5_000;

    private readonly List<MarketListing> unvisited = [];

    private readonly List<MarketListing> rows = [];

    private bool rowsKnown;

    private int writtenCount;
    private int retainerWritten;
    private bool retainerActive;
    private long? prepareStartedAt;
    private string menuEntry = string.Empty;

    private PendingWrite? current;
    private int requestCount;

    private int gateRow = -1;

    private int waitingRow = -1;

    private string retainerName = string.Empty;
    private bool promptAnswered;
    private long closeRequestedAt;

    private bool skipCurrent;

    private sealed record PendingPull(MarketListing Listing, int Row);

    private readonly List<MarketListing> pullPlan = [];

    private PendingPull? currentPull;
    private bool skipPull;
    private bool pullPromptAnswered;
    private int pulledCount;
    private int refusedForSpace;
    private bool bagFullReported;

    private string stage = string.Empty;
    private int retainerIndex;
    private int retainerTotal;
    private int rowIndex;
    private int pullIndex;

    public bool IsRunning => tasks.IsBusy;

    public string Stage => stage;

    public string CurrentRetainer => retainerName;

    public int RetainerIndex => retainerIndex;

    public int RetainerTotal => retainerTotal;

    public int RowIndex => rowIndex;

    public int RowTotal => rows.Count;

    public int PullIndex => pullIndex;

    public int PullTotal => pullPlan.Count;

    public IReadOnlyList<RepriceOutcome> Outcomes => outcomes;

    public int Written => writtenCount;

    public int Pulled => pulledCount;

    public int RefusedForSpace => refusedForSpace;

    public bool LastRunWasDry { get; private set; }

    public event Action? RunFinished;

    public void Dispose() => tasks.Dispose();

    public void Abort()
    {
        tasks.Abort();
        Plugin.Scheduler.Cancel();
        Plugin.Log.Warning("[reprice] aborted");
    }

    public bool Start()
    {
        if (IsRunning)
            return false;

        if (!GameUi.IsReady(SellListAddon))
        {
            Plugin.Log.Error("[reprice] open a retainer's market listings first.");
            return false;
        }

        var listings = RetainerMarketReader.ActiveRetainerListings();
        if (listings.Count == 0)
        {
            Plugin.Log.Warning("[reprice] this retainer has nothing listed.");
            return false;
        }

        if (!Plugin.Configuration.DryRun && string.IsNullOrWhiteSpace(Plugin.Configuration.AdjustPriceMenuEntry))
        {
            Plugin.Log.Error(
                "[reprice] the context menu entry that opens an item's price has not been set. " +
                "Right-click a listed item and pick it in the debug window.");
            return false;
        }

        ResetRun();
        Plugin.Scheduler.BeginRun();
        retainerActive = true;
        retainerName = RetainerIdentity.ActiveRetainerName();

        if (Plugin.Configuration.IsSkipped(retainerName))
        {
            Plugin.Log.Warning(
                "[reprice] {Name} is left out of full runs in the settings, but you asked for this " +
                "one by hand, so it is being repriced anyway",
                retainerName);
        }

        var displayed = SellListReader.DisplayOrder(listings);
        if (displayed is not null)
        {
            listings = displayed;
            rowsKnown = true;
        }
        else
        {
            Plugin.Log.Warning(
                "[reprice] could not read the window's display order - falling back to opening " +
                "every row to find out what it holds");
        }

        var itemIds = listings.Select(l => l.ItemId).Distinct().ToList();
        var prefetchBudget = (itemIds.Count * (Plugin.Configuration.MarketRequestDelayMs + 4_000)) + 30_000;
        requestCount = itemIds.Count;

        Plugin.Log.Information(
            "[reprice] {Name}: {Listings} listing(s), {Items} market request(s), about {Seconds}s of prefetch",
            retainerName, listings.Count, itemIds.Count,
            itemIds.Count * Plugin.Configuration.MarketRequestDelayMs / 1000);

        Plugin.Scheduler.Enqueue(itemIds);

        retainerIndex = 1;
        retainerTotal = 1;
        stage = $"asking the market about {retainerName}'s items";

        if (Plugin.Configuration.DryRun)
        {
            if (rowsKnown)
                rows.AddRange(listings);

            tasks.Enqueue(
                () => !Plugin.Scheduler.IsRunning,
                "wait for the market data",
                new TaskManagerConfiguration { TimeLimitMS = prefetchBudget, AbortOnTimeout = true });

            tasks.Enqueue(() => DecideAll(listings), "decide");
            tasks.Enqueue(() => BuildPullPlan(retainerName), "plan the returns");
            tasks.Enqueue(() => FinishRetainer(retainerName), "wrap up");
            return true;
        }

        if (rowsKnown)
            rows.AddRange(listings);
        else
            unvisited.AddRange(listings);

        for (var row = 0; row < listings.Count; row++)
            QueueRow(row);

        tasks.Enqueue(() => BuildPullPlan(retainerName), "plan the returns");
        tasks.Enqueue(() => FinishRetainer(retainerName), "wrap up");
        tasks.Enqueue(Finish, "finish");
        return true;
    }

    private void ResetRun()
    {
        outcomes.Clear();
        unvisited.Clear();
        rows.Clear();
        current = null;
        skipCurrent = false;
        rowsKnown = false;
        retainerActive = false;
        writtenCount = 0;
        retainerWritten = 0;
        pullPlan.Clear();
        currentPull = null;
        skipPull = false;
        pullPromptAnswered = false;
        pulledCount = 0;
        refusedForSpace = 0;
        bagFullReported = false;
        gateRow = -1;
        waitingRow = -1;
        prepareStartedAt = null;
        retainerName = string.Empty;
        stage = string.Empty;
        retainerIndex = 0;
        retainerTotal = 0;
        rowIndex = 0;
        pullIndex = 0;
    }

    public bool StartAll()
    {
        if (IsRunning)
            return false;

        menuEntry = Plugin.Configuration.MarketMenuEntry;
        if (string.IsNullOrWhiteSpace(menuEntry))
        {
            Plugin.Log.Error(
                "[reprice] the retainer menu entry that opens the market has not been set. " +
                "Open a retainer, then pick it in the debug window.");
            return false;
        }

        if (!Plugin.Configuration.DryRun && string.IsNullOrWhiteSpace(Plugin.Configuration.AdjustPriceMenuEntry))
        {
            Plugin.Log.Error(
                "[reprice] the context menu entry that opens an item's price has not been set. " +
                "Right-click a listed item and pick it in the debug window.");
            return false;
        }

        var entries = RetainerNavigation.Active();
        if (entries is null)
        {
            Plugin.Log.Error("[reprice] the retainer list is not open. Use the bell first.");
            return false;
        }

        if (entries.Count == 0)
        {
            Plugin.Log.Warning("[reprice] no selectable retainer in the list");
            return false;
        }

        ResetRun();
        Plugin.Scheduler.BeginRun();

        var known = RetainerIdentity.List()
            .ToDictionary(r => r.Name, r => r.MarketItemCount, StringComparer.OrdinalIgnoreCase);

        var visiting = 0;
        foreach (var entry in entries)
        {
            if (Plugin.Configuration.IsSkipped(entry.Name))
            {
                Plugin.Log.Information("[reprice] {Name}: left out of runs in the settings", entry.Name);
                continue;
            }

            if (!known.TryGetValue(entry.Name, out var expected))
            {
                Plugin.Log.Warning(
                    "[reprice] {Name} is in the list but not in the retainer manager, skipping it",
                    entry.Name);
                continue;
            }

            if (expected == 0)
            {
                Plugin.Log.Information("[reprice] {Name}: nothing listed, skipped", entry.Name);
                continue;
            }

            QueueVisit(entry, (int)expected);
            visiting++;
        }

        if (visiting == 0)
        {
            Plugin.Log.Warning(
                "[reprice] nothing to do: every retainer is either empty or left out in the settings");
            return false;
        }

        retainerTotal = visiting;
        stage = "starting";

        Plugin.Log.Information("[reprice] visiting {Visiting} of {Total} retainer(s)", visiting, entries.Count);

        tasks.Enqueue(Finish, "finish");
        return true;
    }

    private void QueueVisit(RetainerEntry entry, int expected)
    {
        RetainerNavigation.QueueVisit(tasks, entry, menuEntry, t =>
        {
            t.Enqueue(() => Announce(entry.Name), $"announce {entry.Name}");
            t.Enqueue(() => Prepare(entry, expected), $"read {entry.Name}'s order", WaitingForData);

            if (Plugin.Configuration.DryRun)
            {
                var budget = (expected * (Plugin.Configuration.MarketRequestDelayMs + 4_000)) + 30_000;
                t.Enqueue(
                    () => !retainerActive || !Plugin.Scheduler.IsRunning,
                    $"wait for {entry.Name}'s market data",
                    new TaskManagerConfiguration { TimeLimitMS = budget, AbortOnTimeout = true });

                t.Enqueue(DecideRows, $"decide for {entry.Name}");
            }
            else
            {
                for (var row = 0; row < expected; row++)
                    QueueRow(row);
            }

            t.Enqueue(() => BuildPullPlan(entry.Name), $"plan {entry.Name}'s returns");
            t.Enqueue(() => FinishRetainer(entry.Name), $"wrap up {entry.Name}");
        });
    }

    private static readonly string Rule = new('-', 60);

    private bool Announce(string name)
    {
        prepareStartedAt = null;
        retainerIndex++;
        rowIndex = 0;
        pullIndex = 0;
        stage = $"opening {name}";

        Plugin.Log.Information("[reprice] {Rule}", Rule);
        Plugin.Log.Information("[reprice] >>> {Name}", name);

        return true;
    }

    private bool Prepare(RetainerEntry entry, int expected)
    {
        prepareStartedAt ??= Environment.TickCount64;

        rows.Clear();
        rowsKnown = false;
        retainerActive = false;
        retainerWritten = 0;
        retainerName = entry.Name;

        var listings = RetainerMarketReader.ActiveRetainerListings();
        var waited = Environment.TickCount64 - prepareStartedAt.Value;

        if (listings.Count != expected && waited < ContainerGraceMs)
            return false;

        var active = RetainerIdentity.ActiveRetainerName();
        if (!string.Equals(active, entry.Name, StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Log.Warning(
                "[reprice] expected to be at {Expected} but the open retainer is {Active}, skipping it",
                entry.Name, active);
            return true;
        }

        if (listings.Count > expected)
        {
            Plugin.Log.Warning(
                "[reprice] {Name} holds {Now} listing(s) but the retainer list said {Expected}; " +
                "the last {Extra} will not be touched this run",
                entry.Name, listings.Count, expected, listings.Count - expected);
        }

        var displayed = SellListReader.DisplayOrder(listings);
        if (displayed is null)
        {
            Plugin.Log.Warning(
                "[reprice] {Name}: could not read the window's display order, skipping it", entry.Name);
            return true;
        }

        rows.AddRange(displayed);
        rowsKnown = true;
        retainerActive = true;
        stage = $"asking the market about {entry.Name}'s items";

        var fresh = rows.Select(l => l.ItemId).Distinct().Where(id => !Plugin.Scheduler.IsKnown(id)).ToList();
        var reused = rows.Select(l => l.ItemId).Distinct().Count() - fresh.Count;

        requestCount += fresh.Count;
        Plugin.Scheduler.Enqueue(fresh);

        Plugin.Log.Information(
            "[reprice] {Name}: {Rows} listing(s), {Fresh} new request(s), {Reused} already fetched",
            entry.Name, rows.Count, fresh.Count, reused);

        return true;
    }

    private bool DecideRows()
    {
        if (!retainerActive)
            return true;

        foreach (var listing in rows)
            DecideFor(listing);

        return true;
    }

    private bool FinishRetainer(string name)
    {
        if (retainerActive && !Plugin.Configuration.DryRun)
        {
            Plugin.Log.Information(
                "[reprice] {Name}: {Written} price(s) written over {Rows} listing(s)",
                name, retainerWritten, rows.Count);
        }

        retainerActive = false;
        return true;
    }

    private bool BuildPullPlan(string name)
    {
        pullPlan.Clear();
        pullIndex = 0;

        if (!retainerActive)
            return true;

        var plan = outcomes
            .Where(o => o.Retainer == name && o.Decision?.Action == PriceAction.ReturnToInventory)
            .Select(o => (Listing: o.Listing, Row: rows.FindIndex(l => l.Equals(o.Listing))))
            .OrderByDescending(p => p.Row)
            .ToList();

        if (plan.Count == 0)
            return true;

        pullPlan.AddRange(plan.Select(p => p.Listing));

        var bags = InventorySpace.Player();
        var needSlot = plan.Count(p => !InventorySpace.FitsInAStackAlready(
            p.Listing.ItemId, p.Listing.IsHq, p.Listing.Quantity));

        Plugin.Log.Information(
            "[pull] {Name}: {Count} listing(s) below the {Floor:N0} gil floor, {NeedSlot} would need " +
            "a free slot, {Free} free across {Bags} bag(s) - returning is {State}",
            name, plan.Count, Plugin.Configuration.Pricing.MinPrice, needSlot, bags.FreeSlots,
            bags.Bags, Plugin.Configuration.AllowReturnToInventory ? "on" : "off");

        foreach (var (listing, row) in plan)
        {
            Plugin.Log.Information(
                "[pull] row {Row}: {ItemName}{Hq} x{Quantity} at {Price:N0} gil{Merge}",
                row, listing.Name, listing.IsHq ? " (HQ)" : string.Empty, listing.Quantity,
                listing.UnitPrice,
                InventorySpace.FitsInAStackAlready(listing.ItemId, listing.IsHq, listing.Quantity)
                    ? " (merges into a stack you already carry)"
                    : string.Empty);
        }

        if (needSlot > bags.FreeSlots)
        {
            Plugin.Log.Warning(
                "[pull] {Name}: only {Free} free slot(s) for {NeedSlot} item(s), the run will stop " +
                "pulling once the bag is full",
                name, bags.FreeSlots, needSlot);
        }

        if (plan.Any(p => p.Row < 0))
        {
            Plugin.Log.Warning(
                "[pull] {Name}: some listing(s) could not be matched back to a display row",
                name);
        }

        if (Plugin.Configuration.AllowReturnToInventory && !Plugin.Configuration.DryRun
            && string.IsNullOrWhiteSpace(Plugin.Configuration.ReturnToInventoryMenuEntry))
        {
            pullPlan.Clear();
            Plugin.Log.Error(
                "[pull] the context menu entry that returns an item to the bag has not been set. " +
                "Right-click a listed item and pick it in the debug window. Nothing was pulled.");
        }

        if (Plugin.Configuration.DryRun || !Plugin.Configuration.AllowReturnToInventory)
        {
            pullPlan.Clear();
            return true;
        }

        InsertPulls();
        return true;
    }

    private bool DecideAll(IReadOnlyList<MarketListing> listings)
    {
        foreach (var listing in listings)
            DecideFor(listing);

        var writes = outcomes.Count(o => o.Decision?.Action == PriceAction.SetPrice);
        var pulls = outcomes.Count(o => o.Decision?.Action == PriceAction.ReturnToInventory);

        Plugin.Log.Information(
            "[reprice] {Name}: {Writes} to reprice, {Pulls} below floor, {Skips} left alone " +
            "(dry run, nothing written)",
            retainerName, writes, pulls, outcomes.Count - writes - pulls);

        return true;
    }

    private PriceDecision? DecideFor(MarketListing listing)
    {
        var snapshot = Plugin.Collector.TryGet(listing.ItemId);
        if (snapshot is null || !snapshot.HasOfferings)
            return null;

        var decision = PricingEngine.Decide(
            new ItemContext
            {
                ItemId = listing.ItemId,
                ItemName = listing.Name,
                IsHq = listing.IsHq,
                MyUnitPrice = listing.UnitPrice,
                MyQuantity = listing.Quantity,
                Offerings = snapshot.Offerings,
                History = snapshot.History,
                MyRetainers = RetainerIdentity.Set(),
            },
            Plugin.Configuration.Pricing,
            DateTimeOffset.UtcNow);

        outcomes.Add(new RepriceOutcome(retainerName, listing, decision));
        Plugin.Log.Information(
            "[reprice] {Name}{Hq}: {Action} - {Explanation}",
            listing.Name, listing.IsHq ? " (HQ)" : string.Empty, decision.Action, decision.Explanation);

        return decision;
    }

    private void QueueRow(int row)
    {
        tasks.Enqueue(() => Begin(row), $"start on row {row}");
        tasks.Enqueue(() => ReadyForRow(row), $"hold row {row} until something has come back", WaitingForData);
        tasks.Enqueue(() => skipCurrent || GameUi.IsReady(SellListAddon), $"wait for the sell list (row {row})");
        SettleRow();
        tasks.Enqueue(() => skipCurrent || OpenContextMenu(row), $"open the context menu for row {row}");
        tasks.Enqueue(() => skipCurrent || GameUi.IsReady(ContextMenuAddon), $"wait for row {row}'s context menu", Patient);
        SettleRow();
        tasks.Enqueue(() => skipCurrent || AdjustPrice(row), $"choose adjust price for row {row}");
        tasks.Enqueue(() => skipCurrent || GameUi.IsReady(SellAddon), $"wait for row {row}'s price window", Patient);
        SettleRow();
        tasks.Enqueue(() => Identify(row), $"identify row {row}", WaitingForData);
        tasks.Enqueue(SetAskingPrice, $"type the new price for row {row}");
        SettleRow();
        tasks.Enqueue(ConfirmPrice, $"confirm row {row}");
        tasks.Enqueue(PriceWindowClosed, $"wait for row {row}'s price window to close");
        SettleRow();
    }

    private void SettleRow()
    {
        var delay = Plugin.Configuration.StepDelayMs;
        if (delay <= 0)
            return;

        long? startedAt = null;

        tasks.Enqueue(
            () =>
            {
                if (skipCurrent)
                    return true;

                startedAt ??= Environment.TickCount64;
                return Environment.TickCount64 - startedAt.Value >= delay;
            },
            "settle unless the row is being skipped");
    }

    private bool Begin(int row)
    {
        current = null;
        promptAnswered = false;
        rowIndex = row + 1;
        stage = "repricing";

        skipCurrent = !retainerActive
            || (rowsKnown && row >= rows.Count)
            || (!rowsKnown && unvisited.Count == 0);
        return true;
    }

    private bool ReadyForRow(int row)
    {
        if (skipCurrent)
            return true;

        return rowsKnown ? DecideRow(row) : HoldForFirstAnswer(row);
    }

    private bool DecideRow(int row)
    {
        var listing = rows[row];

        if (!Plugin.Scheduler.HasAnswered(listing.ItemId))
        {
            if (!Plugin.Scheduler.IsRunning)
                return NoData(listing);

            if (Plugin.Scheduler.Prioritise(listing.ItemId))
                Plugin.Log.Information("[reprice] row {Row} is {Name}, asking for it next", row, listing.Name);
            else if (gateRow != row)
                Plugin.Log.Debug(
                    "[reprice] row {Row} is {Name}, its request has not finished yet", row, listing.Name);

            gateRow = row;
            return false;
        }

        var decision = DecideFor(listing);
        if (decision is null)
            return NoData(listing);

        if (decision.Action != PriceAction.SetPrice)
        {
            skipCurrent = true;
            return true;
        }

        current = new PendingWrite(listing, decision.TargetPrice);
        return true;
    }

    private bool NoData(MarketListing listing)
    {
        outcomes.Add(new RepriceOutcome(retainerName, listing, null, "no market data"));
        Plugin.Log.Warning("[reprice] {Name}: no market data came back, left alone", listing.Name);
        skipCurrent = true;
        return true;
    }

    private bool HoldForFirstAnswer(int row)
    {
        if (!Plugin.Scheduler.IsRunning || Plugin.Scheduler.Answered > 0)
            return true;

        if (gateRow != row)
        {
            gateRow = row;
            Plugin.Log.Debug(
                "[reprice] row {Row} held until the first item comes back " +
                "({Pending} queued, {InFlight} in flight, {Requests} in total)",
                row, Plugin.Scheduler.Pending, Plugin.Scheduler.InFlight, requestCount);
        }

        return false;
    }

    public static unsafe bool OpenContextMenu(int row)
    {
        var sellList = GameUi.Ready(SellListAddon);
        if (sellList is null)
        {
            Plugin.Log.Warning("[reprice] the sell list is not open, cannot open row {Row}", row);
            return false;
        }

        Plugin.Log.Information("[reprice] opening the context menu for row {Row}", row);
        Callback.Fire(sellList, true, 0, row, 1);
        return true;
    }

    private unsafe bool AdjustPrice(int row)
    {
        var menu = GameUi.Ready(ContextMenuAddon);
        if (menu is null)
            return false;

        var wanted = Plugin.Configuration.AdjustPriceMenuEntry;
        var entries = new AddonMaster.ContextMenu(menu).Entries;

        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Text, wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            return entry.Select();
        }

        Plugin.Log.Warning(
            "[reprice] row {Row} has no \"{Wanted}\" entry; it offers {Offered}",
            row, wanted, string.Join(" | ", entries.Select(e => e.Text)));

        skipCurrent = true;
        GameUi.Close(ContextMenuAddon);
        return true;
    }

    private unsafe bool Identify(int row)
    {
        if (skipCurrent)
            return true;

        var addon = GameUi.Ready(SellAddon);
        if (addon is null)
            return false;

        var sell = new AddonMaster.RetainerSell(addon);
        var shown = sell.ItemName;
        var price = sell.AskingPrice;

        var isHq = shown.Contains(HqGlyph);
        var name = shown.Replace(HqGlyph, string.Empty).Trim();
        var label = $"{name}{(isHq ? " (HQ)" : string.Empty)}";

        if (rowsKnown)
            return Confirms(row, label, name, isHq, price);

        var index = unvisited.FindIndex(l =>
            string.Equals(l.Name, name, StringComparison.Ordinal)
            && l.IsHq == isHq
            && l.UnitPrice == price);

        if (index < 0)
        {
            Plugin.Log.Warning(
                "[reprice] row {Row} is {Label} at {Price:N0}, which is not one of the listings " +
                "read at the start - it may have sold since",
                row, label, price);

            return Leave();
        }

        var listing = unvisited[index];

        if (Plugin.Scheduler.IsRunning && !Plugin.Scheduler.HasAnswered(listing.ItemId))
        {
            if (Plugin.Scheduler.Prioritise(listing.ItemId))
                Plugin.Log.Information("[reprice] row {Row} is {Label}, asking for it next", row, label);
            else if (waitingRow != row)
                Plugin.Log.Information(
                    "[reprice] row {Row} is {Label}, its request has not finished yet", row, label);

            waitingRow = row;
            return false;
        }

        var decision = DecideFor(listing);

        if (decision is null)
        {
            if (!Plugin.Scheduler.IsRunning)
            {
                unvisited.RemoveAt(index);
                outcomes.Add(new RepriceOutcome(retainerName, listing, null, "no market data"));
                Plugin.Log.Warning("[reprice] {Label}: no market data came back, left alone", label);
                return Leave();
            }

            if (Plugin.Scheduler.Prioritise(listing.ItemId))
            {
                Plugin.Log.Information(
                    "[reprice] row {Row} is {Label}, asking for it next", row, label);
            }
            else if (waitingRow != row)
            {
                Plugin.Log.Information(
                    "[reprice] row {Row} is {Label}, waiting on its market data", row, label);
            }

            waitingRow = row;
            return false;
        }

        unvisited.RemoveAt(index);

        if (decision.Action != PriceAction.SetPrice)
            return Leave();

        current = new PendingWrite(listing, decision.TargetPrice);
        return true;
    }

    private bool Confirms(int row, string label, string name, bool isHq, long price)
    {
        if (current is not { Listing: var expected })
            return Leave();

        if (string.Equals(expected.Name, name, StringComparison.Ordinal)
            && expected.IsHq == isHq
            && expected.UnitPrice == price)
        {
            return true;
        }

        Plugin.Log.Warning(
            "[reprice] row {Row} was read as {Expected}{ExpectedHq} at {ExpectedPrice:N0} but its " +
            "window shows {Label} at {Price:N0}, leaving it alone",
            row, expected.Name, expected.IsHq ? " (HQ)" : string.Empty, expected.UnitPrice,
            label, price);

        var failed = new RepriceOutcome(
            retainerName, expected, null, "the row held a different item than expected");
        var already = outcomes.FindIndex(o => o.Listing.Equals(expected));
        if (already >= 0)
            outcomes[already] = failed;
        else
            outcomes.Add(failed);

        current = null;
        return Leave();
    }

    private bool Leave()
    {
        skipCurrent = true;
        closeRequestedAt = Environment.TickCount64;
        GameUi.Close(SellAddon);
        return true;
    }

    private unsafe bool SetAskingPrice()
    {
        if (skipCurrent || current is null)
            return true;

        var addon = GameUi.Ready(SellAddon);
        if (addon is null)
            return false;

        new AddonMaster.RetainerSell(addon).AskingPrice = (int)current.Target;
        return true;
    }

    private unsafe bool ConfirmPrice()
    {
        if (skipCurrent || current is null)
            return true;

        var addon = GameUi.Ready(SellAddon);
        if (addon is null)
            return false;

        var sell = new AddonMaster.RetainerSell(addon);
        if (sell.AskingPrice != current.Target)
            return false;

        Callback.Fire(addon, true, 0);
        closeRequestedAt = Environment.TickCount64;

        writtenCount++;
        retainerWritten++;
        Plugin.Log.Information(
            "[reprice] {Name}: {Old:N0} -> {New:N0} written",
            current.Listing.Name, current.Listing.UnitPrice, current.Target);
        return true;
    }

    private void InsertPulls()
    {
        var steps = new List<TaskManagerTask>();
        var delay = Plugin.Configuration.StepDelayMs;

        void Settle()
        {
            if (delay > 0)
                steps.Add(new DelayTask(delay));
        }

        for (var index = 0; index < pullPlan.Count; index++)
        {
            var pull = index;
            steps.Add(new TaskManagerTask(() => BeginPull(pull), $"start pull {pull}"));
            steps.Add(new TaskManagerTask(
                () => skipPull || GameUi.IsReady(SellListAddon), $"wait for the sell list (pull {pull})"));
            Settle();
            steps.Add(new TaskManagerTask(
                () => skipPull || OpenContextMenu(currentPull!.Row), $"open the context menu for pull {pull}"));
            steps.Add(new TaskManagerTask(
                () => skipPull || GameUi.IsReady(ContextMenuAddon), $"wait for pull {pull}'s context menu", Patient));
            Settle();
            steps.Add(new TaskManagerTask(
                () => skipPull || ReturnToInventory(), $"choose return to inventory for pull {pull}"));
            Settle();
            steps.Add(new TaskManagerTask(PullFinished, $"wait for pull {pull} to leave the sell list", Patient));
            Settle();
        }

        tasks.InsertMulti([.. steps]);
    }

    private bool BeginPull(int index)
    {
        currentPull = null;
        pullPromptAnswered = false;
        skipPull = true;
        pullIndex = index + 1;
        stage = "returning below-floor items to the bag";

        if (Plugin.Configuration.DryRun || !Plugin.Configuration.AllowReturnToInventory)
            return true;

        if (index >= pullPlan.Count)
            return true;

        var listing = pullPlan[index];

        var displayed = SellListReader.DisplayOrder(RetainerMarketReader.ActiveRetainerListings());
        if (displayed is null)
            return PullFailed(listing, "the sell list could not be read again");

        var row = displayed.FindIndex(l => l.Slot == listing.Slot && l.ItemId == listing.ItemId);
        if (row < 0)
            return PullFailed(listing, "it is no longer in the sell list");

        var bags = InventorySpace.Player();
        if (bags.FreeSlots == 0
            && !InventorySpace.FitsInAStackAlready(listing.ItemId, listing.IsHq, listing.Quantity))
        {
            refusedForSpace++;

            if (!bagFullReported)
            {
                bagFullReported = true;
                Plugin.Log.Warning(
                    "[pull] your bag is full. Nothing is half-done - the item stays listed and the run " +
                    "carries on repricing. Empty a slot and run it again to pull the rest.");
            }

            return PullFailed(listing, "the bag has no free slot for it");
        }

        currentPull = new PendingPull(listing, row);
        skipPull = false;

        Plugin.Log.Information(
            "[pull] returning {Name}{Hq} x{Quantity} from row {Row} ({Free} free slot(s) left)",
            listing.Name, listing.IsHq ? " (HQ)" : string.Empty, listing.Quantity, row, bags.FreeSlots);

        return true;
    }

    private bool PullFailed(MarketListing listing, string why)
    {
        var index = outcomes.FindIndex(o => o.Listing.Equals(listing));
        if (index >= 0)
            outcomes[index] = outcomes[index] with { Failure = $"not returned: {why}" };

        Plugin.Log.Warning("[pull] {Name}: {Why}, left on the market", listing.Name, why);
        return true;
    }

    private unsafe bool ReturnToInventory()
    {
        var menu = GameUi.Ready(ContextMenuAddon);
        if (menu is null)
            return false;

        var wanted = Plugin.Configuration.ReturnToInventoryMenuEntry;

        foreach (var entry in new AddonMaster.ContextMenu(menu).Entries)
        {
            if (!string.Equals(entry.Text, wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            return entry.Select();
        }

        Plugin.Log.Warning(
            "[pull] the context menu has no \"{Wanted}\" entry; it offers {Offered}",
            wanted, string.Join(", ", new AddonMaster.ContextMenu(menu).Entries.Select(e => e.Text)));

        skipPull = true;
        return true;
    }

    private unsafe bool PullFinished()
    {
        if (skipPull || currentPull is null)
            return true;

        var prompt = GameUi.Ready(PromptAddon);
        if (prompt is not null && !pullPromptAnswered)
        {
            Plugin.Log.Information("[pull] confirming: {Text}", new AddonMaster.SelectYesno(prompt).Text);

            Callback.Fire(prompt, true, 0);

            pullPromptAnswered = true;
            return false;
        }

        var stillListed = RetainerMarketReader.ActiveRetainerListings()
            .Any(l => l.Slot == currentPull.Listing.Slot && l.ItemId == currentPull.Listing.ItemId);

        if (stillListed)
            return false;

        pulledCount++;
        Plugin.Log.Information(
            "[pull] {Name} x{Quantity} is back in the bag, {Free} free slot(s) left",
            currentPull.Listing.Name, currentPull.Listing.Quantity, InventorySpace.Player().FreeSlots);

        currentPull = null;
        return true;
    }

    private bool Finish()
    {
        foreach (var miss in unvisited)
        {
            outcomes.Add(new RepriceOutcome(retainerName, miss, null,
                "no row in the sell list turned out to be this item - it may have sold during the run"));
            Plugin.Log.Warning("[reprice] {Name}: never found in the sell list", miss.Name);
        }

        var pulls = outcomes.Count(o => o.Decision?.Action == PriceAction.ReturnToInventory);
        if (pulls > 0 && !Plugin.Configuration.DryRun && !Plugin.Configuration.AllowReturnToInventory)
        {
            Plugin.Log.Warning(
                "[reprice] {Pulls} item(s) fell below the {Floor:N0} gil floor but returning items " +
                "to the bag is switched off, so they were left untouched.",
                pulls, Plugin.Configuration.Pricing.MinPrice);
        }

        var failures = outcomes.Count(o => o.Failure is not null);

        Plugin.Log.Information("[reprice] {Rule}", Rule);

        stage = "done";
        LastRunWasDry = Plugin.Configuration.DryRun;

        if (Plugin.Configuration.DryRun)
        {
            var writes = outcomes.Count(o => o.Decision?.Action == PriceAction.SetPrice);
            Plugin.Log.Information(
                "[reprice] done: {Writes} to reprice, {Pulls} below floor, {Skips} left alone, " +
                "{Failures} problem(s) (dry run, nothing written)",
                writes, pulls, outcomes.Count - writes - pulls - failures, failures);

            RunFinished?.Invoke();
            return true;
        }

        if (refusedForSpace > 0)
        {
            Plugin.Log.Warning(
                "[pull] {Refused} item(s) stayed on the market for want of a free bag slot",
                refusedForSpace);
        }

        var opened = rowsKnown ? writtenCount : outcomes.Count;
        Plugin.Log.Information(
            "[reprice] done: {Written} price(s) written, {Pulled} of {Pulls} below-floor item(s) " +
            "returned, {Seen} listing(s) seen, {Opened} row(s) opened, {Failures} problem(s)",
            writtenCount, pulledCount, pulls, outcomes.Count, opened, failures);

        RunFinished?.Invoke();
        return true;
    }

    private unsafe bool PriceWindowClosed()
    {
        var prompt = GameUi.Ready(PromptAddon);
        if (prompt is not null && !promptAnswered)
        {
            Plugin.Log.Information("[reprice] confirming: {Text}", new AddonMaster.SelectYesno(prompt).Text);

            Callback.Fire(prompt, true, 0);

            promptAnswered = true;
            return false;
        }

        if (GameUi.IsGone(SellAddon))
            return true;

        if (Environment.TickCount64 - closeRequestedAt <= NudgeAfterMs)
            return false;

        Plugin.Log.Information("[reprice] the price window stayed open, closing it");
        GameUi.Close(SellAddon);
        closeRequestedAt = Environment.TickCount64;
        return false;
    }

    private void Settle()
    {
        var delay = Plugin.Configuration.StepDelayMs;
        if (delay > 0)
            tasks.EnqueueDelay(delay);
    }
}
