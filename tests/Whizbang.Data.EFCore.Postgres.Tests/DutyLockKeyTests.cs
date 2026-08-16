using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The duty lock key must be identical in every process contending for the same duty in the same
/// schema — that is the whole point — and distinct across duties, schemas, and the other
/// advisory-lock families sharing Postgres's single-bigint key space.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/DutyLockKey.cs</code-under-test>
[Category("Migrations")]
public class DutyLockKeyTests {

  [Test]
  public async Task Compute_SameSchemaAndDuty_IsProcessStableAsync() {
    await Assert.That(DutyLockKey.Compute("inventory", "migrator"))
      .IsEqualTo(DutyLockKey.Compute("inventory", "migrator"))
      .Because("every process contending for the same duty must take the same lock");
  }

  [Test]
  public async Task Compute_DifferentDuties_TakeDifferentLocksAsync() {
    await Assert.That(DutyLockKey.Compute("inventory", "migrator"))
      .IsNotEqualTo(DutyLockKey.Compute("inventory", "maintainer"));
  }

  [Test]
  public async Task Compute_DifferentSchemas_TakeDifferentLocksAsync() {
    await Assert.That(DutyLockKey.Compute("inventory", "migrator"))
      .IsNotEqualTo(DutyLockKey.Compute("ordering", "migrator"))
      .Because("two services' fleets in one database must not contend for each other's duties");
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("public")]
  [Arguments("\"public\"")]
  public async Task Compute_EverySpellingOfPublic_TakesTheSameLockAsync(string? schema) {
    await Assert.That(DutyLockKey.Compute(schema, "migrator"))
      .IsEqualTo(DutyLockKey.Compute("public", "migrator"))
      .Because("an unset schema, an explicit public, and a quoted public are the same physical "
             + "schema — different keys would mean instances of one fleet excluding nothing");
  }

  [Test]
  public async Task Compute_DoesNotCollideWithTheSchemaInitFamilyAsync() {
    await Assert.That(DutyLockKey.Compute("public", "migrator"))
      .IsNotEqualTo(SchemaInitializationLockKey.Compute("public"))
      .Because("the families share one bigint key space; the namespace prefix keeps them apart");
  }
}
