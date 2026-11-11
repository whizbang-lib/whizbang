// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Shutdown;

/// <summary>
/// #1721: the batchers' background work. An idle sweep evicts a quiet stream and waits for its drain worker, which may
/// still be flushing the stream's last batch; the sweep ran fire-and-forget and its stream left the map, so the batcher's
/// stop did not wait for that flush. The transport batch collector flushed on a full batch in the background and its
/// disposal did not wait for that flush either. Each test holds that work, stops its owner the way a stopping host
/// does, and checks the stop waited and the work then ran against a live service provider.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class BatcherShutdownTests {

  private static readonly TimeSpan _sweepInterval = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan _evictAfter = TimeSpan.FromSeconds(10);

  private static IHost _host(Action<IServiceCollection> configure) {
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    builder.Services.TryAddWhizbangDefaults();
    builder.Services.AddSingleton<ProviderLiveness>();
    // The worker pipeline's stop-time flush of the batchers
    builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, BatchStrategyShutdownFlush>());
    configure(builder.Services);
    return builder.Build();
  }

  [Test]
  public async Task AnOutboxIdleSweep_EvictingAStreamMidFlush_HostStopWaitsForTheFlushAsync() {
    var held = new HeldWork();
    var time = new FakeTimeProvider();
    IServiceProvider? root = null;
    using var host = _host(services => services.AddSingleton<IOutboxBatchStrategy>(new SlidingWindowOutboxBatchStrategy(
      (_, _) => held.HoldAsync(root!),
      NullLogger<SlidingWindowOutboxBatchStrategy>.Instance,
      new SlidingWindowOutboxOptions { MaxSize = 1, IdleEvictionWindow = _evictAfter, IdleSweepInterval = _sweepInterval },
      time)));
    root = host.Services;
    await host.StartAsync();
    var strategy = (SlidingWindowOutboxBatchStrategy)host.Services.GetRequiredService<IOutboxBatchStrategy>();

    await strategy.AppendAsync(HostShutdown.OutboxMessage());
    await held.Entered.Task;
    // The sweep evicts the quiet stream and waits, in the background, for its worker still flushing
    time.Advance(_sweepInterval);

    var waited = await HostShutdown.StopWaitsForAsync(host, strategy.IdleSweeps, held);

    await Assert.That(waited).IsTrue()
      .Because("an evicted stream's last flush still stores through services the host disposes once it has stopped");
    await Assert.That(await held.Finished.Task).IsTrue();
  }

  [Test]
  public async Task AnInboxIdleSweep_EvictingAStreamMidFlush_HostStopWaitsForTheFlushAsync() {
    var held = new HeldWork();
    var time = new FakeTimeProvider();
    IServiceProvider? root = null;
    using var host = _host(services => services.AddSingleton<IInboxBatchStrategy>(new SlidingWindowInboxBatchStrategy(
      (_, _) => held.HoldAsync(root!),
      NullLogger<SlidingWindowInboxBatchStrategy>.Instance,
      new SlidingWindowInboxOptions { MaxSize = 1, IdleEvictionWindow = _evictAfter, IdleSweepInterval = _sweepInterval },
      time)));
    root = host.Services;
    await host.StartAsync();
    var strategy = (SlidingWindowInboxBatchStrategy)host.Services.GetRequiredService<IInboxBatchStrategy>();
    var outbox = HostShutdown.OutboxMessage();

    await strategy.AppendAsync(new InboxMessage {
      MessageId = outbox.MessageId,
      HandlerName = "handler",
      Envelope = outbox.Envelope,
      EnvelopeType = outbox.EnvelopeType,
      MessageType = outbox.MessageType,
      StreamId = Guid.CreateVersion7(),
    });
    await held.Entered.Task;
    time.Advance(_sweepInterval);

    var waited = await HostShutdown.StopWaitsForAsync(host, strategy.IdleSweeps, held);

    await Assert.That(waited).IsTrue();
    await Assert.That(await held.Finished.Task).IsTrue();
  }

  [Test]
  public async Task AnApplyIdleSweep_EvictingAStreamMidFlush_StopWaitsForTheFlushAsync() {
    var held = new HeldWork();
    var time = new FakeTimeProvider();
    var services = new ServiceCollection().AddSingleton<ProviderLiveness>().BuildServiceProvider();
    var strategy = new SlidingWindowApplyBatchStrategy(
      (_, _, _) => held.HoldAsync(services),
      NullLogger<SlidingWindowApplyBatchStrategy>.Instance,
      new SlidingWindowApplyOptions { MaxSize = 1, IdleEvictionWindow = _evictAfter, IdleSweepInterval = _sweepInterval },
      time);

    await strategy.AppendAsync(Guid.CreateVersion7());
    await held.Entered.Task;
    time.Advance(_sweepInterval);

    var waited = await HostShutdown.StopWaitsForAsync(async () => {
      await strategy.FlushAndStopAsync();
      await services.DisposeAsync();
    }, strategy.IdleSweeps, held);

    await Assert.That(waited).IsTrue();
    await Assert.That(await held.Finished.Task).IsTrue();
  }

  [Test]
  public async Task APerStreamSerializerIdleSweep_EvictingAStreamMidItem_StopWaitsForTheItemAsync() {
    var held = new HeldWork();
    var time = new FakeTimeProvider();
    var services = new ServiceCollection().AddSingleton<ProviderLiveness>().BuildServiceProvider();
    var serializer = new PerStreamSerializer<Guid>(
      static item => item,
      (_, _) => held.HoldAsync(services),
      NullLogger.Instance,
      options: new PerStreamSerializerOptions {
        DrainBatchWindow = TimeSpan.Zero,
        IdleEvictionWindow = _evictAfter,
        IdleSweepInterval = _sweepInterval,
      },
      timeProvider: time);

    await serializer.EnqueueAsync(Guid.CreateVersion7());
    await held.Entered.Task;
    time.Advance(_sweepInterval);

    var waited = await HostShutdown.StopWaitsForAsync(async () => {
      await serializer.FlushAndStopAsync();
      await services.DisposeAsync();
    }, serializer.IdleSweeps, held);

    await Assert.That(waited).IsTrue();
    await Assert.That(await held.Finished.Task).IsTrue();
  }

  [Test]
  public async Task ATransportBatchCollectorFlush_InFlight_DisposalWaitsForItAsync() {
    var held = new HeldWork();
    var services = new ServiceCollection().AddSingleton<ProviderLiveness>().BuildServiceProvider();
    var collector = new TransportBatchCollector<int>(
      new TransportBatchOptions { BatchSize = 1, SlideMs = int.MaxValue - 1, MaxWaitMs = int.MaxValue - 1 },
      _ => held.HoldAsync(services));

    collector.Enqueue(1);
    await held.Entered.Task;

    var waited = await HostShutdown.StopWaitsForAsync(async () => {
      await collector.DisposeAsync();
      await services.DisposeAsync();
    }, collector.InFlightFlushes, held);

    await Assert.That(waited).IsTrue()
      .Because("a flush started by a full batch still stores what the transport received");
    await Assert.That(await held.Finished.Task).IsTrue();
  }

  private static TransportBatchOptions _flushOnEveryMessage() =>
    new() { BatchSize = 1, SlideMs = int.MaxValue - 1, MaxWaitMs = int.MaxValue - 1 };

  /// <summary>
  /// A flush callback that disposes its own collector (an in-process request that unsubscribes once its reply arrives)
  /// used to wait, inside that flush, for the flush to finish: it never did. Disposal now finishes without waiting for
  /// the flush it was called from. The callback reports whether disposal had finished before it returned, so a disposal
  /// that waits fails this test instead of hanging it.
  /// </summary>
  [Test]
  public async Task ATransportBatchCollector_DisposedFromItsOwnFlush_DoesNotWaitForThatFlushAsync() {
    var finishedInsideTheFlush = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    TransportBatchCollector<int>? collector = null;
    collector = new TransportBatchCollector<int>(_flushOnEveryMessage(), _ => {
      var disposal = collector!.DisposeAsync().AsTask();
      finishedInsideTheFlush.TrySetResult(disposal.IsCompleted);
      return Task.CompletedTask;
    });

    collector.Enqueue(1);

    await Assert.That(await finishedInsideTheFlush.Task).IsTrue()
      .Because("a disposal called from the collector's own flush cannot wait for that flush to finish");
  }

  /// <summary>
  /// The same disposal reached through a continuation the flush callback runs inline: completing a task whose awaiter
  /// then disposes the collector, on the flush's own thread, as an in-process request's reply does.
  /// </summary>
  [Test]
  public async Task ATransportBatchCollector_DisposedByAContinuationItsFlushRanInline_DoesNotWaitForThatFlushAsync() {
    var replied = new TaskCompletionSource();
    var collector = new TransportBatchCollector<int>(_flushOnEveryMessage(), _ => {
      // Without RunContinuationsAsynchronously, the awaiter below runs here, inside the flush
      replied.TrySetResult();
      return Task.CompletedTask;
    });
    var finishedInsideTheFlush = _disposeOnceRepliedAsync();

    collector.Enqueue(1);

    await Assert.That(await finishedInsideTheFlush).IsTrue()
      .Because("the request's continuation runs on the flush's own thread, so its disposal cannot wait for that flush");

    async Task<bool> _disposeOnceRepliedAsync() {
      await replied.Task.ConfigureAwait(false);
      return collector.DisposeAsync().AsTask().IsCompleted;
    }
  }

  /// <summary>
  /// The same disposal reached later on the callback's own awaited path, after it has left the flush's thread.
  /// </summary>
  [Test]
  public async Task ATransportBatchCollector_DisposedAlongItsFlushsAwaitedPath_DoesNotWaitForThatFlushAsync() {
    var finishedInsideTheFlush = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    TransportBatchCollector<int>? collector = null;
    collector = new TransportBatchCollector<int>(_flushOnEveryMessage(), async _ => {
      await Task.Yield();
      finishedInsideTheFlush.TrySetResult(collector!.DisposeAsync().AsTask().IsCompleted);
    });

    collector.Enqueue(1);

    await Assert.That(await finishedInsideTheFlush.Task).IsTrue()
      .Because("the flush is still awaiting this callback, so its disposal cannot wait for that flush");
  }
}
