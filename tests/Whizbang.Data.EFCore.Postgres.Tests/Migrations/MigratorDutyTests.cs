// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The migrator duty schema initialization holds (#2127): a grant it holds is renewed on its role's renew interval
/// until it is released, replaced or disposed, and never after.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/MigratorDuty.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
[Category("Unit")]
[Category("Shard1")]
public class MigratorDutyTests {
  private static readonly TimeSpan _interval = TimeSpan.FromSeconds(4);
  private static readonly TimeSpan _tick = TimeSpan.FromSeconds(2);

  /// <summary>A leased grant that reports each verify, and whether it was released.</summary>
  internal sealed class LeasedGrant(TimeSpan? renewInterval) : IDutyGrant {
    private readonly Channel<int> _verifies = Channel.CreateUnbounded<int>();
    private int _count;

    public string Duty => StartupDuties.MIGRATOR;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public TimeSpan? RenewInterval => renewInterval;
    public bool Answer { get; set; } = true;
    public int Count => Volatile.Read(ref _count);
    public bool Disposed { get; private set; }

    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) {
      _verifies.Writer.TryWrite(Interlocked.Increment(ref _count));
      return Task.FromResult(Answer);
    }

    public ValueTask<int> NextVerifyAsync(CancellationToken cancellationToken = default) => _verifies.Reader.ReadAsync(cancellationToken);

    public ValueTask DisposeAsync() {
      Disposed = true;
      return ValueTask.CompletedTask;
    }
  }

  private static SchemaStaging _migrator(IDutyGrant grant) => new(SchemaStage.Migrator, grant, "won");

  [Test]
  public async Task ANewDuty_HoldsNothing_AndReadsUnstagedAsync() {
    await using var duty = new MigratorDuty(new FakeTimeProvider());

    await Assert.That(duty.Stage).IsEqualTo(SchemaStage.Unstaged);
    await Assert.That(duty.Grant).IsNull();
    await Assert.That(duty.IsKeptAlive).IsFalse();
  }

  [Test]
  public async Task NoClock_IsRefusedAsync() {
    await Assert.That(() => new MigratorDuty(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  [Timeout(30000)]
  public async Task AHeldGrant_IsRenewedOnItsRoleInterval_UntilTheDutyIsDisposedAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var grant = new LeasedGrant(_interval);
    var duty = new MigratorDuty(time);

    await duty.HoldAsync(_migrator(grant));

    await Assert.That(duty.Stage).IsEqualTo(SchemaStage.Migrator);
    await Assert.That(duty.Grant).IsSameReferenceAs(grant);
    await Assert.That(duty.IsKeptAlive).IsTrue();
    time.Advance(_tick);
    await Assert.That(await grant.NextVerifyAsync(cancellationToken)).IsEqualTo(1);
    time.Advance(_tick);
    await Assert.That(await grant.NextVerifyAsync(cancellationToken)).IsEqualTo(2);

    await duty.DisposeAsync();
    time.Advance(TimeSpan.FromHours(1));

    await Assert.That(grant.Disposed).IsTrue();
    await Assert.That(duty.IsKeptAlive).IsFalse();
    await Assert.That(grant.Count).IsEqualTo(2)
      .Because("a released grant is never renewed: the keepalive stops before the grant is given back");
  }

  [Test]
  [Timeout(30000)]
  public async Task HoldingANewElection_ReleasesTheOldGrant_AndKeepsOnlyTheNewOneAliveAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var first = new LeasedGrant(_interval);
    var second = new LeasedGrant(_interval);
    await using var duty = new MigratorDuty(time);
    await duty.HoldAsync(_migrator(first));

    await duty.HoldAsync(_migrator(second));
    time.Advance(_tick);

    await Assert.That(await second.NextVerifyAsync(cancellationToken)).IsEqualTo(1);
    await Assert.That(first.Disposed).IsTrue();
    await Assert.That(first.Count).IsEqualTo(0)
      .Because("one grant and one keepalive at a time: the replaced grant is stopped and released first");
  }

  [Test]
  public async Task AWaiter_HoldsNoGrant_AndNothingIsKeptAliveAsync() {
    await using var duty = new MigratorDuty(new FakeTimeProvider());

    await duty.HoldAsync(new SchemaStaging(SchemaStage.Waiter, null, "another instance holds it"));

    await Assert.That(duty.Stage).IsEqualTo(SchemaStage.Waiter);
    await Assert.That(duty.IsKeptAlive).IsFalse();
  }

  [Test]
  public async Task AGrantWithNoLease_IsHeld_ButNeedsNoKeepaliveAsync() {
    var grant = new LeasedGrant(null);
    await using var duty = new MigratorDuty(new FakeTimeProvider());

    await duty.HoldAsync(_migrator(grant));

    await Assert.That(duty.Grant).IsSameReferenceAs(grant);
    await Assert.That(duty.IsKeptAlive).IsFalse();
  }

  [Test]
  public async Task Release_GivesTheGrantBack_AndTheDutyReadsUnstagedAsync() {
    var time = new FakeTimeProvider();
    var grant = new LeasedGrant(_interval);
    await using var duty = new MigratorDuty(time);
    await duty.HoldAsync(_migrator(grant));

    await duty.ReleaseAsync();
    time.Advance(TimeSpan.FromHours(1));

    await Assert.That(grant.Disposed).IsTrue();
    await Assert.That(grant.Count).IsEqualTo(0);
    await Assert.That(duty.Stage).IsEqualTo(SchemaStage.Unstaged);
    await Assert.That(duty.Grant).IsNull();
  }
}
