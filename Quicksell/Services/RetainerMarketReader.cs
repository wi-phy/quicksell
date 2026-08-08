using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Quicksell.Services;

public readonly record struct MarketListing(
    int Slot,
    uint ItemId,
    string Name,
    uint Quantity,
    bool IsHq,
    long UnitPrice);

public static class RetainerMarketReader
{
    public static unsafe IReadOnlyList<MarketListing> ActiveRetainerListings()
    {
        var inventory = InventoryManager.Instance();
        if (inventory is null)
            return [];

        var container = inventory->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container is null || !container->IsLoaded)
            return [];

        var prices = inventory->RetainerMarketPrices;
        var result = new List<MarketListing>();

        for (var slot = 0; slot < container->Size; slot++)
        {
            var item = container->GetInventorySlot(slot);
            if (item is null || item->ItemId == 0)
                continue;

            result.Add(new MarketListing(
                slot,
                item->ItemId,
                Plugin.ItemName(item->ItemId),
                (uint)item->Quantity,
                item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
                slot < prices.Length ? (long)prices[slot] : 0));
        }

        return result;
    }
}
