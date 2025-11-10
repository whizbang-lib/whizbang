using System.Runtime.CompilerServices;
using Medo;
using Vogen;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Groups related messages together across distributed operations.
/// All messages in a logical workflow share the same CorrelationId.
/// Uses UUIDv7 (time-ordered, database-friendly) for optimal indexing performance.
/// Uses Medo.Uuid7 for monotonic counter-based generation with guaranteed uniqueness.
/// </summary>
[ValueObject<Guid>]
public readonly partial struct CorrelationId {
  /// <summary>
  /// Creates a new CorrelationId with a new unique identifier.
  /// Uses Medo.Uuid7 for time-ordered, sequential generation with monotonicity guarantees.
  /// Provides at least 2^21 unique IDs per millisecond with a monotonic counter.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static CorrelationId New() => From(Uuid7.NewUuid7().ToGuid());
}
