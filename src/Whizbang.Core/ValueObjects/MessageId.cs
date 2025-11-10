using System.Runtime.CompilerServices;
using Medo;
using Vogen;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Uniquely identifies a message within the system.
/// Uses UUIDv7 (time-ordered, database-friendly) for optimal indexing performance.
/// Uses Medo.Uuid7 for monotonic counter-based generation with guaranteed uniqueness.
/// </summary>
[ValueObject<Guid>]
public readonly partial struct MessageId {
  /// <summary>
  /// Creates a new MessageId with a new unique identifier.
  /// Uses Medo.Uuid7 for time-ordered, sequential generation with monotonicity guarantees.
  /// Provides at least 2^21 unique IDs per millisecond with a monotonic counter.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static MessageId New() => From(Uuid7.NewUuid7().ToGuid());
}
