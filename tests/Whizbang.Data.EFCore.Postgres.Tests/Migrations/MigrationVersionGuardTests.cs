using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The applier runs a migration whenever its computed hash differs from the recorded one, and hash
/// inequality is symmetric — it cannot tell newer content from older. Because pre-v1 migration files
/// are edited in place, an instance from the previous version that restarts after a newer one has
/// migrated would otherwise re-apply its own older definitions through <c>CREATE OR REPLACE</c>,
/// downgrading the schema underneath the instances still running against it, with no error raised
/// anywhere. This guard is what makes that impossible.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationVersionGuard.cs</code-under-test>
[Category("Migrations")]
[Category("Shard4")]
public class MigrationVersionGuardTests {

  // ── the defect this exists to prevent ───────────────────────────────────

  [Test]
  [Arguments("0.9.4", "0.9.5")]
  [Arguments("0.9.4", "0.10.0")]
  [Arguments("0.99.0", "0.100.0")]
  [Arguments("1.0.0-alpha.2", "1.0.0-alpha.10")]
  [Arguments("0.100.0-local.99", "0.100.0-local.111")]
  [Arguments("1.0.0-rc.1", "1.0.0")]
  public async Task MayApply_WhenRecordedVersionIsNewer_RefusesAsync(string mine, string recorded) {
    await Assert.That(MigrationVersionGuard.MayApply(mine, recorded, out var reason)).IsFalse();
    await Assert.That(reason).IsNotEmpty();
  }

  // ── the ordinary upgrade path stays open ────────────────────────────────

  [Test]
  [Arguments("0.9.5", "0.9.4")]
  [Arguments("0.10.0", "0.9.4")]
  [Arguments("1.0.0-alpha.10", "1.0.0-alpha.2")]
  [Arguments("1.0.0", "1.0.0-rc.1")]
  public async Task MayApply_WhenRecordedVersionIsOlder_AllowsAsync(string mine, string recorded) {
    await Assert.That(MigrationVersionGuard.MayApply(mine, recorded, out _)).IsTrue();
  }

  // CI's pull-request builds stamp the placeholder 0.0.0-prNNN.N when the version job is
  // skipped. That build outranks only the SemVer floor (numeric pre-release identifiers rank
  // below alphanumeric ones) — it does NOT outrank 0.0.1, and the guard must refuse there
  // exactly as it would for any older build. Caught live: an integration test seeded 0.0.1 as
  // "ancient" and the guard correctly froze the re-apply under a PR build.
  [Test]
  public async Task MayApply_PlaceholderPrBuild_OutranksOnlyTheSemVerFloorAsync() {
    await Assert.That(MigrationVersionGuard.MayApply("0.0.0-pr478.13", "0.0.0-0", out _)).IsTrue()
      .Because("every stampable build outranks the floor — the ordinary upgrade path stays open");
    await Assert.That(MigrationVersionGuard.MayApply("0.0.0-pr478.13", "0.0.1", out var reason)).IsFalse()
      .Because("0.0.0-pr478.13 is a pre-release of 0.0.0, which is older than 0.0.1 — a "
             + "placeholder-version build must never downgrade a schema written by a real one");
    await Assert.That(reason).IsNotEmpty();
  }

  // Re-applying at the same version is how a hash-drifted file gets corrected, and how the
  // redefinition closure re-runs a later definition. It must stay allowed.
  [Test]
  [Arguments("0.9.4")]
  [Arguments("1.0.0-alpha.1")]
  public async Task MayApply_AtTheSameVersion_AllowsAsync(string version) {
    await Assert.That(MigrationVersionGuard.MayApply(version, version, out _)).IsTrue();
  }

  // ── absent information ──────────────────────────────────────────────────

  // A row with no recorded version predates this guard. Refusing would permanently block migration
  // with no recovery path, so it is allowed — the fast path's hash check still governs whether
  // anything runs at all.
  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("   ")]
  public async Task MayApply_WhenNothingIsRecorded_AllowsAsync(string? recorded) {
    await Assert.That(MigrationVersionGuard.MayApply("0.9.4", recorded, out _)).IsTrue();
  }

  // An unreadable recorded version is anomalous rather than informative. Same reasoning as above:
  // it must not brick the schema permanently, but it is worth surfacing.
  [Test]
  public async Task MayApply_WhenRecordedVersionIsUnreadable_AllowsWithAReasonAsync() {
    await Assert.That(MigrationVersionGuard.MayApply("0.9.4", "not-a-version", out var reason)).IsTrue();
    await Assert.That(reason).IsNotEmpty();
  }

  // My own version being unreadable is a different matter — that is this build being wrong, and a
  // build that cannot say what it is has no business writing DDL.
  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("not-a-version")]
  public async Task MayApply_WhenMyOwnVersionIsUnreadable_RefusesAsync(string? mine) {
    await Assert.That(MigrationVersionGuard.MayApply(mine, "0.9.4", out var reason)).IsFalse();
    await Assert.That(reason).IsNotEmpty();
  }

  // ── the reason is for a human reading a log during an incident ──────────

  [Test]
  public async Task MayApply_WhenRefusing_NamesBothVersionsAsync() {
    MigrationVersionGuard.MayApply("0.9.4", "0.10.0", out var reason);
    await Assert.That(reason).Contains("0.9.4");
    await Assert.That(reason).Contains("0.10.0");
  }
}
