// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Rules;

/// <summary>
/// The versions of the eligibility rules that are not declared per type (#2362): what a digest means, and the SQL
/// rule every digest fold runs. Both travel in every rule fingerprint, so two services whose code counts differently
/// halt their pair instead of comparing digests computed two ways.
/// </summary>
/// <docs>resilience/stream-integrity#rule-fingerprint</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/EligibilitySqlVersionTests.cs:TheEligibilityConstants_MatchTheGoldenHashOfTheirVersionAsync</tests>
public static class RuleSemantics {
  /// <summary>
  /// The rule-semantics version: what an integrity digest counts and how. Bump it with any change to what a digest
  /// means (a cut that carries its removed part, a superseding key counted once), so a mixed pair during the rollout
  /// halts instead of repairing losses that are not real.
  /// </summary>
  public const int VERSION = 1;

  /// <summary>
  /// The version of the SQL eligibility rule (<c>__DIGEST_ELIGIBLE__</c>, <c>__INTEGRITY_COUNTED__</c> and
  /// <c>__DIGEST_EXEMPT_TYPES__</c>). A guard test holds the constants' text to a golden copy, so the text cannot change
  /// without this version changing with it.
  /// </summary>
  public const int ELIGIBILITY_SQL_VERSION = 1;
}
