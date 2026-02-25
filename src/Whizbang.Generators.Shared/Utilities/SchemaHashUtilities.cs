using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/SchemaHashUtilitiesTests.cs</tests>
/// <docs>data/schema-migration</docs>
/// Utilities for canonical JSON serialization and SHA-256 hashing of perspective schemas.
/// Ensures consistent hash generation across platforms for schema drift detection.
///
/// <para>
/// <b>Canonical JSON Rules:</b>
/// <list type="bullet">
/// <item>Sort object keys alphabetically</item>
/// <item>No whitespace (compact format)</item>
/// <item>Lowercase property names (camelCase)</item>
/// <item>Lowercase type names (uuid, jsonb, etc.)</item>
/// <item>Lowercase booleans (true/false)</item>
/// <item>Omit null values</item>
/// <item>UTF-8 encoding</item>
/// </list>
/// </para>
/// </summary>
public static class SchemaHashUtilities {
  private static readonly JsonSerializerOptions _canonicalJsonOptions = new() {
    WriteIndented = false,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };

  /// <summary>
  /// Computes SHA-256 hash of the input string.
  /// Returns lowercase hexadecimal string (64 characters).
  /// </summary>
  /// <param name="input">UTF-8 string to hash</param>
  /// <returns>Lowercase hex SHA-256 hash (64 characters)</returns>
  public static string ComputeHash(string input) {
    var bytes = Encoding.UTF8.GetBytes(input);
    using var sha256 = SHA256.Create();
    var hash = sha256.ComputeHash(bytes);
    // Convert to lowercase hex without dashes (netstandard2.0 compatible)
    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
  }

  /// <summary>
  /// Serializes a perspective table schema to canonical JSON format.
  /// Ensures consistent byte-for-byte output for hash comparison.
  /// </summary>
  /// <param name="schema">Schema to serialize</param>
  /// <returns>Canonical JSON string</returns>
  public static string ToCanonicalJson(PerspectiveTableSchema schema) {
    // Create canonical representation with sorted columns and indexes
    var canonicalSchema = new CanonicalSchema {
      Columns = schema.Columns
          .Select(c => new CanonicalColumn {
            IsPrimaryKey = c.IsPrimaryKey ? true : null,
            IsVector = c.IsVector ? true : null,
            Name = c.Name,
            Nullable = c.Nullable ? true : null,
            Type = c.Type.ToLowerInvariant(),
            VectorDimensions = c.VectorDimensions
          })
          .OrderBy(c => c.Name, StringComparer.Ordinal)
          .ToList(),
      Indexes = schema.Indexes
          .Select(i => new CanonicalIndex {
            Columns = i.Columns.OrderBy(c => c, StringComparer.Ordinal).ToList(),
            IsUnique = i.IsUnique ? true : null,
            Name = i.Name,
            Type = i.Type.ToLowerInvariant()
          })
          .OrderBy(i => i.Name, StringComparer.Ordinal)
          .ToList()
    };

    return JsonSerializer.Serialize(canonicalSchema, _canonicalJsonOptions);
  }

  /// <summary>
  /// Computes SHA-256 hash of a perspective table schema.
  /// Combines ToCanonicalJson and ComputeHash for convenience.
  /// </summary>
  /// <param name="schema">Schema to hash</param>
  /// <returns>Lowercase hex SHA-256 hash (64 characters)</returns>
  public static string ComputeSchemaHash(PerspectiveTableSchema schema) {
    var json = ToCanonicalJson(schema);
    return ComputeHash(json);
  }

  /// <summary>
  /// Internal canonical schema representation for JSON serialization.
  /// Uses nullable booleans so false values can be omitted.
  /// </summary>
  private sealed class CanonicalSchema {
    public List<CanonicalColumn> Columns { get; set; } = [];
    public List<CanonicalIndex> Indexes { get; set; } = [];
  }

  /// <summary>
  /// Internal canonical column representation.
  /// Properties are ordered alphabetically by JSON naming policy.
  /// </summary>
  private sealed class CanonicalColumn {
    public bool? IsPrimaryKey { get; set; }
    public bool? IsVector { get; set; }
    public string Name { get; set; } = "";
    public bool? Nullable { get; set; }
    public string Type { get; set; } = "";
    public int? VectorDimensions { get; set; }
  }

  /// <summary>
  /// Internal canonical index representation.
  /// </summary>
  private sealed class CanonicalIndex {
    public List<string> Columns { get; set; } = [];
    public bool? IsUnique { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
  }
}

/// <summary>
/// Schema record for perspective table columns.
/// Used by SchemaHashUtilities for canonical JSON serialization.
/// </summary>
/// <param name="Name">Column name (e.g., "id", "data")</param>
/// <param name="Type">PostgreSQL column type (lowercase: "uuid", "jsonb", "text", etc.)</param>
/// <param name="Nullable">Whether the column allows NULL values</param>
/// <param name="IsPrimaryKey">Whether this column is the primary key</param>
/// <param name="IsVector">Whether this is a vector column</param>
/// <param name="VectorDimensions">Vector dimensions if IsVector is true, null otherwise</param>
public sealed record ColumnSchema(
    string Name,
    string Type,
    bool Nullable,
    bool IsPrimaryKey,
    bool IsVector,
    int? VectorDimensions);

/// <summary>
/// Schema record for perspective table indexes.
/// </summary>
/// <param name="Name">Index name (e.g., "idx_order_created_at")</param>
/// <param name="Columns">List of column names in the index</param>
/// <param name="Type">Index type (lowercase: "btree", "gin", "ivfflat", "hnsw")</param>
/// <param name="IsUnique">Whether this is a unique index</param>
public sealed record IndexSchema(
    string Name,
    IReadOnlyList<string> Columns,
    string Type,
    bool IsUnique);

/// <summary>
/// Schema record for a complete perspective table.
/// </summary>
/// <param name="Columns">List of columns in the table</param>
/// <param name="Indexes">List of indexes on the table</param>
public sealed record PerspectiveTableSchema(
    IReadOnlyList<ColumnSchema> Columns,
    IReadOnlyList<IndexSchema> Indexes);
