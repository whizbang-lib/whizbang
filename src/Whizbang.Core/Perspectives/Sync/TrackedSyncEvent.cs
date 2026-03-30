namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// A tracked event awaiting perspective sync.
/// </summary>
/// <remarks>
/// <para>
/// This record represents an event that has been emitted and is being tracked
/// for perspective synchronization. Events are tracked from the moment they are
/// emitted (before they reach the database) until they are confirmed processed.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync#tracked-events</docs>
public sealed record TrackedSyncEvent(
    Type EventType,
    Guid EventId,
    Guid StreamId,
    string PerspectiveName,
    DateTime TrackedAt
);
