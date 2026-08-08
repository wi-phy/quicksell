using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.Automation.NeoTaskManager;
using ECommons.UIHelpers.AddonMasterImplementations;

namespace Quicksell.Services;

public readonly record struct RetainerEntry(int Index, string Name);

public static unsafe class RetainerNavigation
{
    public const string ListAddon = "RetainerList";
    public const string MenuAddon = "SelectString";
    public const string SellListAddon = "RetainerSellList";

    private static readonly TaskManagerConfiguration Patient =
        new() { TimeLimitMS = 20_000, AbortOnTimeout = true };

    public static IReadOnlyList<RetainerEntry>? Active()
    {
        var list = GameUi.Ready(ListAddon);
        if (list is null)
            return null;

        return new AddonMaster.RetainerList(list).Retainers
            .Where(r => r.IsActive)
            .Select(r => new RetainerEntry(r.Index, r.Name))
            .ToList();
    }

    public static void QueueVisit(
        TaskManager tasks, RetainerEntry entry, string menuEntry, Action<TaskManager> queueWork)
    {
        tasks.Enqueue(() => GameUi.IsReady(ListAddon), $"wait for the retainer list ({entry.Name})");
        Settle(tasks);
        tasks.Enqueue(() => Select(entry.Index), $"select {entry.Name}");
        tasks.Enqueue(() => GameUi.IsReady(MenuAddon), $"wait for {entry.Name}'s menu", Patient);
        Settle(tasks);
        tasks.Enqueue(() => OpenMarket(menuEntry), $"open {entry.Name}'s market");
        tasks.Enqueue(() => GameUi.IsReady(SellListAddon), $"wait for {entry.Name}'s sell list", Patient);
        Settle(tasks);

        queueWork(tasks);

        tasks.Enqueue(() => GameUi.Close(SellListAddon), $"close {entry.Name}'s sell list");
        tasks.Enqueue(() => GameUi.IsGone(SellListAddon), "wait for the sell list to go away");
        Settle(tasks);
        tasks.Enqueue(() => GameUi.Close(MenuAddon), $"dismiss {entry.Name}");
        tasks.Enqueue(() => GameUi.IsGone(MenuAddon), "wait for the menu to go away");
        Settle(tasks);
    }

    public static void Settle(TaskManager tasks)
    {
        var delay = Plugin.Configuration.StepDelayMs;
        if (delay > 0)
            tasks.EnqueueDelay(delay);
    }

    public static bool Select(int index)
    {
        var list = GameUi.Ready(ListAddon);
        if (list is null)
            return false;

        var retainers = new AddonMaster.RetainerList(list).Retainers;
        if (index >= retainers.Length)
        {
            Plugin.Log.Error("[walk] retainer {Index} vanished from the list", index);
            return true;
        }

        return retainers[index].Select();
    }

    public static bool OpenMarket(string menuEntry)
    {
        var select = GameUi.Ready(MenuAddon);
        if (select is null)
            return false;

        var entries = new AddonMaster.SelectString(select).Entries;
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Text, menuEntry, StringComparison.OrdinalIgnoreCase))
                continue;

            entry.Select();
            return true;
        }

        Plugin.Log.Error(
            "[walk] no menu entry called \"{Entry}\". The menu offers: {Offered}",
            menuEntry, string.Join(" | ", entries.Select(e => e.Text)));
        return true;
    }
}
