namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// The result of a perspective synchronization wait operation.
/// </summary>
/// <param name="Outcome">The outcome of the wait operation.</param>
/// <param name="EventsAwaited">The number of events that were awaited.</param>
/// <param name="ElapsedTime">The time spent waiting.</param>
/// <param name="EventsEmitted">Total events emitted in scope before filtering. Helps distinguish "nothing happened" from "events emitted but not tracked."</param>
/// <param name="EventsTracked">Events found in the singleton tracker for the target perspective. Zero when event types are not registered.</param>
/// <param name="PerspectiveName">The perspective that was waited on, if applicable.</param>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncAwaiterTests.cs</tests>
public readonly record struct SyncResult(
    SyncOutcome Outcome,
    int EventsAwaited,
    TimeSpan ElapsedTime,
    int EventsEmitted = 0,
    int EventsTracked = 0,
    string? PerspectiveName = null);
