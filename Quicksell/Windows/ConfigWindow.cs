using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Quicksell.Pricing;
using Quicksell.Services;

namespace Quicksell.Windows;

public class ConfigWindow : Window, IDisposable
{
    public ConfigWindow() : base("Quicksell settings###QuicksellConfig")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    private static Configuration Config => Plugin.Configuration;

    private static PricingConfig Pricing => Config.Pricing;

    public void Dispose() { }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("##quicksell-tabs");
        if (!tabs) return;

        DrawSafetyTab();
        DrawQuickSellTab();
        DrawPositionTab();
        DrawRetainersTab();
        DrawPricingTab();
        DrawOutlierTab();
    }

    private static void DrawRetainersTab()
    {
        using var tab = ImRaii.TabItem("Retainers");
        if (!tab) return;

        ImGui.TextWrapped("Which retainers \"Reprice every retainer\" walks through.");

        Hint("Unticking one only leaves it out of a full run. Its listings still count as yours, " +
             "so the engine will never undercut them. Repricing it by hand from its own sell " +
             "list still works.");

        ImGui.Separator();

        var retainers = RetainerIdentity.List();
        if (retainers.Count == 0)
        {
            Hint("None readable. Log in and open the retainer bell once.");
            DrawForgottenRetainers([]);
            return;
        }

        foreach (var retainer in retainers)
        {
            var included = !Config.IsSkipped(retainer.Name);
            if (ImGui.Checkbox($"{retainer.Name}##retainer{retainer.RetainerId}", ref included))
                Config.SetSkipped(retainer.Name, !included);

            ImGui.SameLine();
            Hint(retainer.MarketItemCount == 0
                ? "nothing listed, skipped either way"
                : $"{retainer.MarketItemCount} listed");
        }

        DrawForgottenRetainers(retainers);
    }

    private static void DrawForgottenRetainers(IReadOnlyList<RetainerInfo> retainers)
    {
        var stale = Config.SkippedRetainers
            .Where(name => !retainers.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (stale.Count == 0)
            return;

        ImGui.Separator();
        Hint($"Left out but not in this character's retainer list: {string.Join(", ", stale)}");

        if (!ImGui.SmallButton("Forget them"))
            return;

        foreach (var name in stale)
            Config.SetSkipped(name, false);
    }

    private static void DrawSafetyTab()
    {
        using var tab = ImRaii.TabItem("Safety");
        if (!tab) return;

        var dryRun = Config.DryRun;
        if (ImGui.Checkbox("Dry run (never write a price)", ref dryRun))
        {
            Config.DryRun = dryRun;
            Config.Save();
        }

        Hint("Decisions are computed and logged to /xllog, but nothing is changed in game.");

        var allowReturn = Config.AllowReturnToInventory;
        if (ImGui.Checkbox("Allow pulling sub-floor items back to inventory", ref allowReturn))
        {
            Config.AllowReturnToInventory = allowReturn;
            Config.Save();
        }

        Hint("Delisting cannot be undone by the plugin, and it needs a free inventory slot.");

        var openReport = Config.OpenReportWhenDone;
        if (ImGui.Checkbox("Show the report when a run ends", ref openReport))
        {
            Config.OpenReportWhenDone = openReport;
            Config.Save();
        }

        Hint("It is also available at any time with /quicksell report.");

        var openReportOnStart = Config.OpenReportWhenStarting;
        if (ImGui.Checkbox("Show the report when a run starts", ref openReportOnStart))
        {
            Config.OpenReportWhenStarting = openReportOnStart;
            Config.Save();
        }

        Hint("The report fills in as the run goes, so you can watch it instead of waiting for the end.");

        var dump = Config.DumpFixtures;
        if (ImGui.Checkbox("Dump market responses as test fixtures", ref dump))
        {
            Config.DumpFixtures = dump;
            Config.Save();
        }

        ImGui.Separator();

        var delay = Config.MarketRequestDelayMs;
        if (ImGui.SliderInt("Market request interval (ms)", ref delay, 500, 6000))
        {
            Config.MarketRequestDelayMs = delay;
            Config.Save();
        }

        Hint("The server throttles market board requests. Being throttled mid-run leaves it " +
             "half finished, so keep this comfortably above whatever the debug window's " +
             "calibration shows as the limit.");

        var stepDelay = Config.StepDelayMs;
        if (ImGui.SliderInt("Pause after each window step (ms)", ref stepDelay, 0, 500))
        {
            Config.StepDelayMs = stepDelay;
            Config.Save();
        }

        Hint("A window can report itself ready a frame before it really is. This costs a couple " +
             "of seconds over a whole run and avoids clicking into nothing.");
    }

    private static void DrawQuickSellTab()
    {
        using var tab = ImRaii.TabItem("Quick sell");
        if (!tab) return;

        ImGui.TextWrapped("Listing one item straight from your bag, without walking a whole retainer.");

        ImGui.Separator();

        var quickSell = Config.QuickSellFromContextMenu;
        if (ImGui.Checkbox($"Offer \"{QuickSeller.MenuLabel}\" when right-clicking a bag item", ref quickSell))
        {
            Config.QuickSellFromContextMenu = quickSell;
            Config.Save();
        }

        Hint("Only while a retainer's sell list is open, and only on items the market board takes. " +
             "It lists the item at one gil under the cheapest competitor and confirms, so dry run " +
             "does not hold it back. Every quick sell lands in the report.");

        ImGui.Separator();

        var fallback = Config.SellToRetainerWhenCancelled;
        if (ImGui.Checkbox("When the price is refused, sell the item to the retainer for gil", ref fallback))
        {
            Config.SellToRetainerWhenCancelled = fallback;
            Config.Save();
        }

        Hint("Below the floor, or no market data at all: rather than leave the item in your bag, " +
             "Quicksell reopens the menu and takes the shop price. That cannot be undone. " +
             "Anything else that goes wrong mid-sale leaves the item alone.");

        var entry = Config.SellToRetainerMenuEntry;
        Hint(entry.Length > 0
            ? $"Menu entry used: \"{entry}\""
            : "No menu entry set yet, so nothing will happen. Right-click a bag item with the sell " +
              "list open and assign it in the debug window (/quicksell debug).");
    }

    private static void DrawPositionTab()
    {
        using var tab = ImRaii.TabItem("Position");
        if (!tab) return;

        ImGui.TextWrapped("Where the Quicksell button sits against the bell's retainer list and " +
                          "a retainer's sell list.");

        Hint("Another plugin's own overlay may already own that corner. Move it out of the way here.");

        ImGui.Separator();

        var overlay = Config.ShowOverlay;
        if (ImGui.Checkbox("Show the Quicksell button beside the retainer windows", ref overlay))
        {
            Config.ShowOverlay = overlay;
            Config.Save();
        }

        using var disabled = ImRaii.Disabled(!overlay);

        var corner = (int)Config.OverlayCorner;
        if (ImGui.Combo("Corner", ref corner, "Above, left\0Above, right\0Below, left\0Below, right\0"))
        {
            Config.OverlayCorner = (OverlayCorner)corner;
            Config.Save();
        }

        var offsetX = Config.OverlayOffsetX;
        if (ImGui.SliderInt("Nudge sideways (px)", ref offsetX, -400, 400))
        {
            Config.OverlayOffsetX = offsetX;
            Config.Save();
        }

        var offsetY = Config.OverlayOffsetY;
        if (ImGui.SliderInt("Nudge up or down (px)", ref offsetY, -400, 400))
        {
            Config.OverlayOffsetY = offsetY;
            Config.Save();
        }

        Hint("Ctrl-click a slider to type an exact value.");

        if (!ImGui.Button("Reset"))
            return;

        Config.OverlayCorner = OverlayCorner.AboveLeft;
        Config.OverlayOffsetX = 0;
        Config.OverlayOffsetY = 0;
        Config.Save();
    }

    private static void DrawPricingTab()
    {
        using var tab = ImRaii.TabItem("Pricing");
        if (!tab) return;

        var undercut = (int)Pricing.UndercutAmount;
        if (ImGui.InputInt("Undercut by (gil)", ref undercut))
        {
            Pricing.UndercutAmount = Math.Max(1, undercut);
            Config.Save();
        }

        var floor = (int)Pricing.MinPrice;
        if (ImGui.InputInt("Price floor (gil)", ref floor))
        {
            Pricing.MinPrice = Math.Max(1, floor);
            Config.Save();
        }

        Hint("Below this an item is not worth a retainer slot, so it gets pulled rather than " +
             "repriced.");

        Hint("An HQ listing is priced against HQ offers only. An NQ listing is priced against " +
             "every offer including HQ, because a buyer facing the same price takes the HQ.");

        ImGui.Separator();
        ImGui.TextUnformatted("When nobody else is selling");

        var strategy = (int)Pricing.NoCompetitionStrategy;
        if (ImGui.Combo("Price from history", ref strategy, "Median\0Top quartile\0Highest sale\0"))
        {
            Pricing.NoCompetitionStrategy = (NoCompetitionStrategy)strategy;
            Config.Save();
        }

        Hint("Highest sale is the greediest, but recent history is full of impulse buys, so it " +
             "can leave an item sitting unsold.");

        var multiplier = (float)Pricing.NoCompetitionMultiplier;
        if (ImGui.SliderFloat("Multiplier", ref multiplier, 0.5f, 2.0f, "%.2f"))
        {
            Pricing.NoCompetitionMultiplier = multiplier;
            Config.Save();
        }

        var maxAge = Pricing.HistoryMaxAgeDays;
        if (ImGui.SliderInt("Ignore sales older than (days)", ref maxAge, 1, 90))
        {
            Pricing.HistoryMaxAgeDays = maxAge;
            Config.Save();
        }

        var minSamples = Pricing.MinHistorySamples;
        if (ImGui.SliderInt("Minimum sales to trust history", ref minSamples, 1, 20))
        {
            Pricing.MinHistorySamples = minSamples;
            Config.Save();
        }
    }

    private static void DrawOutlierTab()
    {
        using var tab = ImRaii.TabItem("Aggressive undercuts");
        if (!tab) return;

        ImGui.TextWrapped(
            "The going rate is the middle of the offers sitting above the cheapest ones. A seller " +
            "far below that is ignored: when the board reads 4, 1800, 2084, 2084, 2094, the 4 is " +
            "somebody being silly and the real price is 1800.");

        ImGui.Separator();

        var ratio = (float)Pricing.OutlierRatio;
        if (ImGui.SliderFloat("Ignore below this share of the going rate", ref ratio, 0.05f, 0.9f, "%.2f"))
        {
            Pricing.OutlierRatio = ratio;
            Config.Save();
        }

        Hint("The going rate is read off the sellers who agree with each other, so neither a " +
             "handful of dumpers nor a couple of dreamers asking a million sets it. Stack sizes " +
             "are not looked at. Sales history, when there is any, only ever vetoes: an offer " +
             "selling at what the item really goes for is never treated as an anomaly.");

        ImGui.Separator();

        var maxOutliers = Pricing.MaxAggressiveUndercuts;
        if (ImGui.SliderInt("Ignore at most this many of them", ref maxOutliers, 0, 10))
        {
            Pricing.MaxAggressiveUndercuts = maxOutliers;
            Config.Save();
        }

        Hint(
            "Past this many sellers below the going rate, they stop being anomalies and become " +
            "the rate: the filter is dropped and we undercut them like anyone else. Otherwise a " +
            "market that has genuinely moved would leave us listed above everybody who sells.");
    }

    private static void Hint(string text)
    {
        using var colour = ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f));
        ImGui.TextWrapped(text);
    }
}
