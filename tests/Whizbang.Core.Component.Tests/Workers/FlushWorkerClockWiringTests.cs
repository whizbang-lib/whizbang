// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1868: the five workers that coalesce through a <see cref="BatchFlusher{T}"/> hand it the host's registered
/// <see cref="TimeProvider"/>, so the coalesce window, the retry backoff and the shutdown drain bound all run on the
/// host's clock rather than the system clock.
/// </summary>
/// <remarks>
/// Each worker is resolved through the library's own registration with a fake clock registered as the host's
/// <see cref="TimeProvider"/>. One enqueued item opens the flusher's coalesce window: it is armed on the registered
/// clock, or the wait for it never ends and the test fails at its timeout. The schema gate is never opened, so the
/// flush that closing the window starts parks on it; stopping the worker then arms the drain bound on the same clock,
/// and moving the clock past it is what lets the stop finish.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxCompletionFlushWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveCompletionFlushWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/FailureFlushWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/LeaseRenewalWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/InboxHandlerWorker.cs</code-under-test>
public class FlushWorkerClockWiringTests {

  [Test]
  [Timeout(30000)]
  public async Task OutboxCompletionFlushWorker_RunsItsFlusherOnTheRegisteredClockAsync(CancellationToken cancellationToken) {
    await _assertFlusherRunsOnTheRegisteredClockAsync(
      sp => (sp.GetRequiredService<OutboxCompletionFlushWorker>(),
             sp.GetRequiredService<IOptions<OutboxCompletionFlushWorkerOptions>>().Value.Flusher),
      (sp, ct) => sp.GetRequiredService<OutboxCompletionFlushWorker>().EnqueueAsync(TrackedGuid.New(), ct),
      cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task PerspectiveCompletionFlushWorker_RunsItsFlusherOnTheRegisteredClockAsync(CancellationToken cancellationToken) {
    await _assertFlusherRunsOnTheRegisteredClockAsync(
      sp => (sp.GetRequiredService<PerspectiveCompletionFlushWorker>(),
             sp.GetRequiredService<IOptions<PerspectiveCompletionFlushWorkerOptions>>().Value.Flusher),
      (sp, ct) => sp.GetRequiredService<PerspectiveCompletionFlushWorker>().EnqueueEventWorkIdAsync(TrackedGuid.New(), ct),
      cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task FailureFlushWorker_RunsItsFlusherOnTheRegisteredClockAsync(CancellationToken cancellationToken) {
    await _assertFlusherRunsOnTheRegisteredClockAsync(
      sp => (sp.GetRequiredService<FailureFlushWorker>(),
             sp.GetRequiredService<IOptions<FailureFlushWorkerOptions>>().Value.Flusher),
      (sp, ct) => sp.GetRequiredService<FailureFlushWorker>().EnqueueAsync(
        WorkCategory.Inbox,
        new MessageFailure { MessageId = TrackedGuid.New(), CompletedStatus = MessageProcessingStatus.Stored, Error = "failed" },
        ct),
      cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task LeaseRenewalWorker_RunsItsFlusherOnTheRegisteredClockAsync(CancellationToken cancellationToken) {
    await _assertFlusherRunsOnTheRegisteredClockAsync(
      sp => (sp.GetRequiredService<LeaseRenewalWorker>(),
             sp.GetRequiredService<IOptions<LeaseRenewalWorkerOptions>>().Value.Flusher),
      (sp, ct) => sp.GetRequiredService<LeaseRenewalWorker>().EnqueueAsync(WorkCategory.Inbox, TrackedGuid.New(), ct),
      cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task InboxHandlerWorker_RunsItsFlusherOnTheRegisteredClockAsync(CancellationToken cancellationToken) {
    await _assertFlusherRunsOnTheRegisteredClockAsync(
      sp => (sp.GetRequiredService<InboxHandlerWorker>(),
             sp.GetRequiredService<IOptions<InboxHandlerWorkerOptions>>().Value.Flusher),
      (sp, ct) => sp.GetRequiredService<InboxHandlerWorker>().EnqueueAsync(
        new HandlerCommitRequest(
          HandlerId: TrackedGuid.New(),
          InstanceId: TrackedGuid.New(),
          ServiceName: "svc",
          HostName: "host",
          ProcessId: 1,
          PartitionCount: 2,
          InboxCompletion: new HandlerInboxCompletion(TrackedGuid.New(), 0)),
        ct),
      cancellationToken);
  }

  private static async Task _assertFlusherRunsOnTheRegisteredClockAsync(
      Func<IServiceProvider, (IHostedService Worker, BatchFlusherOptions Flusher)> resolve,
      Func<IServiceProvider, CancellationToken, ValueTask> enqueueOne,
      CancellationToken cancellationToken) {
    var clock = new ArmingClock();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<TimeProvider>(clock);
    services.AddWhizbangWorkers();
    services.TryAddSingleton<IPinnedConnectionPool>(NoOpPinnedConnectionPool.Instance);
    services.TryAddSingleton<IInboxChannelWriter, InboxChannelWriter>();
    await using var sp = services.BuildServiceProvider();
    var (worker, flusher) = resolve(sp);
    var window = TimeSpan.FromMilliseconds(flusher.CoalesceWindowMs);
    var drainBound = TimeSpan.FromMilliseconds(flusher.DrainTimeoutMs);

    await enqueueOne(sp, cancellationToken);

    // One item is below every worker's immediate-flush threshold, so the flusher opens its coalesce window
    await clock.WaitForArmedAsync(window, cancellationToken);
    clock.Advance(window);

    // The flush parks on the schema gate, which nothing opens; the stop's drain bound is what ends it
    var stop = worker.StopAsync(CancellationToken.None);
    await clock.WaitForArmedAsync(drainBound, cancellationToken);
    clock.Advance(drainBound);
    await stop.WaitAsync(cancellationToken);

    await Assert.That(stop.IsCompletedSuccessfully).IsTrue()
      .Because("the worker's flusher waited out its window and its drain bound on the host's registered clock");
  }
}
