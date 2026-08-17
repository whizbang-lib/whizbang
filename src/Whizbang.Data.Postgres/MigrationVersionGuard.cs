using Whizbang.Core.Versioning;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Decides whether this build may apply a migration over what is already recorded in the ledger —
/// the rule that an older instance never overwrites a newer one's work.
/// </summary>
/// <remarks>
/// <para>
/// The applier's own test is <c>computed hash != recorded hash</c>, and inequality is symmetric: it
/// says the content <em>differs</em>, never which of the two is newer. Because pre-v1 migration files
/// are edited in place rather than superseded, an instance from the previous version that restarts
/// after a newer one has migrated computes a different hash for its own older copy and re-applies it
/// through <c>CREATE OR REPLACE</c> — silently returning objects to an earlier definition beneath the
/// instances still running against them. The redefinition closure cannot help: it re-runs the
/// <em>later</em> files defining the same objects, and an older instance does not have them.
/// </para>
/// <para>
/// The information needed to prevent this is already written on every run —
/// <c>wh_schema_migrations.version_id</c> references the library version that applied each object.
/// This type only reads it.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#an-older-instance-never-overwrites-a-newer-one</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigrationVersionGuardTests.cs</tests>
public static class MigrationVersionGuard {
  /// <summary>
  /// Whether a build identified as <paramref name="myVersion"/> may apply a migration whose ledger
  /// row was written by <paramref name="recordedVersion"/>.
  /// </summary>
  /// <param name="myVersion">This build's library version.</param>
  /// <param name="recordedVersion">
  /// The library version recorded against the migration, or <see langword="null"/> when the ledger
  /// carries none — a row written before this guard existed.
  /// </param>
  /// <param name="reason">
  /// Why the answer is what it is, phrased for a log read during an incident. Set whenever the
  /// answer is worth explaining, which includes some of the permitted cases.
  /// </param>
  /// <returns>
  /// <see langword="false"/> only when this build is provably older than the recorded one. Absent or
  /// unreadable ledger versions permit the apply: refusing on missing information would leave a
  /// schema permanently unmigratable with no way back, and the hash check still governs whether
  /// anything runs at all. An unreadable version for <em>this</em> build refuses, because a build
  /// that cannot state what it is has no business writing DDL.
  /// </returns>
  public static bool MayApply(string? myVersion, string? recordedVersion, out string reason) {
    if (!SemanticVersion.TryParse(myVersion, out var mine)) {
      reason = $"this build's library version '{myVersion}' is not a readable semantic version, "
             + "so it cannot be ordered against the schema and will not apply migrations";
      return false;
    }

    if (string.IsNullOrWhiteSpace(recordedVersion)) {
      reason = string.Empty;
      return true;
    }

    if (!SemanticVersion.TryParse(recordedVersion, out var recorded)) {
      reason = $"the ledger records library version '{recordedVersion}', which is not a readable "
             + "semantic version; proceeding, because refusing on an unreadable record would leave "
             + "the schema permanently unmigratable";
      return true;
    }

    if (mine < recorded) {
      reason = $"this build is version {mine}, and the migration was applied by {recorded} — "
             + "applying would replace newer definitions with older ones";
      return false;
    }

    reason = string.Empty;
    return true;
  }
}
