using System.Runtime.CompilerServices;
using Vogen;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Uniquely identifies a message within the system.
/// Uses UUIDv7 (time-ordered, database-friendly) for optimal indexing performance.
/// </summary>
[ValueObject<Guid>]
public readonly partial struct MessageId {
  /// <summary>
  /// Creates a new MessageId with a new unique identifier.
  /// Uses UUIDv7 for time-ordered, sequential generation.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static MessageId New() => From(Guid.CreateVersion7());
}
