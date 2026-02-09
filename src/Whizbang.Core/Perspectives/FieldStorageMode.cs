namespace Whizbang.Core.Perspectives;

/// <summary>
/// Defines how physical fields are stored relative to JSONB in a perspective.
/// </summary>
/// <docs>perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/FieldStorageModeTests.cs</tests>
public enum FieldStorageMode {
  /// <summary>
  /// Default mode. No physical fields; all data stored in JSONB column only.
  /// Backwards compatible with existing perspectives.
  /// </summary>
  JsonOnly = 0,

  /// <summary>
  /// JSONB contains the full model (including fields marked with <see cref="PhysicalFieldAttribute"/>).
  /// Physical columns are indexed copies for query optimization.
  /// Use when: You need fast indexed queries but also want full model in JSONB for flexibility.
  /// Trade-off: Slight storage overhead from duplication.
  /// </summary>
  Extracted = 1,

  /// <summary>
  /// Physical columns hold <see cref="PhysicalFieldAttribute"/> values; JSONB holds only remaining fields.
  /// Avoids data duplication but model reconstruction requires reading both sources.
  /// Use when: Storage efficiency is critical or physical columns dominate the model (e.g., vectors).
  /// Trade-off: More complex reads, but cleaner separation and no duplication.
  /// </summary>
  Split = 2
}
