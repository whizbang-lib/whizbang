using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The migration advisory-lock key must be identical in every process that initializes the same
/// schema — that is the entire point of the lock. Deriving it from <c>string.GetHashCode</c> does
/// not satisfy that: .NET randomizes string hash seeds per process, so each instance computes a
/// different key, every instance acquires "the" lock, and the lock excludes nothing. These tests
/// pin the key to a process-stable function and to specific values, so a future change to the
/// hash silently breaking cross-instance agreement fails here instead of in a fleet.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaInitializationLockKey.cs</code-under-test>
[Category("Migrations")]
public class SchemaInitializationLockKeyTests {

  // ── process stability ───────────────────────────────────────────────────

  // These are the load-bearing tests. The expected values are pinned literals derived from the
  // FNV-1a 64 definition itself, NOT captured from a run of the implementation: a key that varies
  // per process (the defect) cannot match a fixed literal, and a key that changes between library
  // versions would break a rolling deploy exactly as badly as a per-process one.

  [Test]
  [Arguments("public", -9093863223194374290L)]
  [Arguments("inventory", -8862321946245490211L)]
  [Arguments("user", 2191991040936337792L)]
  [Arguments("a", -1888698773829427290L)]
  public async Task Compute_ForSchema_ReturnsPinnedProcessStableKeyAsync(string schema, long expected) {
    await Assert.That(SchemaInitializationLockKey.Compute(schema)).IsEqualTo(expected);
  }

  [Test]
  public async Task Compute_CalledTwice_ReturnsSameKeyAsync() {
    await Assert.That(SchemaInitializationLockKey.Compute("inventory"))
      .IsEqualTo(SchemaInitializationLockKey.Compute("inventory"));
  }

  // ── schema-name normalization ───────────────────────────────────────────

  // An unset schema and an explicit "public" address the same physical schema — the migration SQL
  // transform already normalizes empty to "public". If the lock key did not agree, two instances
  // of the same service configured either way would migrate the same schema concurrently.

  [Test]
  [Arguments("")]
  [Arguments(null)]
  public async Task Compute_ForUnsetSchema_MatchesPublicAsync(string? schema) {
    await Assert.That(SchemaInitializationLockKey.Compute(schema))
      .IsEqualTo(SchemaInitializationLockKey.Compute("public"));
  }

  // ── distinctness ────────────────────────────────────────────────────────

  [Test]
  public async Task Compute_ForDifferentSchemas_ReturnsDifferentKeysAsync() {
    await Assert.That(SchemaInitializationLockKey.Compute("inventory"))
      .IsNotEqualTo(SchemaInitializationLockKey.Compute("billing"));
  }

  // The key is namespaced so it cannot collide with the other advisory-lock families that share
  // the single-bigint key space (collective apply, instance liveness, per-stream event locks).
  [Test]
  public async Task Compute_DoesNotCollideWithCollectiveApplyKeyAsync() {
    await Assert.That(SchemaInitializationLockKey.Compute("inventory"))
      .IsNotEqualTo(Whizbang.Data.Postgres.Collective.CollectiveApplyLockKey.Compute("inventory", ""));
  }

  // ── total function ──────────────────────────────────────────────────────

  // The previous implementation was Math.Abs(schema.GetHashCode()) % int.MaxValue, which throws
  // OverflowException whenever GetHashCode returns int.MinValue — a startup crash reachable purely
  // by which schema name and hash seed a given process drew.
  [Test]
  [Arguments("")]
  [Arguments("a")]
  [Arguments("schema-with-hyphens")]
  [Arguments("Ünïcödé")]
  [Arguments("a_very_long_schema_name_that_goes_on_for_quite_a_while_indeed_0123456789")]
  public async Task Compute_ForAnySchemaName_DoesNotThrowAsync(string schema) {
    await Assert.That(() => SchemaInitializationLockKey.Compute(schema)).ThrowsNothing();
  }
}
