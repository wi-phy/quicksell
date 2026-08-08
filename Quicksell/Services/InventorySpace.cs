using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace Quicksell.Services;

public readonly record struct BagSpace(int FreeSlots, int Bags);

public static class InventorySpace
{
    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    public static unsafe BagSpace Player()
    {
        var inventory = InventoryManager.Instance();
        if (inventory is null)
            return new BagSpace(0, 0);

        var free = 0;
        var bags = 0;

        foreach (var bag in PlayerBags)
        {
            var container = inventory->GetInventoryContainer(bag);
            if (container is null || !container->IsLoaded)
                continue;

            bags++;

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item is null || item->ItemId == 0)
                    free++;
            }
        }

        return new BagSpace(free, bags);
    }

    public static unsafe bool FitsInAStackAlready(uint itemId, bool isHq, uint quantity)
    {
        var stackSize = MaxStack(itemId);
        if (stackSize <= 1)
            return false;

        var inventory = InventoryManager.Instance();
        if (inventory is null)
            return false;

        foreach (var bag in PlayerBags)
        {
            var container = inventory->GetInventoryContainer(bag);
            if (container is null || !container->IsLoaded)
                continue;

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item is null || item->ItemId != itemId)
                    continue;

                if (item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) != isHq)
                    continue;

                if (item->Quantity + quantity <= stackSize)
                    return true;
            }
        }

        return false;
    }

    private static uint MaxStack(uint itemId) =>
        Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var row) ? row.StackSize : 1;
}
