// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
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
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Branch coverage for <see cref="ServiceBusConsumerWorker"/>: an explicitly configured global
/// concurrency limit (positive, and zero meaning "no gate"), the start span's subscription tags, the
/// inbox span's status on success and on failure, and a strongly-typed envelope whose payload is null.
/// </summary>
/// <remarks>
/// Messages are delivered through the batch handler the worker hands the transport, after
/// <see cref="ServiceBusConsumerWorker.SubscriptionsReady"/> (a signal the worker body emits) has
/// completed, so the body has provably run before anything is asserted.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs</code-under-test>
[Category("Workers")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ServiceBusConsumerWorkerBranchCoverageTests {
  private const string JSON_ENVELOPE_TYPE =
    "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.SbcGapTestEvent, Whizbang.Core.Tests]], Whizbang.Core";
  private static readonly TimeSpan _signalTimeout = TimeSpan.FromSeconds(10);

  // ========================================
  // Global concurrency limit
  // ========================================

  [Test]
  public async Task HandleMessage_ConcurrencyLimitOfOne_ReleasesTheSlotAfterEachMessageAsync() {
    var transport = new CapturingTransport();
    var strategy = new SignalingStrategy();
    var worker = _createWorker(transport, strategy, new MessageProcessingOptions { MaxConcurrentMessages = 1 });
    await _startAsync(worker);

    await transport.Deliver(_message(MessageId.New(), Guid.CreateVersion7())).WaitAsync(_signalTimeout);
    await transport.Deliver(_message(MessageId.New(), Guid.CreateVersion7())).WaitAsync(_signalTimeout);

    await Assert.That(strategy.FlushCallCount).IsEqualTo(2)
      .Because("with a single slot, the second message only runs if the first gave its slot back");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleMessage_ConcurrencyLimitOfOne_ReleasesTheSlotWhenAMessageFailsAsync() {
    var transport = new CapturingTransport();
    var strategy = new SignalingStrategy {
      OnFlush = call => call == 1
        ? Task.FromException(new InvalidOperationException("first flush fails"))
        : Task.CompletedTask
    };
    var worker = _createWorker(transport, strategy, new MessageProcessingOptions { MaxConcurrentMessages = 1 });
    await _startAsync(worker);

    await Assert.That(async () =>
      await transport.Deliver(_message(MessageId.New(), Guid.CreateVersion7())).WaitAsync(_signalTimeout)
    ).Throws<InvalidOperationException>();
    await transport.Deliver(_message(MessageId.New(), Guid.CreateVersion7())).WaitAsync(_signalTimeout);

    await Assert.That(strategy.FlushCallCount).IsEqualTo(2)
      .Because("a failed message must still release its slot, or one failure would wedge every later message");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleMessage_ConcurrencyLimitZero_RunsMessagesWithNoGlobalGateAsync() {
    var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var transport = new CapturingTransport();
    var strategy = new SignalingStrategy {
      OnFlush = call => {
        if (call == 1) {
          firstEntered.TrySetResult();
          return releaseFirst.Task;
        }
        secondEntered.TrySetResult();
        return Task.CompletedTask;
      }
    };
    var worker = _createWorker(transport, strategy, new MessageProcessingOptions { MaxConcurrentMessages = 0 });
    await _startAsync(worker);

    try {
      var first = transport.Deliver(_message(MessageId.New(), Guid.CreateVersion7()));
      await firstEntered.Task.WaitAsync(_signalTimeout);
      var second = transport.Deliver(_message(MessageId.New(), Guid.CreateVersion7()));

      await secondEntered.Task.WaitAsync(_signalTimeout);
      await Assert.That(first.IsCompleted).IsFalse()
        .Because("the second message reached the flush while the first still held it, so nothing gated them");

      releaseFirst.TrySetResult();
      await Task.WhenAll(first, second).WaitAsync(_signalTimeout);
      await Assert.That(strategy.FlushCallCount).IsEqualTo(2);
    } finally {
      releaseFirst.TrySetResult();
      await worker.StopAsync(CancellationToken.None);
    }
  }

  // ========================================
  // Start span tags
  // ========================================

  [Test]
  public async Task ExecuteAsync_WithListener_SubscriptionWithFilter_TagsStartSpanHasFilterTrueAsync() {
    using var parent = new Activity("sbc-branch-start-filter").Start();
    using var capture = new SpanCapture("Whizbang.Hosting", parent.TraceId);
    var transport = new CapturingTransport();
    var worker = _createWorker(transport, new SignalingStrategy(), options: new ServiceBusConsumerOptions {
      Subscriptions = [
        new TopicSubscription("branch-topic-a", "branch-sub-a"),
        new TopicSubscription("branch-topic-b", "branch-sub-b", DestinationFilter: "orders")
      ]
    });

    await _startAsync(worker);
    await worker.StopAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(_signalTimeout);

    var span = capture.Single("ServiceBusConsumerWorker.Start");
    await Assert.That((int?)span.GetTagItem("worker.subscriptions_count")).IsEqualTo(2);
    await Assert.That((bool?)span.GetTagItem("servicebus.has_filter")).IsEqualTo(true)
      .Because("one subscription carries a destination filter");
    await Assert.That(transport.SubscribeCount).IsEqualTo(2);
  }

  [Test]
  public async Task ExecuteAsync_WithListener_SubscriptionWithoutFilter_TagsStartSpanHasFilterFalseAsync() {
    using var parent = new Activity("sbc-branch-start-nofilter").Start();
    using var capture = new SpanCapture("Whizbang.Hosting", parent.TraceId);
    var transport = new CapturingTransport();
    var worker = _createWorker(transport, new SignalingStrategy(), options: new ServiceBusConsumerOptions {
      Subscriptions = [new TopicSubscription("branch-topic", "branch-sub", DestinationFilter: "   ")]
    });

    await _startAsync(worker);
    await worker.StopAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(_signalTimeout);

    var span = capture.Single("ServiceBusConsumerWorker.Start");
    await Assert.That((int?)span.GetTagItem("worker.subscriptions_count")).IsEqualTo(1);
    await Assert.That((bool?)span.GetTagItem("servicebus.has_filter")).IsEqualTo(false)
      .Because("a whitespace-only filter is no filter");
  }

  // ========================================
  // Inbox span status
  // ========================================

  [Test]
  public async Task HandleMessage_WithTraceParentAndListener_Success_MarksInboxSpanOkAsync() {
    var traceId = ActivityTraceId.CreateRandom();
    using var capture = new SpanCapture("Whizbang.Transport", traceId);
    var transport = new CapturingTransport();
    var strategy = new SignalingStrategy { ClaimQueuedInbox = true };
    var worker = _createWorker(transport, strategy);
    await _startAsync(worker);

    await transport.Deliver(_message(MessageId.New(), Guid.CreateVersion7(), _traceParent(traceId)))
      .WaitAsync(_signalTimeout);

    await Assert.That(strategy.FlushCallCount).IsEqualTo(2)
      .Because("the message was claimed, processed and reported, so the completion flush ran too");
    var span = capture.SingleInbox();
    await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Ok)
      .Because("a message that flushed cleanly closes its receive span as Ok");
    await Assert.That((int?)span.GetTagItem("whizbang.hop_count")).IsEqualTo(1);
    await Assert.That(span.GetTagItem("exception.type")).IsNull();

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleMessage_WithTraceParentAndListener_Failure_MarksInboxSpanErrorWithExceptionTagsAsync() {
    var traceId = ActivityTraceId.CreateRandom();
    using var capture = new SpanCapture("Whizbang.Transport", traceId);
    var transport = new CapturingTransport();
    var strategy = new SignalingStrategy {
      OnFlush = _ => Task.FromException(new InvalidOperationException("inbox flush failed"))
    };
    var worker = _createWorker(transport, strategy);
    await _startAsync(worker);

    await Assert.That(async () =>
      await transport.Deliver(_message(MessageId.New(), Guid.CreateVersion7(), _traceParent(traceId)))
        .WaitAsync(_signalTimeout)
    ).Throws<InvalidOperationException>();

    var span = capture.SingleInbox();
    await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
    await Assert.That(span.StatusDescription).IsEqualTo("inbox flush failed");
    await Assert.That(span.GetTagItem("exception.type"))
      .IsEqualTo(TypeNameFormatter.DisplayName(typeof(InvalidOperationException)));
    await Assert.That(span.GetTagItem("exception.message")).IsEqualTo("inbox flush failed")
      .Because("the trace must say what failed, not only that something did");

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // Strongly-typed envelope with a null payload
  // ========================================

  [Test]
  public async Task HandleMessage_TypedEnvelopeWithNullPayload_SerializesAsObjectAsync() {
    var transport = new CapturingTransport();
    var strategy = new SignalingStrategy();
    var messageId = MessageId.New();
    var streamId = Guid.CreateVersion7();
    var serializer = new RecordingEnvelopeSerializer(_jsonEnvelope(messageId, streamId, traceParent: null));
    // Anonymous processing is allowed so the security layer lets a payload-less envelope through
    // (it refuses one otherwise); the branch under test is the serializer's type choice after it.
    var worker = _createWorker(transport, strategy, envelopeSerializer: serializer, allowAnonymous: true);
    await _startAsync(worker);

    var envelope = new MessageEnvelope<object> {
      MessageId = messageId,
      Payload = null!,
      Hops = _hops(streamId, traceParent: null),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    await transport.Deliver(new TransportMessage(envelope, JSON_ENVELOPE_TYPE)).WaitAsync(_signalTimeout);

    await Assert.That(serializer.BoundTypes.Select(t => t.FullName)).IsEquivalentTo(new[] { typeof(object).FullName })
      .Because("with no payload to inspect, the envelope is serialized under the object type rather than failing");
    await Assert.That(strategy.CapturedInboxMessages.Count).IsEqualTo(1);
    await Assert.That(strategy.CapturedInboxMessages[0].MessageId).IsEqualTo(messageId.Value);
    await Assert.That(strategy.CapturedInboxMessages[0].IsEvent).IsFalse()
      .Because("a null payload is not an event when no event-type provider says otherwise");

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // Helpers
  // ========================================

  private static async Task _startAsync(ServiceBusConsumerWorker worker) {
    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(_signalTimeout);
  }

  private static ServiceBusConsumerWorker _createWorker(
      CapturingTransport transport,
      SignalingStrategy strategy,
      MessageProcessingOptions? processing = null,
      ServiceBusConsumerOptions? options = null,
      IEnvelopeSerializer? envelopeSerializer = null,
      bool allowAnonymous = false) {
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(o => o.AllowAnonymous = allowAnonymous);
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    return new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: scopeFactory,
      logger: NullLogger<ServiceBusConsumerWorker>.Instance,
      orderedProcessor: new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance, parallelizeStreams: false),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      envelopeSerializer: envelopeSerializer ?? new EnvelopeSerializer(),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      options: options ?? new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("branch-topic", "branch-sub")]
      },
      messageProcessingOptions: processing);
  }

  private static string _traceParent(ActivityTraceId traceId) =>
    $"00-{traceId.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01";

  private static TransportMessage _message(MessageId messageId, Guid streamId, string? traceParent = null) =>
    new(_jsonEnvelope(messageId, streamId, traceParent), JSON_ENVELOPE_TYPE);

  private static MessageEnvelope<JsonElement> _jsonEnvelope(MessageId messageId, Guid streamId, string? traceParent) =>
    new() {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{\"Data\":\"branch-test-data\"}").RootElement,
      Hops = _hops(streamId, traceParent),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

  private static List<MessageHop> _hops(Guid streamId, string? traceParent) => [
    new MessageHop {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      TraceParent = traceParent,
      ServiceInstance = new ServiceInstanceInfo {
        InstanceId = Guid.CreateVersion7(),
        ServiceName = "BranchTestService",
        HostName = "branch-test-host",
        ProcessId = 4321
      },
      Metadata = new Dictionary<string, JsonElement> {
        ["AggregateId"] = JsonDocument.Parse($"\"{streamId}\"").RootElement
      }
    }
  ];

  // ========================================
  // Test doubles
  // ========================================

  /// <summary>Collects the stopped spans of one source that belong to one trace.</summary>
  private sealed class SpanCapture : IDisposable {
    private readonly ActivityListener _listener;
    private readonly List<Activity> _stopped = [];

    public SpanCapture(string sourceName, ActivityTraceId traceId) {
      _listener = new ActivityListener {
        ShouldListenTo = source => source.Name == sourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        ActivityStopped = activity => {
          if (activity.TraceId == traceId) {
            lock (_stopped) {
              _stopped.Add(activity);
            }
          }
        }
      };
      ActivitySource.AddActivityListener(_listener);
    }

    public Activity Single(string displayName) {
      lock (_stopped) {
        return _stopped.Single(a => a.DisplayName == displayName);
      }
    }

    public Activity SingleInbox() {
      lock (_stopped) {
        return _stopped.Single(a => a.DisplayName.StartsWith("Inbox ", StringComparison.Ordinal));
      }
    }

    public void Dispose() => _listener.Dispose();
  }

  /// <summary>Transport that captures the batch handler so a test can deliver messages directly.</summary>
  private sealed class CapturingTransport : ITransport {
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _handler;
    private int _subscribeCount;

    public int SubscribeCount => Volatile.Read(ref _subscribeCount);
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task Deliver(TransportMessage message) => _handler!([message], CancellationToken.None);

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      _handler = batchHandler;
      Interlocked.Increment(ref _subscribeCount);
      return Task.FromResult<ISubscription>(new InertSubscription());
    }

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope,
        TransportDestination destination, CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();
  }

  private sealed class InertSubscription : ISubscription {
    public bool IsActive { get; private set; } = true;
#pragma warning disable CS0067 // Event required by interface but unused in test double
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
    public Task PauseAsync() {
      IsActive = false;
      return Task.CompletedTask;
    }
    public Task ResumeAsync() {
      IsActive = true;
      return Task.CompletedTask;
    }
    public void Dispose() => IsActive = false;
  }

  /// <summary>
  /// Strategy that captures queued inbox messages, counts flushes thread-safely, and lets a test
  /// gate or fail a given flush by call number.
  /// </summary>
  private sealed class SignalingStrategy : IWorkCoordinatorStrategy {
    private readonly Lock _gate = new();
    private readonly List<InboxMessage> _captured = [];
    private int _flushCalls;

    public Func<int, Task>? OnFlush { get; init; }

    /// <summary>When set, the first flush hands back the queued inbox rows as this instance's claimed work.</summary>
    public bool ClaimQueuedInbox { get; init; }

    public int FlushCallCount => Volatile.Read(ref _flushCalls);

    public IReadOnlyList<InboxMessage> CapturedInboxMessages {
      get {
        lock (_gate) {
          return [.. _captured];
        }
      }
    }

    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) {
      lock (_gate) {
        _captured.Add(message);
      }
    }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => FlushAndGetBatchAsync(flags, ct);

    public async Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      var call = Interlocked.Increment(ref _flushCalls);
      if (OnFlush is not null) {
        await OnFlush(call);
      }
      List<InboxWork> claimed = [];
      if (ClaimQueuedInbox && call == 1) {
        lock (_gate) {
          claimed.AddRange(_captured.Select(m => new InboxWork {
            MessageId = m.MessageId,
            Envelope = m.Envelope,
            MessageType = m.MessageType,
            StreamId = m.StreamId,
            Status = MessageProcessingStatus.None,
            Attempts = 0
          }));
        }
      }
      return new WorkBatch { InboxWork = claimed, OutboxWork = [], PerspectiveWork = [] };
    }
  }

  /// <summary>Records the type argument each serialization was bound to and returns a fixed JSON envelope.</summary>
  private sealed class RecordingEnvelopeSerializer(MessageEnvelope<JsonElement> envelopeToReturn) : IEnvelopeSerializer {
    private readonly Lock _gate = new();
    private readonly List<Type> _boundTypes = [];

    public IReadOnlyList<Type> BoundTypes {
      get {
        lock (_gate) {
          return [.. _boundTypes];
        }
      }
    }

    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      lock (_gate) {
        _boundTypes.Add(typeof(TMessage));
      }
      return new SerializedEnvelope(envelopeToReturn, JSON_ENVELOPE_TYPE, "Whizbang.Core.Tests.Workers.SbcGapTestEvent, Whizbang.Core.Tests");
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }
}
