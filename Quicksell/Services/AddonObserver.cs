using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace Quicksell.Services;

public sealed class AddonObserver : IDisposable
{
    private static readonly string[] Watched =
    [
        "RetainerList",
        "RetainerSellList",
        "RetainerSell",
        "ItemSearchResult",
        "SelectYesno",
        "SelectString",
    ];

    private readonly long startTick = Environment.TickCount64;

    public AddonObserver()
    {
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, Watched, OnPostSetup);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, Watched, OnPreFinalize);
    }

    public bool Enabled { get; set; } = true;

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(OnPostSetup);
        Plugin.AddonLifecycle.UnregisterListener(OnPreFinalize);
    }

    public bool DumpSellList { get; set; }

    private void OnPostSetup(AddonEvent type, AddonArgs args)
    {
        Log("opened", args);

        if (DumpSellList && args.AddonName == "RetainerSellList")
            Plugin.Framework.RunOnTick(SellListProbe.Dump, delayTicks: 2);
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
        Log("closed", args);

        if (args.AddonName != RetainerNavigation.ListAddon)
            return;

        if (Plugin.Repricer.IsRunning || Plugin.Walker.IsRunning)
            return;

        Plugin.Scheduler.BeginRun();
        Plugin.Collector.Clear();
        Plugin.Log.Information("[market] left the bell, the market data gathered there was dropped");
    }

    private void Log(string what, AddonArgs args)
    {
        if (!Enabled)
            return;

        Plugin.Log.Information("[addon] {Name} {What} at t+{Elapsed}ms", args.AddonName, what, Environment.TickCount64 - startTick);
    }
}
