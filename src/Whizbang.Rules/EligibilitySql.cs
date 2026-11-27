// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Rules;

/// <summary>
/// The source of the SQL eligibility rule (#2343): the values of the migration constants every digest fold, answer, heal,
/// checkpoint and cleanup path writes. The migrations read them from <c>Migrations/constants.txt</c>, whose values must
/// equal these (<c>EligibilitySqlSourceTests</c>), and <see cref="RuleSemantics.ELIGIBILITY_SQL_VERSION"/> is held to
/// their text by a golden hash, so the rule cannot change without its version.
/// </summary>
/// <docs>resilience/stream-integrity#rule-fingerprint</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/EligibilitySqlVersionTests.cs:TheMigrationConstants_AreTheRulesLibrarysSourceAsync</tests>
public static class EligibilitySql {
  /// <summary><c>__DIGEST_ELIGIBLE__(e)</c>: not ephemeral (8), not delivered at most once (32), not an exempt type.</summary>
  public const string DIGEST_ELIGIBLE =
    "(COALESCE(e.flags, 0) & 8 = 0 AND COALESCE(e.flags, 0) & 32 = 0 AND split_part(e.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__))";

  /// <summary><c>__INTEGRITY_COUNTED__(e)</c>: the digest rule without the ephemeral test, for continuity checkpoints.</summary>
  public const string INTEGRITY_COUNTED =
    "(COALESCE(e.flags, 0) & 32 = 0 AND split_part(e.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__))";

  /// <summary>Version 1 of <c>__DIGEST_ELIGIBLE__(e)</c>, frozen: the text every shipped migration writes.</summary>
  public const string DIGEST_ELIGIBLE_V1 =
    "(COALESCE(e.flags, 0) & 8 = 0 AND COALESCE(e.flags, 0) & 32 = 0 AND split_part(e.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__))";

  /// <summary>Version 1 of <c>__INTEGRITY_COUNTED__(e)</c>, frozen: the text every shipped migration writes.</summary>
  public const string INTEGRITY_COUNTED_V1 =
    "(COALESCE(e.flags, 0) & 32 = 0 AND split_part(e.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__))";

  /// <summary><c>__DIGEST_EXEMPT_TYPES__</c>: the audit system events, which no digest counts.</summary>
  public const string DIGEST_EXEMPT_TYPES =
    "ARRAY['Whizbang.Core.SystemEvents.EventAudited','Whizbang.Core.SystemEvents.CommandAudited']";
}
