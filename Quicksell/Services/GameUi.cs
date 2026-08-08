using ECommons;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Quicksell.Services;

public static unsafe class GameUi
{
    public static AtkUnitBase* Ready(string name) =>
        GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon) && GenericHelpers.IsAddonReady(addon)
            ? addon
            : null;

    public static bool IsReady(string name) => Ready(name) is not null;

    public static bool IsGone(string name) => !GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out _);

    public static bool Close(string name)
    {
        var addon = Ready(name);
        if (addon is null)
            return false;

        Callback.Fire(addon, true, -1);
        return true;
    }
}
