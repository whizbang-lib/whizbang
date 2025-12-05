using Whizbang.Core;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Uniquely identifies a message within the system.
/// Uses UUIDv7 (time-ordered, database-friendly) for optimal indexing performance.
/// Uses Medo.Uuid7 for monotonic counter-based generation with guaranteed uniqueness.
/// </summary>
[WhizbangId]
public readonly partial struct MessageId;
