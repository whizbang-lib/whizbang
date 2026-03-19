namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// The result of a perspective synchronization wait operation.
/// </summary>
/// <param name="Outcome">The outcome of the wait operation.</param>
/// <param name="EventsAwaited">The number of events that were awaited.</param>
/// <param name="ElapsedTime">The time spent waiting.</param>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncAwaiterTests.cs</tests>
public readonly record struct SyncResult(
    SyncOutcome Outcome,
    int EventsAwaited,
    TimeSpan ElapsedTime);
