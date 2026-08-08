using System;
using Dalamud.Configuration;
using Quicksell.Pricing;
using Quicksell.Services;

namespace Quicksell;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 4;

    public int Version { get; set; } = CurrentVersion;

    public PricingConfig Pricing { get; set; } = new();

    public bool DryRun { get; set; } = true;

    public int MarketRequestDelayMs { get; set; } = 3000;

    public bool AllowReturnToInventory { get; set; }

    public int StepDelayMs { get; set; } = 100;

    public bool DumpFixtures { get; set; }

    public string MarketMenuEntry { get; set; } = string.Empty;

    public string AdjustPriceMenuEntry { get; set; } = string.Empty;

    public string ReturnToInventoryMenuEntry { get; set; } = string.Empty;

    public void Migrate()
    {
        if (Version >= CurrentVersion)
            return;

        if (Version < 2 && Pricing.HistoryMaxAgeDays == 7)
        {
            Pricing.HistoryMaxAgeDays = 30;
            Plugin.Log.Information("[config] history window raised from 7 days to 30");
        }

        if (Version < 4 && MarketRequestDelayMs == 4000)
        {
            MarketRequestDelayMs = 3000;
            Plugin.Log.Information("[config] market request interval set back to 3000ms");
        }

        Version = CurrentVersion;
        Save();
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
