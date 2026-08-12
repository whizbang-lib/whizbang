using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Message type deliberately NOT registered in <see cref="TestJsonContext"/> — used to drive
/// per-item serialization failures in PublishBatchAsync.
/// </summary>
internal sealed record UnregisteredBatchMessage(string Content);

/// <summary>
/// Shared builders for envelopes, broker messages, and SDK event args used by the
/// AzureServiceBusTransport path tests. Complements (does not replace) the private fakes in
/// the older per-file test classes.
/// </summary>
internal static class AsbTransportTestData {
  /// <summary>Combined JsonSerializerOptions from every registered test JsonSerializerContext.</summary>
  internal static JsonSerializerOptions CombinedOptions { get; } = JsonContextRegistry.CreateCombinedOptions();

  /// <summary>Builds a fully-populated envelope, optionally stamping correlation/causation on the first hop.</summary>
  internal static MessageEnvelope<TestMessage> CreateEnvelope(
    string content = "transport-path-test",
    CorrelationId? correlationId = null,
    MessageId? causationId = null) => new() {
      MessageId = MessageId.New(),
      Payload = new TestMessage(content),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          CausationId = causationId,
          CorrelationId = correlationId,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "transport-topic"
        }
      ]
    };

  /// <summary>Builds a broker message carrying a real serialized envelope.</summary>
  internal static ServiceBusReceivedMessage EnvelopeMessage(MessageEnvelope<TestMessage> envelope, int deliveryCount = 1) {
    var typeInfo = CombinedOptions.GetTypeInfo(typeof(MessageEnvelope<TestMessage>));
    var body = JsonSerializer.Serialize(envelope, typeInfo);
    return RawMessage(body, typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!, deliveryCount);
  }

  /// <summary>Builds a broker message with an arbitrary body and optional EnvelopeType property.</summary>
  internal static ServiceBusReceivedMessage RawMessage(string body, string? envelopeTypeName, int deliveryCount = 1) {
    var properties = new Dictionary<string, object>();
    if (envelopeTypeName is not null) {
      properties["EnvelopeType"] = envelopeTypeName;
    }
    return ServiceBusModelFactory.ServiceBusReceivedMessage(
      body: BinaryData.FromString(body),
      messageId: Guid.CreateVersion7().ToString(),
      properties: properties,
      deliveryCount: deliveryCount);
  }

  /// <summary>Parses raw JSON into a detached JsonElement for destination metadata.</summary>
  internal static JsonElement Json(string raw) {
    using var doc = JsonDocument.Parse(raw);
    return doc.RootElement.Clone();
  }

  internal static ProcessMessageEventArgs MessageArgs(ServiceBusReceivedMessage message, RecordingTransportReceiver receiver) =>
    new(message, receiver, CancellationToken.None);

  internal static ProcessSessionMessageEventArgs SessionArgs(ServiceBusReceivedMessage message, RecordingTransportSessionReceiver receiver) =>
    new(message, receiver, CancellationToken.None);
}

/// <summary>
/// ILogger with every level enabled — exercises the transport's IsEnabled-gated diagnostic
/// blocks that NullLogger-based tests skip. Records entries and raises
/// <see cref="MessageLogged"/> synchronously so tests can await specific log events without
/// polling.
/// </summary>
internal sealed class RecordingTransportLogger : ILogger<AzureServiceBusTransport> {
  private readonly Lock _sync = new();
  private readonly List<(LogLevel Level, string Message)> _entries = [];

  /// <summary>Raised synchronously from Log — usable as a deterministic completion signal.</summary>
  public event Action<LogLevel, string>? MessageLogged;

  public bool Contains(LogLevel level, string fragment) {
    lock (_sync) {
      return _entries.Exists(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));
    }
  }

  IDisposable? ILogger.BeginScope<TState>(TState state) => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
    var message = formatter(state, exception);
    lock (_sync) {
      _entries.Add((logLevel, message));
    }
    MessageLogged?.Invoke(logLevel, message);
  }
}

/// <summary>
/// Mockable ServiceBusClient that hands out raisable fake processors and recording senders
/// (with full message-batch support) without opening any connection.
/// </summary>
internal sealed class RaisableServiceBusClient(string fullyQualifiedNamespace = "unit-test.servicebus.windows.net") : ServiceBusClient {
  public RaisableProcessor? LastProcessor { get; private set; }
  public RaisableSessionProcessor? LastSessionProcessor { get; private set; }
  public RecordingBatchSender? LastSender { get; private set; }

  /// <summary>Injected into every created sender's single-message send path.</summary>
  public Exception? SendException { get; set; }

