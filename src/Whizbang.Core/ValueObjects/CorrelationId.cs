using Whizbang.Core;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Groups related messages together across distributed operations.
/// All messages in a logical workflow share the same CorrelationId.
/// Uses UUIDv7 (time-ordered, database-friendly) for optimal indexing performance.
/// Uses Medo.Uuid7 for monotonic counter-based generation with guaranteed uniqueness.
/// </summary>
[WhizbangId]
public readonly partial struct CorrelationId;
