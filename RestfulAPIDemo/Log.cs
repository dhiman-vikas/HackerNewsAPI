namespace RestfulAPIDemo;

/// <summary>Source-generated, allocation-free log messages for the whole service.</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Best stories refreshed: {ListedIds} ids listed, {UniqueIds} unique, {Stories} stories published, {Excluded} excluded, {Reused} reused from the previous snapshot, {Unresolved} unresolved, {ElapsedMs} ms")]
    public static partial void CycleCompleted(ILogger logger, int listedIds, int uniqueIds, int stories, int excluded, int reused, int unresolved, long elapsedMs);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "Best stories refresh failed: {Reason}. Serving the previous snapshot (age {SnapshotAgeSeconds} s; null = none yet)")]
    public static partial void CycleFailed(ILogger logger, string reason, long? snapshotAgeSeconds);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Warning,
        Message = "Best stories refresh rejected: only {Resolved} of {Total} ids resolved, below the {MinCoveragePercent}% minimum. Serving the previous snapshot (age {SnapshotAgeSeconds} s; null = none yet)")]
    public static partial void CycleRejectedByCoverage(ILogger logger, int resolved, int total, int minCoveragePercent, long? snapshotAgeSeconds);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning,
        Message = "{Count} Hacker News item(s) could not be fetched this cycle and fell back to the previous snapshot where possible: {Ids}")]
    public static partial void ItemsFailed(ILogger logger, int count, string ids);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug,
        Message = "Hacker News item {Id} excluded from the best stories: {Reason}")]
    public static partial void ItemExcluded(ILogger logger, long id, string reason);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error,
        Message = "Best stories refresh cycle crashed unexpectedly; the next cycle runs after the refresh interval")]
    public static partial void CycleCrashed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning,
        Message = "Best stories refresh cycle exceeded RefreshTimeout ({TimeoutSeconds} s); items not yet fetched fall back to the previous snapshot")]
    public static partial void CycleTimedOut(ILogger logger, double timeoutSeconds);

    [LoggerMessage(EventId = 2000, Level = LogLevel.Warning,
        Message = "Hacker News request '{Path}' failed: {Reason}")]
    public static partial void ListRequestFailed(ILogger logger, string path, string reason);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Debug,
        Message = "Hacker News request '{Path}' failed: {Reason}")]
    public static partial void ItemRequestFailed(ILogger logger, string path, string reason);
}
