using System;
using Dalamud.Game.Command;
using ECommons;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Quicksell.Services;
using Quicksell.Windows;

namespace Quicksell;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    private const string CommandName = "/quicksell";
    private const string CommandAlias = "/qs";

    internal static Configuration Configuration { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("Quicksell");
    private readonly ConfigWindow configWindow;
    private readonly DebugWindow debugWindow;
    private readonly ReportWindow reportWindow;
    private readonly RetainerListOverlay overlay;

    private static Plugin instance = null!;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ECommonsMain.Init(PluginInterface, this);

        Configuration.Migrate();

        Collector = new MarketDataCollector();
        Scheduler = new RequestScheduler(Collector);
        AddonObserver = new AddonObserver();
        Walker = new RetainerWalker();
        Repricer = new RetainerRepricer();

        Collector.SnapshotUpdated += OnSnapshotUpdated;
        Repricer.RunFinished += OnRunFinished;

        configWindow = new ConfigWindow();
        debugWindow = new DebugWindow();
        reportWindow = new ReportWindow();
        overlay = new RetainerListOverlay();
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(debugWindow);
        windowSystem.AddWindow(reportWindow);
        windowSystem.AddWindow(overlay);

        instance = this;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open Quicksell settings. \"/quicksell report\" opens the last run's report, " +
                "\"/quicksell debug\" the market data inspector.",
        });

        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = $"Alias for {CommandName}.",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleDebugUi;

        Log.Information("Quicksell loaded.");
    }

    internal static MarketDataCollector Collector { get; private set; } = null!;

    internal static RequestScheduler Scheduler { get; private set; } = null!;

    internal static AddonObserver AddonObserver { get; private set; } = null!;

    internal static RetainerWalker Walker { get; private set; } = null!;

    internal static RetainerRepricer Repricer { get; private set; } = null!;

    internal static void OpenConfig() => instance.configWindow.IsOpen = true;

    internal static void OpenReport() => instance.reportWindow.IsOpen = true;

    internal static bool IsReportOpen => instance.reportWindow.IsOpen;

    internal static void ToggleReport() => instance.reportWindow.Toggle();

    internal static string ItemName(uint itemId) =>
        DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var row)
            ? row.Name.ToString()
            : $"#{itemId}";

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleDebugUi;

        Collector.SnapshotUpdated -= OnSnapshotUpdated;
        Repricer.RunFinished -= OnRunFinished;

        windowSystem.RemoveAllWindows();
        configWindow.Dispose();
        debugWindow.Dispose();
        reportWindow.Dispose();
        overlay.Dispose();

        Repricer.Dispose();
        Walker.Dispose();
        AddonObserver.Dispose();
        Scheduler.Dispose();
        Collector.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);

        ECommonsMain.Dispose();
    }

    private static void OnSnapshotUpdated(MarketSnapshot snapshot)
    {
        if (!Configuration.DumpFixtures || snapshot is not { HasOfferings: true, HasHistory: true })
            return;

        Framework.RunOnFrameworkThread(() => FixtureWriter.Write(snapshot));
    }

    private void OnRunFinished()
    {
        if (Configuration.OpenReportWhenDone)
            reportWindow.IsOpen = true;
    }

    private void OnCommand(string command, string args)
    {
        var argument = args.Trim();

        if (argument.Equals("debug", StringComparison.OrdinalIgnoreCase))
            debugWindow.Toggle();
        else if (argument.Equals("report", StringComparison.OrdinalIgnoreCase))
            reportWindow.Toggle();
        else
            configWindow.Toggle();
    }

    private void ToggleConfigUi() => configWindow.Toggle();

    private void ToggleDebugUi() => debugWindow.Toggle();
}
