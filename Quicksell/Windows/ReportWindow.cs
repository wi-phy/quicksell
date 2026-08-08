using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Quicksell.Pricing;
using Quicksell.Services;

namespace Quicksell.Windows;

public class ReportWindow : Window, IDisposable
{
    private static readonly Vector4 Grey = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Green = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Yellow = new(0.95f, 0.8f, 0.35f, 1f);
    private static readonly Vector4 Red = new(0.9f, 0.45f, 0.45f, 1f);

    private bool problemsOnly;
    private bool hideUntouched;

    public ReportWindow() : base("Quicksell run report###QuicksellReport")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(700, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var outcomes = Plugin.Repricer.Outcomes;

        if (outcomes.Count == 0)
        {
            Coloured(Grey, "No run yet. The report fills in as soon as one finishes.");
            return;
        }

        DrawSummary(outcomes);

        ImGui.Separator();

        ImGui.Checkbox("Hide the ones already at the right price", ref hideUntouched);
        Hint("Listings the engine left alone because nothing needed changing.");

        ImGui.Checkbox("Only what needs attention", ref problemsOnly);
        Hint("Problems, items with no market data, and items pulled below the floor.");

        DrawTable(outcomes);
    }

    private static void DrawSummary(IReadOnlyList<RepriceOutcome> outcomes)
    {
        var planned = outcomes.Count(o => o.Decision?.Action == PriceAction.SetPrice);
        var floor = outcomes.Count(o => o.Decision?.Action == PriceAction.ReturnToInventory);
        var failures = outcomes.Count(o => o.Failure is not null);
        var retainers = outcomes.Select(o => o.Retainer).Distinct().Count();

        if (Plugin.Repricer.LastRunWasDry)
        {
            Coloured(Yellow, "Dry run - nothing was changed in game.");
            ImGui.TextUnformatted(
                $"{outcomes.Count} listing(s) over {retainers} retainer(s): " +
                $"{planned} would be repriced, {floor} are below the floor, " +
                $"{outcomes.Count - planned - floor - failures} would be left alone.");
        }
        else
        {
            ImGui.TextUnformatted(
                $"{outcomes.Count} listing(s) over {retainers} retainer(s): " +
                $"{Plugin.Repricer.Written} price(s) written, " +
                $"{Plugin.Repricer.Pulled} of {floor} below-floor item(s) returned to your bag.");
        }

        if (failures > 0)
            Coloured(Red, $"{failures} item(s) had a problem and were left untouched.");

        if (Plugin.Repricer.RefusedForSpace > 0)
        {
            Coloured(
                Red,
                $"{Plugin.Repricer.RefusedForSpace} item(s) stayed listed because your bag was full. " +
                "Free a slot and run again to pull the rest.");
        }
    }

    private void DrawTable(IReadOnlyList<RepriceOutcome> outcomes)
    {
        var shown = outcomes.Where(Keep).ToList();

        if (shown.Count == 0)
        {
            Coloured(Green, $"Nothing to show. {outcomes.Count} listing(s) hidden by the filters above.");
            return;
        }

        if (shown.Count < outcomes.Count)
            Coloured(Grey, $"{outcomes.Count - shown.Count} of {outcomes.Count} listing(s) hidden.");

        using var table = ImRaii.Table(
            "##report", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.Resizable);

        if (!table) return;

        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, 200f);
        ImGui.TableSetupColumn("Was", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Became", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Why");
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var outcome in shown)
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
                Coloured(Red, "untouched");
                ImGui.TableNextColumn();
                Coloured(Red, outcome.Failure);
                continue;
            }

            if (outcome.Decision is not { } decision)
            {
                Coloured(Yellow, "untouched");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted("no market data came back");
                continue;
            }

            switch (decision.Action)
            {
                case PriceAction.SetPrice:
                    Coloured(Green, decision.TargetPrice.ToString("N0", CultureInfo.InvariantCulture));
                    break;

                case PriceAction.ReturnToInventory:
                    Coloured(Red, "pulled");
                    break;

                default:
                    if (decision.Reason == PriceReason.NoData)
                        Coloured(Yellow, "no data");
                    else
                        Coloured(Grey, "kept");
                    break;
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(decision.Explanation);
        }
    }

    private bool Keep(RepriceOutcome outcome)
    {
        if (outcome.Failure is not null)
            return true;

        var untouched = outcome.Decision is { Action: PriceAction.Skip, Reason: not PriceReason.NoData };
        if (hideUntouched && untouched)
            return false;

        if (!problemsOnly)
            return true;

        return outcome.Decision is null
            || outcome.Decision.Action == PriceAction.ReturnToInventory
            || outcome.Decision.Reason == PriceReason.NoData;
    }

    private static void Hint(string text)
    {
        using var pushed = ImRaii.PushColor(ImGuiCol.Text, Grey);
        using var indent = ImRaii.PushIndent();
        ImGui.TextWrapped(text);
    }

    private static void Coloured(Vector4 colour, string text)
    {
        using var pushed = ImRaii.PushColor(ImGuiCol.Text, colour);
        ImGui.TextWrapped(text);
    }
}
