// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// #2352: which events a digest counts, and which a checkpoint counts, as the work coordinator's SQL writes them for the
/// event store alias <c>es</c>. Read from the migrations' constants (<c>__DIGEST_ELIGIBLE__</c>,
/// <c>__INTEGRITY_COUNTED__</c>), so C# SQL and every SQL function apply one rule and none keeps a copy. Each is a constant
/// expression (flag bits and the exempt type list), never a bound value, so a query plan reads it as the SQL functions'
/// plans do.
/// </summary>
/// <docs>resilience/stream-integrity#one-eligibility-rule</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/DigestEligibilityGuardTests.cs:NoCSharpSql_WritesTheEligibilityRuleByHandAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEligibilitySqlTests.cs:TheConsumersCheckpointCount_LeavesOutAtMostOnceEvents_AsTheOriginsCountDoesAsync</tests>
internal static class DigestRuleSql {
  /// <summary>The digest eligibility rule for the event store alias <c>es</c>.</summary>
  internal static readonly string EligibleEs = Whizbang.Data.Postgres.MigrationConstants.Apply("__DIGEST_ELIGIBLE__(es)");

  /// <summary>The checkpoint counting rule for the event store alias <c>es</c>.</summary>
  internal static readonly string CountedEs = Whizbang.Data.Postgres.MigrationConstants.Apply("__INTEGRITY_COUNTED__(es)");

  /// <summary>
  /// What the event store alias <c>es</c> folds as (#2364): its event id, or, for a superseding row, its key's slot
  /// (<c>_wh_slot_id</c>), as every SQL fold computes it.
  /// </summary>
  /// <param name="schema">The validated schema name.</param>
  /// <returns>The SQL expression.</returns>
  internal static string DigestIdEs(string schema) =>
    "CASE WHEN es.supersede_key IS NULL THEN es.event_id " +
    $"ELSE {schema}._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END";

  /// <summary>The type bucket <c>es</c> folds into (#2364): a superseding key's slot stays in its first copy's type.</summary>
  internal const string DIGEST_TYPE_ES =
    "CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END";

  /// <summary>
  /// Whether <c>es</c> counts at all (#2364): every row but a superseding copy that is not its key's anchor (an older
  /// copy is held), so a key counts once.
  /// </summary>
  /// <param name="schema">The validated schema name.</param>
  /// <returns>The SQL condition.</returns>
  internal static string SlotCountsEs(string schema) =>
    $"(es.supersede_key IS NULL OR {schema}._wh_is_slot_anchor(es.event_id, es.stream_id, " +
    "COALESCE(es.supersede_group, es.event_type), es.supersede_key, es.origin_service_id, es.origin_commit_sequence, es.version))";
}
