using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.UIHelpers.AddonMasterImplementations;
using Quicksell.Pricing;
using Quicksell.Services;

namespace Quicksell.Windows;

public class DebugWindow : Window, IDisposable
{
    private static readonly Vector4 Grey = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Green = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Yellow = new(0.95f, 0.8f, 0.35f, 1f);
    private static readonly Vector4 Red = new(0.9f, 0.45f, 0.45f, 1f);

    private readonly List<(MarketListing Listing, PriceDecision? Decision)> decisions = [];

    private int itemIdInput = 5057;
    private int myPriceInput = 5000;
    private int myQuantityInput = 10;
    private bool myHqInput;

    private string calibrationItemIds = "5057, 5058, 5059, 5060, 5061";
    private int calibrationInterval = 3000;

    public DebugWindow() : base("Quicksell market inspector###QuicksellDebug")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawRetainers();
        ImGui.Separator();
        DrawWalk();
        ImGui.Separator();
        DrawActiveRetainerMarket();
        ImGui.Separator();
        DrawRequest();
        ImGui.Separator();
        DrawSnapshot();
        ImGui.Separator();
        DrawCalibration();
    }

    private static void DrawRetainers()
    {
        var retainers = RetainerIdentity.List();

        ImGui.TextUnformatted($"Retainers ({retainers.Count})");
        if (retainers.Count == 0)
        {
            Coloured(Grey, "None readable. Log in and open the retainer bell once.");
            return;
        }

        using var indent = ImRaii.PushIndent();
        foreach (var retainer in retainers)
            ImGui.TextUnformatted($"{retainer.Name}  -  {retainer.MarketItemCount} listed");
    }

    private static void DrawWalk()
    {
        ImGui.TextUnformatted("Walk every retainer (read-only)");

        var configured = Plugin.Configuration.MarketMenuEntry;
        if (configured.Length > 0)
            Coloured(Grey, $"Market menu entry: \"{configured}\"");
        else
            Coloured(Yellow, "Market menu entry not set. Open a retainer and pick it below.");

        DrawMenuEntryPicker();

        if (Plugin.Walker.IsRunning)
        {
            Coloured(Yellow, "Walking...");
            if (ImGui.Button("Stop the walk"))
                Plugin.Walker.Abort();
        }
        else if (ImGui.Button("Walk all retainers"))
        {
            Plugin.Walker.Start();
        }

        var collected = Plugin.Walker.Collected;
        if (collected.Count == 0)
            return;

        using var indent = ImRaii.PushIndent();
        foreach (var retainer in collected)
        {
            ImGui.TextUnformatted(
                $"{retainer.RetainerName}: {retainer.Listings.Count} listed, " +
                $"{retainer.Listings.Sum(l => l.UnitPrice * l.Quantity):N0} gil asked");
        }
    }

    private static unsafe void DrawMenuEntryPicker()
    {
        var select = GameUi.Ready("SelectString");
        if (select is null)
            return;

        var entries = new AddonMaster.SelectString(select).Entries;
        if (entries.Length == 0)
            return;

        Coloured(Grey, "A retainer menu is open. Which entry opens the market?");
        using var indent = ImRaii.PushIndent();

        foreach (var entry in entries)
        {
            if (!ImGui.SmallButton($"{entry.Text}##entry{entry.Index}"))
                continue;

            Plugin.Configuration.MarketMenuEntry = entry.Text;
            Plugin.Configuration.Save();
            Plugin.Log.Information("[walk] market menu entry set to \"{Entry}\"", entry.Text);
        }
    }

    private List<MarketListing>? displayOrder;

    private void DrawDisplayOrder()
    {
        if (displayOrder is null)
        {
            Coloured(Grey,
                "\"Read the window's order\" matches our list to the order the game shows. " +
                "When it works, rows that need no change are never opened.");
            return;
        }

        if (displayOrder.Count == 0)
        {
            Coloured(Red, "The window's order could not be read - see the log. The run falls back to opening every row.");
            return;
        }

        ImGui.TextUnformatted($"Window order ({displayOrder.Count} row(s)):");

        using var table = ImRaii.Table("##display-order", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
        if (!table)
            return;

        ImGui.TableSetupColumn("Row");
        ImGui.TableSetupColumn("Slot");
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("Price");
        ImGui.TableHeadersRow();

        for (var row = 0; row < displayOrder.Count; row++)
        {
            var listing = displayOrder[row];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.Slot.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{listing.Name}{(listing.IsHq ? " (HQ)" : string.Empty)}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.Quantity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.UnitPrice.ToString("N0", CultureInfo.InvariantCulture));
        }
    }

    private void DrawActiveRetainerMarket()
    {
        var listings = RetainerMarketReader.ActiveRetainerListings();

        ImGui.TextUnformatted($"Active retainer's market ({listings.Count} listed)");
        if (listings.Count == 0)
        {
            Coloured(Grey, "Open a retainer to load this. Read straight from memory, no window driving needed.");
            return;
        }

        using (var table = ImRaii.Table("##retainer-market", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Slot");
                ImGui.TableSetupColumn("Item");
                ImGui.TableSetupColumn("Qty");
                ImGui.TableSetupColumn("HQ");
                ImGui.TableSetupColumn("My price");
                ImGui.TableSetupColumn("");
                ImGui.TableHeadersRow();

                var row = 0;
                foreach (var listing in listings)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{row} / slot {listing.Slot}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(listing.Name);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(listing.Quantity.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(listing.IsHq ? "HQ" : "");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(listing.UnitPrice.ToString("N0", CultureInfo.InvariantCulture));
                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton($"Inspect##{listing.Slot}"))
                    {
                        itemIdInput = (int)listing.ItemId;
                        myPriceInput = (int)listing.UnitPrice;
                        myQuantityInput = (int)listing.Quantity;
                        myHqInput = listing.IsHq;
                    }

                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Open menu##menu{listing.Slot}"))
                        RetainerRepricer.OpenContextMenu(row);

                    row++;
                }
            }
        }

        DrawContextMenuPicker();
        DrawReprice();

        if (ImGui.Button("Prefetch every listed item"))
        {
            var itemIds = listings.Select(l => l.ItemId).Distinct().ToList();
            Plugin.Log.Information(
                "[prefetch] {Distinct} distinct item(s) from {Total} listing(s) at a {Delay}ms " +
                "interval, about {Seconds}s",
                itemIds.Count, listings.Count, Plugin.Configuration.MarketRequestDelayMs,
                itemIds.Count * Plugin.Configuration.MarketRequestDelayMs / 1000);
            Plugin.Scheduler.Enqueue(itemIds);
        }

        ImGui.SameLine();
        if (ImGui.Button("Decide for all (dry run)"))
            EvaluateAll(listings);

        ImGui.SameLine();
        if (ImGui.Button("Read the window's order"))
            displayOrder = SellListReader.DisplayOrder(listings) ?? [];

        ImGui.SameLine();
        if (ImGui.Button("Dump the sell list"))
            SellListProbe.Dump();

        ImGui.SameLine();
        var dumping = Plugin.AddonObserver.DumpSellList;
        if (ImGui.Checkbox("...on every open", ref dumping))
            Plugin.AddonObserver.DumpSellList = dumping;

        DrawDisplayOrder();

        DrawDecisions();
    }

    private static unsafe void DrawContextMenuPicker()
    {
        var menu = GameUi.Ready("ContextMenu");
        if (menu is null)
            return;

        var entries = new AddonMaster.ContextMenu(menu).Entries;
        if (entries.Length == 0)
            return;

        Coloured(Grey, "A context menu is open. Assign its entries:");
        using var indent = ImRaii.PushIndent();

        foreach (var entry in entries)
        {
            ImGui.TextUnformatted(entry.Text);
            ImGui.SameLine();

            if (ImGui.SmallButton($"adjust price##adjust{entry.Index}"))
            {
                Plugin.Configuration.AdjustPriceMenuEntry = entry.Text;
                Plugin.Configuration.Save();
                Plugin.Log.Information("[reprice] adjust price entry set to \"{Entry}\"", entry.Text);
            }

            ImGui.SameLine();
            if (!ImGui.SmallButton($"return to inventory##return{entry.Index}"))
                continue;

            Plugin.Configuration.ReturnToInventoryMenuEntry = entry.Text;
            Plugin.Configuration.Save();
            Plugin.Log.Information("[reprice] return entry set to \"{Entry}\"", entry.Text);
        }
    }

    private static void DrawReprice()
    {
        var adjust = Plugin.Configuration.AdjustPriceMenuEntry;
        Coloured(adjust.Length > 0 ? Grey : Yellow,
            adjust.Length > 0
                ? $"Adjust price entry: \"{adjust}\""
                : "Adjust price entry not set. Right-click a listed item and assign it.");

        if (Plugin.Repricer.IsRunning)
        {
            Coloured(Yellow, "Repricing...");
            if (ImGui.Button("Stop"))
                Plugin.Repricer.Abort();

            return;
        }

        var dryRun = Plugin.Configuration.DryRun;
        var one = dryRun ? "Reprice this retainer (dry run)" : "Reprice this retainer FOR REAL";
        var all = dryRun ? "Reprice every retainer (dry run)" : "Reprice every retainer FOR REAL";

        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.5f, 0.2f, 0.2f, 1f), !dryRun))
        {
            if (ImGui.Button(one))
                Plugin.Repricer.Start();

            ImGui.SameLine();

            if (ImGui.Button(all))
                Plugin.Repricer.StartAll();
        }

        Coloured(Grey, "\"This retainer\" needs the sell list open. \"Every retainer\" needs the bell list.");

        if (!dryRun)
            Coloured(Red, "Dry run is off. This will write prices.");

        DrawRepriceOutcomes();
    }

    private static void DrawRepriceOutcomes()
    {
        var outcomes = Plugin.Repricer.Outcomes;
        if (outcomes.Count == 0)
            return;

        using var table = ImRaii.Table("##outcomes", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
        if (!table) return;

        ImGui.TableSetupColumn("Retainer");
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Now");
        ImGui.TableSetupColumn("Action");
        ImGui.TableSetupColumn("Detail");
        ImGui.TableHeadersRow();

        foreach (var outcome in outcomes)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(outcome.Retainer);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(outcome.Listing.Name + (outcome.Listing.IsHq ? " (HQ)" : string.Empty));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(outcome.Listing.UnitPrice.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();

            if (outcome.Failure is not null)
            {
                Coloured(Red, "failed");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(outcome.Failure);
                continue;
            }

            if (outcome.Decision is not { } decision)
            {
                Coloured(Yellow, "no data");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted("no market data");
                continue;
            }

            var colour = decision.Action switch
            {
                PriceAction.SetPrice => Green,
                PriceAction.ReturnToInventory => Red,
                _ => Grey,
            };

            Coloured(colour, decision.Action == PriceAction.SetPrice
                ? decision.TargetPrice.ToString("N0", CultureInfo.InvariantCulture)
                : decision.Action.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(decision.Explanation);
        }
    }

    private void EvaluateAll(IReadOnlyList<MarketListing> listings)
    {
        var mine = RetainerIdentity.Set();
        var now = DateTimeOffset.UtcNow;
        decisions.Clear();

        foreach (var listing in listings)
        {
            var snapshot = Plugin.Collector.TryGet(listing.ItemId);
            if (snapshot is null || !snapshot.HasOfferings)
            {
                decisions.Add((listing, null));
                Plugin.Log.Warning("[dry run] {Name}: no market data yet, prefetch first", listing.Name);
                continue;
            }

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
                    MyRetainers = mine,
                },
                Plugin.Configuration.Pricing,
                now);

            decisions.Add((listing, decision));
            Plugin.Log.Information(
                "[dry run] {Name}{Hq}: {Action} - {Explanation}",
                listing.Name, listing.IsHq ? " (HQ)" : string.Empty,
                decision.Action, decision.Explanation);
        }

        var changes = decisions.Count(d => d.Decision?.Action == PriceAction.SetPrice);
        var pulls = decisions.Count(d => d.Decision?.Action == PriceAction.ReturnToInventory);
        Plugin.Log.Information(
            "[dry run] {Total} listed: {Changes} would be repriced, {Pulls} pulled, {Skips} left alone",
            decisions.Count, changes, pulls, decisions.Count - changes - pulls);
    }

    private void DrawDecisions()
    {
        if (decisions.Count == 0)
            return;

        using var table = ImRaii.Table("##decisions", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
        if (!table) return;

        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Now");
        ImGui.TableSetupColumn("Action");
        ImGui.TableSetupColumn("Why");
        ImGui.TableHeadersRow();

        foreach (var (listing, decision) in decisions)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.Name + (listing.IsHq ? " (HQ)" : string.Empty));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.UnitPrice.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();

            if (decision is null)
            {
                Coloured(Yellow, "no data");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted("prefetch first");
                continue;
            }

            var colour = decision.Action switch
            {
                PriceAction.SetPrice => Green,
                PriceAction.ReturnToInventory => Red,
                _ => Grey,
            };

            Coloured(colour, decision.Action == PriceAction.SetPrice
                ? decision.TargetPrice.ToString("N0", CultureInfo.InvariantCulture)
                : decision.Action.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(decision.Explanation);
        }
    }

    private void DrawRequest()
    {
        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("Item id", ref itemIdInput);
        ImGui.SameLine();
        ImGui.TextUnformatted(Plugin.ItemName((uint)Math.Max(0, itemIdInput)));

        if (ImGui.Button("Request (no window)"))
        {
            var itemId = (uint)Math.Max(0, itemIdInput);
            Plugin.Framework.RunOnFrameworkThread(() => Plugin.Collector.Request(itemId));
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear cache"))
            Plugin.Collector.Clear();

        ImGui.SameLine();
        var observing = Plugin.AddonObserver.Enabled;
        if (ImGui.Checkbox("Log window lifecycle", ref observing))
            Plugin.AddonObserver.Enabled = observing;

        Coloured(Grey,
            "If this returns data with no comparison window open, the whole run can prefetch " +
            "prices up front instead of opening a window per item.");
    }

    private void DrawSnapshot()
    {
        var snapshot = Plugin.Collector.TryGet((uint)Math.Max(0, itemIdInput));
        if (snapshot is null)
        {
            Coloured(Grey, "No data for this item yet.");
            return;
        }

        var mine = RetainerIdentity.Set();

        ImGui.TextUnformatted(
            $"Offerings: {snapshot.Offerings.Count} over {snapshot.OfferingPages} page(s)   " +
            $"History: {snapshot.History.Count} sale(s)");

        if (!snapshot.HasHistory)
            Coloured(Yellow, "No history arrived. It may only come from a real market board.");

        DrawOfferings(snapshot, mine);
        DrawHistory(snapshot);
        DrawDecision(snapshot, mine);
    }

    private static void DrawOfferings(MarketSnapshot snapshot, RetainerSet mine)
    {
        if (snapshot.Offerings.Count == 0)
            return;

        using var table = ImRaii.Table("##offerings", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
        if (!table) return;

        ImGui.TableSetupColumn("Unit price");
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("HQ");
        ImGui.TableSetupColumn("Retainer");
        ImGui.TableSetupColumn("Mine?");
        ImGui.TableHeadersRow();

        foreach (var listing in snapshot.Offerings.OrderBy(o => o.PricePerUnit))
        {
            var isMine = mine.IsMine(listing);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.PricePerUnit.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.Quantity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.IsHq ? "HQ" : "");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{listing.RetainerName} ({listing.RetainerId})");
            ImGui.TableNextColumn();
            if (isMine)
                Coloured(Green, "mine");
        }
    }

    private static void DrawHistory(MarketSnapshot snapshot)
    {
        if (snapshot.History.Count == 0)
            return;

        ImGui.Spacing();

        switch (HistoryBasisDetector.Detect(snapshot.History))
        {
            case HistoryBasis.PerUnit:
                Coloured(Green, "SalePrice is a unit price. The engine reads it correctly.");
                break;
            case HistoryBasis.StackTotal:
                Coloured(Red,
                    "SalePrice is a stack total. MarketDataCollector must switch to " +
                    "HistoryEntry.FromTotal, every history-based price is currently inflated.");
                break;
            default:
                Coloured(Grey,
                    "No multi-unit sale here, so the basis cannot be told apart. Inspect a " +
                    "stackable item that trades in bulk.");
                break;
        }

        using var table = ImRaii.Table("##history", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
        if (!table) return;

        ImGui.TableSetupColumn("SalePrice");
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("HQ");
        ImGui.TableSetupColumn("When");
        ImGui.TableHeadersRow();

        foreach (var entry in snapshot.History)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.UnitPrice.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Quantity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.IsHq ? "HQ" : "");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.PurchaseTime.ToLocalTime().ToString("g", CultureInfo.InvariantCulture));
        }
    }

    private void DrawDecision(MarketSnapshot snapshot, RetainerSet mine)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Try the pricing engine against this data");

        ImGui.SetNextItemWidth(140);
        ImGui.InputInt("My price", ref myPriceInput);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.InputInt("My qty", ref myQuantityInput);
        ImGui.SameLine();
        ImGui.Checkbox("HQ", ref myHqInput);

        var context = new ItemContext
        {
            ItemId = snapshot.ItemId,
            ItemName = Plugin.ItemName(snapshot.ItemId),
            IsHq = myHqInput,
            MyUnitPrice = Math.Max(1, myPriceInput),
            MyQuantity = (uint)Math.Max(1, myQuantityInput),
            Offerings = [.. snapshot.Offerings],
            History = snapshot.History,
            MyRetainers = mine,
        };

        var decision = PricingEngine.Decide(context, Plugin.Configuration.Pricing, DateTimeOffset.UtcNow);

        var colour = decision.Action switch
        {
            PriceAction.SetPrice => Green,
            PriceAction.ReturnToInventory => Red,
            _ => Grey,
        };

        Coloured(colour, $"{decision.Action}: {decision.Explanation}");
        Coloured(Grey,
            $"reason {decision.Reason}, reference {decision.ReferencePrice?.ToString("N0", CultureInfo.InvariantCulture) ?? "none"}, " +
            $"{decision.IgnoredOutliers} outlier(s) ignored" +
            (decision.CrashGuardTripped ? ", outlier filter dropped" : string.Empty));
    }

    private void DrawCalibration()
    {
        ImGui.TextUnformatted("Throttle calibration");
        Coloured(Grey,
            "Fires one request per item at the interval below, then reports how many came back " +
            "in /xllog. Lower the interval and re-run until requests start going unanswered: " +
            "that is the server's limit, so keep the configured interval above it. Use distinct " +
            "items that actually trade, and watch for the summary line at the end rather than " +
            "the individual round trips.");

        ImGui.SetNextItemWidth(320);
        ImGui.InputText("Item ids", ref calibrationItemIds, 256);

        ImGui.SetNextItemWidth(200);
        ImGui.SliderInt("Interval (ms)", ref calibrationInterval, 250, 6000);

        if (Plugin.Scheduler.IsRunning)
        {
            Coloured(Yellow,
                $"{Plugin.Scheduler.Pending} queued, {Plugin.Scheduler.InFlight} awaiting a response");
            if (ImGui.Button("Stop"))
                Plugin.Scheduler.Cancel();

            return;
        }

        if (!ImGui.Button("Run calibration"))
            return;

        var ids = ParseItemIds(calibrationItemIds);
        if (ids.Count == 0)
        {
            Plugin.Log.Warning("[calibration] no usable item ids in \"{Input}\"", calibrationItemIds);
            return;
        }

        Plugin.Log.Information(
            "[calibration] {Count} item(s) at a {Interval}ms interval", ids.Count, calibrationInterval);
        Plugin.Scheduler.DelayOverrideMs = calibrationInterval;
        Plugin.Scheduler.Enqueue(ids);
    }

    private static List<uint> ParseItemIds(string input) =>
    [
        .. input
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => uint.TryParse(part, out var id) ? id : 0u)
            .Where(id => id != 0),
    ];

    private static void Coloured(Vector4 colour, string text)
    {
        using var pushed = ImRaii.PushColor(ImGuiCol.Text, colour);
        ImGui.TextWrapped(text);
    }
}