  /// <summary>Injected into every created sender's batch send path.</summary>
  public Exception? SendBatchException { get; set; }

  /// <summary>When true, single-message sends never complete (they only observe cancellation).</summary>
  public bool HangOnSend { get; set; }

  /// <summary>Optional TryAddMessage policy for created message batches (default: always accept).</summary>
  public Func<ServiceBusMessage, bool>? TryAddCallback { get; set; }

  public override string FullyQualifiedNamespace => fullyQualifiedNamespace;
  public override bool IsClosed => false;

  public override ServiceBusProcessor CreateProcessor(
    string topicName, string subscriptionName, ServiceBusProcessorOptions options) {
    LastProcessor = new RaisableProcessor();
    return LastProcessor;
  }

  public override ServiceBusSessionProcessor CreateSessionProcessor(
    string topicName, string subscriptionName, ServiceBusSessionProcessorOptions options) {
    LastSessionProcessor = new RaisableSessionProcessor();
    return LastSessionProcessor;
  }

  public override ServiceBusSender CreateSender(string queueOrTopicName) {
    LastSender = new RecordingBatchSender {
      SendException = SendException,
      SendBatchException = SendBatchException,
      HangOnSend = HangOnSend,
      TryAddCallback = TryAddCallback
    };
    return LastSender;
  }
}

/// <summary>
/// ServiceBusProcessor whose event pipeline can be pumped synchronously via the SDK's
/// OnProcessMessageAsync / OnProcessErrorAsync mocking hooks.
/// </summary>
internal sealed class RaisableProcessor : ServiceBusProcessor {
  public override Task StartProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

  public override Task StopProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

  public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

  public Task RaiseMessageAsync(ProcessMessageEventArgs args) => OnProcessMessageAsync(args);

  public Task RaiseErrorAsync(ProcessErrorEventArgs args) => OnProcessErrorAsync(args);
}

/// <summary>
/// ServiceBusSessionProcessor with a real inner processor (the SDK's session processor
/// delegates event storage to the virtual InnerProcessor property), pumpable via the
/// OnProcessSessionMessageAsync / OnProcessErrorAsync mocking hooks.
/// </summary>
internal sealed class RaisableSessionProcessor : ServiceBusSessionProcessor {
  private readonly InnerRaisableProcessor _inner = new();

  protected override ServiceBusProcessor InnerProcessor => _inner;

  public override Task StartProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

  public override Task StopProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

  public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

  public Task RaiseSessionMessageAsync(ProcessSessionMessageEventArgs args) => OnProcessSessionMessageAsync(args);

  public Task RaiseErrorAsync(ProcessErrorEventArgs args) => OnProcessErrorAsync(args);

  private sealed class InnerRaisableProcessor : ServiceBusProcessor {
  }
}

/// <summary>
/// Recording ServiceBusSender with single-message and message-batch support, failure
/// injection, an optional never-completing send (for the SendTimeout branch), and dispose
/// tracking. Batches are created through <see cref="ServiceBusModelFactory.ServiceBusMessageBatch"/>
/// backed by per-batch stores so tests can inspect exactly which messages were accepted.
/// </summary>
internal sealed class RecordingBatchSender : ServiceBusSender {
  private const long BATCH_SIZE_BYTES = 256L * 1024L;
  private readonly Lock _sync = new();

  public List<ServiceBusMessage> Sent { get; } = [];
  public List<List<ServiceBusMessage>> BatchStores { get; } = [];
  public List<int> SentBatchCounts { get; } = [];
  public Exception? SendException { get; init; }
  public Exception? SendBatchException { get; init; }
  public bool HangOnSend { get; init; }
  public Func<ServiceBusMessage, bool>? TryAddCallback { get; init; }
  public int DisposeCount { get; private set; }

