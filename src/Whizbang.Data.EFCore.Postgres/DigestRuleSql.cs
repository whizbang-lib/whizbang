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
}
