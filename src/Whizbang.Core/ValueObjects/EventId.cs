using Whizbang.Core;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Uniquely identifies an event within a stream.
/// Uses UUIDv7 (time-ordered, database-friendly) for optimal indexing performance.
/// Uses Medo.Uuid7 for monotonic counter-based generation with guaranteed uniqueness.
/// </summary>
/// <docs>core-concepts/events</docs>
[WhizbangId]
public readonly partial struct EventId;
