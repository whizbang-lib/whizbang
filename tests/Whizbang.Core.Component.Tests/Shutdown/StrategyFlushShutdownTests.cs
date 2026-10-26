// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Workers;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Tests.Shutdown;

/// <summary>
/// #1721: the interval and batch strategies flush on a timer or a batch-size trigger in the background. Neither flush was
/// awaited by anything: the host disposed the container under a flush still storing, and the strategy's own disposal
/// flush returned early while one was in flight, so the work queued after that flush's snapshot was never stored.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class StrategyFlushShutdownTests {

  /// <summary>Stores outbox rows, holding the first store until released.</summary>
  private sealed class HeldStoreCoordinator(HeldWork held, IServiceProvider scope, ConcurrentBag<Guid> stored)
      : NoOpWorkCoordinator, IWorkCoordinator {
    private static int _calls;

    public static void Reset() => _calls = 0;

    public new async Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) {
      if (Interlocked.Increment(ref _calls) == 1) {
        await held.HoldAsync(scope);
      }
      foreach (var message in messages) {
        stored.Add(message.MessageId);
      }
    }
  }

  private static (IHost Host, HeldWork Held, ConcurrentBag<Guid> Stored) _host<TStrategy>(
      Func<IServiceProvider, TStrategy> create) where TStrategy : class {
    HeldStoreCoordinator.Reset();
    var held = new HeldWork();
    var stored = new ConcurrentBag<Guid>();
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    builder.Services.TryAddWhizbangDefaults();
    builder.Services.AddSingleton<ProviderLiveness>();
    builder.Services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    builder.Services.AddScoped<IWorkCoordinator>(sp => new HeldStoreCoordinator(held, sp, stored));
    // As the generated registration does: one strategy per container, disposed with it
    builder.Services.AddSingleton(create);
    return (builder.Build(), held, stored);
  }

  private static IntervalWorkCoordinatorStrategy _interval(IServiceProvider sp) => new(
    coordinator: null,
    instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
    // The timer never fires on its own here: the test starts each timer flush itself
    options: new WorkCoordinatorOptions { IntervalMilliseconds = int.MaxValue },
    logger: NullLogger<IntervalWorkCoordinatorStrategy>.Instance,
    scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
    lifecycleMessageDeserializer: new PassThroughLifecycleDeserializer(),
    tracingOptions: sp.GetRequiredService<IOptionsMonitor<TracingOptions>>(),
    workChannelWriter: new WorkChannelWriter(),
    inboxChannelWriter: new InboxChannelWriter(),
    detachedWork: sp.GetRequiredService<DetachedLifecycleWork>());

  private static BatchWorkCoordinatorStrategy _batch(IServiceProvider sp, int batchSize) => new(
    coordinator: null,
    instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
    options: new WorkCoordinatorOptions { BatchSize = batchSize, IntervalMilliseconds = int.MaxValue },
    logger: NullLogger<BatchWorkCoordinatorStrategy>.Instance,
    scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
    lifecycleMessageDeserializer: new PassThroughLifecycleDeserializer(),
    tracingOptions: sp.GetRequiredService<IOptionsMonitor<TracingOptions>>(),
    workChannelWriter: new WorkChannelWriter(),
    detachedWork: sp.GetRequiredService<DetachedLifecycleWork>());

  [Test]
  public async Task AnIntervalTimerFlush_InFlight_HostStopWaitsAndStoresWhatWasQueuedAfterItAsync() {
    var (host, held, stored) = _host(_interval);
    using var _ = host;
    await host.StartAsync();
    var strategy = host.Services.GetRequiredService<IntervalWorkCoordinatorStrategy>();
    strategy.QueueOutboxMessage(HostShutdown.OutboxMessage());
    strategy.FlushTimerTick(null);
    await held.Entered.Task;
    var queuedLater = HostShutdown.OutboxMessage();
    strategy.QueueOutboxMessage(queuedLater);

    var waited = await HostShutdown.StopWaitsForAsync(host, strategy.InFlightFlushes, held);

    await Assert.That(waited).IsTrue()
      .Because("the timer flush stores through a scope of the container the host disposes once it has stopped");
    await Assert.That(await held.Finished.Task).IsTrue();
    await Assert.That(stored).Contains(queuedLater.MessageId)
      .Because("work queued while a timer flush was in flight is stored before the container goes, not dropped");
  }

  /// <summary>
  /// The disposal flush found the timer flush's in-progress guard set and returned at once: disposal finished while that
  /// flush still ran, and the work queued after its snapshot was never stored.
  /// </summary>
  [Test]
  public async Task AnIntervalDisposal_WithATimerFlushInFlight_WaitsForItThenFlushesTheRestAsync() {
    var (host, held, stored) = _host(_interval);
    using var _ = host;
    var strategy = host.Services.GetRequiredService<IntervalWorkCoordinatorStrategy>();
    strategy.QueueOutboxMessage(HostShutdown.OutboxMessage());
    strategy.FlushTimerTick(null);
    await held.Entered.Task;
    var queuedLater = HostShutdown.OutboxMessage();
    strategy.QueueOutboxMessage(queuedLater);

    var waited = await HostShutdown.StopWaitsForAsync(() => strategy.DisposeAsync().AsTask(), strategy.InFlightFlushes, held);

    await Assert.That(waited).IsTrue()
      .Because("disposal waits for the timer flush instead of returning on its in-progress guard");
    await Assert.That(stored).Contains(queuedLater.MessageId)
      .Because("the disposal flush runs once the timer flush is done, so nothing queued is left behind");
  }

  [Test]
  public async Task ABatchSizeFlush_InFlight_HostStopWaitsAndStoresWhatWasQueuedAfterItAsync() {
    var (host, held, stored) = _host(sp => _batch(sp, batchSize: 2));
    using var _ = host;
    await host.StartAsync();
    var strategy = host.Services.GetRequiredService<BatchWorkCoordinatorStrategy>();
    strategy.QueueOutboxMessage(HostShutdown.OutboxMessage());
    strategy.QueueOutboxMessage(HostShutdown.OutboxMessage());
    await held.Entered.Task;
    var queuedLater = HostShutdown.OutboxMessage();
    strategy.QueueOutboxMessage(queuedLater);

    var waited = await HostShutdown.StopWaitsForAsync(host, strategy.InFlightFlushes, held);

    await Assert.That(waited).IsTrue();
    await Assert.That(await held.Finished.Task).IsTrue();
    await Assert.That(stored).Contains(queuedLater.MessageId);
  }

  [Test]
  public async Task ADebounceFlush_InFlight_HostStopWaitsAndStoresWhatWasQueuedAfterItAsync() {
    var (host, held, stored) = _host(sp => _batch(sp, batchSize: 100));
    using var _ = host;
    await host.StartAsync();
    var strategy = host.Services.GetRequiredService<BatchWorkCoordinatorStrategy>();
    strategy.QueueOutboxMessage(HostShutdown.OutboxMessage());
    strategy.DebounceTimerTick(null);
    await held.Entered.Task;
    var queuedLater = HostShutdown.OutboxMessage();
    strategy.QueueOutboxMessage(queuedLater);

    var waited = await HostShutdown.StopWaitsForAsync(host, strategy.InFlightFlushes, held);

    await Assert.That(waited).IsTrue();
    await Assert.That(await held.Finished.Task).IsTrue();
    await Assert.That(stored).Contains(queuedLater.MessageId);
  }
}