  public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default) {
    if (HangOnSend) {
      var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      _ = cancellationToken.Register(() => hang.TrySetCanceled(cancellationToken));
      return hang.Task;
    }
    if (SendException is not null) {
      return Task.FromException(SendException);
    }
    lock (_sync) {
      Sent.Add(message);
    }
    return Task.CompletedTask;
  }

  public override ValueTask<ServiceBusMessageBatch> CreateMessageBatchAsync(CancellationToken cancellationToken = default) {
    var store = new List<ServiceBusMessage>();
    lock (_sync) {
      BatchStores.Add(store);
    }
    var batch = ServiceBusModelFactory.ServiceBusMessageBatch(
      batchSizeBytes: BATCH_SIZE_BYTES,
      batchMessageStore: store,
      batchOptions: new CreateMessageBatchOptions(),
      tryAddCallback: TryAddCallback ?? (static _ => true));
    return ValueTask.FromResult(batch);
  }

  public override Task SendMessagesAsync(ServiceBusMessageBatch messageBatch, CancellationToken cancellationToken = default) {
    if (SendBatchException is not null) {
      return Task.FromException(SendBatchException);
    }
    lock (_sync) {
      SentBatchCounts.Add(messageBatch.Count);
    }
    return Task.CompletedTask;
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Base ServiceBusSender.DisposeAsync() calls CloseAsync which NREs on mocking-constructor instances; this fake only records the call")]
  public override ValueTask DisposeAsync() {
    DisposeCount++;
    return ValueTask.CompletedTask;
  }
}

/// <summary>
/// Recording ServiceBusReceiver — captures Complete/Abandon/DeadLetter settlement calls,
/// injects settlement failures, and signals <see cref="CompletedSignal"/> so tests of the
/// Task.Run-based batch collector flush can await completion deterministically.
/// Attempts are recorded before any injected exception is thrown.
/// </summary>
internal sealed class RecordingTransportReceiver : ServiceBusReceiver {
  public List<string> Completed { get; } = [];
  public List<string> Abandoned { get; } = [];
  public List<(string MessageId, string Reason, string? Description)> DeadLettered { get; } = [];
  public Exception? CompleteException { get; init; }
  public Exception? AbandonException { get; init; }
  public Exception? DeadLetterException { get; init; }

  /// <summary>Completes when the first CompleteMessageAsync call is observed.</summary>
  public TaskCompletionSource CompletedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

  public override Task CompleteMessageAsync(
    ServiceBusReceivedMessage message, CancellationToken cancellationToken = default) {
    Completed.Add(message.MessageId);
    CompletedSignal.TrySetResult();
    return CompleteException is null ? Task.CompletedTask : Task.FromException(CompleteException);
  }

  public override Task AbandonMessageAsync(
    ServiceBusReceivedMessage message,
    IDictionary<string, object>? propertiesToModify = null,
    CancellationToken cancellationToken = default) {
    Abandoned.Add(message.MessageId);
    return AbandonException is null ? Task.CompletedTask : Task.FromException(AbandonException);
  }

  public override Task DeadLetterMessageAsync(
    ServiceBusReceivedMessage message,
    string deadLetterReason,
    string? deadLetterErrorDescription = null,
    CancellationToken cancellationToken = default) {
    DeadLettered.Add((message.MessageId, deadLetterReason, deadLetterErrorDescription));
    return DeadLetterException is null ? Task.CompletedTask : Task.FromException(DeadLetterException);
  }
}

/// <summary>Session-receiver counterpart of <see cref="RecordingTransportReceiver"/>.</summary>
internal sealed class RecordingTransportSessionReceiver : ServiceBusSessionReceiver {
  public List<string> Completed { get; } = [];
  public List<string> Abandoned { get; } = [];
  public List<(string MessageId, string Reason, string? Description)> DeadLettered { get; } = [];
  public Exception? CompleteException { get; init; }
  public Exception? AbandonException { get; init; }
  public Exception? DeadLetterException { get; init; }

  public override string SessionId => "unit-test-session";

  public override Task CompleteMessageAsync(
    ServiceBusReceivedMessage message, CancellationToken cancellationToken = default) {
    Completed.Add(message.MessageId);
    return CompleteException is null ? Task.CompletedTask : Task.FromException(CompleteException);
  }

  public override Task AbandonMessageAsync(
    ServiceBusReceivedMessage message,
    IDictionary<string, object>? propertiesToModify = null,
    CancellationToken cancellationToken = default) {
    Abandoned.Add(message.MessageId);
    return AbandonException is null ? Task.CompletedTask : Task.FromException(AbandonException);
  }

  public override Task DeadLetterMessageAsync(
    ServiceBusReceivedMessage message,
    string deadLetterReason,
    string? deadLetterErrorDescription = null,
    CancellationToken cancellationToken = default) {
    DeadLettered.Add((message.MessageId, deadLetterReason, deadLetterErrorDescription));
    return DeadLetterException is null ? Task.CompletedTask : Task.FromException(DeadLetterException);
  }
}

