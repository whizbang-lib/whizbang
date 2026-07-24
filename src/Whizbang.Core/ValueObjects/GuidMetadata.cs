namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Metadata flags for tracking UUID generation provenance.
/// Tracks both the version (v4 vs v7) and the source of the UUID.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#guid-metadata</docs>
[Flags]
public enum GuidMetadatas : ushort {
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
  Reserved = 1 << 7,

  // ========================================
  // Third-Party Library Sources (bits 8-15)
  // ========================================

  /// <summary>Created via Marten's CombGuidIdGeneration.</summary>
  SourceMarten = 1 << 8,

  /// <summary>Created via UUIDNext library.</summary>
  SourceUuidNext = 1 << 9,

  /// <summary>Created via DaanV2.UUID.Net library.</summary>
  SourceDaanV2 = 1 << 10,

  /// <summary>Created via vanbukin/Uuids library.</summary>
  SourceUuids = 1 << 11,

  /// <summary>Created via GuidOne library.</summary>
  SourceGuidOne = 1 << 12,

  /// <summary>Created via UUID (Taiizor) library.</summary>
  SourceTaiizor = 1 << 13
}

/// <summary>
/// Internal helper constants for common metadata combinations.
/// </summary>
internal static class GuidMetadataExtensions {
  internal const GuidMetadatas MEDO_V7 = GuidMetadatas.Version7 | GuidMetadatas.SourceMedo;
  internal const GuidMetadatas MICROSOFT_V7 = GuidMetadatas.Version7 | GuidMetadatas.SourceMicrosoft;
  internal const GuidMetadatas MICROSOFT_V4 = GuidMetadatas.Version4 | GuidMetadatas.SourceMicrosoft;
  internal const GuidMetadatas EXTERNAL_V7 = GuidMetadatas.Version7 | GuidMetadatas.SourceExternal;
  internal const GuidMetadatas EXTERNAL_V4 = GuidMetadatas.Version4 | GuidMetadatas.SourceExternal;
  internal const GuidMetadatas PARSED_V7 = GuidMetadatas.Version7 | GuidMetadatas.SourceParsed;
  internal const GuidMetadatas PARSED_V4 = GuidMetadatas.Version4 | GuidMetadatas.SourceParsed;
  internal const GuidMetadatas UNKNOWN_V7 = GuidMetadatas.Version7 | GuidMetadatas.SourceUnknown;
  internal const GuidMetadatas UNKNOWN_V4 = GuidMetadatas.Version4 | GuidMetadatas.SourceUnknown;
}
