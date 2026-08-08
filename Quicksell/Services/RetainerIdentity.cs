using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Quicksell.Pricing;

namespace Quicksell.Services;

public readonly record struct RetainerInfo(ulong RetainerId, string Name, uint MarketItemCount);

public static class RetainerIdentity
{
    public static unsafe IReadOnlyList<RetainerInfo> List()
    {
        var manager = RetainerManager.Instance();
        if (manager is null)
            return [];

        var result = new List<RetainerInfo>();
        foreach (ref var retainer in manager->Retainers)
        {
            if (retainer.RetainerId == 0)
                continue;

            result.Add(new RetainerInfo(
                retainer.RetainerId,
                retainer.NameString,
                retainer.MarketItemCount));
        }

        return result;
    }

    public static unsafe string ActiveRetainerName()
    {
        var manager = RetainerManager.Instance();
        if (manager is null)
            return string.Empty;

        var active = manager->GetActiveRetainer();
        return active is null ? string.Empty : active->NameString;
    }

    public static RetainerSet Set()
    {
        var retainers = List();
        return new RetainerSet(
            retainers.Select(r => r.RetainerId),
            retainers.Select(r => r.Name));
    }
}
