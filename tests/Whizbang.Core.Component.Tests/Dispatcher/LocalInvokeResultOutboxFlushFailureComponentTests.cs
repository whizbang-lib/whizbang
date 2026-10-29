// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
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
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// #1871 through the real dispatch path: a receptor's event returned through <c>LocalInvokeAsync</c> cascades to the
/// outbox through the stream-affinity strategy and the sliding-window batcher, whose registered flush callback stores
/// it through <see cref="IWorkCoordinator.StoreOutboxMessagesAsync"/>. When that store fails for a batch, the batch is
/// dropped: the caller's <c>LocalInvokeAsync</c> already returned, and nothing ever writes the event.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/SlidingWindowOutboxBatchStrategy.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/StreamAffinityWorkCoordinatorStrategy.cs</code-under-test>
public class LocalInvokeResultOutboxFlushFailureComponentTests {
  private const int STREAMS = 126;

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
    await using var provider = _buildProvider(coordinator);
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

    // What the host does as it stops: every per-stream buffer flushed and its drain worker awaited.
    await provider.GetRequiredService<IOutboxBatchStrategy>().FlushAndStopAsync(CancellationToken.None)
      .WaitAsync(cancellationToken);

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

  private static ServiceProvider _buildProvider(IWorkCoordinator coordinator) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<TimeProvider>(new FakeTimeProvider());
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped(_ => coordinator);
    services.AddSingleton<IEnvelopeSerializer, PolicyContextKeepingEnvelopeSerializer>();
    // One message per batch: each stream's drain worker flushes as soon as its event is appended, without the clock.
    services.Configure<SlidingWindowOutboxOptions>(options => {
      options.MaxSize = 1;
      options.IdleSweepInterval = TimeSpan.FromMinutes(5);
      options.IdleEvictionWindow = TimeSpan.FromSeconds(30);
    });
    // The real batcher and its real flush callback, behind the stream-affinity strategy the drivers register.
    services.AddWhizbangWorkers();
    services.AddScoped<IWorkCoordinatorStrategy>(sp =>
      WorkCoordinatorStrategyFactory.WrapWithStreamAffinity(new PolicyRecordingOutbox(), sp));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    provider.GetRequiredService<ISchemaReadyGate>().MarkReady();
    return provider;
  }

  /// <summary>Stores outbox messages in memory, failing the first store of each chosen stream.</summary>
  private sealed class FailingOnceWorkCoordinator : IWorkCoordinator {
    private readonly ConcurrentDictionary<Guid, byte> _failOnce = new();
    private readonly ConcurrentQueue<OutboxMessage> _stored = new();
    private int _failures;

    public int Failures => Volatile.Read(ref _failures);

    public IReadOnlyList<OutboxMessage> Stored => [.. _stored];

    public void FailFirstStoreOf(Guid stream) => _failOnce.TryAdd(stream, 0);

    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) {
      if (messages.Any(m => m.StreamId is { } stream && _failOnce.TryRemove(stream, out _))) {
        Interlocked.Increment(ref _failures);
        return Task.FromException(new TimeoutException("Timeout during reading attempt"));
      }
      foreach (var message in messages) {
        _stored.Enqueue(message);
      }
      return Task.CompletedTask;
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
