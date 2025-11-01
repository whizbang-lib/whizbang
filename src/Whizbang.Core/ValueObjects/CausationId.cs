using Vogen;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Identifies the message that caused this message to be created.
/// Forms a causal chain for event sourcing and distributed tracing.
/// </summary>
[ValueObject<Guid>]
public readonly partial struct CausationId {
  /// <summary>
  /// Creates a new CausationId with a new unique identifier.
  /// </summary>
  public static CausationId New() => From(Guid.NewGuid());
}
