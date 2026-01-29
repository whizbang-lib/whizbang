namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Metadata flags for tracking UUID generation provenance.
/// Tracks both the version (v4 vs v7) and the source of the UUID.
/// </summary>
/// <docs>core-concepts/whizbang-ids#guid-metadata</docs>
[Flags]
public enum GuidMetadata : byte {
  /// <summary>No metadata set.</summary>
  None = 0,

  // ========================================
  // UUID Version (bits 0-1)
  // ========================================

  /// <summary>Random UUID (v4) - not time-ordered.</summary>
  Version4 = 1 << 0,

  /// <summary>Time-ordered UUID (v7) - chronologically sortable.</summary>
  Version7 = 1 << 1,

  // ========================================
  // Creation Source (bits 2-5)
  // ========================================

  /// <summary>Created via Medo.Uuid7 - has sub-millisecond precision.</summary>
  SourceMedo = 1 << 2,

  /// <summary>Created via Microsoft's Guid.NewGuid() or Guid.CreateVersion7() - millisecond precision only.</summary>
  SourceMicrosoft = 1 << 3,

  /// <summary>Parsed from a string representation.</summary>
  SourceParsed = 1 << 4,

  /// <summary>Received from an external source (database, API, deserialization).</summary>
  SourceExternal = 1 << 5,

  // ========================================
  // Special Markers (bits 6-7)
  // ========================================

  /// <summary>Implicit conversion from raw Guid - provenance is lost.</summary>
  SourceUnknown = 1 << 6,

  /// <summary>Reserved for future use.</summary>
  Reserved = 1 << 7
}

/// <summary>
/// Internal helper constants for common metadata combinations.
/// </summary>
internal static class GuidMetadataExtensions {
  internal const GuidMetadata MEDO_V7 = GuidMetadata.Version7 | GuidMetadata.SourceMedo;
  internal const GuidMetadata MICROSOFT_V7 = GuidMetadata.Version7 | GuidMetadata.SourceMicrosoft;
  internal const GuidMetadata MICROSOFT_V4 = GuidMetadata.Version4 | GuidMetadata.SourceMicrosoft;
  internal const GuidMetadata EXTERNAL_V7 = GuidMetadata.Version7 | GuidMetadata.SourceExternal;
  internal const GuidMetadata EXTERNAL_V4 = GuidMetadata.Version4 | GuidMetadata.SourceExternal;
  internal const GuidMetadata PARSED_V7 = GuidMetadata.Version7 | GuidMetadata.SourceParsed;
  internal const GuidMetadata PARSED_V4 = GuidMetadata.Version4 | GuidMetadata.SourceParsed;
  internal const GuidMetadata UNKNOWN_V7 = GuidMetadata.Version7 | GuidMetadata.SourceUnknown;
  internal const GuidMetadata UNKNOWN_V4 = GuidMetadata.Version4 | GuidMetadata.SourceUnknown;
}
