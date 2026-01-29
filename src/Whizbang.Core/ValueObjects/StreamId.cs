using Whizbang.Core;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Uniquely identifies an event stream within the system.
/// Uses UUIDv7 (time-ordered, database-friendly) for optimal indexing performance.
/// Uses Medo.Uuid7 for monotonic counter-based generation with guaranteed uniqueness.
/// </summary>
/// <docs>core-concepts/event-streams</docs>
[WhizbangId]
public readonly partial struct StreamId;
