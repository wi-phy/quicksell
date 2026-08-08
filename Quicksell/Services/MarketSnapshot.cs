using System;
using System.Collections.Generic;
using System.Linq;
using Quicksell.Pricing;

namespace Quicksell.Services;

public sealed class MarketSnapshot(uint itemId)
{
    private readonly Dictionary<ulong, Listing> listingsById = [];

    public uint ItemId { get; } = itemId;

    public IReadOnlyList<Listing> Offerings { get; private set; } = [];

    public IReadOnlyList<HistoryEntry> History { get; private set; } = [];

    public DateTime? OfferingsReceivedAt { get; private set; }

    public DateTime? HistoryReceivedAt { get; private set; }

    public int OfferingPages { get; private set; }

    public bool HasOfferings => OfferingsReceivedAt is not null;

    public bool HasHistory => HistoryReceivedAt is not null;

    private long lastOfferingTick;

    private long firstOfferingTick;

    public long OfferingsSettledFor =>
        lastOfferingTick == 0 ? 0 : Environment.TickCount64 - lastOfferingTick;

    public long SinceFirstOffering =>
        firstOfferingTick == 0 ? -1 : Environment.TickCount64 - firstOfferingTick;

    public void AddOfferings(IEnumerable<(ulong ListingId, Listing Listing)> listings)
    {
        foreach (var (listingId, listing) in listings)
        {
            listingsById[listingId] = listing;
        }

        Offerings = [.. listingsById.Values];
        OfferingPages++;
        OfferingsReceivedAt = DateTime.UtcNow;
        lastOfferingTick = Environment.TickCount64;

        if (firstOfferingTick == 0)
            firstOfferingTick = lastOfferingTick;
    }

    public void SetHistory(IEnumerable<HistoryEntry> entries)
    {
        History = [.. entries];
        HistoryReceivedAt = DateTime.UtcNow;
    }

    public void Reset()
    {
        listingsById.Clear();
        Offerings = [];
        History = [];
        OfferingsReceivedAt = null;
        HistoryReceivedAt = null;
        OfferingPages = 0;
        lastOfferingTick = 0;
        firstOfferingTick = 0;
    }
}
