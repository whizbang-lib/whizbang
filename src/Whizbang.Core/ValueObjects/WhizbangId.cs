namespace Whizbang.Core.ValueObjects;

/// <summary>
/// A generic strongly-typed ID using TrackedGuid as the backing store.
/// Implements IWhizbangId for use with generic API constraints.
/// Enforces UUIDv7 requirement - v4 Guids are rejected.
/// </summary>
/// <docs>core-concepts/whizbang-ids#generic-whizbang-id</docs>
public readonly struct WhizbangId : IWhizbangId, IEquatable<WhizbangId>, IComparable<WhizbangId> {
  private readonly TrackedGuid _tracked;

  /// <summary>
  /// Creates a WhizbangId from a TrackedGuid.
  /// </summary>
  private WhizbangId(TrackedGuid tracked) {
    _tracked = tracked;
  }

  /// <summary>An empty WhizbangId.</summary>
  public static WhizbangId Empty => new(TrackedGuid.Empty);

  /// <summary>
  /// Creates a new WhizbangId using Medo.Uuid7 with sub-millisecond precision.
  /// This is the preferred method for generating new IDs.
  /// </summary>
  public static WhizbangId New() => new(TrackedGuid.NewMedo());

  /// <summary>
  /// Creates a WhizbangId from a TrackedGuid.
  /// Throws if the TrackedGuid is not UUIDv7.
  /// </summary>
  public static WhizbangId From(TrackedGuid tracked) {
    if (!tracked.IsTimeOrdered) {
      throw new ArgumentException(
          "WhizbangId requires UUIDv7 (time-ordered) but received a non-v7 Guid",
          nameof(tracked));
    }
    return new(tracked);
  }

  /// <summary>
  /// Creates a WhizbangId from a raw Guid.
  /// Throws if the Guid is not UUIDv7.
  /// The resulting ID will not have SubMillisecondPrecision (source is unknown).
  /// </summary>
  public static WhizbangId From(Guid value) {
    if (value.Version != 7) {
      throw new ArgumentException(
          $"WhizbangId requires UUIDv7 but received version {value.Version}",
          nameof(value));
    }
    return new(TrackedGuid.FromExternal(value));
  }

  // ========================================
  // IWhizbangId Implementation
  // ========================================

  /// <inheritdoc />
  public Guid ToGuid() => _tracked.Value;

  /// <inheritdoc />
  public bool IsTimeOrdered => _tracked.IsTimeOrdered;

  /// <inheritdoc />
  public bool SubMillisecondPrecision => _tracked.SubMillisecondPrecision;

  /// <inheritdoc />
  public DateTimeOffset Timestamp => _tracked.Timestamp;

  // ========================================
  // Equality
  // ========================================

  /// <summary>Determines whether this ID equals another WhizbangId.</summary>
  public bool Equals(WhizbangId other) => _tracked.Equals(other._tracked);

  /// <summary>Determines whether this ID equals another IWhizbangId.</summary>
  public bool Equals(IWhizbangId? other) =>
      other is not null && ToGuid().Equals(other.ToGuid());

  /// <inheritdoc />
  public override bool Equals(object? obj) =>
      obj is WhizbangId other && Equals(other);

  /// <inheritdoc />
  public override int GetHashCode() => _tracked.GetHashCode();

  /// <summary>Equality operator.</summary>
  public static bool operator ==(WhizbangId left, WhizbangId right) => left.Equals(right);

  /// <summary>Inequality operator.</summary>
  public static bool operator !=(WhizbangId left, WhizbangId right) => !left.Equals(right);

  // ========================================
  // Comparison
  // ========================================

  /// <summary>Compares this ID to another WhizbangId.</summary>
  public int CompareTo(WhizbangId other) => _tracked.CompareTo(other._tracked);

  /// <summary>Compares this ID to another IWhizbangId.</summary>
  public int CompareTo(IWhizbangId? other) =>
      other is null ? 1 : ToGuid().CompareTo(other.ToGuid());

  /// <summary>Less than operator.</summary>
  public static bool operator <(WhizbangId left, WhizbangId right) => left.CompareTo(right) < 0;

  /// <summary>Less than or equal operator.</summary>
  public static bool operator <=(WhizbangId left, WhizbangId right) => left.CompareTo(right) <= 0;

  /// <summary>Greater than operator.</summary>
  public static bool operator >(WhizbangId left, WhizbangId right) => left.CompareTo(right) > 0;

  /// <summary>Greater than or equal operator.</summary>
  public static bool operator >=(WhizbangId left, WhizbangId right) => left.CompareTo(right) >= 0;

  /// <inheritdoc />
  public override string ToString() => _tracked.ToString();
}
