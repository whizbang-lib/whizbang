// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests.Services;

/// <summary>
/// The maintenance step that prunes the saga claims nothing will read again, and keeps the one that is
/// a record.
/// </summary>
/// <remarks>
/// <para>
/// A saga takes claims as it runs: one per stranded-saga sweep tick, one for its completion and one per
/// continuation it asks for. Nothing removed them, although the docs said something did, so the claim
/// table grew by several rows per saga forever.
/// </para>
/// <para>
/// A sweep claim is dead once its interval has passed, and a completion or continuation claim is taken
/// only when the saga has completed, so each is pruned once older than
/// <see cref="SagaOptions.ClaimRetention"/>. The abandonment claim is kept: it is the record that stops
/// the sweep re-arming an abandoned saga (#971), and it goes only when an operator re-drives the saga.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Sagas/Services/SagaClaimPruneStep.cs</code-under-test>
[Category("Unit")]
[Category("Saga")]
public class SagaClaimPruneStepTests {
  private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-09-30T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

  private static ServiceProvider _services(IClaimedEmissionStore? store, DutyHolderWorker? holder = null, SagaOptions? options = null) {
    var services = new ServiceCollection();
    services.AddSingleton(options ?? new SagaOptions { TimeProvider = new FixedClock(_now) });
    if (store is not null) {
      services.AddSingleton(store);
    }
    if (holder is not null) {
      services.AddSingleton(holder);
    }
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task Run_PrunesSweepCompletionAndContinuationClaimsPastTheRetention_AndKeepsAbandonmentsAsync() {
    var store = new RecordingStore();
    await using var services = _services(store);

    await new SagaClaimPruneStep(NullLogger<SagaClaimPruneStep>.Instance).RunAsync(services, CancellationToken.None);

    await Assert.That(store.Pruned.Select(p => p.Prefix)).IsEquivalentTo(["saga-watchdog-sweep:", "saga-completed:", "saga-continuation:"]);
    await Assert.That(store.Pruned.All(p => p.Before == _now - new SagaOptions().ClaimRetention)).IsTrue()
      .Because("a claim is pruned only once it is older than the retention window");
    await Assert.That(store.Pruned.Any(p => SagaAbandonGuard.ClaimKey("AnySaga", Guid.Empty).StartsWith(p.Prefix, StringComparison.Ordinal))).IsFalse()
      .Because("the abandonment claim is what keeps an abandoned saga from being re-armed; pruning it would undo #971");
  }

  [Test]
  public async Task Run_UsesTheConfiguredRetentionAsync() {
    var store = new RecordingStore { Deletes = 0 };
    await using var services = _services(store, options: new SagaOptions { TimeProvider = new FixedClock(_now), ClaimRetention = TimeSpan.FromDays(2) });

    await new SagaClaimPruneStep(NullLogger<SagaClaimPruneStep>.Instance).RunAsync(services, CancellationToken.None);

    await Assert.That(store.Pruned.Select(p => p.Before).Distinct()).IsEquivalentTo([_now.AddDays(-2)]);
  }

  /// <summary>
  /// With no <see cref="SagaOptions"/> and no clock registered the step still prunes every spent prefix,
  /// on one cutoff taken from the system clock, rather than skipping the cycle. The cutoff's value is
  /// pinned by <see cref="Run_NoOptionsRegistered_MeasuresTheDefaultRetentionFromTheHostClockAsync"/>,
  /// which supplies the clock; this test reads no clock itself.
  /// </summary>
  [Test]
  public async Task Run_NoOptionsRegistered_PrunesOnTheDefaultRetentionFromTheSystemClockAsync() {
    var store = new RecordingStore();
    var services = new ServiceCollection();
    services.AddSingleton<IClaimedEmissionStore>(store);
    await using var provider = services.BuildServiceProvider();

    await new SagaClaimPruneStep(NullLogger<SagaClaimPruneStep>.Instance).RunAsync(provider, CancellationToken.None);

    await Assert.That(store.Pruned.Select(p => p.Prefix)).IsEquivalentTo(SagaClaimPruneStep.SpentClaimPrefixes);
    await Assert.That(store.Pruned.Select(p => p.Before).Distinct().Count()).IsEqualTo(1)
      .Because("one cycle prunes every spent prefix on the same cutoff");
  }

  /// <summary>
  /// With no <see cref="SagaOptions"/> registered the cutoff is the default retention before the host's
  /// clock, the <see cref="TimeProvider"/> in the container, as every other worker reads it.
  /// </summary>
  [Test]
  public async Task Run_NoOptionsRegistered_MeasuresTheDefaultRetentionFromTheHostClockAsync() {
    var store = new RecordingStore();
    var services = new ServiceCollection();
    services.AddSingleton<IClaimedEmissionStore>(store);
    services.AddSingleton<TimeProvider>(new FixedClock(_now));
    await using var provider = services.BuildServiceProvider();

    await new SagaClaimPruneStep(NullLogger<SagaClaimPruneStep>.Instance).RunAsync(provider, CancellationToken.None);

    var expected = _now - new SagaOptions().ClaimRetention;
    await Assert.That(store.Pruned.Select(p => p.Prefix)).IsEquivalentTo(SagaClaimPruneStep.SpentClaimPrefixes);
    await Assert.That(store.Pruned.All(p => p.Before == expected)).IsTrue()
      .Because("the cutoff is the default retention before the registered clock's now");
  }

  /// <summary>With no claim store there is nothing to prune, and the step does not fail the cycle.</summary>
  [Test]
  public async Task Run_NoClaimStore_DoesNothingAsync() {
    await using var services = _services(store: null);

    await new SagaClaimPruneStep(NullLogger<SagaClaimPruneStep>.Instance).RunAsync(services, CancellationToken.None);

    await Assert.That(new SagaClaimPruneStep(NullLogger<SagaClaimPruneStep>.Instance).Name).IsEqualTo("saga-claim-prune");
  }

  /// <summary>
  /// With role assignment managing the maintainer duty, only its holder prunes, so the fleet issues one
  /// delete per cycle rather than one per instance.
  /// </summary>
  /// <param name="holds">Whether this instance holds the maintainer duty.</param>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Run_WithTheMaintainerDutyAssigned_PrunesOnlyOnItsHolderAsync(bool holds) {
    var store = new RecordingStore();
    var holder = _holder(grants: holds);
    await holder.RunOnceAsync(CancellationToken.None);
    await using var services = _services(store, holder);

    await new SagaClaimPruneStep(NullLogger<SagaClaimPruneStep>.Instance).RunAsync(services, CancellationToken.None);

    await Assert.That(holder.Holds(StartupDuties.MAINTAINER)).IsEqualTo(holds)
      .Because("the precondition: the holder loop won or lost the duty as arranged");
    await Assert.That(store.Pruned.Count > 0).IsEqualTo(holds);
  }

