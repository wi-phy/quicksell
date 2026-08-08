using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace Quicksell.Services;

public sealed class RequestScheduler : IDisposable
{
    private const long ResponseTimeoutMs = 2_000;

    private const int MaxAttempts = 3;

    private const long WaitingLogEveryMs = 1_000;

    private readonly MarketDataCollector collector;
    private readonly Queue<uint> pending = new();
    private readonly Dictionary<uint, long> inFlight = [];
    private readonly HashSet<uint> answered = [];
    private readonly Dictionary<uint, int> attempts = [];

    private long lastWaitingLog;

    private int sentCount;
    private int completedCount;
    private int lostCount;
    private int retryCount;
    private long totalRoundTrip;
    private bool runActive;
    private bool allSentLogged;

    public RequestScheduler(MarketDataCollector collector)
    {
        this.collector = collector;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public int? DelayOverrideMs { get; set; }

    public int Pending => pending.Count;

    public int InFlight => inFlight.Count;

    public int Answered => completedCount;

    public bool HasAnswered(uint itemId) => answered.Contains(itemId);

    public bool Prioritise(uint itemId)
    {
        if (pending.Count == 0 || pending.Peek() == itemId || !pending.Contains(itemId))
            return false;

        var rest = pending.Where(id => id != itemId).ToList();
        pending.Clear();
        pending.Enqueue(itemId);

        foreach (var id in rest)
            pending.Enqueue(id);

        Plugin.Log.Information("[scheduler] {Name} moved to the front of the queue", Plugin.ItemName(itemId));
        return true;
    }

    public bool IsRunning => runActive;

    private int DelayMs => DelayOverrideMs ?? Plugin.Configuration.MarketRequestDelayMs;

    public void Dispose() => Plugin.Framework.Update -= OnFrameworkUpdate;

    public void BeginRun()
    {
        pending.Clear();
        inFlight.Clear();
        answered.Clear();
        attempts.Clear();

        sentCount = 0;
        completedCount = 0;
        lostCount = 0;
        retryCount = 0;
        totalRoundTrip = 0;
        allSentLogged = false;
        lastWaitingLog = 0;
        runActive = false;
    }

    public bool IsKnown(uint itemId) =>
        answered.Contains(itemId) || inFlight.ContainsKey(itemId) || pending.Contains(itemId);

    public void Enqueue(IEnumerable<uint> itemIds)
    {
        var added = 0;
        foreach (var itemId in itemIds)
        {
            pending.Enqueue(itemId);
            added++;
        }

        if (added == 0)
            return;

        allSentLogged = false;
        runActive = true;

        Plugin.Log.Debug(
            "[scheduler] {Added} item(s) added, queue now {Pending} at a {Delay}ms interval",
            added, pending.Count, DelayMs);
    }

    public void Cancel()
    {
        pending.Clear();
        inFlight.Clear();
        answered.Clear();
        attempts.Clear();
        DelayOverrideMs = null;
        runActive = false;
        Plugin.Log.Information("[scheduler] cancelled");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!runActive)
            return;

        ResolveInFlight();
        SendNext();
        FinishIfIdle();
    }

    private void SendNext()
    {
        if (pending.Count == 0 || inFlight.Count > 0)
            return;

        var itemId = pending.Peek();
        if (!collector.Request(itemId))
        {
            Plugin.Log.Debug("[scheduler] cannot send {Name} yet, retrying next frame", Plugin.ItemName(itemId));
            return;
        }

        pending.Dequeue();
        inFlight[itemId] = Environment.TickCount64;

        var attempt = attempts.GetValueOrDefault(itemId) + 1;
        attempts[itemId] = attempt;

        if (attempt == 1)
        {
            sentCount++;
            Plugin.Log.Debug(
                "[scheduler] sent #{Sent} {Name} (id {ItemId}), {Pending} still queued",
                sentCount, Plugin.ItemName(itemId), itemId, pending.Count);
        }
        else
        {
            Plugin.Log.Information(
                "[scheduler] resent {Name} (attempt {Attempt} of {Max}), {Pending} still queued",
                Plugin.ItemName(itemId), attempt, MaxAttempts, pending.Count);
        }

        if (pending.Count == 0 && !allSentLogged)
        {
            allSentLogged = true;
            Plugin.Log.Information("[scheduler] all {Count} request(s) sent", sentCount);
        }
    }

