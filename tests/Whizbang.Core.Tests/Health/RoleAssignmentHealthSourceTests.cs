// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;
using Rocks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Startup;

[assembly: Rock(typeof(IRoleAssignmentReader), BuildType.Create)]

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// Requirement 9 of #966: with no valid holder, health says "role unassigned", Degraded rather
/// than Faulted, and names why; with every role held, it names the holder and epoch.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Health/RoleAssignmentHealthSource.cs</code-under-test>
[Category("Core")]
[Category("Health")]
public class RoleAssignmentHealthSourceTests {
  private static readonly Guid _holder = Guid.CreateVersion7();

  private static RoleAssignmentSnapshot _snapshot(string role, RoleAssignmentState state, string? voidReason = null, string? lastReason = null) =>
    new(role, state, state == RoleAssignmentState.Vacant ? null : _holder, 4, null, null,
      state == RoleAssignmentState.Held ? TimeSpan.FromSeconds(9) : null, 4, voidReason, null, null, lastReason, 2);

  private static async Task<ComponentHealth> _reportAsync(RoleAssignmentOptions options, params RoleAssignmentSnapshot[] snapshots) {
    var reader = new IRoleAssignmentReaderCreateExpectations();
    reader.Setups.ReadAssignmentsAsync(Arg.Any<CancellationToken>())
      .ReturnValue(Task.FromResult<IReadOnlyList<RoleAssignmentSnapshot>>(snapshots));
    var health = await new RoleAssignmentHealthSource(reader.Instance(), Options.Create(options)).ReportAsync(CancellationToken.None);
    reader.Verify();
    return health;
  }

  [Test]
  public async Task EveryManagedRoleHeld_IsOperational_NamingHolderEpochAndOwedWorkAsync() {
    var health = await _reportAsync(new RoleAssignmentOptions(),
      _snapshot(StartupDuties.MAINTAINER, RoleAssignmentState.Held),
      _snapshot("unmanaged", RoleAssignmentState.Vacant));

    await Assert.That(health.State).IsEqualTo(ComponentState.Operational);
    await Assert.That(health.Detail).Contains(_holder.ToString());
    await Assert.That(health.Detail).Contains("epoch 4, 2 owed");
  }

  [Test]
  public async Task AVacantRole_IsDegraded_RoleUnassigned_WithTheLastReasonAsync() {
    var health = await _reportAsync(new RoleAssignmentOptions(),
      _snapshot(StartupDuties.MAINTAINER, RoleAssignmentState.Vacant, lastReason: "released"));

    await Assert.That(health.State).IsEqualTo(ComponentState.Degraded).Because("no holder degrades visibly; it is never Faulted");
    await Assert.That(health.Detail).StartsWith("role unassigned: 'maintainer' vacant (last released), 2 owed");
  }

  [Test]
  public async Task AVacantRoleNeverHeld_AndARoleNeverElected_AreBothUnassignedAsync() {
    var options = new RoleAssignmentOptions();
    options.Roles.Add("stamper");

    var health = await _reportAsync(options, _snapshot(StartupDuties.MAINTAINER, RoleAssignmentState.Vacant));

    await Assert.That(health.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(health.Detail).Contains("'maintainer' vacant (last never held)");
    await Assert.That(health.Detail).Contains("'stamper' unassigned (never elected)");
  }

  [Test]
  public async Task ALapsedRole_IsDegraded_NamingTheVoidReasonAsync() {
    var health = await _reportAsync(new RoleAssignmentOptions(),
      _snapshot(StartupDuties.MAINTAINER, RoleAssignmentState.Lapsed, voidReason: "evicted"));

    await Assert.That(health.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(health.Detail).Contains($"'maintainer' lapsed (evicted) on {_holder}");
  }

  [Test]
  public async Task TheMigrator_IsIdleBetweenMigrations_AndUnassignedOnlyWhenItLapsedMidRunAsync() {
    var idle = await _reportAsync(new RoleAssignmentOptions(),
      _snapshot(StartupDuties.MAINTAINER, RoleAssignmentState.Held),
      _snapshot(StartupDuties.MIGRATOR, RoleAssignmentState.Vacant, lastReason: "released"));
    await Assert.That(idle.State).IsEqualTo(ComponentState.Operational)
      .Because("the migrator is held for one migration at a time; vacant between them is its normal state");
    await Assert.That(idle.Detail).Contains("'migrator' idle (held only while it runs)");

    var neverElected = await _reportAsync(new RoleAssignmentOptions(), _snapshot(StartupDuties.MAINTAINER, RoleAssignmentState.Held));
    await Assert.That(neverElected.State).IsEqualTo(ComponentState.Operational);

    var lapsed = await _reportAsync(new RoleAssignmentOptions(),
      _snapshot(StartupDuties.MAINTAINER, RoleAssignmentState.Held),
      _snapshot(StartupDuties.MIGRATOR, RoleAssignmentState.Lapsed, voidReason: "lapsed"));
    await Assert.That(lapsed.State).IsEqualTo(ComponentState.Degraded).Because("a migrator that stopped mid-run is worth seeing");
    await Assert.That(lapsed.Detail).Contains("'migrator' lapsed (lapsed)");
  }

  [Test]
  public async Task AHeldRoleAskedToDrain_SaysSoAsync() {
    var health = await _reportAsync(new RoleAssignmentOptions(),
      _snapshot(StartupDuties.MAINTAINER, RoleAssignmentState.Held) with { DrainRequestedAt = DateTimeOffset.UnixEpoch });

    await Assert.That(health.State).IsEqualTo(ComponentState.Operational);
    await Assert.That(health.Detail).Contains("2 owed, draining");
  }

  [Test]
  public async Task AReadThatFails_IsDegraded_NotThrownAsync() {
    var reader = new IRoleAssignmentReaderCreateExpectations();
    reader.Setups.ReadAssignmentsAsync(Arg.Any<CancellationToken>())
      .ReturnValue(Task.FromException<IReadOnlyList<RoleAssignmentSnapshot>>(new InvalidOperationException("down")));

    var health = await new RoleAssignmentHealthSource(reader.Instance(), Options.Create(new RoleAssignmentOptions()))
      .ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(health.Detail).Contains("InvalidOperationException");
    reader.Verify();
  }

  [Test]
  public async Task CancellationOfTheProbe_PropagatesAsync() {
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();
    var reader = new IRoleAssignmentReaderCreateExpectations();
    reader.Setups.ReadAssignmentsAsync(Arg.Any<CancellationToken>())
      .ReturnValue(Task.FromCanceled<IReadOnlyList<RoleAssignmentSnapshot>>(canceled.Token));
    var source = new RoleAssignmentHealthSource(reader.Instance(), Options.Create(new RoleAssignmentOptions()));

    await Assert.That(async () => await source.ReportAsync(canceled.Token)).Throws<OperationCanceledException>();
    await Assert.That(source.Component).IsEqualTo("roles");
    reader.Verify();
  }

  [Test]
  public async Task Constructor_RefusesNullsAsync() {
    var reader = new IRoleAssignmentReaderCreateExpectations();
    await Assert.That(() => new RoleAssignmentHealthSource(null!, Options.Create(new RoleAssignmentOptions()))).Throws<ArgumentNullException>();
    await Assert.That(() => new RoleAssignmentHealthSource(reader.Instance(), null!)).Throws<ArgumentNullException>();
  }
}
