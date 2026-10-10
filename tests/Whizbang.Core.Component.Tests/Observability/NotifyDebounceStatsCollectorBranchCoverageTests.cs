// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Branch backfill for <see cref="NotifyDebounceStatsCollector"/>: the cycle-completed event with a
/// subscriber attached (the existing tests only run it with no subscriber).
/// </summary>
[Category("Core")]
[Category("Observability")]
public class NotifyDebounceStatsCollectorBranchCoverageTests {

  private static NotifyDebounceMetrics _newMetrics() => new(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));

  [Test]
  public async Task CycleCompleted_WithSubscriber_FiresAfterTheMetricCacheIsWrittenAsync() {
    var fake = new StaticProvider([new NotifyDebounceKindStats("outbox", 3, 40, 7000, 9)]);
    var services = new ServiceCollection();
    services.AddSingleton<INotifyDebounceStatsProvider>(fake);
    await using var sp = services.BuildServiceProvider();

    var metrics = _newMetrics();
    var worker = new NotifyDebounceStatsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: metrics,
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      logger: NullLogger<NotifyDebounceStatsCollector>.Instance);

    var cycleObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    // Capture the cache state AT the moment the event fires: the contract is that the event is
    // raised only after the cycle's values have landed.
    worker.CycleCompleted += () => cycleObserved.TrySetResult(metrics.GetForTest("outbox").HasValue);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    var cacheWrittenWhenRaised = await cycleObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(cacheWrittenWhenRaised).IsTrue()
      .Because("CycleCompleted must be raised after metrics.Update, so a subscriber sees the cycle's values");
  }

  private sealed class StaticProvider(IReadOnlyList<NotifyDebounceKindStats> toReturn) : INotifyDebounceStatsProvider {
    public Task<IReadOnlyList<NotifyDebounceKindStats>> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(toReturn);
  }
}
