using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
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

    public bool IsRunning => tasks.IsBusy;

    public IReadOnlyList<RepriceOutcome> Outcomes => outcomes;

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

        if (Plugin.Configuration.DryRun)
        {
            tasks.Enqueue(
                () => !Plugin.Scheduler.IsRunning,
                "wait for the market data",
                new TaskManagerConfiguration { TimeLimitMS = prefetchBudget, AbortOnTimeout = true });

            tasks.Enqueue(() => DecideAll(listings), "decide");
            return true;
        }

        if (rowsKnown)
            rows.AddRange(listings);
        else
            unvisited.AddRange(listings);

        for (var row = 0; row < listings.Count; row++)
            QueueRow(row);

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
        gateRow = -1;
        waitingRow = -1;
        prepareStartedAt = null;
        retainerName = string.Empty;
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
            Plugin.Log.Warning("[reprice] none of the retainers has anything listed");
            return false;
        }

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

            t.Enqueue(() => FinishRetainer(entry.Name), $"wrap up {entry.Name}");
        });
    }

    private static readonly string Rule = new('-', 60);

    private bool Announce(string name)
    {
        prepareStartedAt = null;

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
        Settle();
        tasks.Enqueue(() => skipCurrent || OpenContextMenu(row), $"open the context menu for row {row}");
        tasks.Enqueue(() => skipCurrent || GameUi.IsReady(ContextMenuAddon), $"wait for row {row}'s context menu", Patient);
        Settle();
        tasks.Enqueue(() => skipCurrent || AdjustPrice(row), $"choose adjust price for row {row}");
        tasks.Enqueue(() => skipCurrent || GameUi.IsReady(SellAddon), $"wait for row {row}'s price window", Patient);
        Settle();
        tasks.Enqueue(() => Identify(row), $"identify row {row}", WaitingForData);
        tasks.Enqueue(SetAskingPrice, $"type the new price for row {row}");
        Settle();
        tasks.Enqueue(ConfirmPrice, $"confirm row {row}");
        tasks.Enqueue(PriceWindowClosed, $"wait for row {row}'s price window to close");
        Settle();
    }

    private bool Begin(int row)
    {
        current = null;
        promptAnswered = false;

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

    private bool Finish()
    {
        foreach (var miss in unvisited)
        {
            outcomes.Add(new RepriceOutcome(retainerName, miss, null,
                "no row in the sell list turned out to be this item - it may have sold during the run"));
            Plugin.Log.Warning("[reprice] {Name}: never found in the sell list", miss.Name);
        }

        var pulls = outcomes.Count(o => o.Decision?.Action == PriceAction.ReturnToInventory);
        if (pulls > 0)
        {
            Plugin.Log.Warning(
                "[reprice] {Pulls} item(s) fell below the {Floor:N0} gil floor. Pulling them off " +
                "the market is not implemented yet, so they were left untouched.",
                pulls, Plugin.Configuration.Pricing.MinPrice);
        }

        var failures = outcomes.Count(o => o.Failure is not null);

        Plugin.Log.Information("[reprice] {Rule}", Rule);

        if (Plugin.Configuration.DryRun)
        {
            var writes = outcomes.Count(o => o.Decision?.Action == PriceAction.SetPrice);
            Plugin.Log.Information(
                "[reprice] done: {Writes} to reprice, {Pulls} below floor, {Skips} left alone, " +
                "{Failures} problem(s) (dry run, nothing written)",
                writes, pulls, outcomes.Count - writes - pulls - failures, failures);

            return true;
        }

        var opened = rowsKnown ? writtenCount : outcomes.Count;
        Plugin.Log.Information(
            "[reprice] done: {Written} price(s) written, {Pulls} below floor, {Seen} listing(s) seen, " +
            "{Opened} row(s) opened, {Failures} problem(s)",
            writtenCount, pulls, outcomes.Count, opened, failures);

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
