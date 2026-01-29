namespace Whizbang.Core;

/// <summary>
/// Interface for strongly-typed ID types in Whizbang.
/// All generated [WhizbangId] types implement this interface.
/// Enables generic constraints on APIs that require IDs with specific properties.
/// </summary>
/// <docs>core-concepts/whizbang-ids</docs>
public interface IWhizbangId : IEquatable<IWhizbangId>, IComparable<IWhizbangId> {
  /// <summary>
  /// Converts this ID to its underlying Guid representation.
  /// </summary>
  Guid ToGuid();

  /// <summary>
  /// Gets whether this ID is time-ordered (UUIDv7).
  /// Time-ordered IDs sort chronologically by creation time.
  /// </summary>
  bool IsTimeOrdered { get; }

  /// <summary>
  /// Gets whether this ID has sub-millisecond precision.
  /// Only true for IDs generated with Medo.Uuid7.
  /// Microsoft's Guid.CreateVersion7() has only millisecond precision.
  /// </summary>
  bool SubMillisecondPrecision { get; }

  /// <summary>
  /// Gets the timestamp embedded in this ID (for UUIDv7).
  /// Returns DateTimeOffset.MinValue for non-v7 IDs.
  /// </summary>
  DateTimeOffset Timestamp { get; }
}
