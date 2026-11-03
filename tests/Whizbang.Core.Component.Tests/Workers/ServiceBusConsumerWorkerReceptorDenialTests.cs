// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1996: the Service Bus consumer runs a message's inbox receptors inline before the broker completes it, so it asks
/// the receptor interceptors first, once the message is stored and before any stage runs, and honors the
/// <see cref="DeniedAction"/>: DropQuiet completes the message (the denied receptor is skipped when it would run);
/// DeadLetter and Quarantine move the stored row to the dead-letter store under their own reasons and complete the
/// broker message, so it is not retried; Throw fails the delivery, so the broker's redelivery policy retries it. No
/// receptor runs for a denied message.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs</code-under-test>
[Category("Security")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ServiceBusConsumerWorkerReceptorDenialTests {
  [Test]
  public async Task Deliver_DropQuiet_CompletesTheMessageAndRunsTheStagesAsync() {
    var h = await Harness.StartAsync(InterceptorResult.Allowed);

    await h.DeliverAsync();

    await Assert.That(h.Strategy.Completions).Contains(h.MessageId)
      .Because("a quietly dropped receptor still acknowledges the message; the receptor itself is skipped when it runs");
    await Assert.That(h.Strategy.Failures).IsEmpty();
    await Assert.That(h.DeadLetters.Moves).IsEmpty();
    await Assert.That(h.Invoker.Invocations).IsGreaterThan(0);
    await h.StopAsync();
  }

  [Test]
  [Arguments(DeniedAction.DeadLetter, MessageFailureReason.ReceptorDenied)]
  [Arguments(DeniedAction.Quarantine, MessageFailureReason.ReceptorQuarantined)]
  public async Task Deliver_DeadLetterOrQuarantine_MovesTheRowWithoutRetryAsync(DeniedAction action, MessageFailureReason reason) {
    var h = await Harness.StartAsync(InterceptorResult.Deny(action, "Missing required permission: orders:approve"));

    await h.DeliverAsync();

    var (sourceTable, sourceId, movedReason, errorText) = h.DeadLetters.Moves.Single();
    await Assert.That(sourceId).IsEqualTo(h.MessageId);
    await Assert.That(sourceTable).IsEqualTo(DeadLetterSourceTable.INBOX);
    await Assert.That(movedReason).IsEqualTo(reason);
    await Assert.That(errorText).Contains("orders:approve");
    await Assert.That(h.Strategy.Completions).IsEmpty().Because("the row is moved, never completed");
    await Assert.That(h.Strategy.Failures).IsEmpty().Because("a dead-lettered denial never enters a retry loop");
    await Assert.That(h.Invoker.Invocations).IsEqualTo(0).Because("no receptor runs for a denied message");
    await h.StopAsync();
  }

  [Test]
  public async Task Deliver_DeadLetter_NoDeadLetterStore_FailsTheRowInsteadOfCompletingItAsync() {
    var h = await Harness.StartAsync(InterceptorResult.Deny(DeniedAction.DeadLetter, "denied"), withDeadLetterStore: false);

    await h.DeliverAsync();

    await Assert.That(h.Strategy.Completions).IsEmpty();
    await Assert.That(h.Strategy.Failures).Contains(h.MessageId)
      .Because("without a store the row keeps its body and is failed for the attempts bound");
    await Assert.That(h.Invoker.Invocations).IsEqualTo(0);
    await h.StopAsync();
  }

  [Test]
  public async Task Deliver_Throw_FailsTheDeliveryForTheBrokersRetryPolicyAsync() {
    var h = await Harness.StartAsync(InterceptorResult.Deny(DeniedAction.Throw, "try later"));

    var denied = await Assert.ThrowsAsync<ReceptorInvocationDeniedException>(h.DeliverAsync);

    await Assert.That(denied!.Action).IsEqualTo(DeniedAction.Throw);
    await Assert.That(h.Strategy.Completions).IsEmpty();
    await Assert.That(h.DeadLetters.Moves).IsEmpty();
    await Assert.That(h.Invoker.Invocations).IsEqualTo(0);
    await h.StopAsync();
  }

  [Test]
  public async Task Deliver_NoInterceptorRegistered_SkipsTheAdmissionCheckAsync() {
    var h = await Harness.StartAsync(InterceptorResult.Deny(DeniedAction.DeadLetter), withInterceptor: false);

    await h.DeliverAsync();

    await Assert.That(h.Invoker.AdmissionStages).IsEmpty()
      .Because("with no interceptor registered the worker reads no payload and asks nothing");
    await Assert.That(h.Strategy.Completions).Contains(h.MessageId);
    await h.StopAsync();
  }

  [Test]
  public async Task Deliver_AsksAboutTheInboxStagesAsync() {
    var h = await Harness.StartAsync(InterceptorResult.Allowed);

    await h.DeliverAsync();

    await Assert.That(h.Invoker.AdmissionStages).IsEquivalentTo([
      LifecycleStage.PreInboxInline, LifecycleStage.PreInboxDetached,
      LifecycleStage.PostInboxInline, LifecycleStage.PostInboxDetached]);
    await h.StopAsync();
  }

  private sealed class Harness : IAsyncDisposable {
    private CancellationTokenSource _cts = null!;
    private ServiceProvider _sp = null!;
    public ServiceBusConsumerWorker Worker { get; private set; } = null!;
    public CapturingTransport Transport { get; } = new();
    public RecordingStrategy Strategy { get; } = new();
    public CapturingDeadLetterStore DeadLetters { get; } = new();
    public AdmissionInvoker Invoker { get; private set; } = null!;
    public Guid MessageId { get; } = (Guid)TrackedGuid.New();

    public static async Task<Harness> StartAsync(InterceptorResult admission, bool withDeadLetterStore = true, bool withInterceptor = true) {
      var h = new Harness { Invoker = new AdmissionInvoker(admission) };
      h.Strategy.WorkFor = h._work;
      var services = new ServiceCollection();
      services.TryAddWhizbangDefaults();
      services.AddSingleton<IServiceInstanceProvider>(new FakeServiceInstanceProvider());
      services.AddScoped<IWorkCoordinatorStrategy>(_ => h.Strategy);
      services.AddSingleton<IReceptorInvoker>(h.Invoker);
      if (withInterceptor) {
        // Without an interceptor the worker skips the admission check entirely
        services.AddSingleton<IReceptorInterceptor, DefaultRequirePermissionInterceptor>();
      }
      if (withDeadLetterStore) {
        services.AddSingleton<IDeadLetterStore>(h.DeadLetters);
      }
      h._sp = services.BuildServiceProvider();
      h.Worker = new ServiceBusConsumerWorker(
        transport: h.Transport,
        scopeFactory: h._sp.GetRequiredService<IServiceScopeFactory>(),
        logger: NullLogger<ServiceBusConsumerWorker>.Instance,
        orderedProcessor: new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance),
        lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
        envelopeSerializer: new EnvelopeSerializer(),
        receptorRegistry: new PermissiveReceptorRegistryQuery(),
        runtimeReceptorRegistry: NullReceptorRegistry.Instance,
        eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
        ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
        options: new ServiceBusConsumerOptions { Subscriptions = [new TopicSubscription("test-topic", "test-sub")] });
      h._cts = new CancellationTokenSource();
      await h.Worker.StartAsync(h._cts.Token);
      await h.Worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));
      return h;
    }

    public Task DeliverAsync() {
      var received = new MessageEnvelope<JsonElement> {
        MessageId = Whizbang.Core.ValueObjects.MessageId.From(MessageId),
        Payload = _payload(),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      };
      return Transport.BatchHandler!.Invoke(
        [new TransportMessage(received, typeof(MessageEnvelope<SbDenialProbeEvent>).AssemblyQualifiedName)], _cts.Token);
    }

    public async Task StopAsync() => await DisposeAsync();

    public async ValueTask DisposeAsync() {
      await Worker.StopAsync(CancellationToken.None);
      await _cts.CancelAsync();
      _cts.Dispose();
      await _sp.DisposeAsync();
    }

    private JsonElement _payload() => JsonElement.Parse($$"""{"Id":"{{MessageId}}"}""");

    private InboxWork _work() => new() {
      MessageId = MessageId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = Whizbang.Core.ValueObjects.MessageId.From(MessageId),
        Payload = _payload(),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      },
      MessageType = TypeNameFormatter.Format(typeof(SbDenialProbeEvent)),
      StreamId = MessageId,
      PartitionNumber = 1,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  /// <summary>An invoker whose admission check answers a fixed result; counts the stages it is asked to run.</summary>
  private sealed class AdmissionInvoker(InterceptorResult admission) : IReceptorInvoker {
    private int _invocations;
    public int Invocations => Volatile.Read(ref _invocations);
    public ConcurrentQueue<LifecycleStage> AdmissionStages { get; } = new();

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _invocations);
      return ValueTask.CompletedTask;
    }

    public ValueTask<InterceptorResult> EvaluateAdmissionAsync(IMessageEnvelope envelope, IReadOnlyList<LifecycleStage> stages,
        CancellationToken cancellationToken = default) {
      foreach (var stage in stages) {
        AdmissionStages.Enqueue(stage);
      }
      return new ValueTask<InterceptorResult>(admission);
    }
  }

  private sealed class RecordingStrategy : IWorkCoordinatorStrategy {
    public Func<InboxWork>? WorkFor { get; set; }
    public ConcurrentQueue<Guid> Completions { get; } = new();
    public ConcurrentQueue<Guid> Failures { get; } = new();
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) => Completions.Enqueue(messageId);
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) => Failures.Enqueue(messageId);
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [WorkFor!()], PerspectiveWork = [] });
  }

  private sealed class CapturingDeadLetterStore : IDeadLetterStore {
    public ConcurrentQueue<(string SourceTable, Guid SourceId, MessageFailureReason Reason, string? ErrorText)> Moves { get; } = new();
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      Moves.Enqueue((sourceTable, sourceId, failureReason, errorText));
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class CapturingTransport : ITransport {
    public Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? BatchHandler { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) {
      BatchHandler = batchHandler;
      return Task.FromResult<ISubscription>(new NopSubscription());
    }

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull =>
      throw new NotImplementedException();

    private sealed class NopSubscription : ISubscription {
      public bool IsActive => true;
#pragma warning disable CS0067
      public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
      public Task PauseAsync() => Task.CompletedTask;
      public Task ResumeAsync() => Task.CompletedTask;
      public void Dispose() { }
    }
  }
}

/// <summary>The message the Service Bus receptor denial tests deliver (outside the class for source generator discovery).</summary>
public record SbDenialProbeEvent([property: StreamId] Guid Id) : IEvent;
