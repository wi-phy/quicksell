using System;
using System.Collections.Generic;
using Dalamud.Game.Gui.ContextMenu;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Quicksell.Pricing;
using Item = Lumina.Excel.Sheets.Item;

namespace Quicksell.Services;

public sealed class QuickSeller : IDisposable
{
    public const string MenuLabel = "Vendre (QuickSell)";

    private const string SellListAddon = "RetainerSellList";
    private const string ContextMenuAddon = "ContextMenu";
    private const string SellAddon = "RetainerSell";
    private const string PromptAddon = "SelectYesno";

    private const string HqGlyph = "\uE03C";

    private const long NudgeAfterMs = 2_000;

    private static readonly TaskManagerConfiguration Patient =
        new() { TimeLimitMS = 20_000, AbortOnTimeout = true };

    private static readonly TaskManagerConfiguration WaitingForData =
        new() { TimeLimitMS = 60_000, AbortOnTimeout = true };

    private readonly TaskManager tasks;

    private readonly List<RepriceOutcome> outcomes = [];

    private sealed record Target(InventoryType Container, short Slot, uint ItemId, string Name, bool IsHq, string Addon);

    private Target? target;

    private string retainerName = string.Empty;
    private long suggestedPrice;
    private uint quantity = 1;
    private PriceDecision? decision;
    private string? failure;
    private bool abandoned;
    private bool sold;
    private bool promptAnswered;
    private long closeRequestedAt;

    public QuickSeller()
    {
        tasks = new TaskManager(new TaskManagerConfiguration
        {
            TimeLimitMS = 10_000,
            AbortOnTimeout = true,

            ShowError = true,

            OnTaskTimeout = OnTaskTimeout,
            OnTaskException = OnTaskException,
        });

        Plugin.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    public event Action? Finished;

    public bool IsRunning => tasks.IsBusy;

    public IReadOnlyList<RepriceOutcome> Outcomes => outcomes;

    public void Dispose()
    {
        Plugin.ContextMenu.OnMenuOpened -= OnMenuOpened;
        tasks.Dispose();
    }

    public void Forget() => outcomes.Clear();

    public void Abort()
    {
        tasks.Abort();
        Plugin.Scheduler.Cancel();
        GiveUp("stopped by hand");
    }

    private void OnTaskTimeout(TaskManagerTask task, ref long remainingTimeMS) =>
        GiveUp($"the game never got as far as \"{task.Name}\"");

    private void OnTaskException(
        TaskManagerTask task, Exception exception, ref bool @continue, ref bool? abort) =>
        GiveUp($"\"{task.Name}\" went wrong: {exception.Message}");

    private void GiveUp(string why)
    {
        if (target is null)
            return;

        Abandon(why);
        Finish();
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!Plugin.Configuration.QuickSellFromContextMenu)
            return;

        if (args.MenuType != ContextMenuType.Inventory || args.Target is not MenuTargetInventory inventory)
            return;

        if (inventory.TargetItem is not { } item)
            return;

        var container = (InventoryType)(int)item.ContainerType;
        if (container == InventoryType.RetainerMarket)
            return;

        if (!GameUi.IsReady(SellListAddon) || !IsMarketable(item.ItemId))
            return;

        if (IsRunning || Plugin.Repricer.IsRunning || Plugin.Walker.IsRunning)
            return;

        var slot = (short)item.InventorySlot;
        var itemId = item.ItemId;
        var isHq = item.IsHq;
        var addon = args.AddonName ?? string.Empty;

        args.AddMenuItem(new MenuItem
        {
            Name = MenuLabel,
            PrefixChar = 'Q',
            OnClicked = _ => Start(container, slot, itemId, isHq, addon),
        });
    }