  /// <summary>
  /// A holder loop that does not manage the maintainer duty says nothing about it, so every instance
  /// prunes, as without role assignment. The delete is by age and idempotent, so running it on each
  /// instance costs a repeated no-op, not a wrong answer.
  /// </summary>
  [Test]
  public async Task Run_HolderLoopWithoutTheMaintainerDuty_PrunesOnEveryInstanceAsync() {
    var store = new RecordingStore();
    var holder = _holder(grants: false, role: StartupDuties.MIGRATOR);
    await using var services = _services(store, holder);

    await new SagaClaimPruneStep(NullLogger<SagaClaimPruneStep>.Instance).RunAsync(services, CancellationToken.None);

    await Assert.That(store.Pruned).IsNotEmpty();
  }

  /// <summary>The prefixes the step prunes are the ones the guards build their keys with.</summary>
  [Test]
  public async Task SpentClaimPrefixes_MatchTheGuardsKeyConventionsAsync() {
    var sagaId = Guid.CreateVersion7();

    await Assert.That(SagaCompletionGuard.ClaimKey("S", sagaId)).StartsWith("saga-completed:");
    await Assert.That(SagaContinuationGuard.ClaimKey("S", sagaId, "Next")).StartsWith("saga-continuation:");
    await Assert.That(SagaAbandonGuard.ClaimKey("S", sagaId)).StartsWith("saga-abandoned:");
  }

