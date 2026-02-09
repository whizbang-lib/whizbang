using Medo;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// A Guid wrapper that tracks metadata about its creation source and version.
/// Enables enforcement of UUIDv7 usage and tracking of sub-millisecond precision.
/// </summary>
/// <docs>core-concepts/whizbang-ids#tracked-guid</docs>
public readonly struct TrackedGuid : IEquatable<TrackedGuid>, IComparable<TrackedGuid> {
  private readonly Guid _value;
  private readonly GuidMetadata _metadata;

  /// <summary>
  /// Creates a TrackedGuid with the specified value and metadata.
  /// </summary>
  private TrackedGuid(Guid value, GuidMetadata metadata) {
    _value = value;
    _metadata = metadata;
  }

  /// <summary>Gets the underlying Guid value.</summary>
  public Guid Value => _value;

  /// <summary>Gets the metadata about this Guid's creation source and version.</summary>
  public GuidMetadata Metadata => _metadata;

  /// <summary>Gets whether this Guid is time-ordered (UUIDv7).</summary>
  public bool IsTimeOrdered => (_metadata & GuidMetadata.Version7) != 0;

  /// <summary>
  /// Gets whether this Guid has sub-millisecond precision.
  /// Only true for Medo-generated UUIDs; Microsoft's CreateVersion7() has millisecond precision only.
  /// </summary>
  public bool SubMillisecondPrecision => (_metadata & GuidMetadata.SourceMedo) != 0;

  /// <summary>
  /// Gets whether this TrackedGuid has authoritative metadata from creation.
  /// True when created via NewMedo(), NewMicrosoftV7(), or NewRandom() - we know exactly how it was generated.
  /// False when loaded from external sources (FromExternal, Parse, implicit conversion) where metadata is inferred.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Tracking metadata is only useful at GUID creation time. Once a GUID is serialized
  /// (to database, JSON, etc.) and deserialized, the tracking information is lost.
  /// The deserialized GUID will have <see cref="IsTracking"/> = false.
  /// </para>
  /// <para>
  /// Use this property to check if metadata like <see cref="SubMillisecondPrecision"/>
  /// is authoritative before relying on it.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// var fresh = TrackedGuid.NewMedo();
  /// Console.WriteLine(fresh.IsTracking);           // true
  /// Console.WriteLine(fresh.SubMillisecondPrecision); // true (authoritative)
  ///
  /// var loaded = TrackedGuid.FromExternal(someGuid);
  /// Console.WriteLine(loaded.IsTracking);          // false
  /// Console.WriteLine(loaded.SubMillisecondPrecision); // false (unknown, not authoritative)
  /// </code>
  /// </example>
  public bool IsTracking => (_metadata & (GuidMetadata.SourceMedo | GuidMetadata.SourceMicrosoft)) != 0;

  /// <summary>
  /// Extracts the timestamp from a UUIDv7.
  /// Returns DateTimeOffset.MinValue for non-v7 UUIDs.
  /// </summary>
  public DateTimeOffset Timestamp => _extractTimestamp(_value);

  /// <summary>An empty TrackedGuid.</summary>
  public static TrackedGuid Empty => new(Guid.Empty, GuidMetadata.None);

  // ========================================
  // Factory Methods
  // ========================================

  /// <summary>
  /// Creates a new UUIDv7 using Medo.Uuid7 with sub-millisecond precision.
  /// This is the preferred method for generating new IDs in Whizbang.
  /// </summary>
  public static TrackedGuid NewMedo() =>
      new(Uuid7.NewUuid7().ToGuid(), GuidMetadataExtensions.MEDO_V7);

  /// <summary>
  /// Creates a new UUIDv7 using Microsoft's Guid.CreateVersion7().
  /// Note: This only has millisecond precision. Prefer NewMedo() for better precision.
  /// </summary>
#pragma warning disable WHIZ056 // TrackedGuid wraps Guid.CreateVersion7() intentionally
  public static TrackedGuid NewMicrosoftV7() =>
      new(Guid.CreateVersion7(), GuidMetadataExtensions.MICROSOFT_V7);
#pragma warning restore WHIZ056

  /// <summary>
  /// Creates a new random UUIDv4 using Guid.NewGuid().
  /// Note: v4 is not time-ordered. Prefer NewMedo() for time-ordered IDs.
  /// </summary>
#pragma warning disable WHIZ055 // TrackedGuid wraps Guid.NewGuid() intentionally
  public static TrackedGuid NewRandom() =>
      new(Guid.NewGuid(), GuidMetadataExtensions.MICROSOFT_V4);
#pragma warning restore WHIZ055

  /// <summary>
  /// Parses a Guid from its string representation.
  /// Detects the version from the Guid and marks as SourceParsed.
  /// </summary>
  public static TrackedGuid Parse(string input) {
    var guid = Guid.Parse(input);
    var versionFlag = _detectVersion(guid);
    return new(guid, versionFlag | GuidMetadata.SourceParsed);
  }

  /// <summary>
  /// Tries to parse a Guid from its string representation.
  /// </summary>
  public static bool TryParse(string? input, out TrackedGuid result) {
    if (Guid.TryParse(input, out var guid)) {
      var versionFlag = _detectVersion(guid);
      result = new(guid, versionFlag | GuidMetadata.SourceParsed);
      return true;
    }
    result = default;
    return false;
  }

  /// <summary>
  /// Creates a TrackedGuid from an external Guid (e.g., from database or API).
  /// Detects the version from the Guid and marks as SourceExternal.
  /// </summary>
  public static TrackedGuid FromExternal(Guid existing) {
    var versionFlag = _detectVersion(existing);
    return new(existing, versionFlag | GuidMetadata.SourceExternal);
  }

  // ========================================
  // Conversion Operators
  // ========================================

  /// <summary>
  /// Implicitly converts a TrackedGuid to a Guid.
  /// </summary>
  public static implicit operator Guid(TrackedGuid tracked) => tracked._value;

  /// <summary>
  /// Implicitly converts a raw Guid to a TrackedGuid.
  /// Warning: This loses provenance information. The resulting TrackedGuid
  /// will have SourceUnknown metadata.
  /// </summary>
  public static implicit operator TrackedGuid(Guid value) {
    var versionFlag = _detectVersion(value);
    return new(value, versionFlag | GuidMetadata.SourceUnknown);
  }

  // ========================================
  // Equality & Comparison
  // ========================================

  /// <summary>
  /// Determines whether this TrackedGuid equals another.
  /// Comparison is based on the underlying Guid value only, not metadata.
  /// </summary>
  public bool Equals(TrackedGuid other) => _value.Equals(other._value);

  /// <summary>Determines whether this TrackedGuid equals the specified object.</summary>
  public override bool Equals(object? obj) => obj is TrackedGuid other && Equals(other);

  /// <summary>Returns the hash code for this TrackedGuid.</summary>
  public override int GetHashCode() => _value.GetHashCode();

  /// <summary>
  /// Compares this TrackedGuid to another for ordering.
  /// UUIDv7s are compared chronologically; v4s use standard Guid comparison.
  /// </summary>
  public int CompareTo(TrackedGuid other) => _value.CompareTo(other._value);

  /// <summary>Equality operator.</summary>
  public static bool operator ==(TrackedGuid left, TrackedGuid right) => left.Equals(right);

  /// <summary>Inequality operator.</summary>
  public static bool operator !=(TrackedGuid left, TrackedGuid right) => !left.Equals(right);

  /// <summary>Less than operator.</summary>
  public static bool operator <(TrackedGuid left, TrackedGuid right) => left.CompareTo(right) < 0;

  /// <summary>Less than or equal operator.</summary>
  public static bool operator <=(TrackedGuid left, TrackedGuid right) => left.CompareTo(right) <= 0;

  /// <summary>Greater than operator.</summary>
  public static bool operator >(TrackedGuid left, TrackedGuid right) => left.CompareTo(right) > 0;

  /// <summary>Greater than or equal operator.</summary>
  public static bool operator >=(TrackedGuid left, TrackedGuid right) => left.CompareTo(right) >= 0;

  /// <summary>Returns the string representation of the underlying Guid.</summary>
  public override string ToString() => _value.ToString();

  // ========================================
  // Private Helpers
  // ========================================

  /// <summary>
  /// Detects the version of a Guid (v4, v7, or unknown).
  /// Uses .NET 9's Guid.Version property.
  /// </summary>
  private static GuidMetadata _detectVersion(Guid guid) {
    return guid.Version switch {
      4 => GuidMetadata.Version4,
      7 => GuidMetadata.Version7,
      _ => GuidMetadata.None // Unknown version
    };
  }

  /// <summary>
  /// Extracts the timestamp from a UUIDv7.
  /// UUIDv7 stores the Unix timestamp in milliseconds in the first 48 bits.
  /// </summary>
  private static DateTimeOffset _extractTimestamp(Guid guid) {
    if (guid.Version != 7) {
      return DateTimeOffset.MinValue;
    }

    // Extract first 48 bits (6 bytes) which contain Unix timestamp in ms
    var bytes = guid.ToByteArray();

    // Guid byte order is: time_low (4), time_mid (2), time_hi_and_version (2), clock_seq (2), node (6)
    // For UUIDv7, the first 48 bits are the timestamp
    // But Guid.ToByteArray() returns in little-endian format on Windows
    // We need to handle the byte order correctly

    // Use Medo's Uuid7 for reliable timestamp extraction
    try {
      var uuid7 = new Uuid7(guid);
      return uuid7.ToDateTimeOffset();
    } catch {
      return DateTimeOffset.MinValue;
    }
  }
}