    private static bool IsMarketable(uint itemId) =>
        Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var row)
        && row.ItemSearchCategory.RowId != 0
        && !row.IsUntradable;

    private bool Start(InventoryType container, short slot, uint itemId, bool isHq, string addon)
    {
        if (IsRunning)
        {
            Plugin.Log.Warning("[quicksell] one item is already going up for sale, wait for it");
            return false;
        }

        if (Plugin.Repricer.IsRunning || Plugin.Walker.IsRunning)
        {
            Plugin.Log.Warning("[quicksell] a run is going on, wait for it to finish");
            return false;
        }

        if (string.IsNullOrWhiteSpace(Plugin.Configuration.PutUpForSaleMenuEntry))
        {
            Plugin.Log.Error(
                "[quicksell] the context menu entry that puts an item up for sale has not been set. " +
                "Right-click an item in your bag with the sell list open and pick it in the debug window.");
            return false;
        }

        target = new Target(container, slot, itemId, Plugin.ItemName(itemId), isHq, addon);

        retainerName = RetainerIdentity.ActiveRetainerName();
        suggestedPrice = 0;
        quantity = 1;
        decision = null;
        failure = null;
        abandoned = false;
        sold = false;
        promptAnswered = false;

        Plugin.Log.Information(
            "[quicksell] {Name}{Hq}: asking the market about it and opening the sell window",
            target.Name, isHq ? " (HQ)" : string.Empty);

        Plugin.Scheduler.BeginRun();
        Plugin.Scheduler.Enqueue([itemId]);

        tasks.Enqueue(() => !GameUi.IsReady(ContextMenuAddon), "wait for the menu you clicked to close", Patient);
        Settle();
        tasks.Enqueue(OpenContextMenu, "open the item's context menu");
        tasks.Enqueue(() => abandoned || GameUi.IsReady(ContextMenuAddon), "wait for the context menu", Patient);
        Settle();
        tasks.Enqueue(PutUpForSale, "choose put up for sale");
        tasks.Enqueue(() => abandoned || GameUi.IsReady(SellAddon), "wait for the price window", Patient);
        Settle();
        tasks.Enqueue(Identify, "read the price window");
        tasks.Enqueue(Decide, "decide a price", WaitingForData);
        tasks.Enqueue(SetAskingPrice, "type the price");
        Settle();
        tasks.Enqueue(Confirm, "confirm the sale");
        tasks.Enqueue(PriceWindowClosed, "wait for the price window to close");
        tasks.Enqueue(Finish, "finish");
        return true;
    }

    private void Settle()
    {
        var delay = Plugin.Configuration.StepDelayMs;
        if (delay > 0)
            tasks.EnqueueDelay(delay);
    }

    private unsafe bool OpenContextMenu()
    {
        if (abandoned)
            return true;

        if (!GameUi.IsReady(SellListAddon))
            return Abandon("the retainer's sell list was closed");

        var agent = AgentInventoryContext.Instance();
        if (agent is null)
            return Abandon("the inventory context agent is unavailable");

        var owner = GameUi.Ready(target!.Addon);
        agent->OpenForItemSlot(target.Container, target.Slot, 0, owner is null ? 0u : owner->Id);
        return true;
    }

    private unsafe bool PutUpForSale()
    {
        if (abandoned)
            return true;

        var menu = GameUi.Ready(ContextMenuAddon);
        if (menu is null)
            return false;

        var wanted = Plugin.Configuration.PutUpForSaleMenuEntry;
        var entries = new AddonMaster.ContextMenu(menu).Entries;

        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Text, wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            return entry.Select();
        }

        GameUi.Close(ContextMenuAddon);

        return Abandon(
            $"the item's context menu has no \"{wanted}\" entry; it offers " +
            string.Join(" | ", Array.ConvertAll(entries, e => e.Text)));
    }

    private unsafe bool Identify()
    {
        if (abandoned)
            return true;

        var addon = GameUi.Ready(SellAddon);
        if (addon is null)
            return false;

        var sell = new AddonMaster.RetainerSell(addon);
        var shown = sell.ItemName;

        var isHq = shown.Contains(HqGlyph);
        var name = shown.Replace(HqGlyph, string.Empty).Trim();

        if (!string.Equals(name, target!.Name, StringComparison.Ordinal) || isHq != target.IsHq)
        {
            return Abandon(
                $"the price window holds {name}{(isHq ? " (HQ)" : string.Empty)}, not " +
                $"{target.Name}{(target.IsHq ? " (HQ)" : string.Empty)}");
        }

        suggestedPrice = sell.AskingPrice;
        quantity = (uint)Math.Max(1, sell.Quantity);
        return true;
    }

    private bool Decide()
    {
        if (abandoned || decision is not null)
            return true;

        if (Plugin.Scheduler.IsRunning && !Plugin.Scheduler.HasAnswered(target!.ItemId))
            return false;

        var snapshot = Plugin.Collector.TryGet(target!.ItemId);
        if (snapshot is null || !snapshot.HasOfferings)
            return Abandon("no market data came back, so there is nothing to price against");

        var decided = PricingEngine.Decide(
            new ItemContext
            {
                ItemId = target.ItemId,
                ItemName = target.Name,
                IsHq = target.IsHq,
                MyUnitPrice = suggestedPrice,
                MyQuantity = quantity,
                Offerings = snapshot.Offerings,
                History = snapshot.History,
                MyRetainers = RetainerIdentity.Set(),
            },
            Plugin.Configuration.Pricing,
            DateTimeOffset.UtcNow);

        Plugin.Log.Information(
            "[quicksell] {Name}{Hq}: {Action} - {Explanation}",
            target.Name, target.IsHq ? " (HQ)" : string.Empty, decided.Action, decided.Explanation);

        if (decided.Action == PriceAction.ReturnToInventory)
            return Abandon($"not listed: {decided.Explanation}");

        if (decided.Reason == PriceReason.NoData)
            return Abandon($"not listed: {decided.Explanation}");

        decision = decided.Action == PriceAction.SetPrice
            ? decided
            : decided with { Action = PriceAction.SetPrice };

        return true;
    }

    private unsafe bool SetAskingPrice()
    {
        if (abandoned || decision is null)
            return true;

        var addon = GameUi.Ready(SellAddon);
        if (addon is null)
            return false;

        var sell = new AddonMaster.RetainerSell(addon);
        if (sell.AskingPrice != decision.TargetPrice)
            sell.AskingPrice = (int)decision.TargetPrice;

        return true;
    }

    private unsafe bool Confirm()
    {
        if (abandoned || decision is null)
            return true;

        var addon = GameUi.Ready(SellAddon);
        if (addon is null)
            return false;

        var sell = new AddonMaster.RetainerSell(addon);
        if (sell.AskingPrice != decision.TargetPrice)
            return false;

        Callback.Fire(addon, true, 0);
        closeRequestedAt = Environment.TickCount64;
        sold = true;

        Plugin.Log.Information(
            "[quicksell] {Name} x{Quantity} put up at {Price:N0} gil",
            target!.Name, quantity, decision.TargetPrice);

        return true;
    }

    private unsafe bool PriceWindowClosed()
    {
        var prompt = GameUi.Ready(PromptAddon);
        if (!abandoned && prompt is not null && !promptAnswered)
        {
            Plugin.Log.Information("[quicksell] confirming: {Text}", new AddonMaster.SelectYesno(prompt).Text);

            Callback.Fire(prompt, true, 0);

            promptAnswered = true;
            return false;
        }

        if (GameUi.IsGone(SellAddon))
            return true;

        if (Environment.TickCount64 - closeRequestedAt <= NudgeAfterMs)
            return false;

        Plugin.Log.Information("[quicksell] the price window stayed open, closing it");
        GameUi.Close(SellAddon);
        closeRequestedAt = Environment.TickCount64;
        return false;
    }

    private bool Abandon(string why)
    {
        abandoned = true;
        failure = why;
        closeRequestedAt = Environment.TickCount64;

        Plugin.Log.Warning("[quicksell] {Name}: {Why}", target?.Name ?? "the item", why);

        GameUi.Close(SellAddon);
        return true;
    }

    private bool Finish()
    {
        if (target is null)
            return true;

        outcomes.Add(new RepriceOutcome(
            retainerName,
            new MarketListing(-1, target.ItemId, target.Name, quantity, target.IsHq, suggestedPrice),
            decision,
            failure));

        if (!sold && failure is null)
            outcomes[^1] = outcomes[^1] with { Failure = "the sale was never confirmed" };

        target = null;
        Finished?.Invoke();
        return true;
    }
}