/// <summary>
/// IReceptorRegistry stub that reports a single receptor for the configured message type at
/// every lifecycle stage (or none, when constructed with null). Drives the slice-2
/// _buildIsHandledLocally predicate without a source-generated registry.
/// </summary>
internal sealed class StubReceptorRegistry(Type? handledMessageType) : IReceptorRegistry {
  private static readonly ReceptorInfo _receptorInfo = new(
    typeof(TestMessage),
    "stub-receptor",
    static (_, _, _, _, _) => ValueTask.FromResult<object?>(null));

  public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) =>
    handledMessageType is not null && messageType == handledMessageType ? [_receptorInfo] : [];

  public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage {
    // Not needed — this stub only serves GetReceptorsFor lookups.
  }

  public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage {
    // Not needed — this stub only serves GetReceptorsFor lookups.
  }

  public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;

  public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
}

/// <summary>
/// IPerspectiveRunnerRegistry stub exposing a fixed set of Apply'd event types for the
/// slice-2 receive filter. Runner lookups are unused by the transport.
/// </summary>
internal sealed class StubPerspectiveRegistry(params Type[] eventTypes) : IPerspectiveRunnerRegistry {
  private static readonly HashSet<LifecycleStage> _noStages = [];

  public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;

  public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];

  public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors => _noStages;

  public IReadOnlyList<Type> GetEventTypes() => eventTypes;
}

/// <summary>IMessageTypeBinder that always misses — forces the raw-receptor fallback path.</summary>
internal sealed class MissTypeBinder : IMessageTypeBinder {
  public Type? Bind(string assemblyQualifiedName) => null;

  public (Type? Type, MessageTypeBinderPass Pass) BindWithDiagnostics(string assemblyQualifiedName) => (null, MessageTypeBinderPass.Miss);
}

/// <summary>Recording IRawReceptor for the slice-5 InvokeRawReceptor receive branch.</summary>
internal sealed class RecordingRawReceptor(string targetMessageTypeName) : IRawReceptor {
  public List<JsonElement> Handled { get; } = [];

  public string TargetMessageTypeName => targetMessageTypeName;

  public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken) {
    Handled.Add(payload);
    return Task.CompletedTask;
  }
}

/// <summary>Single-entry IRawReceptorRegistry keyed on the receptor's target type name.</summary>
internal sealed class SingleRawReceptorRegistry(IRawReceptor receptor) : IRawReceptorRegistry {
  public IRawReceptor? FindByTypeName(string messageTypeName) =>
    string.Equals(messageTypeName, receptor.TargetMessageTypeName, StringComparison.Ordinal) ? receptor : null;

  public IReadOnlyCollection<string> RegisteredTypeNames => [receptor.TargetMessageTypeName];
}

/// <summary>
/// Recording IMessageDiscardPolicy — captures RecordDiscard calls so the session ack+drop
/// NoLocalConsumer telemetry routing can be asserted without a meter listener.
/// </summary>
internal sealed class RecordingDiscardPolicy : IMessageDiscardPolicy {
  public List<(MessageDiscardGate Gate, MessageDiscardDecision Decision, string PayloadClrType)> Recorded { get; } = [];

  public MessageDiscardDecision EvaluateReceive(string payloadClrType, string topic, string subscription) =>
    new(ShouldDiscard: false, Reason: MessageDiscardReason.None);

  public MessageDiscardDecision EvaluateInbox(string payloadClrType) =>
    new(ShouldDiscard: false, Reason: MessageDiscardReason.None);

  public MessageDiscardDecision EvaluateOutbox(string payloadClrType) =>
    new(ShouldDiscard: false, Reason: MessageDiscardReason.None);

  public void RecordDiscard(
    MessageDiscardGate gate,
    MessageDiscardDecision decision,
    string payloadClrType,
    IReadOnlyDictionary<string, object?>? additionalTags = null) {
    Recorded.Add((gate, decision, payloadClrType));
  }
}

/// <summary>
/// Recording IServiceBusAdminClient for the provisioning-logging tests: tracks topics,
/// subscriptions, migration probes, and rules, with per-operation failure injection.
/// </summary>
internal sealed class RecordingProvisioningAdminClient : IServiceBusAdminClient {
  public HashSet<string> ExistingTopics { get; init; } = [];
  public List<string> CreatedTopics { get; } = [];
  public HashSet<(string Topic, string Subscription)> ExistingSubscriptions { get; init; } = [];
  public HashSet<(string Topic, string Subscription)> SessionRequiredSubscriptions { get; init; } = [];
  public List<(string Topic, string Subscription, bool? RequiresSession, int MaxDeliveryCount)> CreatedSubscriptions { get; } = [];
  public List<(string Topic, string Subscription)> DeletedSubscriptions { get; } = [];
  public List<RuleProperties> ExistingRules { get; init; } = [];
  public List<(string Topic, string Subscription, string RuleName)> DeletedRules { get; } = [];
  public List<(string Topic, string Subscription, CreateRuleOptions Options)> CreatedRules { get; } = [];

