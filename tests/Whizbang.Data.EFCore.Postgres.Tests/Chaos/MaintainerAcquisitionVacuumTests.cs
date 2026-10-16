// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Chaos;

/// <summary>
/// #1351 across instances, on the real elector, holder loop and coordinator: the work tables are vacuumed
/// by the instance that acquires the maintainer duty, never merely by an instance that started. Two
/// instances starting vacuum once; an instance joining a fleet with a live holder never vacuums; a
/// hand-off or a takeover after a lapse vacuums again on the new holder, unless
/// <see cref="MaintenanceWorkerOptions.MaintainOnReassignment"/> is off.
/// </summary>
/// <remarks>
/// Judged by <c>vacuum_count</c> on <c>wh_outbox</c>, which counts manual vacuums only. Each pass is
/// driven with <see cref="DutyHolderWorker.RunOnceAsync"/>, and a lapse is database time shifted back,
/// as in <see cref="RoleAssignmentChaosTests"/>; nothing sleeps.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/WorkTableVacuumOnAcquisition.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/DutyHolderWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgRoleElector.cs</code-under-test>
[Category("Integration")]
[Category("Chaos")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class MaintainerAcquisitionVacuumTests : EFCoreTestBase {
  private const string ROLE = StartupDuties.MAINTAINER;
  private readonly List<ServiceProvider> _providers = [];

  [After(Test)]
  public async Task DisposeProvidersAsync() {
    foreach (var provider in _providers) {
      await provider.DisposeAsync();
    }
  }

  private sealed class Recorder : IDutyAcquisitionHandler {
    public List<Guid?> PreviousHolders { get; } = [];
    public string Role => ROLE;
    public ValueTask OnAcquiredAsync(IDutyGrant grant, CancellationToken cancellationToken) {
      PreviousHolders.Add(grant.PreviousHolderInstanceId);
      return ValueTask.CompletedTask;
    }
  }

  private async Task<(ChaosPod Pod, Recorder Acquisitions)> _podAsync(CancellationToken ct, bool maintainOnReassignment = true) {
    var pod = await ChaosPod.JoinAsync(ConnectionString, CreateDbContext, ct);
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(), JsonContextRegistry.CreateCombinedOptions()));
    var provider = services.BuildServiceProvider();
    _providers.Add(provider);
    var vacuum = new WorkTableVacuumOnAcquisition(
      provider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new MaintenanceWorkerOptions { MaintainOnReassignment = maintainOnReassignment }),
      pod,
      NullLogger<WorkTableVacuumOnAcquisition>.Instance);
    var recorder = new Recorder();
    pod.Build(ConnectionString, ConnectionString, version: null, bridge: false, pause: null, new FakeTimeProvider(), [],
      onAcquired: [vacuum, recorder]);
    return (pod, recorder);
  }

  private async Task<long> _outboxVacuumsAsync(CancellationToken ct) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync(ct);
    await using (var flush = new NpgsqlCommand("SELECT pg_stat_force_next_flush()", db)) {
      await flush.ExecuteNonQueryAsync(ct);
    }
    await using var read = new NpgsqlCommand(
      "SELECT vacuum_count FROM pg_stat_user_tables WHERE schemaname = 'public' AND relname = 'wh_outbox'", db);
    return (long)(await read.ExecuteScalarAsync(ct))!;
  }

  [Test]
  [Timeout(120000)]
  public async Task TwoInstancesStart_OnlyTheOneThatAcquiresTheDutyVacuumsAsync(CancellationToken cancellationToken) {
    var (a, aAcquired) = await _podAsync(cancellationToken);
    var (b, bAcquired) = await _podAsync(cancellationToken);
    var before = await _outboxVacuumsAsync(cancellationToken);

    await a.PassAsync(cancellationToken);
    await b.PassAsync(cancellationToken);

    await Assert.That(new[] { a, b }.Count(p => p.Worker.Holds(ROLE))).IsEqualTo(1);
    await Assert.That(aAcquired.PreviousHolders.Count + bAcquired.PreviousHolders.Count).IsEqualTo(1);
    await Assert.That(aAcquired.PreviousHolders.Concat(bAcquired.PreviousHolders).Single()).IsNull()
      .Because("the first assignment in a fleet has no previous holder");
    await Assert.That(await _outboxVacuumsAsync(cancellationToken) - before).IsEqualTo(1);
  }

  [Test]
  [Timeout(120000)]
  public async Task AnInstanceJoiningAFleetWithALiveHolder_NeverVacuumsAsync(CancellationToken cancellationToken) {
    var (holder, _) = await _podAsync(cancellationToken);
    await holder.PassAsync(cancellationToken);
    var afterHolder = await _outboxVacuumsAsync(cancellationToken);

    var (joiner, joinerAcquired) = await _podAsync(cancellationToken);
    await joiner.PassAsync(cancellationToken);
    await joiner.PassAsync(cancellationToken);
    await holder.PassAsync(cancellationToken);

    await Assert.That(joiner.Worker.Holds(ROLE)).IsFalse();
    await Assert.That(joinerAcquired.PreviousHolders).IsEmpty();
    await Assert.That(await _outboxVacuumsAsync(cancellationToken)).IsEqualTo(afterHolder)
      .Because("neither the joiner nor the holder keeping its duty acquired anything");
  }

  [Test]
  [Timeout(120000)]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AHandOff_VacuumsOnTheNewHolder_UnlessReassignmentIsOffAsync(
      bool maintainOnReassignment, CancellationToken cancellationToken) {
    var (old, _) = await _podAsync(cancellationToken, maintainOnReassignment);
    var (next, nextAcquired) = await _podAsync(cancellationToken, maintainOnReassignment);
    await old.PassAsync(cancellationToken);
    await next.PassAsync(cancellationToken);
    var beforeHandOff = await _outboxVacuumsAsync(cancellationToken);

    await old.Worker.ReleaseHeldAsync();   // a graceful stop hands the duty off
    await next.PassAsync(cancellationToken);

    await Assert.That(next.Worker.Holds(ROLE)).IsTrue();
    await Assert.That(nextAcquired.PreviousHolders).IsEquivalentTo(new Guid?[] { old.InstanceId });
    await Assert.That(await _outboxVacuumsAsync(cancellationToken) - beforeHandOff).IsEqualTo(maintainOnReassignment ? 1 : 0);
  }

  [Test]
  [Timeout(120000)]
  public async Task ATakeoverAfterTheHolderLapsed_VacuumsOnTheNewHolderAsync(CancellationToken cancellationToken) {
    var (dead, _) = await _podAsync(cancellationToken);
    var (survivor, survivorAcquired) = await _podAsync(cancellationToken);
    await dead.PassAsync(cancellationToken);
    var beforeTakeover = await _outboxVacuumsAsync(cancellationToken);

    // The holder stops renewing: one lease of database time later the survivor's pass takes over.
    await ChaosPod.AgeAsync(ConnectionString, ROLE, new RoleAssignmentOptions().Lease + TimeSpan.FromSeconds(1), cancellationToken);
    await survivor.PassAsync(cancellationToken);

    await Assert.That(survivor.Worker.Holds(ROLE)).IsTrue();
    await Assert.That(survivorAcquired.PreviousHolders).IsEquivalentTo(new Guid?[] { dead.InstanceId });
    await Assert.That(await _outboxVacuumsAsync(cancellationToken) - beforeTakeover).IsEqualTo(1);
  }
}
