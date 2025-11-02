using System.Runtime.CompilerServices;
using Vogen;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Identifies the message that caused this message to be created.
/// Forms a causal chain for event sourcing and distributed tracing.
/// Uses UUIDv7 (time-ordered, database-friendly) for optimal indexing performance.
/// </summary>
[ValueObject<Guid>]
public readonly partial struct CausationId {
  /// <summary>
  /// Creates a new CausationId with a new unique identifier.
  /// Uses UUIDv7 for time-ordered, sequential generation.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static CausationId New() => From(Guid.CreateVersion7());
}
