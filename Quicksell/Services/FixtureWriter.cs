using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Quicksell.Services;

public static class FixtureWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Directory =>
        Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "fixtures");

    public static void Write(MarketSnapshot snapshot)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            var payload = new
            {
                snapshot.ItemId,
                ItemName = Plugin.ItemName(snapshot.ItemId),
                CapturedAt = DateTimeOffset.UtcNow,
                Offerings = snapshot.Offerings.ToList(),
                snapshot.History,
                Retainers = RetainerIdentity.List(),
            };

            var path = Path.Combine(
                Directory,
                $"{snapshot.ItemId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

            File.WriteAllText(path, JsonSerializer.Serialize(payload, Options));
            Plugin.Log.Information("[fixture] wrote {Path}", path);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[fixture] failed to write fixture for {ItemId}", snapshot.ItemId);
        }
    }
}
