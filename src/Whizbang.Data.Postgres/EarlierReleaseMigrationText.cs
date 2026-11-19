// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The text the EF Core driver hashed for a migration in releases before #1681, which recorded SHA-256 of it: only so
/// the hash upgrade can recognize what those releases recorded (#2125). Nothing runs this text.
/// </summary>
/// <remarks>
/// <para>
/// Those releases qualified the bare framework names in a migration for a named schema with regular expressions that
/// reached inside comments and string literals; <see cref="MigrationSchemaQualifier"/> has since left both alone. For a
/// context in a named schema the two give different text for almost every file, so the raw hash such a release recorded
/// can only be reproduced by the rule it used. This is that rule, frozen as v0.2350.0 through v0.2623.1 shipped it
/// (unchanged across those releases): do not change it, whatever the current qualifier does.
/// </para>
/// <para>
/// For <c>public</c> (or an empty schema) there was no qualification, and the text is the current driver's.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#what-counts-as-a-change</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/EarlierReleaseMigrationTextTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/NormalizedMigrationHashUpgradeTests.cs:Upgrade_ANamedSchemaFromV0_2623_1_AppliesNoneOfItsMigrationsAsync</tests>
public static class EarlierReleaseMigrationText {
  private const string PUBLIC_SCHEMA = "public";
  private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(5);

  // The lists as those releases had them, including functions that no longer exist.
  private static readonly string[] _tableNames = [
    "wh_inbox",
    "wh_outbox",
    "wh_event_store",
    "wh_events",
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
    "wh_event_sequence"
  ];

  private static readonly string[] _functionNames = [
    "register_message_associations",
    "reconcile_perspective_registry",
    "process_work_batch",
    "process_inbox_batch",
    "process_outbox_batch",
    "process_perspectives_batch",
    "perform_maintenance"
  ];

  /// <summary>
  /// The EF Core driver's transform of an embedded migration as releases before #1681 applied it: the schema
  /// placeholder, the migration constants, and for a named schema the earlier qualification of bare framework names.
  /// </summary>
  /// <param name="sql">The migration as the generated initializer embeds it (placeholder <c>__MIGRATION_SCHEMA__</c>).</param>
  /// <param name="schema">The context's schema; empty means <c>public</c>.</param>
  /// <returns>The text those releases hashed.</returns>
  public static string EFCore(string sql, string schema) {
    ArgumentNullException.ThrowIfNull(sql);
    var effectiveSchema = string.IsNullOrEmpty(schema) ? PUBLIC_SCHEMA : schema;
    var quotedSchema = $"\"{effectiveSchema}\"";
    var transformed = MigrationConstants.Apply(sql.Replace("__MIGRATION_SCHEMA__", quotedSchema, StringComparison.Ordinal));
    return effectiveSchema == PUBLIC_SCHEMA ? transformed : _qualify(transformed, quotedSchema);
  }

  private static string _qualify(string sql, string quotedSchema) {
    // A RENAME TO target must stay unqualified, so it is set aside while the table names are qualified.
    var renameTargets = new List<(string Placeholder, string Original)>();
    var transformed = Regex.Replace(
      sql,
      @"(RENAME\s+TO\s+)(\w+)",
      m => {
        var placeholder = $"__RENAME_TARGET_{renameTargets.Count}__";
        renameTargets.Add((placeholder, m.Groups[2].Value));
        return m.Groups[1].Value + placeholder;
      },
      RegexOptions.IgnoreCase,
      _regexTimeout);

    foreach (var tableName in _tableNames) {
      transformed = Regex.Replace(
        transformed, $@"(?<!\.)(\b{Regex.Escape(tableName)}\b)", $"{quotedSchema}.$1", RegexOptions.IgnoreCase, _regexTimeout);
    }

    foreach (var (placeholder, original) in renameTargets) {
      transformed = transformed.Replace(placeholder, original, StringComparison.Ordinal);
    }

    foreach (var functionName in _functionNames) {
      var name = Regex.Escape(functionName);
      transformed = Regex.Replace(
        transformed, $@"(CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+)(?<!\.)\b{name}\b(\s*\()",
        $"$1{quotedSchema}.{functionName}$2", RegexOptions.IgnoreCase, _regexTimeout);
      transformed = Regex.Replace(
        transformed, $@"(DROP\s+FUNCTION\s+(?:IF\s+EXISTS\s+)?)(?<!\.)\b{name}\b",
        $"$1{quotedSchema}.{functionName}", RegexOptions.IgnoreCase, _regexTimeout);
      transformed = Regex.Replace(
        transformed, $@"((?:GRANT|REVOKE)\s+.*\s+ON\s+FUNCTION\s+)(?<!\.)\b{name}\b",
        $"$1{quotedSchema}.{functionName}", RegexOptions.IgnoreCase, _regexTimeout);
      transformed = Regex.Replace(
        transformed, $@"(COMMENT\s+ON\s+FUNCTION\s+)(?<!\.)\b{name}\b",
        $"$1{quotedSchema}.{functionName}", RegexOptions.IgnoreCase, _regexTimeout);
    }

    return transformed;
  }
}
