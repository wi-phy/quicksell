using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Network.Structures;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Quicksell.Pricing;

namespace Quicksell.Services;

public sealed class MarketDataCollector : IDisposable
{
    private readonly Dictionary<uint, MarketSnapshot> snapshots = [];
    private readonly object gate = new();

    private long lastRequestTick;
    private uint lastRequestedItemId;

    public MarketDataCollector()
    {
        Plugin.MarketBoard.OfferingsReceived += OnOfferingsReceived;
        Plugin.MarketBoard.HistoryReceived += OnHistoryReceived;
    }

    public event Action<MarketSnapshot>? SnapshotUpdated;

    public long MillisecondsSinceLastRequest =>
        lastRequestTick == 0 ? long.MaxValue : Environment.TickCount64 - lastRequestTick;

    public void Dispose()
    {
        Plugin.MarketBoard.OfferingsReceived -= OnOfferingsReceived;
        Plugin.MarketBoard.HistoryReceived -= OnHistoryReceived;
    }

    public MarketSnapshot? TryGet(uint itemId)
    {
        lock (gate)
        {
            return snapshots.GetValueOrDefault(itemId);
        }
    }

    public IReadOnlyList<MarketSnapshot> All()
    {
        lock (gate)
        {
            return [.. snapshots.Values];
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            snapshots.Clear();
        }
    }

    public unsafe bool Request(uint itemId)
    {
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy is null)
        {
            Plugin.Log.Warning("[market] InfoProxyItemSearch unavailable, cannot request {ItemId}", itemId);
            return false;
        }

        lock (gate)
        {
            GetOrCreate(itemId).Reset();
        }

        proxy->SearchItemId = itemId;
        proxy->RequestData();
        lastRequestTick = Environment.TickCount64;
        lastRequestedItemId = itemId;

        Plugin.Log.Debug("[market] requested {ItemId} ({Name})", itemId, Plugin.ItemName(itemId));
        return true;
    }

    private MarketSnapshot GetOrCreate(uint itemId)
    {
        if (!snapshots.TryGetValue(itemId, out var snapshot))
        {
            snapshot = new MarketSnapshot(itemId);
            snapshots[itemId] = snapshot;
        }

        return snapshot;
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        var listings = offerings.ItemListings;

        var itemId = listings.Count > 0 ? listings[0].ItemId : lastRequestedItemId;
        if (itemId == 0)
            return;

        MarketSnapshot snapshot;

        lock (gate)
        {
            snapshot = GetOrCreate(itemId);
            snapshot.AddOfferings(listings.Select(l => (
                l.ListingId,
                new Listing(
                    l.PricePerUnit,
                    l.ItemQuantity,
                    l.IsHq,
                    l.RetainerId,
                    l.RetainerName ?? string.Empty))));
        }

        Plugin.Log.Debug(
            "[market] offerings page {Page} for {ItemId} ({Name}): {Count} listing(s), {Total} known",
            snapshot.OfferingPages, itemId, Plugin.ItemName(itemId), listings.Count, snapshot.Offerings.Count);

        SnapshotUpdated?.Invoke(snapshot);
    }

    private void OnHistoryReceived(IMarketBoardHistory history)
    {
        MarketSnapshot snapshot;

        var itemId = history.ItemId != 0 ? history.ItemId : lastRequestedItemId;
        if (itemId == 0)
            return;

        lock (gate)
        {
            snapshot = GetOrCreate(itemId);

            snapshot.SetHistory(history.HistoryListings.Select(h => new HistoryEntry(
                h.SalePrice,
                h.Quantity,
                h.IsHq,
                new DateTimeOffset(DateTime.SpecifyKind(h.PurchaseTime, DateTimeKind.Utc)))));
        }

        Plugin.Log.Debug(
            "[market] history for {ItemId} ({Name}): {Count} sale(s)",
            itemId, Plugin.ItemName(itemId), snapshot.History.Count);

        SnapshotUpdated?.Invoke(snapshot);
    }
}
