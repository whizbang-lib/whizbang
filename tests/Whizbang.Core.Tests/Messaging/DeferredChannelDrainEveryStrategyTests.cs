// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Options;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// <para>The deferred outbox channel is drained by every work coordinator strategy's flush (#2228). The
/// channel's contract is that the next flush writes what was queued on it: the event-audit decorator queues
/// an audit record there for every audited direct append, and the dispatcher queues an event there when no
/// strategy is active. Only the Immediate strategy drained it, so under the default Scoped strategy (and
/// Interval and Batch) every record queued there was never stored and stayed in the singleton channel for
/// the life of the process.</para>
/// <para>Each case builds the strategy through <see cref="WorkCoordinatorStrategyFactory"/>, the one place a
/// host's strategy comes from, so a strategy added later without the drain fails here.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/DeferredOutboxDrain.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/WorkCoordinatorStrategyFactory.cs</code-under-test>
public sealed class DeferredChannelDrainEveryStrategyTests {

  [Test]
  [Arguments(WorkCoordinatorStrategy.Scoped)]
  [Arguments(WorkCoordinatorStrategy.Immediate)]
  [Arguments(WorkCoordinatorStrategy.Interval)]
  [Arguments(WorkCoordinatorStrategy.Batch)]
  public async Task Flush_WithOnlyDeferredMessages_StoresThemAndEmptiesTheChannelAsync(WorkCoordinatorStrategy kind) {
    var coordinator = new RecordingWorkCoordinator();
    var channel = new DeferredOutboxChannel();
    await using var provider = _buildProvider(coordinator, channel);
    var strategy = WorkCoordinatorStrategyFactory.Create(kind, provider);
    try {
      var deferred = _outboxMessage();
      await channel.QueueAsync(deferred);

      await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None);

      await Assert.That(coordinator.Stored.Select(m => m.MessageId)).Contains(deferred.MessageId)
        .Because("a record queued on the deferred channel must be stored by the next flush of whatever "
               + "strategy the host runs, or it is lost and held in memory forever (#2228)");
      await Assert.That(channel.HasPending).IsFalse();
    } finally {
      await _disposeAsync(strategy);
    }
  }

  [Test]
  [Arguments(WorkCoordinatorStrategy.Scoped)]
  [Arguments(WorkCoordinatorStrategy.Immediate)]
  [Arguments(WorkCoordinatorStrategy.Interval)]
  [Arguments(WorkCoordinatorStrategy.Batch)]
  public async Task Flush_WithDeferredAndQueuedMessages_StoresDeferredFirstAsync(WorkCoordinatorStrategy kind) {
    var coordinator = new RecordingWorkCoordinator();
    var channel = new DeferredOutboxChannel();
    await using var provider = _buildProvider(coordinator, channel);
    var strategy = WorkCoordinatorStrategyFactory.Create(kind, provider);
    try {
      var deferred = _outboxMessage();
      await channel.QueueAsync(deferred);
      var direct = _outboxMessage();
      await strategy.QueueOutboxMessageAsync(direct);

      await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None);

      await Assert.That(coordinator.Stored.Select(m => m.MessageId).ToArray())
        .IsEquivalentTo(new[] { deferred.MessageId, direct.MessageId }, TUnit.Assertions.Enums.CollectionOrdering.Matching)
        .Because("deferred messages were queued before this flush's own work, so they are written first");
      await Assert.That(channel.HasPending).IsFalse();
    } finally {
      await _disposeAsync(strategy);
    }
  }

  [Test]
  [Arguments(WorkCoordinatorStrategy.Scoped)]
  [Arguments(WorkCoordinatorStrategy.Interval)]
  [Arguments(WorkCoordinatorStrategy.Batch)]
  public async Task Flush_WithNothingQueuedAnywhere_StoresNothingAsync(WorkCoordinatorStrategy kind) {
    var coordinator = new RecordingWorkCoordinator();
    var channel = new DeferredOutboxChannel();
    await using var provider = _buildProvider(coordinator, channel);
    var strategy = WorkCoordinatorStrategyFactory.Create(kind, provider);
    try {
      await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None);

      await Assert.That(coordinator.StoreCalls).IsEqualTo(0)
        .Because("an empty channel and empty queues keep the empty-flush short-circuit");
    } finally {
      await _disposeAsync(strategy);
    }
  }

  private static async Task _disposeAsync(IWorkCoordinatorStrategy strategy) {
    if (strategy is IAsyncDisposable disposable) {
      await disposable.DisposeAsync();
    }
  }

  private static ServiceProvider _buildProvider(IWorkCoordinator coordinator, IDeferredOutboxChannel channel) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    services.AddSingleton(channel);
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddSingleton<IWorkChannelWriter>(new WorkChannelWriter());
    services.AddSingleton<IInboxChannelWriter>(new InboxChannelWriter());
    // A timer that never fires on its own: each case flushes explicitly, so no tick can race it.
    services.AddSingleton(new WorkCoordinatorOptions { IntervalMilliseconds = int.MaxValue, BatchSize = 1_000 });
    services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    services.AddSingleton<ILifecycleMessageDeserializer>(new JsonLifecycleMessageDeserializer());
    services.AddSingleton<IOptionsMonitor<TracingOptions>>(new StaticOptionsMonitor<TracingOptions>(new TracingOptions()));
    services.AddSingleton(Options.Create(new SystemEventOptions()));
    services.AddSingleton(new DetachedLifecycleWork());
    return services.BuildServiceProvider();
  }

  private static OutboxMessage _outboxMessage() {
    var messageId = Guid.NewGuid();
    return new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = new MessageEnvelope<System.Text.Json.JsonElement> {
        MessageId = new MessageId(messageId),
        Payload = System.Text.Json.JsonElement.Parse("{}"),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      },
      Metadata = new EnvelopeMetadata { MessageId = new MessageId(messageId), Hops = [] },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestEvent]], Whizbang.Core",
      StreamId = Guid.NewGuid(),
      IsEvent = true,
      MessageType = "TestEvent"
    };
  }

  private sealed class RecordingWorkCoordinator : IWorkCoordinator {
    public List<OutboxMessage> Stored { get; } = [];
    public int StoreCalls { get; private set; }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });

    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) {
      StoreCalls++;
      Stored.AddRange(messages);
      return Task.CompletedTask;
    }

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) {
      StoreCalls++;
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
