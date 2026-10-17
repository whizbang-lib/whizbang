// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Qualifies the legacy framework table and function names a migration writes bare with the schema
/// the EF Core driver migrates, so a service in a named schema does not create or read them in
/// <c>public</c>. Applied by the generated <c>EnsureWhizbangDatabaseInitializedAsync</c> to every
/// embedded migration before it runs and before its hash is taken; the Dapper driver substitutes
/// <c>__SCHEMA__</c> only and never calls it.
/// </summary>
/// <remarks>
/// Every migration is meant to write <c>__SCHEMA__.wh_*</c> already (migrations README rule 3), so
/// this is a safety net for the names the oldest migrations wrote bare, not the contract.
/// </remarks>
/// <docs>contributors/data-engines/writing-migrations</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/MigrationSchemaQualifierTests.cs</tests>
public static class MigrationSchemaQualifier {
  private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(5);

  /// <summary>The framework tables (and one sequence) a bare reference to which is qualified.</summary>
  public static IReadOnlyList<string> TableNames { get; } = [
    "wh_inbox",
    "wh_outbox",
    "wh_event_store",
    "wh_events", // Alias for wh_event_store
    "wh_receptor_processing",
    "wh_perspective_cursors",
    "wh_perspective_events",
    "wh_message_associations",
    "wh_request_response",
    "wh_service_instances",
    "wh_partition_assignments",
    "wh_message_deduplication",
    "wh_sequences",
    "wh_active_streams",
    "wh_event_sequence" // Sequence name
  ];

  /// <summary>
  /// The framework functions whose bare name is qualified where it is created, dropped, granted or
  /// commented on. Functions are database-wide, so two schemas in one database must each own theirs.
  /// </summary>
  public static IReadOnlyList<string> FunctionNames { get; } = [
    "register_message_associations",
    "reconcile_perspective_registry",
    "process_work_batch",
    "process_inbox_batch",
    "process_outbox_batch",
    "process_perspectives_batch",
    "perform_maintenance"
  ];

  /// <summary>
  /// Prefixes every bare reference to a <see cref="TableNames">framework table</see>, and every bare
  /// <see cref="FunctionNames">framework function</see> a statement defines, drops, grants or
  /// comments on, with <paramref name="quotedSchema"/>.
  /// </summary>
  /// <param name="sql">The migration text, with its schema placeholder already substituted.</param>
  /// <param name="quotedSchema">The schema as a quoted identifier, for example <c>"inventory"</c>.</param>
  /// <returns>The migration text with the bare framework names qualified.</returns>
  /// <remarks>
  /// A name already preceded by a period is left alone, and a <c>RENAME TO</c> target is never
  /// qualified because PostgreSQL requires it bare.
  /// </remarks>
  public static string Qualify(string sql, string quotedSchema) {
    ArgumentNullException.ThrowIfNull(sql);
    ArgumentException.ThrowIfNullOrEmpty(quotedSchema);

    // First, protect RENAME TO targets by replacing them with a placeholder
    // PostgreSQL RENAME TO requires an unqualified name, so these must NOT be schema-qualified
    var renameToPlaceholders = new Dictionary<string, string>(StringComparer.Ordinal);
    var transformedSql = Regex.Replace(
      sql,
      @"(RENAME\s+TO\s+)(\w+)",
      m => {
        var placeholder = $"__RENAME_TARGET_{renameToPlaceholders.Count}__";
        renameToPlaceholders[placeholder] = m.Groups[2].Value;
        return m.Groups[1].Value + placeholder;
      },
      RegexOptions.IgnoreCase,
      _regexTimeout);

    foreach (var tableName in TableNames) {
      // Pattern: tableName NOT preceded by period (to avoid replacing already-qualified names)
      // Matches: "FROM wh_inbox", "ALTER TABLE wh_inbox", etc.
      // Does NOT match: "inventory.wh_inbox", "wh_inbox_id" (column name)
      transformedSql = Regex.Replace(
        transformedSql,
        $@"(?<!\.)(\b{Regex.Escape(tableName)}\b)",
        $"{quotedSchema}.$1",
        RegexOptions.IgnoreCase,
        _regexTimeout);
    }

    foreach (var (placeholder, original) in renameToPlaceholders) {
      transformedSql = transformedSql.Replace(placeholder, original, StringComparison.Ordinal);
    }

    foreach (var functionName in FunctionNames) {
      var name = Regex.Escape(functionName);

      // "CREATE [OR REPLACE] FUNCTION name(" → "CREATE [OR REPLACE] FUNCTION "schema".name("
      transformedSql = Regex.Replace(
        transformedSql,
        $@"(CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+)(?<!\.)\b{name}\b(\s*\()",
        $"$1{quotedSchema}.{functionName}$2",
        RegexOptions.IgnoreCase,
        _regexTimeout);

      // "DROP FUNCTION [IF EXISTS] name" → "DROP FUNCTION [IF EXISTS] "schema".name"
      transformedSql = Regex.Replace(
        transformedSql,
        $@"(DROP\s+FUNCTION\s+(?:IF\s+EXISTS\s+)?)(?<!\.)\b{name}\b",
        $"$1{quotedSchema}.{functionName}",
        RegexOptions.IgnoreCase,
        _regexTimeout);

      // "GRANT ... ON FUNCTION name" → "GRANT ... ON FUNCTION "schema".name" (and REVOKE)
      transformedSql = Regex.Replace(
        transformedSql,
        $@"((?:GRANT|REVOKE)\s+.*\s+ON\s+FUNCTION\s+)(?<!\.)\b{name}\b",
        $"$1{quotedSchema}.{functionName}",
        RegexOptions.IgnoreCase,
        _regexTimeout);

      // "COMMENT ON FUNCTION name" → "COMMENT ON FUNCTION "schema".name"
      transformedSql = Regex.Replace(
        transformedSql,
        $@"(COMMENT\s+ON\s+FUNCTION\s+)(?<!\.)\b{name}\b",
        $"$1{quotedSchema}.{functionName}",
        RegexOptions.IgnoreCase,
        _regexTimeout);
    }

    return transformedSql;
  }
}
