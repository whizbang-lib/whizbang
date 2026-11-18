// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1999: the maintenance worker restricts the ephemeral body reap to the batch it read for the destruction hook, whatever
/// the hook decided, so a body that becomes eligible after the read is never reaped unseen. With nothing registered to see
/// a batch, no batch is read and the reap is unrestricted, as before.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#reap-deletes-what-the-hook-saw</docs>
public class MaintenanceWorkerReapScopeTests {
  private static readonly DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

  private static readonly EphemeralDestructionTarget[] _batch = [
    new(new Guid("0192f1a0-0000-7000-8000-000000000011"), new Guid("0192f1a0-0000-7000-8000-0000000000b1"), "A"),
    new(new Guid("0192f1a0-0000-7000-8000-000000000012"), new Guid("0192f1a0-0000-7000-8000-0000000000b2"), "B"),
  ];

  private static readonly Guid[] _batchIds = [.. _batch.Select(t => t.EventId)];

  private sealed class Hook(Func<DestructionResult> decide) : IDestructionHook {
    public int After { get; private set; }

    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(decide());

    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      After++;
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Records which maintenance call ran: the unrestricted one, or the restricted one and its ids.</summary>
  private sealed class Coordinator(IReadOnlyList<EphemeralDestructionTarget> targets) : NoOpWorkCoordinator, IWorkCoordinator {
    public int Unrestricted { get; private set; }
    public List<IReadOnlyCollection<Guid>?> Restricted { get; } = [];

    Task<IReadOnlyList<EphemeralDestructionTarget>> IWorkCoordinator.GetEphemeralBodiesAboutToReapAsync(CancellationToken cancellationToken) =>
      Task.FromResult(targets);

    Task<int> IWorkCoordinator.RecordDestructionFailureAsync(IReadOnlyList<Guid> eventIds, DateTimeOffset retryHoldUntil, int maxRetries,
        OnDestroyFailure onFailure, CancellationToken cancellationToken) => Task.FromResult(1);

    Task<IReadOnlyList<MaintenanceResult>> IWorkCoordinator.PerformMaintenanceAsync(CancellationToken cancellationToken) {
      Unrestricted++;
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }

    Task<IReadOnlyList<MaintenanceResult>> IWorkCoordinator.PerformMaintenanceAsync(IReadOnlyCollection<Guid>? ephemeralReapEventIds,
        CancellationToken cancellationToken) {
      Restricted.Add(ephemeralReapEventIds);
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }
  }

  /// <summary>An engine that implements only the unrestricted call, so the restricted one runs its default body.</summary>
  private sealed class UnrestrictedOnly : NoOpWorkCoordinator, IWorkCoordinator {
    public int Calls { get; private set; }

    Task<IReadOnlyList<MaintenanceResult>> IWorkCoordinator.PerformMaintenanceAsync(CancellationToken cancellationToken) {
      Calls++;
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([new MaintenanceResult("t", 1, 0, "ok")]);
    }
  }

  private static MaintenanceWorker _worker(IWorkCoordinator coordinator, IDestructionHook? hook, ICompactionHandler? compactor = null) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    services.AddSingleton<TimeProvider>(new FakeTimeProvider(_now));
    if (hook is not null) {
      services.AddSingleton(hook);
    }
    if (compactor is not null) {
      services.AddSingleton(compactor);
    }
    return new MaintenanceWorker(
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, StuckRowSentinelEnabled = false }),
      NullLogger<MaintenanceWorker>.Instance);
  }

  private static async Task<Coordinator> _runAsync(IReadOnlyList<EphemeralDestructionTarget> targets, IDestructionHook? hook) {
    var coordinator = new Coordinator(targets);
    await _worker(coordinator, hook).RunMaintenanceOnceAsync(CancellationToken.None);
    return coordinator;
  }

  [Test]
  public async Task WithNoHook_NoBatchIsRead_AndTheReapIsUnrestrictedAsync() {
    var coordinator = await _runAsync(_batch, hook: null);

    await Assert.That(coordinator.Unrestricted).IsEqualTo(1)
      .Because("nothing is registered to see a batch, so every eligible body goes, as before");
    await Assert.That(coordinator.Restricted).IsEmpty();
  }

  [Test]
  public async Task AHookThatProceeds_RestrictsTheReapToTheBatchItSawAsync() {
    var hook = new Hook(() => DestructionResult.Proceed());
    var coordinator = await _runAsync(_batch, hook);

    await Assert.That(coordinator.Unrestricted).IsEqualTo(0);
    await Assert.That(coordinator.Restricted).Count().IsEqualTo(1);
    await Assert.That(coordinator.Restricted[0]).IsEquivalentTo(_batchIds);
    await Assert.That(hook.After).IsEqualTo(1);
  }

  [Test]
  public async Task AnEmptyBatch_RestrictsTheReapToNothingAsync() {
    // The hook saw nothing, so a body that became eligible after the read must wait for the next read.
    var hook = new Hook(() => DestructionResult.Proceed());
    var coordinator = await _runAsync([], hook);

    await Assert.That(coordinator.Restricted).Count().IsEqualTo(1);
    await Assert.That(coordinator.Restricted[0]).IsEmpty();
    await Assert.That(coordinator.Unrestricted).IsEqualTo(0);
    await Assert.That(hook.After).IsEqualTo(0);
  }

  [Test]
  public async Task AHeldBatch_CanceledDeferredOrUnavailable_StillRestrictsTheReapToItAsync() {
    // Held bodies stay in the restriction: the hold keeps them, and nothing outside the batch may go.
    foreach (var decide in new Func<DestructionResult>[] {
        () => DestructionResult.Canceled,
        () => DestructionResult.Defer(_now.AddMinutes(5)),
        () => DestructionResult.Proceed(Disposition.CryptoShred),
    }) {
      var hook = new Hook(decide);
      var coordinator = await _runAsync(_batch, hook);

      await Assert.That(coordinator.Restricted).Count().IsEqualTo(1);
      await Assert.That(coordinator.Restricted[0]).IsEquivalentTo(_batchIds);
      await Assert.That(coordinator.Unrestricted).IsEqualTo(0);
      await Assert.That(hook.After).IsEqualTo(0).Because("nothing was destroyed, so nothing is announced as destroyed");
    }
  }

  [Test]
  public async Task AHookThatThrows_RestrictsTheReapToTheBatch_SoAForcedDeleteReachesOnlyItAsync() {
    var hook = new Hook(() => throw new InvalidOperationException("hook down"));
    var coordinator = await _runAsync(_batch, hook);

    await Assert.That(coordinator.Restricted).Count().IsEqualTo(1);
    await Assert.That(coordinator.Restricted[0]).IsEquivalentTo(_batchIds)
      .Because("past the retry cap the failure path lifts the hold so the reap force-deletes the batch, and only the batch");
    await Assert.That(coordinator.Unrestricted).IsEqualTo(0);
  }

  [Test]
  public async Task TheRestrictedCall_OnAnEngineThatImplementsOnlyTheUnrestrictedOne_RunsItAsync() {
    IWorkCoordinator engine = new UnrestrictedOnly();

    var results = await engine.PerformMaintenanceAsync(_batchIds, CancellationToken.None);

    await Assert.That(((UnrestrictedOnly)engine).Calls).IsEqualTo(1)
      .Because("an engine without the ephemeral body offload reads an empty batch, so it has nothing to restrict");
    await Assert.That(results).Count().IsEqualTo(1);
  }
}
