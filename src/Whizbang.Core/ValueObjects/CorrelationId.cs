using System.Runtime.CompilerServices;
using Vogen;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Groups related messages together across distributed operations.
/// All messages in a logical workflow share the same CorrelationId.
/// </summary>
[ValueObject<Guid>]
public readonly partial struct CorrelationId {
  /// <summary>
  /// Creates a new CorrelationId with a new unique identifier.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static CorrelationId New() => From(Guid.NewGuid());
}
