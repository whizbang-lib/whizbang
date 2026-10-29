// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.FlushFailure;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Tests.Workers;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// #1871 through the real dispatch path: a receptor's event returned through <c>LocalInvokeAsync</c> cascades to the
/// outbox through the stream-affinity strategy and the sliding-window batcher, whose registered flush callback stores
/// it through <see cref="IWorkCoordinator.StoreOutboxMessagesAsync"/>. When that store failed for a batch, the batch was
/// dropped: the caller's <c>LocalInvokeAsync</c> had already returned, and nothing ever wrote the event. A transient
/// failure is now retried, and a batch that still cannot be stored is dead-lettered through the coordinator and
/// reported to every <see cref="IOutboxFlushFailureObserver"/>; <c>LocalInvokeAsync</c> still does not wait for the
/// write.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/SlidingWindowOutboxBatchStrategy.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/StreamAffinityWorkCoordinatorStrategy.cs</code-under-test>
public class LocalInvokeResultOutboxFlushFailureComponentTests {
  private const int STREAMS = 126;

  private static readonly TimeSpan _idleSweep = TimeSpan.FromMinutes(5);

  /// <summary>
  /// 126 commands, one per stream, each returning its event through <c>LocalInvokeAsync</c>. The store fails once
  /// for every 10th stream (12 of 126, inside the reported 7-13%), the way a remote database fails a few of many concurrent
  /// connections; a second try succeeds. Every returned event has to reach the outbox store.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task LocalInvokeResults_StoreFailsOnceForSomeStreams_EveryReturnedEventReachesTheOutboxAsync(
      CancellationToken cancellationToken) {
    var coordinator = new FailingOnceWorkCoordinator();
    var clock = new TimerAdvancingClock(_idleSweep);
    await using var provider = _buildProvider(coordinator, clock);
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var returned = new List<Guid>(STREAMS);
    for (var i = 1; i <= STREAMS; i++) {
      var stream = (Guid)TrackedGuid.New();
      if (i % 10 == 0) {
        coordinator.FailFirstStoreOf(stream);
      }
      var recorded = await dispatcher.LocalInvokeAsync<FlRecordedEvent>(new FlRecordCommand(stream));
      returned.Add(recorded.Id);
    }

    // What the host does as it stops: every per-stream buffer flushed and its drain worker awaited, its retries'
    // waits let elapse on the fake clock.
    await clock.AdvanceTimersUntilAsync(
      provider.GetRequiredService<IOutboxBatchStrategy>().FlushAndStopAsync(CancellationToken.None), cancellationToken);

    await Assert.That(coordinator.Failures).IsEqualTo(STREAMS / 10)
      .Because("every chosen stream's first store has to really fail, or the assertion below would hold vacuously");
    var eventTypeName = TypeNameFormatter.AssemblyQualifiedName(typeof(FlRecordedEvent));
    var storedStreams = coordinator.Stored
      .Where(m => m.MessageType == eventTypeName && m.StreamId is not null)
      .Select(m => m.StreamId!.Value)
      .ToHashSet();
    var missing = returned.Where(stream => !storedStreams.Contains(stream)).ToList();
    await Assert.That(missing).IsEmpty()
      .Because($"{missing.Count} of {STREAMS} events returned through LocalInvokeAsync never reached the outbox and "
             + "no caller saw a failure: the batcher dropped each failed flush, promising a re-emission nothing makes (#1871)");
  }

  /// <summary>
  /// A store that keeps failing transiently gives the batch up after its attempts and dead-letters it through the
  /// coordinator, as wh_outbox dead letters with reason OutboxStoreFailed, and every observer is told, with the
  /// messages, the cause and that they were dead-lettered.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task LocalInvokeResult_StoreKeepsFailing_IsDeadLetteredAndObserversAreToldAsync(CancellationToken cancellationToken) {
    var coordinator = new FailingOnceWorkCoordinator { AlwaysFail = true };
    var observer = new RecordingObserver();
    var clock = new TimerAdvancingClock(_idleSweep);
    await using var provider = _buildProvider(coordinator, clock, services => {
      services.AddSingleton<IOutboxFlushFailureObserver>(observer);
      services.Configure<SlidingWindowOutboxOptions>(options => options.MaxFlushAttempts = 2);
    });
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var stream = (Guid)TrackedGuid.New();

    var recorded = await dispatcher.LocalInvokeAsync<FlRecordedEvent>(new FlRecordCommand(stream));
    await clock.AdvanceTimersUntilAsync(
      provider.GetRequiredService<IOutboxBatchStrategy>().FlushAndStopAsync(CancellationToken.None), cancellationToken);

    var eventTypeName = TypeNameFormatter.AssemblyQualifiedName(typeof(FlRecordedEvent));
    var deadLettered = coordinator.DeadLettered.Where(d => d.Message.MessageType == eventTypeName).ToList();
    await Assert.That(deadLettered).Count().IsEqualTo(1)
      .Because("the event the receptor returned is kept in the dead-letter table, from where recovery stores it");
    await Assert.That(deadLettered[0].Message.StreamId).IsEqualTo(recorded.Id);
    await Assert.That(deadLettered[0].Request.FailureReason).IsEqualTo(MessageFailureReason.OutboxStoreFailed);
    await Assert.That(deadLettered[0].Request.Attempts).IsEqualTo(2);
    await Assert.That(deadLettered[0].Request.ErrorText).Contains("too many clients");
    var told = observer.Failures.Single(f => f.Messages.Any(m => m.MessageType == eventTypeName));
    await Assert.That(told.Cause).IsEqualTo(OutboxFlushFailureCause.RetriesExhausted);
    await Assert.That(told.DeadLettered).IsTrue();
  }

  private static ServiceProvider _buildProvider(IWorkCoordinator coordinator, TimeProvider clock, Action<IServiceCollection>? configure = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(clock);
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped(_ => coordinator);
    services.AddSingleton<IEnvelopeSerializer, PolicyContextKeepingEnvelopeSerializer>();
    // One message per batch: each stream's drain worker flushes as soon as its event is appended, without the clock.
    services.Configure<SlidingWindowOutboxOptions>(options => {
      options.MaxSize = 1;
      options.IdleSweepInterval = _idleSweep;
      options.IdleEvictionWindow = TimeSpan.FromSeconds(30);
    });
    // The real batcher and its real flush callback, behind the stream-affinity strategy the drivers register.
    services.AddWhizbangWorkers();
    services.AddScoped<IWorkCoordinatorStrategy>(sp =>
      WorkCoordinatorStrategyFactory.WrapWithStreamAffinity(new PolicyRecordingOutbox(), sp));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    configure?.Invoke(services);
    var provider = services.BuildServiceProvider();
    provider.GetRequiredService<ISchemaReadyGate>().MarkReady();
    return provider;
  }

  /// <summary>Told about each batch that was not stored.</summary>
  private sealed class RecordingObserver : IOutboxFlushFailureObserver {
    private readonly ConcurrentQueue<OutboxFlushFailure> _failures = new();

    public IReadOnlyList<OutboxFlushFailure> Failures => [.. _failures];

    public ValueTask OnOutboxFlushFailedAsync(OutboxFlushFailure failure, CancellationToken cancellationToken) {
      _failures.Enqueue(failure);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Stores outbox messages in memory, failing the first store of each chosen stream (or every store) the way a
  /// database out of connection slots does, and keeps what it is asked to dead-letter.
  /// </summary>
  private sealed class FailingOnceWorkCoordinator : IWorkCoordinator {
    private readonly ConcurrentDictionary<Guid, byte> _failOnce = new();
    private readonly ConcurrentQueue<OutboxMessage> _stored = new();
    private readonly ConcurrentQueue<(OutboxMessage Message, OutboxDeadLetterRequest Request)> _deadLettered = new();
    private int _failures;

    public bool AlwaysFail { get; init; }

    public int Failures => Volatile.Read(ref _failures);

    public IReadOnlyList<OutboxMessage> Stored => [.. _stored];

    public IReadOnlyList<(OutboxMessage Message, OutboxDeadLetterRequest Request)> DeadLettered => [.. _deadLettered];

    public void FailFirstStoreOf(Guid stream) => _failOnce.TryAdd(stream, 0);

    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) {
      if (AlwaysFail || messages.Any(m => m.StreamId is { } stream && _failOnce.TryRemove(stream, out _))) {
        Interlocked.Increment(ref _failures);
        return Task.FromException(FakeDbException.WithSqlState("53300", message: "sorry, too many clients already"));
      }
      foreach (var message in messages) {
        _stored.Enqueue(message);
      }
      return Task.CompletedTask;
    }

    public Task<bool> StoreOutboxDeadLettersAsync(OutboxMessage[] messages, OutboxDeadLetterRequest request, CancellationToken cancellationToken = default) {
      foreach (var message in messages) {
        _deadLettered.Enqueue((message, request));
      }
      return Task.FromResult(true);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
