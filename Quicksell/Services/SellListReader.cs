using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Quicksell.Services;

public static unsafe class SellListReader
{
    private const int CountIndex = 9;

    private const int FirstRow = 10;

    private const int Stride = 13;

    private const int IconOffset = 0;
    private const int QuantityOffset = 2;
    private const int SlotOffset = 5;

    private const int HqIconOffset = 1_000_000;

    public static List<MarketListing>? DisplayOrder(IReadOnlyList<MarketListing> listings)
    {
        var addon = GameUi.Ready("RetainerSellList");
        if (addon is null)
        {
            Plugin.Log.Debug("[selllist] the window is not open, cannot read its order");
            return null;
        }

        var values = addon->AtkValues;
        var available = addon->AtkValuesCount;

        if (values is null || available <= CountIndex)
        {
            Plugin.Log.Warning("[selllist] the window holds only {Count} value(s), too few to read", available);
            return null;
        }

        var rows = values[CountIndex].Int;
        if (rows != listings.Count)
        {
            Plugin.Log.Warning(
                "[selllist] the window shows {Rows} row(s) but the container holds {Listings} listing(s)",
                rows, listings.Count);
            return null;
        }

        var needed = FirstRow + (rows * Stride);
        if (needed > available)
        {
            Plugin.Log.Warning(
                "[selllist] {Rows} row(s) would need {Needed} value(s), the window has {Available}",
                rows, needed, available);
            return null;
        }

        var bySlot = new Dictionary<int, MarketListing>();
        foreach (var listing in listings)
            bySlot[listing.Slot] = listing;

        var ordered = new List<MarketListing>(rows);
        var taken = new HashSet<int>();

        for (var row = 0; row < rows; row++)
        {
            var record = FirstRow + (row * Stride);

            var icon = values[record + IconOffset].Int;
            var slot = values[record + SlotOffset].Int;
            var quantity = values[record + QuantityOffset].Int;
            var isHq = icon >= HqIconOffset;

            if (!bySlot.TryGetValue(slot, out var listing))
            {
                Plugin.Log.Warning("[selllist] row {Row} points at slot {Slot}, which is not listed", row, slot);
                return null;
            }

            if (!taken.Add(slot))
            {
                Plugin.Log.Warning("[selllist] row {Row} points at slot {Slot} again", row, slot);
                return null;
            }

            if (listing.IsHq != isHq || listing.Quantity != quantity)
            {
                Plugin.Log.Warning(
                    "[selllist] row {Row} reads as{Hq} x{Quantity} but slot {Slot} holds " +
                    "{Name}{RealHq} x{RealQuantity}",
                    row, isHq ? " HQ" : " NQ", quantity, slot,
                    listing.Name, listing.IsHq ? " (HQ)" : " (NQ)", listing.Quantity);
                return null;
            }

            ordered.Add(listing);
        }

        Plugin.Log.Information("[selllist] read the display order of {Count} row(s)", ordered.Count);
        for (var row = 0; row < ordered.Count; row++)
        {
            Plugin.Log.Debug(
                "[selllist]   row {Row} = slot {Slot}: {Name}{Hq} at {Price:N0}",
                row, ordered[row].Slot, ordered[row].Name,
                ordered[row].IsHq ? " (HQ)" : string.Empty, ordered[row].UnitPrice);
        }

        return ordered;
    }
}
