// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// One perspective's entry in the registry payload: the model it folds, the table that holds it, and what
/// the table is built with.
/// </summary>
/// <param name="ClrTypeName">The model's CLR type name (<c>Outer+Model</c> for a nested model), the registry key.</param>
/// <param name="TableName">The perspective table.</param>
/// <param name="PerspectiveName">The perspective's CLR type name, the form its cursors and snapshots key on.</param>
/// <param name="PhysicalFields">The physical columns the table carries.</param>
/// <param name="BuildsDataIndex">Whether the table carries the index over its whole document.</param>
/// <param name="BuildsMetadataIndex">Whether the table carries the index over its metadata.</param>
public sealed record PerspectiveRegistryEntry(
    string ClrTypeName,
    string TableName,
    string PerspectiveName,
    IReadOnlyList<PhysicalFieldInfo> PhysicalFields,
    bool BuildsDataIndex,
    bool BuildsMetadataIndex);

/// <summary>
/// The JSON payload <c>reconcile_perspective_registry</c> reads: one entry per perspective, keyed by the model's
/// CLR type name, with the table schema and its hash. Every driver's generator builds it here, so a table's hash
/// is the same whichever driver registered it.
/// </summary>
/// <docs>fundamentals/perspectives/row-retention</docs>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/PerspectiveRegistryPayloadTests.cs</tests>
public static class PerspectiveRegistryPayload {
  private const string TIMESTAMPTZ = "timestamptz";
  private const string PERSPECTIVE_TABLE_PREFIX = "wh_per_";

  /// <summary>
  /// The payload for <paramref name="entries"/>, as a JSON array.
  /// </summary>
  /// <param name="entries">The perspectives to register.</param>
  /// <param name="serviceName">The service that owns them (the assembly name).</param>
  /// <param name="columnType">The PostgreSQL type the table DDL gives a physical field.</param>
  /// <returns>The JSON array; <c>[]</c> when there are no entries.</returns>
  public static string ToJson(
      IReadOnlyList<PerspectiveRegistryEntry> entries, string serviceName, Func<PhysicalFieldInfo, string> columnType) {
    var sb = new StringBuilder();
    sb.Append('[');
    for (var i = 0; i < entries.Count; i++) {
      if (i > 0) {
        sb.Append(',');
      }
      _appendEntry(sb, entries[i], serviceName, columnType);
    }
    sb.Append(']');
    return sb.ToString();
  }

  private static void _appendEntry(
      StringBuilder sb, PerspectiveRegistryEntry entry, string serviceName, Func<PhysicalFieldInfo, string> columnType) {
    var tableSchema = new PerspectiveTableSchema(Columns(entry, columnType), Indexes(entry));
    var schemaJson = SchemaHashUtilities.ToCanonicalJson(tableSchema);
    var schemaHash = SchemaHashUtilities.ComputeSchemaHash(tableSchema);

    sb.Append('{');
    // The registry key is the CLR form (Outer+Model), the same form the runtime looks it up by
    // (TypeNameFormatter.FormatClrTypeName). A display-string rendering (Outer.Model) matched
    // nothing for nested models and left row retention silently un-enrolled (issue #697).
    sb.Append($"\"ClrTypeName\":\"{EscapeJsonString(entry.ClrTypeName)}\",");
    sb.Append($"\"TableName\":\"{EscapeJsonString(entry.TableName)}\",");
    sb.Append($"\"SchemaJson\":{schemaJson},");
    sb.Append($"\"SchemaHash\":\"{schemaHash}\",");
    sb.Append($"\"ServiceName\":\"{EscapeJsonString(serviceName)}\",");
    // The perspective that folds this model, in the CLR form its cursors and snapshots are keyed by
    // (TypeNameUtilities.BuildClrTypeName at discovery). One entry per perspective, so a model folded by
    // two perspectives names both; the reconcile records them for the row-eviction cleanup (issue #1483).
    sb.Append($"\"PerspectiveName\":\"{EscapeJsonString(entry.PerspectiveName)}\"");
    sb.Append('}');
  }

