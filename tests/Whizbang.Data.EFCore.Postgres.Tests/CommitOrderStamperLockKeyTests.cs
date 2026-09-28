using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The commit-order stamper's leadership lock must be identical in every instance of one service
/// (one stamper per schema) and distinct across schemas: a Postgres advisory lock is scoped to the
/// whole database, so a key shared by every schema lets one service's stamper exclude every other
/// service's stamper in the same database, and those services' events are never stamped.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/CommitOrderStamperLockKey.cs</code-under-test>
[Category("Shard3")]
public class CommitOrderStamperLockKeyTests {
  private static readonly long _defaultBase = new CommitOrderStamperOptions().AdvisoryLockKey;

  [Test]
  public async Task Compute_SameSchema_IsProcessStableAsync() {
    await Assert.That(CommitOrderStamperLockKey.Compute("inventory", _defaultBase))
      .IsEqualTo(CommitOrderStamperLockKey.Compute("inventory", _defaultBase))
      .Because("every instance of one service must contend for the same stamper lock");
  }

  [Test]
  public async Task Compute_DifferentSchemas_TakeDifferentLocksAsync() {
    await Assert.That(CommitOrderStamperLockKey.Compute("inventory", _defaultBase))
      .IsNotEqualTo(CommitOrderStamperLockKey.Compute("ordering", _defaultBase))
      .Because("two services sharing one database each need their own stamper");
  }

  [Test]
  public async Task Compute_DifferentBaseKeys_TakeDifferentLocksAsync() {
    await Assert.That(CommitOrderStamperLockKey.Compute("inventory", _defaultBase))
      .IsNotEqualTo(CommitOrderStamperLockKey.Compute("inventory", _defaultBase + 1))
      .Because("an operator who overrides AdvisoryLockKey to avoid a collision must get a different lock");
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("public")]
  [Arguments("\"public\"")]
  public async Task Compute_EverySpellingOfPublic_TakesTheSameLockAsync(string? schema) {
    await Assert.That(CommitOrderStamperLockKey.Compute(schema, _defaultBase))
      .IsEqualTo(CommitOrderStamperLockKey.Compute("public", _defaultBase))
      .Because("an unset schema, an explicit public, and a quoted public are the same physical schema");
  }

  [Test]
  public async Task Compute_DoesNotCollideWithTheDutyFamilyAsync() {
    await Assert.That(CommitOrderStamperLockKey.Compute("public", _defaultBase))
      .IsNotEqualTo(DutyLockKey.Compute("public", "migrator"))
      .Because("the advisory-lock families share one bigint key space; the namespace prefix keeps them apart");
  }
}
