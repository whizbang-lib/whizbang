namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Signal sent when a perspective cursor is updated.
/// </summary>
/// <param name="PerspectiveType">The type of the perspective.</param>
/// <param name="StreamId">The stream ID that was processed.</param>
/// <param name="LastEventId">The ID of the last event processed.</param>
/// <param name="Timestamp">The time the cursor was updated.</param>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncSignalerTests.cs</tests>
public readonly record struct PerspectiveCursorSignal(
    Type PerspectiveType,
    Guid StreamId,
    Guid LastEventId,
    DateTimeOffset Timestamp);