  public Exception? CreateTopicException { get; init; }
  public Exception? CreateSubscriptionException { get; init; }

  /// <summary>Active-message count reported for every subscription (liveness backlog probe).</summary>
  public long ActiveMessageCountResult { get; set; }

  /// <summary>Failure injection for the liveness backlog probe.</summary>
  public Exception? ActiveMessageCountException { get; set; }

  public Task<long> GetSubscriptionActiveMessageCountAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) =>
    ActiveMessageCountException is null
      ? Task.FromResult(ActiveMessageCountResult)
      : Task.FromException<long>(ActiveMessageCountException);

  public Task<NamespaceProperties> GetNamespacePropertiesAsync(CancellationToken cancellationToken = default) =>
    // NamespaceProperties has no public constructor; the transport ignores the value.
    Task.FromResult<NamespaceProperties>(null!);

  public Task<bool> TopicExistsAsync(string topicName, CancellationToken cancellationToken = default) =>
    Task.FromResult(ExistingTopics.Contains(topicName));

  public Task CreateTopicAsync(string topicName, CancellationToken cancellationToken = default) {
    if (CreateTopicException is not null) {
      return Task.FromException(CreateTopicException);
    }
    CreatedTopics.Add(topicName);
    ExistingTopics.Add(topicName);
    return Task.CompletedTask;
  }

  public Task<bool> SubscriptionExistsAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) =>
    Task.FromResult(ExistingSubscriptions.Contains((topicName, subscriptionName)));

  public Task CreateSubscriptionAsync(string topicName, string subscriptionName, int maxDeliveryCount, TimeSpan lockDuration, CancellationToken cancellationToken = default) {
    if (CreateSubscriptionException is not null) {
      return Task.FromException(CreateSubscriptionException);
    }
    CreatedSubscriptions.Add((topicName, subscriptionName, null, maxDeliveryCount));
    ExistingSubscriptions.Add((topicName, subscriptionName));
    return Task.CompletedTask;
  }

  public Task CreateSubscriptionAsync(string topicName, string subscriptionName, bool requiresSession, int maxDeliveryCount, TimeSpan lockDuration, CancellationToken cancellationToken = default) {
    if (CreateSubscriptionException is not null) {
      return Task.FromException(CreateSubscriptionException);
    }
    CreatedSubscriptions.Add((topicName, subscriptionName, requiresSession, maxDeliveryCount));
    ExistingSubscriptions.Add((topicName, subscriptionName));
    if (requiresSession) {
      SessionRequiredSubscriptions.Add((topicName, subscriptionName));
    }
    return Task.CompletedTask;
  }

  public Task UpdateSubscriptionLockDurationAsync(string topicName, string subscriptionName, TimeSpan lockDuration, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;



  public Task<SubscriptionProperties> GetSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
    var properties = ServiceBusModelFactory.SubscriptionProperties(
      topicName: topicName,
      subscriptionName: subscriptionName,
      lockDuration: TimeSpan.FromMinutes(1),
      requiresSession: SessionRequiredSubscriptions.Contains((topicName, subscriptionName)),
      defaultMessageTimeToLive: TimeSpan.FromDays(14),
      autoDeleteOnIdle: TimeSpan.FromDays(30),
      maxDeliveryCount: 10,
      userMetadata: string.Empty);
    return Task.FromResult(properties);
  }

  public Task DeleteSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
    DeletedSubscriptions.Add((topicName, subscriptionName));
    ExistingSubscriptions.Remove((topicName, subscriptionName));
    return Task.CompletedTask;
  }

  public async IAsyncEnumerable<RuleProperties> GetRulesAsync(
    string topicName,
    string subscriptionName,
    [EnumeratorCancellation] CancellationToken cancellationToken = default) {
    await Task.CompletedTask;
    foreach (var rule in ExistingRules.ToList()) {
      yield return rule;
    }
  }

  public Task DeleteRuleAsync(string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default) {
    DeletedRules.Add((topicName, subscriptionName, ruleName));
    ExistingRules.RemoveAll(r => r.Name == ruleName);
    return Task.CompletedTask;
  }

  public Task CreateRuleAsync(string topicName, string subscriptionName, CreateRuleOptions options, CancellationToken cancellationToken = default) {
    CreatedRules.Add((topicName, subscriptionName, options));
    return Task.CompletedTask;
  }
}
