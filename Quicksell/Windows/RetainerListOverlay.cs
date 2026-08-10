using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Quicksell.Services;

namespace Quicksell.Windows;

public class RetainerListOverlay : Window, IDisposable
{
    private static readonly Vector4 Grey = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Yellow = new(0.95f, 0.8f, 0.35f, 1f);
    private static readonly Vector4 Red = new(0.9f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 DangerButton = new(0.5f, 0.2f, 0.2f, 1f);

    private const float Overlap = 10f;

    private Vector2 anchor;
    private Vector2 pivot;
    private bool everyRetainer;

    public RetainerListOverlay() : base(
        "##QuicksellOverlay",
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize |
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoDocking |
        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus)
    {
        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
    }

    public void Dispose() { }

    public override bool DrawConditions()
    {
        if (!Plugin.Configuration.ShowOverlay)
            return false;

        if (Anchor(RetainerNavigation.ListAddon, out var found, out var corner))
        {
            anchor = found;
            pivot = corner;
            everyRetainer = true;
            return true;
        }

        if (Anchor(RetainerNavigation.SellListAddon, out found, out corner))
        {
            anchor = found;
            pivot = corner;
            everyRetainer = false;
            return true;
        }

        return Plugin.Repricer.IsRunning && anchor != Vector2.Zero;
    }

    public override void PreDraw() => ImGui.SetNextWindowPos(anchor, ImGuiCond.Always, pivot);

    public override void Draw()
    {
        if (Plugin.Repricer.IsRunning)
        {
            DrawProgress();
            return;
        }

        var dryRun = Plugin.Configuration.DryRun;

        var label = everyRetainer
            ? dryRun ? "Reprice all (dry run)" : "Reprice all"
            : dryRun ? "Reprice (dry run)" : "Reprice";

        using (ImRaii.PushColor(ImGuiCol.Button, DangerButton, !dryRun))
        {
            if (ImGui.Button(label))
            {
                var started = everyRetainer ? Plugin.Repricer.StartAll() : Plugin.Repricer.Start();
                if (!started)
                    Plugin.Log.Warning("[reprice] the run did not start - the reason is just above");
                else if (Plugin.Configuration.OpenReportWhenStarting)
                    Plugin.OpenReport();
            }
        }

        if (Missing() is { } missing)
            Coloured(Yellow, missing);

        ImGui.SameLine();
        if (ImGui.SmallButton("Settings"))
            Plugin.OpenConfig();

        ImGui.SameLine();
        ReportToggle();
    }

    private static void DrawProgress()
    {
        var repricer = Plugin.Repricer;

        var name = repricer.CurrentRetainer;

        var where = repricer.RetainerTotal > 1
            ? name.Length > 0
                ? $"retainer {repricer.RetainerIndex} of {repricer.RetainerTotal} ({name})"
                : $"retainer {repricer.RetainerIndex} of {repricer.RetainerTotal}"
            : name;

        Coloured(Yellow, $"repricing: {where}");
        ImGui.SameLine();
        if (ImGui.Button("STOP"))
            repricer.Abort();

        var pending = Plugin.Scheduler.Pending + Plugin.Scheduler.InFlight;
        if (pending > 0)
        {
            var seconds = pending * Plugin.Configuration.MarketRequestDelayMs / 1000;
            Coloured(Grey, $"{pending} market request(s) left, about {seconds}s");
        }

        if (repricer.PullTotal > 0 && repricer.PullIndex > 0)
            Bar(repricer.PullIndex / (float)repricer.PullTotal, $"returning {repricer.PullIndex}/{repricer.PullTotal}");
        else if (repricer.RowTotal > 0)
            Bar(repricer.RowIndex / (float)repricer.RowTotal, $"item {repricer.RowIndex}/{repricer.RowTotal}");

        ReportToggle();
    }

    private static void ReportToggle()
    {
        var open = Plugin.IsReportOpen;

        if (ImGui.SmallButton(open ? "Hide report" : "Show report"))
            Plugin.ToggleReport();
    }

    private static void Bar(float fraction, string label) =>
        ImGui.ProgressBar(fraction, new Vector2(260f, 0f), label);

    private static string? Missing()
    {
        if (string.IsNullOrWhiteSpace(Plugin.Configuration.MarketMenuEntry))
            return "The retainer menu entry that opens the market is not set - see /quicksell debug.";

        if (!Plugin.Configuration.DryRun
            && string.IsNullOrWhiteSpace(Plugin.Configuration.AdjustPriceMenuEntry))
            return "The context menu entry that opens an item's price is not set - see /quicksell debug.";

        return null;
    }

    private static unsafe bool Anchor(string name, out Vector2 position, out Vector2 pivot)
    {
        position = Vector2.Zero;
        pivot = Vector2.Zero;

        var addon = GameUi.Ready(name);
        if (addon is null || !addon->IsVisible)
            return false;

        var left = (float)addon->X;
        var right = left + addon->GetScaledWidth(true);
        var top = (float)addon->Y;
        var bottom = top + addon->GetScaledHeight(true);

        (position, pivot) = Plugin.Configuration.OverlayCorner switch
        {
            OverlayCorner.AboveRight => (new Vector2(right, top + Overlap), new Vector2(1f, 1f)),
            OverlayCorner.BelowLeft => (new Vector2(left, bottom - Overlap), new Vector2(0f, 0f)),
            OverlayCorner.BelowRight => (new Vector2(right, bottom - Overlap), new Vector2(1f, 0f)),
            _ => (new Vector2(left, top + Overlap), new Vector2(0f, 1f)),
        };

        position += new Vector2(Plugin.Configuration.OverlayOffsetX, Plugin.Configuration.OverlayOffsetY);
        return true;
    }

    private static void Coloured(Vector4 colour, string text)
    {
        using var pushed = ImRaii.PushColor(ImGuiCol.Text, colour);
        ImGui.TextUnformatted(text);
    }
}
