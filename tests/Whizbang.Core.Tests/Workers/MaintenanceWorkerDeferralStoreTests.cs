// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The maintenance sweep keeps its forced-pass ceiling in the service's store (#2288): a deferred tick records the first
/// deferral, and a sweep that ran clears it once it ends, thrown or not.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/HousekeepingDeferralStore.cs</code-under-test>
/// <docs>operations/workers/housekeeping-arbitration#the-ceiling-survives-a-restart</docs>
[Category("Workers")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class MaintenanceWorkerDeferralStoreTests {

  private static readonly DateTimeOffset _origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private static readonly string[] _maintenanceOnly = ["maintenance"];

  private static readonly (string, DateTimeOffset)[] _maintenanceAtOrigin = [("maintenance", _origin)];

  private static (MaintenanceWorker Worker, DeferralRecordingCoordinator Store, FakeTimeProvider Clock) _build() {
    var clock = new FakeTimeProvider(_origin);
    var store = new DeferralRecordingCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(store);
    var sp = services.BuildServiceProvider();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 10 }),
      NullLogger<MaintenanceWorker>.Instance,
      metrics: null,
      housekeeping: new HousekeepingCoordinator(new HousekeepingCoordinator.Settings { SettledCooldown = TimeSpan.Zero }, clock));
    return (worker, store, clock);
  }

  [Test]
  public async Task ADeferredSweep_RecordsItsFirstDeferral_AndAPassThatEnds_ClearsItAsync() {
    var (worker, store, clock) = _build();
    store.Backlog = new ServiceBacklog {
      UnprocessedInboxRows = 500,
      HousekeepingDeferrals = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal),
    };
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    var recordedWhileDeferred = store.Recorded.ToList();
    var clearedWhileDeferred = store.Cleared.ToList();

    clock.Advance(TimeSpan.FromMinutes(10));
    store.Backlog = new ServiceBacklog {
      HousekeepingDeferrals = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal) { ["maintenance"] = _origin },
    };
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(recordedWhileDeferred).IsEquivalentTo(_maintenanceAtOrigin)
      .Because("the first refused tick of a run starts the ceiling every process and every instance measures from");
    await Assert.That(clearedWhileDeferred).IsEmpty();
    await Assert.That(store.Cleared).IsEquivalentTo(_maintenanceOnly)
      .Because("a sweep that ran and ended re-arms the stored ceiling, as it re-arms the in-memory one");
    await Assert.That(store.Recorded).Count().IsEqualTo(1);
  }
}
