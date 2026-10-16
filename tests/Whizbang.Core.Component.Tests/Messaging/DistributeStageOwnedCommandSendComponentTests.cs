// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.DistributeStageSends.Owned;
using Whizbang.Core.Component.Tests.DistributeStageSends.Upstream;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Core.Component.Tests.DistributeStageSends;

/// <summary>
/// #1379 at the distribute stages: a receptor that fires at PreDistributeInline, PreDistributeDetached,
/// DistributeDetached, PostDistributeInline or PostDistributeDetached sends a command the service owns,
/// handled here by a receptor that returns no result. The command must be handled exactly once,
/// in-process, and never reach the work coordinator as an outbox or inbox row. Before the fix SendAsync
/// answered Accepted and dropped the command from every stage alike.
/// </summary>
/// <remarks>
/// <para>
/// The distribute stages fire during a work-coordinator strategy flush, for every row the flush
/// stores, and only when the strategy was built with a scope factory and a lifecycle message
/// deserializer. Each strategy here is built by <see cref="WorkCoordinatorStrategyFactory"/>, the
/// production wiring that supplies both, over the REAL generated dispatcher of this assembly and a
/// recording coordinator in place of the database.
/// </para>
/// <para>
/// The trigger is an event from a namespace the service does not own, so PublishAsync queues its row
/// and the strategy flushes it: Scoped and Immediate inside PublishAsync, Batch (batch size one) on its
/// own background flush. The owned command's handling never depends on that flush: it runs in-process
/// from the hook. Detached stages run on a fresh task and scope per message, so every assertion waits
/// on a signal for its exact transition (the receptor completing, the hook's SendAsync returning, the
/// trigger row reaching the coordinator), never on time.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/WorkCoordinatorFlushHelper.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/LifecycleInvocationHelper.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/WorkCoordinatorStrategyFactory.cs</code-under-test>
[Category("Component")]
public class DistributeStageOwnedCommandSendComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.DistributeStageSends.Owned";
  private static readonly TimeSpan _signalGuard = TimeSpan.FromSeconds(30);

  [Test]
  [Arguments(LifecycleStage.PreDistributeInline, FlushPath.Scoped)]
  [Arguments(LifecycleStage.PreDistributeDetached, FlushPath.Scoped)]
  [Arguments(LifecycleStage.DistributeDetached, FlushPath.Scoped)]
  [Arguments(LifecycleStage.PostDistributeInline, FlushPath.Scoped)]
  [Arguments(LifecycleStage.PostDistributeDetached, FlushPath.Scoped)]
  [Arguments(LifecycleStage.PreDistributeInline, FlushPath.Immediate)]
  [Arguments(LifecycleStage.PreDistributeDetached, FlushPath.Immediate)]
  [Arguments(LifecycleStage.DistributeDetached, FlushPath.Immediate)]
  [Arguments(LifecycleStage.PostDistributeInline, FlushPath.Immediate)]
  [Arguments(LifecycleStage.PostDistributeDetached, FlushPath.Immediate)]
  [Arguments(LifecycleStage.PreDistributeInline, FlushPath.Batch)]
  [Arguments(LifecycleStage.PreDistributeDetached, FlushPath.Batch)]
  [Arguments(LifecycleStage.DistributeDetached, FlushPath.Batch)]
  [Arguments(LifecycleStage.PostDistributeInline, FlushPath.Batch)]
  [Arguments(LifecycleStage.PostDistributeDetached, FlushPath.Batch)]
  [Arguments(LifecycleStage.PreDistributeInline, FlushPath.Interval)]
  [Arguments(LifecycleStage.PreDistributeDetached, FlushPath.Interval)]
  [Arguments(LifecycleStage.DistributeDetached, FlushPath.Interval)]
  [Arguments(LifecycleStage.PostDistributeInline, FlushPath.Interval)]
  [Arguments(LifecycleStage.PostDistributeDetached, FlushPath.Interval)]
  [Arguments(LifecycleStage.PreDistributeInline, FlushPath.StreamAffinity)]
  [Arguments(LifecycleStage.PreDistributeDetached, FlushPath.StreamAffinity)]
  [Arguments(LifecycleStage.DistributeDetached, FlushPath.StreamAffinity)]
  [Arguments(LifecycleStage.PostDistributeInline, FlushPath.StreamAffinity)]
  [Arguments(LifecycleStage.PostDistributeDetached, FlushPath.StreamAffinity)]
  public async Task ReceptorAtDistributeStage_SendsOwnedVoidCommand_HandledOnceAndNeverFlushedAsync(
      LifecycleStage stage, FlushPath path) {
    var coordinator = new RecordingWorkCoordinator();
    await using var provider = _buildProvider(coordinator, path);
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var id = Guid.CreateVersion7();
    var handled = DistributeStageSendProbe.Handled(id);
    var sendReturned = DistributeStageSendProbe.SendReturned(id);
    var triggerStored = coordinator.WaitForOutboxRowAsync(id);
    var trigger = DistributeStageTriggers.For(stage, id);

    await _publishAsync(dispatcher, trigger);
    if (path == FlushPath.Interval) {
      // Interval batches until its timer ticks; the dispatcher's FlushAsync is a no-op there.
      // Drive the tick directly instead of waiting out the interval.
      ((IntervalWorkCoordinatorStrategy)provider.GetRequiredService<IWorkCoordinatorStrategy>()).FlushTimerTick(null);
    }

    // The owned command's receptor completing is the transition under test; a send the dispatcher
    // dropped or threw on never completes it (the hook reports a throw as a fault)
    await handled.Task.WaitAsync(_signalGuard);
    var status = await sendReturned.Task.WaitAsync(_signalGuard);
    // The trigger's own row reaching the coordinator proves the flush ran and the recorder sees rows,
    // so the absence of the command's row below is a real observation
    await triggerStored.WaitAsync(_signalGuard);

    await Assert.That(DistributeStageSendProbe.CountFor(id)).IsEqualTo(1)
      .Because("the owned command must be handled exactly once, in-process");
    await Assert.That(status).IsEqualTo(DeliveryStatus.Delivered)
      .Because("before #1379 the send answered Accepted and nothing ran");

    var triggerName = trigger.GetType().FullName!;
    var commandName = typeof(DistributeStageOwnedCommand).FullName!;
    await Assert.That(coordinator.StoredOutbox.Count(m => m.MessageType.Contains(triggerName, StringComparison.Ordinal))).IsEqualTo(1)
      .Because("the unowned trigger event is the row the flush stores and fires the stage for");
    await Assert.That(coordinator.StoredOutbox.Count(m => m.MessageType.Contains(commandName, StringComparison.Ordinal))).IsEqualTo(0)
      .Because("a command the service owns and handles never goes to the outbox");
    await Assert.That(coordinator.StoredInbox.Count(m => m.MessageType.Contains(commandName, StringComparison.Ordinal))).IsEqualTo(0)
      .Because("the owned command is handled in-process, not stored for a later inbox pass");
  }

  private static Task<IDeliveryReceipt> _publishAsync(IDispatcher dispatcher, object trigger) => trigger switch {
    PreDistributeInlineTrigger t => dispatcher.PublishAsync(t),
    PreDistributeDetachedTrigger t => dispatcher.PublishAsync(t),
    DistributeDetachedTrigger t => dispatcher.PublishAsync(t),
    PostDistributeInlineTrigger t => dispatcher.PublishAsync(t),
    PostDistributeDetachedTrigger t => dispatcher.PublishAsync(t),
    _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger.GetType().Name, "Unknown distribute-stage trigger")
  };

  /// <summary>
  /// Every way a flush reaches the distribute stages: the four strategies, and the stream-affinity
  /// batcher that bypasses the strategy's queue and fires the stages from its own flush callback.
  /// </summary>
  public enum FlushPath { Scoped, Immediate, Batch, Interval, StreamAffinity }

  private static ServiceProvider _buildProvider(RecordingWorkCoordinator coordinator, FlushPath path) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();

    services.AddSingleton<IWorkCoordinator>(coordinator);
    var strategy = path switch {
      FlushPath.Immediate => WorkCoordinatorStrategy.Immediate,
      FlushPath.Batch => WorkCoordinatorStrategy.Batch,
      FlushPath.Interval => WorkCoordinatorStrategy.Interval,
      _ => WorkCoordinatorStrategy.Scoped
    };
    // BatchSize 1 makes the Batch strategy flush on the queue call itself (with lifecycle, #485);
    // the long interval keeps every timer out of the picture (Interval is ticked by the test).
    var options = new WorkCoordinatorOptions {
      Strategy = strategy,
      PartitionCount = 4,
      LeaseSeconds = 30,
      AbandonStaleInstanceThresholdSeconds = 300,
      BatchSize = 1,
      IntervalMilliseconds = 600_000
    };
    services.AddSingleton(options);
    services.AddSingleton(Options.Create(options));

    if (path == FlushPath.StreamAffinity) {
      // The production registration of the producer-side batcher and its flush callback; nothing
      // is started, since no host runs. The Immediate batcher flushes each message as it arrives.
      services.AddWhizbangWorkers();
      services.AddWhizbangOutboxStrategy<ImmediateOutboxBatchStrategy>();
      var gate = new SchemaReadyGate();
      gate.MarkReady();
      services.AddSingleton<ISchemaReadyGate>(gate);
      services.AddScoped<IWorkCoordinatorStrategy>(sp =>
        WorkCoordinatorStrategyFactory.WrapWithStreamAffinity(WorkCoordinatorStrategyFactory.Create(strategy, sp), sp));
    } else if (strategy is WorkCoordinatorStrategy.Batch or WorkCoordinatorStrategy.Interval) {
      services.AddSingleton<IWorkCoordinatorStrategy>(sp => WorkCoordinatorStrategyFactory.Create(strategy, sp));
    } else {
      services.AddScoped<IWorkCoordinatorStrategy>(sp => WorkCoordinatorStrategyFactory.Create(strategy, sp));
    }

    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddWhizbangLifecycleMessageDeserializer();

    return services.BuildServiceProvider();
  }

  /// <summary>
  /// Records every row a flush stores, and signals when a row for a given stream arrives.
  /// </summary>
  private sealed class RecordingWorkCoordinator : IWorkCoordinator {
    private readonly ConcurrentQueue<OutboxMessage> _outbox = new();
    private readonly ConcurrentQueue<InboxMessage> _inbox = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _outboxStored = new();

    public IReadOnlyList<OutboxMessage> StoredOutbox => [.. _outbox];
    public IReadOnlyList<InboxMessage> StoredInbox => [.. _inbox];

    public Task WaitForOutboxRowAsync(Guid streamId) => _signal(streamId).Task;

    private TaskCompletionSource _signal(Guid streamId) =>
      _outboxStored.GetOrAdd(streamId, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) {
      foreach (var message in messages) {
        _outbox.Enqueue(message);
        if (message.StreamId is { } streamId) {
          _signal(streamId).TrySetResult();
        }
      }
      return Task.CompletedTask;
    }

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) {
      foreach (var message in messages) {
        _inbox.Enqueue(message);
      }
      return Task.CompletedTask;
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => throw new NotSupportedException("not exercised by this test");
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => throw new NotSupportedException("not exercised by this test");
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }
}
