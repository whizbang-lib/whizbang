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
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1676: a received collective event ran through the post-perspective stages twice in the same service. The
/// inbox fired PostAllPerspectives and PostLifecycle for it straight away, because no per-stream perspective lists a
/// collective event type, and the collective sink fired them again once the set-based apply finished. A service that
/// runs the collective sink leaves those stages to the sink; a service without one still fires them on receipt, where
/// they would otherwise never fire at all.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDispatchWorkerCollectiveLifecycleTests {

  /// <summary>A collective event no per-stream perspective folds; only the collective sink applies it.</summary>
  private sealed record InboxProbeCollective : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }

  /// <summary>An ordinary event, with no perspective in this service either.</summary>
  private sealed record InboxProbePlain : IEvent {
    public string Name { get; init; } = "";
  }

  private static readonly LifecycleStage[] _postPerspectiveStages = [
    LifecycleStage.PostAllPerspectivesDetached, LifecycleStage.PostAllPerspectivesInline,
    LifecycleStage.PostLifecycleDetached, LifecycleStage.PostLifecycleInline,
  ];

  [Test]
  public async Task CollectiveEvent_InAServiceRunningTheCollectiveSink_LeavesThePostPerspectiveStagesToTheSinkAsync() {
    var invoker = new StageRecordingInvoker();
    var payload = new InboxProbeCollective { Scope = new TenantCollectiveScope("t-1") };
    var harness = new Harness(invoker, payload, withCollectiveSink: true);
    var work = _work(TypeNameFormatter.Format(typeof(InboxProbeCollective)));

    await harness.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(harness.Commits.All.Count).IsEqualTo(1)
      .Because("the row is still received and stored; only the stages after the perspectives move to the sink");
    await Assert.That(invoker.Count(LifecycleStage.PostAllPerspectivesInline)).IsEqualTo(0)
      .Because("the collective sink fires PostAllPerspectives once its apply completes; firing it here as well runs every hook on the event twice");
    await Assert.That(invoker.Count(LifecycleStage.PostLifecycleInline)).IsEqualTo(0)
      .Because("PostLifecycle is the sink's to fire for the same reason");
  }

  [Test]
  public async Task CollectiveEvent_InAServiceWithoutTheCollectiveSink_StillFiresThePostPerspectiveStagesOnceAsync() {
    var invoker = new StageRecordingInvoker();
    var payload = new InboxProbeCollective { Scope = new TenantCollectiveScope("t-1") };
    var harness = new Harness(invoker, payload, withCollectiveSink: false);
    var work = _work(TypeNameFormatter.Format(typeof(InboxProbeCollective)));

    await harness.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(invoker.Count(LifecycleStage.PostAllPerspectivesInline)).IsEqualTo(1)
      .Because("with no collective sink in this service nothing else fires the stage, so the inbox still does");
    await Assert.That(invoker.Count(LifecycleStage.PostLifecycleInline)).IsEqualTo(1);
  }

  [Test]
  public async Task PlainEvent_WithNoPerspective_FiresThePostPerspectiveStagesOnReceipt_EvenWithACollectiveSinkAsync() {
    var invoker = new StageRecordingInvoker();
    var harness = new Harness(invoker, new InboxProbePlain { Name = "plain" }, withCollectiveSink: true);
    var work = _work(TypeNameFormatter.Format(typeof(InboxProbePlain)));

    await harness.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(invoker.Count(LifecycleStage.PostAllPerspectivesInline)).IsEqualTo(1)
      .Because("only a collective event is the sink's; an ordinary event with no perspective completes on receipt");
    await Assert.That(invoker.Count(LifecycleStage.PostLifecycleInline)).IsEqualTo(1);
  }

  // ------------------------------------------------------------------------------------------

  private static InboxWork _work(string messageType) {
    var messageId = (Guid)TrackedGuid.New();
    return new InboxWork {
      MessageId = messageId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(messageId),
        Payload = JsonElement.Parse("{}"),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      },
      MessageType = messageType,
      StreamId = (Guid)TrackedGuid.New(),
      PartitionNumber = 1,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  private sealed class Harness {
    public CapturingCommitChannel Commits { get; } = new();
    public InboxDispatchWorker Worker { get; }

    public Harness(StageRecordingInvoker invoker, IMessage payload, bool withCollectiveSink) {
      var services = new ServiceCollection();
      services.AddSingleton<IReceptorInvoker>(invoker);
      // The service's perspectives fold an unrelated event; neither probe type is listed, as a generated registry
      // never lists a collective event under a per-stream perspective.
      services.AddSingleton<IPerspectiveRunnerRegistry>(new UnrelatedPerspectiveRegistry());
      if (withCollectiveSink) {
        services.AddSingleton<ICollectiveDispatcher>(new UnusedCollectiveDispatcher());
      }
      var sp = services.BuildServiceProvider();
      var gate = new SchemaReadyGate();
      gate.MarkReady();
      Worker = new InboxDispatchWorker(
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        instanceProvider: new FakeInstanceProvider(),
        inboxChannelWriter: new NoOpInboxChannelWriter(),
        handlerCommitChannel: Commits,
        failureChannel: new NoOpFailureChannel(),
        schemaReadyGate: gate,
        options: Options.Create(new InboxDispatchWorkerOptions()),
        coordinatorOptions: Options.Create(new WorkCoordinatorOptions()),
        logger: NullLogger<InboxDispatchWorker>.Instance,
        integrityOptions: Options.Create(new StreamIntegrityOptions()),
        lifecycleMessageDeserializer: new FixedDeserializer(payload),
        leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
        leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
        // No receptor at the gated inbox stages, so only the post-perspective stages can reach the invoker.
        receptorRegistry: new NoInboxReceptors(),
        discardPolicy: new MessageDiscardPolicy(new PermissiveReceptorRegistryQuery(), NullLogger<MessageDiscardPolicy>.Instance, new System.Diagnostics.Metrics.Meter("test"), Options.Create(new RoutingOptions()), new EventMarkerResolver(NullMessageTypeCatalog.Instance)),
        runtimeReceptorRegistry: NullReceptorRegistry.Instance,
        deadLetterStore: NullDeadLetterStore.Instance,
        generationProvider: new DefaultGenerationProvider());
    }
  }

  /// <summary>Records the inline stages, which the dispatch awaits, so a count read after it returns is exact.</summary>
  private sealed class StageRecordingInvoker : IReceptorInvoker {
    private readonly ConcurrentQueue<LifecycleStage> _stages = new();
    public int Count(LifecycleStage stage) => _stages.Count(s => s == stage);
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      if (Array.IndexOf(_postPerspectiveStages, stage) >= 0) {
        _stages.Enqueue(stage);
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FixedDeserializer(object payload) : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => payload;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => payload;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => payload;
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => payload;
  }

  private sealed class NoInboxReceptors : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => false;
    public bool HasAnyConsumer(string messageType) => true;
  }

  private sealed class UnrelatedPerspectiveRegistry : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [
      new PerspectiveRegistrationInfo("Test.UnrelatedPerspective", "global::Test.UnrelatedPerspective", "global::Test.Model",
        ["Test.UnrelatedEvent, Test"])
    ];
    public IReadOnlyList<Type> GetEventTypes() => [typeof(InboxProbeCollective)];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class UnusedCollectiveDispatcher : ICollectiveDispatcher {
    public Task<CollectiveDispatchResult> DispatchAsync(ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession,
        Func<CancellationToken, ValueTask>? onBatchApplied = null, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("the inbox never applies a collective event; the sink does");
  }

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "consumer-svc";
    public string HostName => "consumer-host";
    public int ProcessId => 12;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class NoOpInboxChannelWriter : IInboxChannelWriter {
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

  private sealed class CapturingCommitChannel : IInboxHandlerCommitChannel {
    public ConcurrentQueue<HandlerCommitRequest> All { get; } = new();
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) {
      All.Enqueue(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class NoOpFailureChannel : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }
}
