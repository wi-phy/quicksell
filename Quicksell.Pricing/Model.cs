namespace Quicksell.Pricing;

public sealed record Listing(
    long PricePerUnit,
    uint Quantity,
    bool IsHq,
    ulong RetainerId = 0,
    string RetainerName = "");

public sealed record HistoryEntry(
    long UnitPrice,
    uint Quantity,
    bool IsHq,
    DateTimeOffset PurchaseTime)
{
    public static HistoryEntry FromTotal(long total, uint quantity, bool isHq, DateTimeOffset purchaseTime) =>
        new(quantity == 0 ? total : total / quantity, quantity, isHq, purchaseTime);
}

public sealed class RetainerSet
{
    private readonly HashSet<ulong> ids;
    private readonly HashSet<string> names;

    public RetainerSet(IEnumerable<ulong>? retainerIds = null, IEnumerable<string>? retainerNames = null)
    {
        ids = [.. (retainerIds ?? []).Where(id => id != 0)];
        names = new HashSet<string>(
            (retainerNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n)),
            StringComparer.OrdinalIgnoreCase);
    }

    public static RetainerSet Empty { get; } = new();

    public bool IsMine(Listing listing) =>
        (listing.RetainerId != 0 && ids.Contains(listing.RetainerId)) ||
        (listing.RetainerName.Length > 0 && names.Contains(listing.RetainerName));
}

public sealed record ItemContext
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;

    public bool IsHq { get; init; }

    public long MyUnitPrice { get; init; }

    public uint MyQuantity { get; init; } = 1;

    public IReadOnlyList<Listing> Offerings { get; init; } = [];
    public IReadOnlyList<HistoryEntry> History { get; init; } = [];
    public RetainerSet MyRetainers { get; init; } = RetainerSet.Empty;
}

public enum PriceAction
{
    Skip,
    SetPrice,
    ReturnToInventory,
}

public enum PriceReason
{
    AlreadyOptimal,

    UndercutCompetitor,

    RaisedTowardCompetitor,

    HistoryFallback,

    NoData,

    BelowFloor,
}

public sealed record PriceDecision(
    PriceAction Action,
    long TargetPrice,
    PriceReason Reason,
    string Explanation,
    long? ReferencePrice = null,
    int IgnoredOutliers = 0,
    bool CrashGuardTripped = false);
