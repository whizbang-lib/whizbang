using System.Runtime.CompilerServices;
using Vogen;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Uniquely identifies a message within the system.
/// </summary>
[ValueObject<Guid>]
public readonly partial struct MessageId {
  /// <summary>
  /// Creates a new MessageId with a new unique identifier.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static MessageId New() => From(Guid.NewGuid());
}
