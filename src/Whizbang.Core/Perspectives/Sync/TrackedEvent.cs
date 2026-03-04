namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Represents an event that has been emitted and is being tracked for synchronization.
/// </summary>
/// <param name="StreamId">The stream ID the event belongs to.</param>
/// <param name="EventType">The type of the event.</param>
/// <param name="EventId">The unique identifier of the event.</param>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/ScopedEventTrackerTests.cs</tests>
public readonly record struct TrackedEvent(Guid StreamId, Type EventType, Guid EventId);
