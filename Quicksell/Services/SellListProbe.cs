using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Quicksell.Services;

public static unsafe class SellListProbe
{
    private const string SellListAddon = "RetainerSellList";

    private const int MaxStringLength = 96;

    private const int MaxDepth = 20;

    public static void Dump()
    {
        Plugin.Log.Information("[probe] ---- sell list dump ----");

        DumpContainer();
        DumpSorter();

        var addon = GameUi.Ready(SellListAddon);
        if (addon is null)
        {
            Plugin.Log.Warning("[probe] the sell list window is not open, so only the data above");
            return;
        }

        Plugin.Log.Information(
            "[probe] window: {Values} value(s), {Nodes} node(s)",
            addon->AtkValuesCount, addon->UldManager.NodeListCount);

        DumpValues(addon);

        Plugin.Log.Information("[probe] drawn text:");
        DumpTextNodes(addon->RootNode, 0);

        Plugin.Log.Information("[probe] ---- end ----");
    }

    private static void DumpContainer()
    {
        var listings = RetainerMarketReader.ActiveRetainerListings();
        Plugin.Log.Information("[probe] container holds {Count} listing(s), in container order:", listings.Count);

        for (var i = 0; i < listings.Count; i++)
        {
            var listing = listings[i];
            Plugin.Log.Information(
                "[probe]   [{Index}] slot {Slot}: {Name}{Hq} x{Qty} at {Price:N0}",
                i, listing.Slot, listing.Name, listing.IsHq ? " (HQ)" : string.Empty,
                listing.Quantity, listing.UnitPrice);
        }
    }

    private static void DumpSorter()
    {
        var module = ItemOrderModule.Instance();
        if (module is null)
        {
            Plugin.Log.Warning("[probe] no item order module");
            return;
        }

        var active = module->ActiveRetainerId;
        Plugin.Log.Information(
            "[probe] item order module: {Count} retainer sorter(s), active retainer {Retainer}",
            module->RetainerSorter.Count, active);

        foreach (var (retainerId, pointer) in module->RetainerSorter)
        {
            var sorter = pointer.Value;
            if (sorter is null)
                continue;

            Plugin.Log.Information(
                "[probe]   retainer {Retainer}{Active}: inventory type {Type} " +
                "(the market container is {Market}), {Count} entry(ies), {PerPage} per page",
                retainerId, retainerId == active ? " (active)" : string.Empty,
                sorter->InventoryType, (int)InventoryType.RetainerMarket,
                sorter->Items.LongCount, sorter->ItemsPerPage);

            if (retainerId != active)
                continue;

            for (var i = 0; i < sorter->Items.LongCount && i < 100; i++)
            {
                var entry = sorter->Items[i].Value;
                if (entry is null)
                    continue;

                Plugin.Log.Information(
                    "[probe]     display {Index} -> page {Page} slot {Slot} (index {Inner}, flags {Flags})",
                    i, entry->Page, entry->Slot, entry->Index, entry->Flags);
            }
        }
    }

    private static void DumpValues(AtkUnitBase* addon)
    {
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            var value = &addon->AtkValues[i];
            var rendered = value->Type switch
            {
                AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString =>
                    Text(value->String),
                AtkValueType.Int => value->Int.ToString(),
                AtkValueType.UInt => value->UInt.ToString(),
                AtkValueType.Bool => value->Byte != 0 ? "true" : "false",

                AtkValueType.Undefined => null,
                _ => $"<{value->Type}>",
            };

            if (rendered is not null)
                Plugin.Log.Information("[probe]   value {Index}: {Type} = {Value}", i, value->Type, rendered);
        }
    }

    private static void DumpTextNodes(AtkResNode* node, int depth)
    {
        if (node is null || depth > MaxDepth)
            return;

        if (node->Type == NodeType.Text)
        {
            var text = Text(((AtkTextNode*)node)->NodeText.StringPtr.Value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                Plugin.Log.Information(
                    "[probe]   {Indent}node {Id}: {Text} (visible {Visible})",
                    new string(' ', depth * 2), node->NodeId, text, node->IsVisible());
            }
        }

        if ((int)node->Type >= 1000)
        {
            var component = ((AtkComponentNode*)node)->Component;
            if (component is not null)
                DumpTextNodes(component->UldManager.RootNode, depth + 1);
        }

        DumpTextNodes(node->ChildNode, depth + 1);

        DumpTextNodes(node->PrevSiblingNode, depth);
    }

    private static string Text(byte* raw)
    {
        if (raw is null)
            return string.Empty;

        var length = 0;
        while (length < MaxStringLength && raw[length] != 0)
            length++;

        return Encoding.UTF8.GetString(raw, length);
    }
}