  /// <summary>
  /// The general expiry prune leaves every saga claim to this step: a completion claim lives out the
  /// saga retention instead of going a day after its expiry, and an abandonment claim never goes (#999).
  /// </summary>
  [Test]
  public async Task AddWhizbangSagas_RetainsEverySagaPrefixFromTheGeneralPrune_OnceEachAsync() {
    var prior = SagaItemStreams.AppDefaultNamespace;
    try {
      var services = new ServiceCollection();
      services.AddWhizbangSagas();
      services.AddWhizbangSagas();

      var retained = services
        .Where(d => d.ServiceType == typeof(RetainedClaimKeyPrefix))
        .Select(d => ((RetainedClaimKeyPrefix)d.ImplementationInstance!).KeyPrefix)
        .ToList();
      await Assert.That(retained).IsEquivalentTo(["saga-watchdog-sweep:", "saga-completed:", "saga-continuation:", "saga-abandoned:"]);
      await Assert.That(SagaAbandonGuard.ClaimKey("S", Guid.Empty)).StartsWith(SagaClaimPruneStep.ABANDONED_CLAIM_PREFIX);
    } finally {
      SagaItemStreams.AppDefaultNamespace = prior;
    }
  }

  [Test]
  public async Task Options_ClaimRetention_MustBePositiveAsync() {
    await Assert.That(() => new SagaOptions { ClaimRetention = TimeSpan.Zero }).ThrowsExactly<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task AddWhizbangSagas_RegistersTheStepOnce_HoweverOftenItIsCalledAsync() {
    var prior = SagaItemStreams.AppDefaultNamespace;
    try {
      var services = new ServiceCollection();
      services.AddWhizbangSagas();
      services.AddWhizbangSagas();

      await Assert.That(services.Count(d => d.ServiceType == typeof(IMaintenanceStep)
                                         && d.ImplementationType == typeof(SagaClaimPruneStep))).IsEqualTo(1);
    } finally {
      SagaItemStreams.AppDefaultNamespace = prior;
    }
  }

  private static DutyHolderWorker _holder(bool grants, string role = StartupDuties.MAINTAINER) =>
    new(new Elector(grants), new NoOwedWork(), [new Handler(role)],
      Options.Create(new RoleAssignmentOptions { Roles = { StartupDuties.MIGRATOR } }),
      NullLogger<DutyHolderWorker>.Instance, notify: null);

  private sealed record PruneCall(string Prefix, DateTimeOffset Before);

  private sealed class RecordingStore : IClaimedEmissionStore {
    public List<PruneCall> Pruned { get; } = [];
    /// <summary>How many claims each prune reports deleting.</summary>
    public int Deletes { get; init; } = 1;
    public Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<int> PruneAsync(string keyPrefix, DateTimeOffset claimedBefore, CancellationToken cancellationToken) {
      Pruned.Add(new PruneCall(keyPrefix, claimedBefore));
      return Task.FromResult(Deletes);
    }
  }

  private sealed class FixedClock(DateTimeOffset now) : TimeProvider {
    public override DateTimeOffset GetUtcNow() => now;
  }

  private sealed class Grant : IDutyGrant {
    public string Duty => StartupDuties.MAINTAINER;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class Elector(bool grants) : IDutyElector {
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) =>
      Task.FromResult(grants ? DutyAttempt.Granted(new Grant()) : DutyAttempt.Lost(DutyRefusal.Contended, "held elsewhere"));
  }

  private sealed class NoOwedWork : IPendingDutyWorkStore {
    public Task OweAsync(string role, string workKey, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<PendingDutyWork>> ListOwedAsync(string role, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<PendingDutyWork>>([]);
    public Task<DutyWorkCompletion> CompleteAsync(PendingDutyWork work, IDutyGrant grant, CancellationToken cancellationToken) =>
      Task.FromResult(DutyWorkCompletion.Completed);
    public Task<bool> RecordFailureAsync(PendingDutyWork work, IDutyGrant grant, string failure, CancellationToken cancellationToken) =>
      Task.FromResult(true);
  }

  private sealed class Handler(string role) : IDutyWorkHandler {
    public string Role => role;
    public string WorkKey => "SagaClaimPruneTest";
    public ValueTask<DutyWorkResult> RunAsync(IDutyGrant grant, CancellationToken cancellationToken) =>
      ValueTask.FromResult(DutyWorkResult.Done());
  }
}