    private void ResolveInFlight()
    {
        if (inFlight.Count == 0)
            return;

        var now = Environment.TickCount64;

        foreach (var (itemId, sentTick) in inFlight.ToList())
        {
            var name = Plugin.ItemName(itemId);
            var snapshot = collector.TryGet(itemId);
            var sinceFirstPage = snapshot?.SinceFirstOffering ?? -1;

            if (sinceFirstPage >= DelayMs)
            {
                var elapsed = now - sentTick;
                inFlight.Remove(itemId);
                answered.Add(itemId);
                completedCount++;
                totalRoundTrip += elapsed;

                Plugin.Log.Information(
                    "[scheduler] {Name}: {Offers} offer(s) over {Pages} page(s), {Sales} sale(s) " +
                    "- first page after {FirstPage}ms, held {Held}ms, {Elapsed}ms total " +
                    "(game's own proxy: {Proxy})",
                    name, snapshot!.Offerings.Count, snapshot.OfferingPages, snapshot.History.Count,
                    elapsed - sinceFirstPage, sinceFirstPage, elapsed, ProxyEntryCount(itemId));

                if (!snapshot.HasHistory)
                    Plugin.Log.Warning("[scheduler] {Name}: no sale history arrived with it", name);

                continue;
            }

            if (sinceFirstPage < 0 && now - sentTick >= ResponseTimeoutMs)
            {
                inFlight.Remove(itemId);

                if (attempts.GetValueOrDefault(itemId) < MaxAttempts)
                {
                    retryCount++;
                    PushFront(itemId);

                    Plugin.Log.Warning(
                        "[scheduler] {Name}: no offering after {Timeout}ms (history: {History}), " +
                        "asking again",
                        name, ResponseTimeoutMs, snapshot?.HasHistory == true ? "arrived" : "none");

                    continue;
                }

                lostCount++;

                Plugin.Log.Warning(
                    "[scheduler] {Name}: still nothing after {Max} attempt(s), giving up on it",
                    name, MaxAttempts);

                continue;
            }

            if (now - lastWaitingLog < WaitingLogEveryMs)
                continue;

            lastWaitingLog = now;
            Plugin.Log.Debug(
                "[scheduler] waiting on {Name}: {Elapsed}ms since sent, first page {FirstPage}, " +
                "{Pages} page(s), {Offers} offer(s), history {History}",
                name, now - sentTick,
                sinceFirstPage < 0 ? "not yet" : $"{sinceFirstPage}ms ago",
                snapshot?.OfferingPages ?? 0, snapshot?.Offerings.Count ?? 0,
                snapshot?.HasHistory == true ? "in" : "waiting");
        }
    }

    private void PushFront(uint itemId)
    {
        var rest = pending.ToList();
        pending.Clear();
        pending.Enqueue(itemId);

        foreach (var id in rest)
            pending.Enqueue(id);
    }

    private static unsafe string ProxyEntryCount(uint itemId)
    {
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy is null)
            return "unavailable";

        return proxy->SearchItemId == itemId
            ? $"{proxy->EntryCount} entry(ies)"
            : "moved on already";
    }

    private void FinishIfIdle()
    {
        if (pending.Count > 0 || inFlight.Count > 0)
            return;

        var average = completedCount > 0 ? totalRoundTrip / completedCount : 0;
        var verdict = lostCount == 0
            ? "clean, this interval is safe"
            : $"{lostCount} lost, back the interval off";

        Plugin.Log.Information(
            "[scheduler] done at a {Delay}ms interval: {Sent} sent, {Completed} answered " +
            "(avg {Average}ms), {Retries} resent, {Lost} lost - {Verdict}",
            DelayMs, sentCount, completedCount, average, retryCount, lostCount, verdict);

        DelayOverrideMs = null;
        runActive = false;
    }
}
