// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.InboxStageSends;
using Whizbang.Core.Component.Tests.InboxStageSends.Owned;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1379 on the INBOX path, in process and without a database: an event from another service
/// arrives through the REAL <see cref="InboxDispatchWorker"/>, a receptor fires at a lifecycle stage,
/// and it sends a command the service owns, handled here by a receptor that returns no result. The
/// command must be handled exactly once, in-process, and never queued to the outbox. Before the fix
/// SendAsync answered Accepted and dropped the command, from every stage alike; the fixtures that
/// hid it ran without owned domains, which this one configures the way a deployed service does.
/// </summary>
/// <remarks>
/// <para>
/// The component counterpart of <c>LifecycleStageOwnedCommandSendIntegrationTests</c> in the
/// PostgreSQL suite. The generated receptor registry, receptor invoker, dispatcher and lifecycle
/// deserializer are real; only the inbox channel, the handler-commit and failure channels, and the
/// outbox (a recording <see cref="IWorkCoordinatorStrategy"/>) are in memory.
/// </para>
/// <para>
/// The worker fires the PreInbox and PostInbox stages, and for an event with no perspectives also
/// PostAllPerspectives and PostLifecycle, each as an inline and a detached variant. Detached stages
/// run on a fresh task and scope, so every assertion waits on the owned command's own completion
/// signal and on the hook's send returning, never on time.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/InboxDispatchWorker.cs</code-under-test>
[Category("Workers")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxStageOwnedCommandSendComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.InboxStageSends.Owned";

  [Test]
  [Arguments(LifecycleStage.PreInboxInline)]
  [Arguments(LifecycleStage.PreInboxDetached)]
  [Arguments(LifecycleStage.PostInboxInline)]
  [Arguments(LifecycleStage.PostInboxDetached)]
  [Arguments(LifecycleStage.PostAllPerspectivesInline)]
  [Arguments(LifecycleStage.PostAllPerspectivesDetached)]
  [Arguments(LifecycleStage.PostLifecycleInline)]
  [Arguments(LifecycleStage.PostLifecycleDetached)]
  public async Task ReceptorAtInboxStage_SendsOwnedVoidCommand_HandledOnceAndNeverInTheOutboxAsync(LifecycleStage stage) {
    await _assertOwnedCommandHandledOnceAsync(InboxStageTriggers.For(stage, asSystemForTenant: false));
  }

  [Test]
  [Arguments(LifecycleStage.PostInboxDetached)]
  [Arguments(LifecycleStage.PostAllPerspectivesDetached)]
  public async Task ReceptorAtInboxStage_SendsOwnedVoidCommandAsSystemForTenant_HandledOnceAndNeverInTheOutboxAsync(LifecycleStage stage) {
    await _assertOwnedCommandHandledOnceAsync(InboxStageTriggers.For(stage, asSystemForTenant: true));
  }

  private static async Task _assertOwnedCommandHandledOnceAsync(object trigger) {
    var outbox = new RecordingOutbox();
    var (serviceProvider, jsonOptions) = _createServices(outbox);
    var id = InboxStageTriggers.IdOf(trigger);
    var handled = InboxStageSendProbe.Handled(id);
    var sendReturned = InboxStageSendProbe.SendReturned(id);

    var work = new InboxWork {
      MessageId = (Guid)TrackedGuid.New(),
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From((Guid)TrackedGuid.New()),
        Payload = JsonSerializer.SerializeToElement(trigger, trigger.GetType(), jsonOptions),
        Hops = [new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.CreateVersion7(),
            ServiceName = "upstream-service",
            HostName = "upstream-host",
            ProcessId = 1
          },
          CorrelationId = CorrelationId.New(),
          CausationId = MessageId.New(),
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = InboxStageTriggers.TENANT, UserId = "user-inbox-stage-sends" })
        }],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      MessageType = TypeNameFormatter.AssemblyQualifiedName(trigger.GetType()),
      StreamId = id,
      PartitionNumber = 0,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    var inbox = new FakeInboxChannelWriter();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = null;

    var worker = new InboxDispatchWorker(
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: serviceProvider.GetRequiredService<IServiceInstanceProvider>(),
      inboxChannelWriter: inbox,
      handlerCommitChannel: new NullHandlerCommitChannel(),
      failureChannel: new NullFailureChannel(),
      schemaReadyGate: gate,
      options: Options.Create(new InboxDispatchWorkerOptions()),
      coordinatorOptions: Options.Create(new WorkCoordinatorOptions()),
      logger: NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new StreamIntegrityOptions()),
      lifecycleMessageDeserializer: serviceProvider.GetRequiredService<ILifecycleMessageDeserializer>(),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions { LeaseGraceSeconds = 30, MaxRenewalsPerWork = 6 }),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = 60 }),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      discardPolicy: new MessageDiscardPolicy(new PermissiveReceptorRegistryQuery(), NullLogger<MessageDiscardPolicy>.Instance, new System.Diagnostics.Metrics.Meter("test"), Options.Create(new RoutingOptions()), new EventMarkerResolver(NullMessageTypeCatalog.Instance)),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      leaseRegistry: new LeaseRegistry());

    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await inbox.WriteAsync(work, cts.Token);

      // The owned command's receptor completing, and the hook's send returning, are the
      // transitions under test; a send the dispatcher dropped never completes them and a throw
      // faults them
      await handled.Task.WaitAsync(TimeSpan.FromSeconds(30));
      await sendReturned.Task.WaitAsync(TimeSpan.FromSeconds(30));

      await Assert.That(InboxStageSendProbe.CountFor(id)).IsEqualTo(1)
        .Because("the owned command must be handled exactly once, in-process");
      await Assert.That(outbox.QueuedOf(typeof(OwnedInboxStageCommand))).IsEmpty()
        .Because("a command the service owns and handles never goes to the outbox");
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  private static (ServiceProvider Provider, JsonSerializerOptions JsonOptions) _createServices(RecordingOutbox outbox) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();
    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

    // The outbox: anything the dispatcher routes there is recorded, never delivered
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);

    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddWhizbangLifecycleMessageDeserializer();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();

    return (services.BuildServiceProvider(), jsonOptions);
  }

  /// <summary>Records what the dispatcher queues to the outbox; never delivers anything.</summary>
  private sealed class RecordingOutbox : IWorkCoordinatorStrategy {
    private readonly List<OutboxMessage> _queued = [];

    /// <summary>Outbox entries whose payload is of the given type.</summary>
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

  private sealed class FakeInboxChannelWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class NullHandlerCommitChannel : IInboxHandlerCommitChannel {
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private sealed class NullFailureChannel : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }
}