  /// <summary>The columns a perspective table is built with: the fixed ones, then each physical field.</summary>
  /// <param name="entry">The perspective.</param>
  /// <param name="columnType">The PostgreSQL type the table DDL gives a physical field.</param>
  /// <returns>The columns, in table order.</returns>
  public static List<ColumnSchema> Columns(PerspectiveRegistryEntry entry, Func<PhysicalFieldInfo, string> columnType) {
    var schemaColumns = new List<ColumnSchema> {
      new("id", "uuid", false, true, false, null),
      new("data", "jsonb", false, false, false, null),
      new("metadata", "jsonb", false, false, false, null),
      new("scope", "jsonb", false, false, false, null),
      new("created_at", TIMESTAMPTZ, false, false, false, null),
      new("updated_at", TIMESTAMPTZ, false, false, false, null),
      new("sys_created_at", TIMESTAMPTZ, true, false, false, null),
      new("sys_updated_at", TIMESTAMPTZ, true, false, false, null),
      new("expires_at", TIMESTAMPTZ, true, false, false, null),
      new("version", "integer", false, false, false, null)
    };

    foreach (var field in entry.PhysicalFields) {
      schemaColumns.Add(new ColumnSchema(
          field.ColumnName,
          columnType(field).ToLowerInvariant(),
          true,
          false,
          field.IsVector,
          field.VectorDimensions
      ));
    }

    return schemaColumns;
  }

  /// <summary>The indexes a perspective table is built with, by the same decisions its DDL makes.</summary>
  /// <param name="entry">The perspective.</param>
  /// <returns>The indexes.</returns>
  public static List<IndexSchema> Indexes(PerspectiveRegistryEntry entry) {
    var shortName = entry.TableName.Replace(PERSPECTIVE_TABLE_PREFIX, "");
    var schemaIndexes = new List<IndexSchema> {
      new(PostgresIdentifiers.WithinLimit($"idx_{shortName}_created_at"), ["created_at"], "btree", false),
      new(PostgresIdentifiers.WithinLimit($"idx_{shortName}_updated_at"), ["updated_at"], "btree", false),
    };
    // The same decisions the DDL makes, so drift detection expects the indexes the table is meant to have
    // rather than ones this release no longer builds.
    if (entry.BuildsDataIndex) {
      schemaIndexes.Add(new(PostgresIdentifiers.WithinLimit($"idx_{shortName}_data_gin"), ["data"], "gin", false));
    }
    if (entry.BuildsMetadataIndex) {
      schemaIndexes.Add(new(PostgresIdentifiers.WithinLimit($"idx_{shortName}_metadata_gin"), ["metadata"], "gin", false));
    }
    schemaIndexes.Add(new(PostgresIdentifiers.WithinLimit($"idx_{shortName}_scope_gin"), ["scope"], "gin", false));

    foreach (var field in entry.PhysicalFields) {
      schemaIndexes.AddRange(_fieldIndexes(field, shortName));
    }

    return schemaIndexes;
  }

  // The indexes one physical field asks for: its containment index, then its own index when it is indexed, built
  // as a btree, or for a vector with the method the field names (none past 2000 dimensions, or for a method no
  // pgvector index maps to).
  private static IEnumerable<IndexSchema> _fieldIndexes(PhysicalFieldInfo field, string shortName) {
    if (field.IsContainmentIndexed) {
      yield return new(PostgresIdentifiers.WithinLimit($"idx_{shortName}_{field.ColumnName}_gin"), [field.ColumnName], "gin", false);
    }

    if (!field.IsIndexed || (field.IsVector && field.VectorDimensions is > 2000)) {
      yield break;
    }

    var indexType = field.IsVector ? _vectorIndexMethod(field.VectorIndexType) : "btree";
    if (indexType is not null) {
      yield return new IndexSchema(
          PostgresIdentifiers.WithinLimit($"idx_{shortName}_{field.ColumnName}" + (field.IsVector ? "_vec" : "")),
          [field.ColumnName],
          indexType,
          false);
    }
  }

  private static string? _vectorIndexMethod(GeneratorVectorIndexType? indexType) => indexType switch {
    GeneratorVectorIndexType.HNSW => "hnsw",
    GeneratorVectorIndexType.IVFFlat => "ivfflat",
    _ => null
  };

  /// <summary>Escapes a string for use inside a JSON string literal.</summary>
  /// <param name="value">The raw string.</param>
  /// <returns>The escaped string.</returns>
  public static string EscapeJsonString(string value) {
    return value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n")
        .Replace("\r", "\\r")
        .Replace("\t", "\\t");
  }
}
