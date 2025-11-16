using System.Text;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Data;

/// <summary>
/// Abstraction for splitting domain objects into JSONB columns for PostgreSQL persistence.
/// Hides the 3-column pattern (data/metadata/scope) from developers.
/// Implementations handle serialization and size calculation in C#.
/// </summary>
/// <typeparam name="T">The type to be persisted (event envelope or perspective model)</typeparam>
public interface IJsonbPersistenceAdapter<T> {
  /// <summary>
  /// Splits an object into JSONB-compatible components for persistence.
  /// Calculates sizes in C# (not stored unless threshold crossed).
  /// </summary>
  /// <param name="source">The object to split into JSONB columns</param>
  /// <param name="policyConfig">Optional policy configuration for size validation</param>
  /// <returns>JSONB persistence model with data/metadata/scope JSON strings</returns>
  JsonbPersistenceModel ToJsonb(T source, PolicyConfiguration? policyConfig = null);

  /// <summary>
  /// Reconstructs an object from JSONB components.
  /// </summary>
  /// <param name="jsonb">The JSONB persistence model to reconstruct from</param>
  /// <returns>The reconstructed domain object</returns>
  T FromJsonb(JsonbPersistenceModel jsonb);
}

/// <summary>
/// Container for the 3-column JSONB pattern.
/// Includes C#-calculated size properties (not stored unless threshold crossed).
/// </summary>
public record JsonbPersistenceModel {
  /// <summary>
  /// Event data or model data (3-6 KiB typical).
  /// </summary>
  public string DataJson { get; init; } = string.Empty;

  /// <summary>
  /// Metadata including correlation, causation, hops (800 bytes - 1.5 KiB typical).
  /// May contain __size_warning if threshold was crossed.
  /// </summary>
  public string MetadataJson { get; init; } = string.Empty;

  /// <summary>
  /// Scope information: tenant, user, org (200-500 bytes typical).
  /// Optional.
  /// </summary>
  public string? ScopeJson { get; init; }

  /// <summary>
  /// Calculated size of data JSON in bytes (C# calculation, not stored).
  /// </summary>
  public int DataSizeBytes => Encoding.UTF8.GetByteCount(DataJson);

  /// <summary>
  /// Calculated size of metadata JSON in bytes (C# calculation, not stored).
  /// </summary>
  public int MetadataSizeBytes => Encoding.UTF8.GetByteCount(MetadataJson);

  /// <summary>
  /// Calculated size of scope JSON in bytes (C# calculation, not stored).
  /// </summary>
  public int ScopeSizeBytes => ScopeJson != null ? Encoding.UTF8.GetByteCount(ScopeJson) : 0;

  /// <summary>
  /// Total calculated size across all JSONB columns (C# calculation, not stored).
  /// </summary>
  public int TotalSizeBytes => DataSizeBytes + MetadataSizeBytes + ScopeSizeBytes;
}
