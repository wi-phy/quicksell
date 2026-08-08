using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.Automation.NeoTaskManager;
using ECommons.UIHelpers.AddonMasterImplementations;

namespace Quicksell.Services;

public sealed record RetainerListings(string RetainerName, IReadOnlyList<MarketListing> Listings);

public sealed class RetainerWalker : IDisposable
{
    private const string RetainerListAddon = "RetainerList";
    private const string SelectStringAddon = "SelectString";
    private const string SellListAddon = "RetainerSellList";

    private static readonly TaskManagerConfiguration Patient = new() { TimeLimitMS = 20_000, AbortOnTimeout = true };

    private readonly TaskManager tasks = new(new TaskManagerConfiguration
    {
        TimeLimitMS = 10_000,
        AbortOnTimeout = true,

        ShowError = true,
    });

    private const long LoadGraceMs = 5_000;

    private readonly List<RetainerListings> collected = [];

    private long? readStartedAt;

    public bool IsRunning => tasks.IsBusy;

    public IReadOnlyList<RetainerListings> Collected => collected;

    public event Action<IReadOnlyList<RetainerListings>>? Finished;

    public void Dispose() => tasks.Dispose();

    public void Abort()
    {
        tasks.Abort();
        Plugin.Log.Warning("[walk] aborted");
    }

    public unsafe bool Start()
    {
        if (IsRunning)
            return false;

        var menuEntry = Plugin.Configuration.MarketMenuEntry;
        if (string.IsNullOrWhiteSpace(menuEntry))
        {
            Plugin.Log.Error(
                "[walk] the retainer menu entry that opens the market has not been set. Open a " +
                "retainer, then pick it in the debug window - the wording depends on your " +
                "client language, so it cannot be guessed.");
            return false;
        }

        var list = GameUi.Ready(RetainerListAddon);
        if (list is null)
        {
            Plugin.Log.Error("[walk] the retainer list is not open. Use the bell first.");
            return false;
        }

        var retainers = new AddonMaster.RetainerList(list).Retainers;
        var active = retainers.Where(r => r.IsActive).ToList();
        if (active.Count == 0)
        {
            Plugin.Log.Warning("[walk] no selectable retainer in the list");
            return false;
        }

        collected.Clear();

        var expectedByName = RetainerIdentity.List()
            .ToDictionary(r => r.Name, r => r.MarketItemCount, StringComparer.OrdinalIgnoreCase);

        var queued = 0;
        foreach (var entry in active)
        {
            if (!expectedByName.TryGetValue(entry.Name, out var expected))
            {
                Plugin.Log.Warning(
                    "[walk] {Name} is in the list but not in the retainer manager, visiting anyway",
                    entry.Name);
                expected = uint.MaxValue;
            }

            if (expected == 0)
            {
                collected.Add(new RetainerListings(entry.Name, []));
                Plugin.Log.Information("[walk] {Name}: nothing listed, skipped", entry.Name);
                continue;
            }

            QueueRetainer(entry.Index, entry.Name, expected, menuEntry);
            queued++;
        }

        Plugin.Log.Information(
            "[walk] visiting {Queued} of {Total} retainer(s)", queued, active.Count);

        tasks.Enqueue(() => Finished?.Invoke(collected), "report");
        return true;
    }

    private void QueueRetainer(int index, string name, uint expected, string menuEntry)
    {
        RetainerNavigation.QueueVisit(tasks, new RetainerEntry(index, name), menuEntry, t =>
        {
            t.Enqueue(() => { readStartedAt = null; return true; }, $"start reading {name}");
            t.Enqueue(() => ReadListings(expected), $"read {name}'s listings", Patient);
        });
    }

    private bool ReadListings(uint expected)
    {
        readStartedAt ??= Environment.TickCount64;

        var listings = RetainerMarketReader.ActiveRetainerListings();
        var waited = Environment.TickCount64 - readStartedAt.Value;

        if (expected != uint.MaxValue && listings.Count != expected && waited < LoadGraceMs)
            return false;

        var name = RetainerIdentity.ActiveRetainerName();
        collected.Add(new RetainerListings(name, listings));

        if (expected != uint.MaxValue && listings.Count != expected)
        {
            Plugin.Log.Warning(
                "[walk] {Name}: read {Count} listing(s) but the retainer list said {Expected}. " +
                "Items may have sold, or the container had not loaded.",
                name, listings.Count, expected);
        }

        Plugin.Log.Information(
            "[walk] {Name}: {Count} listing(s) worth {Total:N0} gil at current prices, after {Waited}ms",
            name, listings.Count, listings.Sum(l => l.UnitPrice * l.Quantity), waited);

        return true;
    }
}
