// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.OutboxStageSends.Owned.Commands;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Core.Component.Tests.OutboxStageSends;

/// <summary>
/// Component proof for #1379 on the outbox side: the REAL <see cref="OutboxDrainWorker"/> drains a
/// row, the REAL generated dispatcher and <see cref="ReceptorInvoker"/> fire a receptor declared at a
/// PreOutbox or PostOutbox stage, and that receptor sends a command the service owns, handled here by
/// a receptor that returns no result. The command must be handled exactly once, in-process, and never
/// queued to the outbox. Before the fix SendAsync answered Accepted and dropped such a command from
/// every stage alike.
/// </summary>
/// <remarks>
/// <para>
/// Only the storage and transport edges are fakes: an in-memory coordinator hands the worker the row,
/// a publish strategy reports success, and a recording <see cref="IWorkCoordinatorStrategy"/> stands
/// in for the dispatcher's outbox. Owned domains are configured the way a deployed service
/// configures them, which is what the receptor invoker's PreOutbox ownership filter keys on; the
/// trigger is therefore an owned event, and its envelope carries no local-dispatch flag, since a
/// locally dispatched envelope skips PreOutbox.
/// </para>
/// <para>
/// Each stage runs through both publish paths: one row at a time, and the bulk path a transport
/// that supports batches takes. Detached stages run on a fresh task and scope, so every assertion
/// waits on the owned command's own completion signal and on the trigger row's completion, never on
/// time.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxDrainWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class OutboxStageOwnedCommandSendComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.OutboxStageSends.Owned";
  private static readonly TimeSpan _signalTimeout = TimeSpan.FromSeconds(30);

  [Test]
  [Arguments(LifecycleStage.PreOutboxInline, false)]
  [Arguments(LifecycleStage.PreOutboxInline, true)]
  [Arguments(LifecycleStage.PreOutboxDetached, false)]
  [Arguments(LifecycleStage.PreOutboxDetached, true)]
  [Arguments(LifecycleStage.PostOutboxInline, false)]
  [Arguments(LifecycleStage.PostOutboxInline, true)]
  [Arguments(LifecycleStage.PostOutboxDetached, false)]
  [Arguments(LifecycleStage.PostOutboxDetached, true)]
  public async Task ReceptorAtOutboxStage_SendsOwnedVoidCommand_HandledOnceAndNeverQueuedToTheOutboxAsync(
      LifecycleStage stage, bool bulkPublish) {
    var trigger = OutboxStageTriggers.For(stage);
    var id = OutboxStageTriggers.IdOf(trigger);
    var handled = OutboxStageSendProbe.Expect(id);

    var instance = new FixedServiceInstanceProvider();
    var outbox = new RecordingOutbox();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var row = _triggerRow(trigger, id, instance, jsonOptions);

    var coordinator = new SingleRowWorkCoordinator(id, row);
    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new SignalingCompletionChannel();
    var failure = new RecordingFailureChannel();
    var publish = new SucceedingPublishStrategy(bulkPublish);
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(instance);
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(jsonOptions));
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddWhizbangLifecycleMessageDeserializer();
    await using var serviceProvider = services.BuildServiceProvider();

    var options = new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 };
    // The registry query and the runtime registry are resolved the way the host resolves them, so
    // the worker's stage gate reads the generated discovery of the [FireAt] hook, not a stub.
    var worker = new OutboxDrainWorker(
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: instance,
      drainChannel: drainChannel,
      completionChannel: completion,
      failureChannel: failure,
      schemaReadyGate: gate,
      options: Options.Create(options),
      jsonOptions: jsonOptions,
      logger: NullLogger<OutboxDrainWorker>.Instance,
      publishStrategy: publish,
      lifecycleMessageDeserializer: serviceProvider.GetRequiredService<ILifecycleMessageDeserializer>(),
      receptorRegistry: serviceProvider.GetRequiredService<IReceptorRegistryQuery>(),
      runtimeReceptorRegistry: serviceProvider.GetRequiredService<IReceptorRegistry>(),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      governor: OutboxDrainWorker.CreateDefaultGovernor(options));

    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = null;
    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await drainChannel.WriteAsync(id, cts.Token);

      // The owned command's receptor completing is the transition under test; a send the
      // dispatcher dropped or threw on never completes it (the hook reports a throw as a fault)
      await handled.Task.WaitAsync(_signalTimeout);
      // The trigger row's completion: every inline stage for it has run by now, so a second
      // handling from the same row could not still be on its way for the inline variants
      await completion.WaitForAsync(row.MessageId).WaitAsync(_signalTimeout);

      await Assert.That(OutboxStageSendProbe.CountFor(id)).IsEqualTo(1)
        .Because("the owned command must be handled exactly once, in-process");
      await Assert.That(outbox.QueuedOf(typeof(OutboxStageOwnedCommand))).IsEmpty()
        .Because("a command the service owns and handles never goes to the outbox");
      await Assert.That(publish.PublishedIds).Contains(row.MessageId)
        .Because("the trigger row itself must have gone through the worker's publish path");
      await Assert.That(failure.All).IsEmpty()
        .Because("no stage hook may fault the trigger row");
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  /// <summary>
  /// An outbox row for the trigger as the drain worker reads it: a serialized envelope whose payload
  /// is the trigger, a transport destination (stage hooks never fire for event-store-only rows), and
  /// an outbox-only dispatch mode, because a locally dispatched envelope skips PreOutbox.
  /// </summary>
  private static OutboxBatchRow _triggerRow(object trigger, Guid streamId, IServiceInstanceProvider instance, JsonSerializerOptions jsonOptions) {
    var messageId = (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.SerializeToElement(trigger, jsonOptions.GetTypeInfo(trigger.GetType())),
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = instance.ToInfo(),
        CorrelationId = CorrelationId.New(),
        CausationId = MessageId.New(),
        Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "tenant-outbox-stage-sends", UserId = "user-outbox-stage-sends" })
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local }
    };
    var envelopeJson = JsonSerializer.Serialize(envelope, jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
    var messageType = TypeNameFormatter.AssemblyQualifiedName(trigger.GetType());
    return new OutboxBatchRow {
      MessageId = messageId,
      StreamId = streamId,
      Destination = "outbox-stage-sends-topic",
      MessageType = messageType,
      EnvelopeType = EnvelopeTypeNameHelper.Format(messageType),
      EventData = envelopeJson,
      Metadata = "{}",
      Scope = null,
      Status = 1,
      Attempts = 0,
      PartitionNumber = 0,
      IsEvent = true,
    };
  }

  // --- fakes (storage and transport edges only) ---

  private sealed class FixedServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "outbox-stage-sends-svc";
    public string HostName => "outbox-stage-sends-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class FakeOutboxDrainChannel : IOutboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) => _channel.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
  }

  /// <summary>Signals per message id when the worker enqueues its completion.</summary>
  private sealed class SignalingCompletionChannel : IOutboxCompletionChannel {
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _completed = new();

    public Task WaitForAsync(Guid messageId) => _signal(messageId).Task;

    public ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) {
      _signal(outboxMessageId).TrySetResult();
      return ValueTask.CompletedTask;
    }

    private TaskCompletionSource _signal(Guid messageId) =>
      _completed.GetOrAdd(messageId, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
  }

  private sealed class RecordingFailureChannel : IFailureChannel {
    public ConcurrentBag<MessageFailure> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      All.Add(failure);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Reports every publish as successful; optionally takes the bulk path.</summary>
  private sealed class SucceedingPublishStrategy(bool supportsBulkPublish) : IMessagePublishStrategy {
    public ConcurrentBag<Guid> PublishedIds { get; } = [];
    public bool SupportsBulkPublish => supportsBulkPublish;
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) =>
      Task.FromResult(_succeed(work));

    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> workItems, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<MessagePublishResult>>([.. workItems.Select(_succeed)]);

    private MessagePublishResult _succeed(OutboxWork work) {
      PublishedIds.Add(work.MessageId);
      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      };
    }
  }

  /// <summary>Hands the worker the one trigger row for its stream; everything else is a no-op.</summary>
  private sealed class SingleRowWorkCoordinator(Guid streamId, OutboxBatchRow row) : IWorkCoordinator {
    public Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
      IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<OutboxBatchRow>>(streamIds.Contains(streamId) ? [row] : []);

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(Guid.CreateVersion7());

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>Records what the dispatcher queues to the outbox; never delivers anything.</summary>
  private sealed class RecordingOutbox : IWorkCoordinatorStrategy {
    private readonly List<OutboxMessage> _queued = [];

    public IReadOnlyList<OutboxMessage> QueuedOf(Type messageType) {
      var typeName = TypeNameFormatter.AssemblyQualifiedName(messageType);
      lock (_queued) {
        return [.. _queued.Where(m => m.MessageType == typeName)];
      }
    }

    public void QueueOutboxMessage(OutboxMessage message) {
      lock (_queued) {
        _queued.Add(message);
      }
    }

    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => FlushAndGetBatchAsync(flags, ct);

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }
}
