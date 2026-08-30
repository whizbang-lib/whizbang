using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.Resilience;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Body-offload (claim-check) receive-side coverage for <see cref="TransportConsumerWorker"/>:
/// <list type="bullet">
/// <item><description>Claim envelope rehydrate → original envelope stored in the inbox</description></item>
/// <item><description>Rehydrate dead-letter (unknown provider) → message dropped + failure metric</description></item>
/// <item><description>ActiveCleanup=true → body deleted AFTER the inbox insert commits</description></item>
/// <item><description>ActiveCleanup delete failure / missing store → inbox insert unaffected (provider TTL backstop)</description></item>
/// <item><description>Non-claim pass-through when JsonSerializerOptions is registered in DI</description></item>
/// </list>
/// All waits are signal-based (TaskCompletionSource, SubscriptionsReady) — no polling.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
[Category("Workers")]
public class TransportConsumerWorkerBodyOffloadTests {

  private const string MEM_PROVIDER_KEY = "mem";

  // ============================================================
  // Rehydrate success
  // ============================================================

  [Test]
  public async Task BatchHandler_ClaimEnvelope_RehydratesOriginalEnvelopeIntoInboxAsync() {
    using var meterFactory = new TestMeterFactory();
    var metrics = new TransportMetrics(new WhizbangMetrics(meterFactory));
    using var metricHelper = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var store = new TestBodyStore(MEM_PROVIDER_KEY);
    var coordinator = new NoOpWorkCoordinator();
    await using var sp = _buildProvider(coordinator, jsonOptions, services => {
      services.AddKeyedSingleton<IMessageBodyStore>(MEM_PROVIDER_KEY, (_, _) => store);
      services.AddSingleton(metrics);
    });
    var transport = new OffloadTransport();
    var worker = _buildWorker(transport, sp, metrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    var (claimEnvelope, originalMessageId, originalTypeName, _) =
      await _uploadOriginalEnvelopeAsync(store, jsonOptions);

    await transport.DeliverBatchAsync([new TransportMessage(claimEnvelope, originalTypeName)]);

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
      .Because("A successfully rehydrated claim must produce exactly one inbox row for the ORIGINAL message.");
    var stored = coordinator.StoredMessages[0];
    await Assert.That(stored.MessageId).IsEqualTo(originalMessageId.Value)
      .Because("The inbox row must carry the original envelope's MessageId — not the claim wrapper's — so downstream dedup/ordering sees the real message identity.");
    await Assert.That(stored.EnvelopeType).IsEqualTo(originalTypeName)
      .Because("After rehydrate the inbox row must use the ORIGINAL envelope type, not the claim sentinel type.");

    var rehydrated = metricHelper.GetByName("whizbang.transport.body_claim.rehydrated.count");
    await Assert.That(rehydrated).Count().IsEqualTo(1)
      .Because("The rehydrator must observe the rehydration through the worker's DI scope.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task BatchHandler_ClaimEnvelope_RegistryLacksClaimConsumer_StillRehydratesAsync() {
    // Regression (production bulk no-op): a real service's IReceptorRegistryQuery has NO consumer for the
    // internal BodyClaimEnvelopePayload sentinel, so the pre-serialization no-consumer gate dropped
    // EVERY offloaded message BEFORE rehydration (metric only, no log, no inbox row — unrecoverable).
    // The gate must exempt claim envelopes so the ORIGINAL message is rehydrated + stored.
    using var meterFactory = new TestMeterFactory();
    var metrics = new TransportMetrics(new WhizbangMetrics(meterFactory));
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var store = new TestBodyStore(MEM_PROVIDER_KEY);
    var coordinator = new NoOpWorkCoordinator();
    await using var sp = _buildProvider(coordinator, jsonOptions, services => {
      services.AddKeyedSingleton<IMessageBodyStore>(MEM_PROVIDER_KEY, (_, _) => store);
      services.AddSingleton(metrics);
    });
    var transport = new OffloadTransport();
    var worker = _buildWorker(transport, sp, metrics, receptorRegistry: new NoConsumerRegistry());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    var (claimEnvelope, originalMessageId, _, _) =
      await _uploadOriginalEnvelopeAsync(store, jsonOptions);

    // Faithful to the wire: the transport hands the worker the CLAIM envelope's type as EnvelopeType
    // (ASB ENVELOPE_TYPE = MessageEnvelope<BodyClaimEnvelopePayload>) — the gate keys on this.
    var claimEnvelopeType = claimEnvelope.GetType().AssemblyQualifiedName!;
    await transport.DeliverBatchAsync([new TransportMessage(claimEnvelope, claimEnvelopeType)]);

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
      .Because("The no-consumer gate must exempt the offloaded claim sentinel so the rehydrated ORIGINAL message is stored — even though no service has a receptor for BodyClaimEnvelopePayload.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Rehydrate dead-letter
  // ============================================================

  [Test]
  public async Task BatchHandler_ClaimEnvelope_UnknownProvider_DropsMessageAndRecordsFailureAsync() {
    using var meterFactory = new TestMeterFactory();
    var metrics = new TransportMetrics(new WhizbangMetrics(meterFactory));
    using var metricHelper = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var coordinator = new NoOpWorkCoordinator();
    // NO IMessageBodyStore registered — the claim's provider cannot be resolved.
    await using var sp = _buildProvider(coordinator, jsonOptions);
    var transport = new OffloadTransport();
    var worker = _buildWorker(transport, sp, metrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    var body = "orphaned-claim-body"u8.ToArray();
    var hash = "sha256-" + Convert.ToHexString(SHA256.HashData(body));
    var claim = new MessageBodyClaim(
      ProviderName: "ghost-provider",
      StorageKey: "test://missing",
      Size: body.Length,
      ContentHash: hash,
      ContentType: "application/json",
      UploadedAt: DateTimeOffset.UtcNow);
    var originalTypeName = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!;
    var claimEnvelope = _wrapInClaimEnvelope(claim, originalTypeName);

    await transport.DeliverBatchAsync([new TransportMessage(claimEnvelope, originalTypeName)]);

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(0)
      .Because("A claim whose provider is unknown MUST be dropped (dead-letter path) — storing it without its body would poison downstream processing.");
    var failed = metricHelper.GetByName("whizbang.transport.inbox.messages_failed");
    await Assert.That(failed).Count().IsEqualTo(1)
      .Because("The rehydrate dead-letter path must record the drop in the failed-messages counter.");
    await Assert.That(failed[0].Value).IsEqualTo(1d);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Active cleanup
  // ============================================================

  [Test]
  public async Task BatchHandler_ActiveCleanup_DeletesOffloadedBodyAfterInboxInsertAsync() {
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var store = new TestBodyStore(MEM_PROVIDER_KEY);
    var coordinator = new NoOpWorkCoordinator();
    await using var sp = _buildProvider(coordinator, jsonOptions, services => {
      services.AddKeyedSingleton<IMessageBodyStore>(MEM_PROVIDER_KEY, (_, _) => store);
      services.AddOptions<MessageBodyOffloadOptions>().Configure(o => o.ActiveCleanup = true);
    });
    var transport = new OffloadTransport();
    var worker = _buildWorker(transport, sp);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    var (claimEnvelope, _, originalTypeName, claim) =
      await _uploadOriginalEnvelopeAsync(store, jsonOptions);

    await transport.DeliverBatchAsync([new TransportMessage(claimEnvelope, originalTypeName)]);

    // The cleanup is fire-and-forget — wait on the store's delete completion signal.
    await store.DeleteObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
      .Because("The inbox insert must commit before active cleanup fires.");
    await Assert.That(store.ContainsBody(claim.StorageKey)).IsFalse()
      .Because("ActiveCleanup=true must delete the offloaded body once the inbox row is durable.");
    await Assert.That(store.DeleteCallCount).IsEqualTo(1)
      .Because("Exactly one delete per surfaced cleanup claim.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task BatchHandler_ActiveCleanup_DeleteThrows_InboxInsertStillSucceedsAsync() {
    var logger = new SignalOnLogLogger("Active cleanup failed");
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var store = new TestBodyStore(MEM_PROVIDER_KEY) { ThrowOnDelete = true };
    var coordinator = new NoOpWorkCoordinator();
    await using var sp = _buildProvider(coordinator, jsonOptions, services => {
      services.AddKeyedSingleton<IMessageBodyStore>(MEM_PROVIDER_KEY, (_, _) => store);
      services.AddOptions<MessageBodyOffloadOptions>().Configure(o => o.ActiveCleanup = true);
    });
    var transport = new OffloadTransport();
    var worker = _buildWorker(transport, sp, metrics: null, logger);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    var (claimEnvelope, _, originalTypeName, claim) =
      await _uploadOriginalEnvelopeAsync(store, jsonOptions);

    await transport.DeliverBatchAsync([new TransportMessage(claimEnvelope, originalTypeName)]);

    // Wait for the cleanup warning — the observable proof the failure was contained.
    await logger.Signaled.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
      .Because("A failing cleanup delete must never affect the already-committed inbox insert or the transport ACK.");
    await Assert.That(store.DeleteCallCount).IsEqualTo(1)
      .Because("The cleanup delete must have been attempted.");
    await Assert.That(store.ContainsBody(claim.StorageKey)).IsTrue()
      .Because("When the delete fails the body stays in the store — the provider's TTL is the backstop.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task BatchHandler_ActiveCleanup_StoreMissingInCleanupScope_SkipsAndKeepsBodyAsync() {
    var logger = new SignalOnLogLogger("Active cleanup skipped");
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var store = new TestBodyStore(MEM_PROVIDER_KEY);
    var coordinator = new NoOpWorkCoordinator();
    var resolveCount = 0;
    await using var sp = _buildProvider(coordinator, jsonOptions, services => {
      // First resolution (batch scope, rehydrate) gets the real store; the cleanup scope's
      // resolution returns null — simulating a provider registration the cleanup scope
      // can no longer see. The worker must log the skip and keep going, never throw.
      services.AddKeyedScoped<IMessageBodyStore>(
        MEM_PROVIDER_KEY,
        (_, _) => Interlocked.Increment(ref resolveCount) == 1 ? store : null!);
      services.AddOptions<MessageBodyOffloadOptions>().Configure(o => o.ActiveCleanup = true);
    });
    var transport = new OffloadTransport();
    var worker = _buildWorker(transport, sp, metrics: null, logger);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    var (claimEnvelope, _, originalTypeName, claim) =
      await _uploadOriginalEnvelopeAsync(store, jsonOptions);

    await transport.DeliverBatchAsync([new TransportMessage(claimEnvelope, originalTypeName)]);

    await logger.Signaled.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
      .Because("A missing cleanup store must never affect the committed inbox insert.");
    await Assert.That(store.DeleteCallCount).IsEqualTo(0)
      .Because("No store was resolved in the cleanup scope, so no delete may run.");
    await Assert.That(store.ContainsBody(claim.StorageKey)).IsTrue()
      .Because("The body stays put — the provider's TTL is the backstop.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Non-claim pass-through
  // ============================================================

  [Test]
  public async Task BatchHandler_NonClaimEnvelope_WithJsonOptionsRegistered_PassesThroughUnchangedAsync() {
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var store = new TestBodyStore(MEM_PROVIDER_KEY);
    var coordinator = new NoOpWorkCoordinator();
    await using var sp = _buildProvider(coordinator, jsonOptions, services => {
      services.AddKeyedSingleton<IMessageBodyStore>(MEM_PROVIDER_KEY, (_, _) => store);
      services.AddOptions<MessageBodyOffloadOptions>().Configure(o => o.ActiveCleanup = true);
    });
    var transport = new OffloadTransport();
    var worker = _buildWorker(transport, sp);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    var messageId = MessageId.New();
    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType =
      "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestMessage, TestApp]], Whizbang.Core";

    await transport.DeliverBatchAsync([new TransportMessage(envelope, envelopeType)]);

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
      .Because("Ordinary (non-claim) messages must flow through the rehydrate seam untouched.");
    var stored = coordinator.StoredMessages[0];
    await Assert.That(stored.MessageId).IsEqualTo(messageId.Value)
      .Because("Pass-through must preserve the original message identity.");
    await Assert.That(stored.EnvelopeType).IsEqualTo(envelopeType)
      .Because("Pass-through must preserve the wire envelope type.");
    await Assert.That(store.DeleteCallCount).IsEqualTo(0)
      .Because("A non-claim message must never trigger active cleanup, even with ActiveCleanup=true.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static TransportConsumerWorker _buildWorker(
      ITransport transport,
      IServiceProvider serviceProvider,
      TransportMetrics? metrics = null,
      ILogger<TransportConsumerWorker>? logger = null,
      IReceptorRegistryQuery? receptorRegistry = null) {
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("offload-topic"));
    return new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      serviceProvider.GetRequiredService<IServiceScopeFactory>(), new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, metrics: metrics,
      logger ?? NullLogger<TransportConsumerWorker>.Instance,
      receptorRegistry: receptorRegistry,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider());
  }

  /// <summary>Registry that consumes NOTHING — mirrors a real service that has no receptor for the
  /// internal BodyClaimEnvelopePayload sentinel (which is every service).</summary>
  private sealed class NoConsumerRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => false;
    public bool HasAnyConsumer(string messageType) => false;
  }

  private static ServiceProvider _buildProvider(
      NoOpWorkCoordinator coordinator,
      JsonSerializerOptions jsonOptions,
      Action<ServiceCollection>? configure = null) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    // The worker only enters the rehydrate seam when JsonSerializerOptions resolves from DI.
    services.AddSingleton(jsonOptions);
    configure?.Invoke(services);
    return services.BuildServiceProvider();
  }

  /// <summary>
  /// Serializes a real <c>MessageEnvelope&lt;JsonElement&gt;</c>, uploads the bytes to the
  /// store, and wraps the resulting claim in a claim envelope — mirroring what the send-side
  /// offload hook puts on the wire.
  /// </summary>
  private static async Task<(MessageEnvelope<BodyClaimEnvelopePayload> ClaimEnvelope, MessageId OriginalMessageId, string OriginalTypeName, MessageBodyClaim Claim)>
      _uploadOriginalEnvelopeAsync(TestBodyStore store, JsonSerializerOptions jsonOptions) {
    var originalEnvelope = _createJsonEnvelope(MessageId.New());
    var typeInfo = jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var json = JsonSerializer.Serialize(originalEnvelope, typeInfo);
    var bytes = Encoding.UTF8.GetBytes(json);
    var claim = await store.UploadAsync(bytes, "application/json");
    var originalTypeName = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!;
    var claimEnvelope = _wrapInClaimEnvelope(claim, originalTypeName);
    return (claimEnvelope, originalEnvelope.MessageId, originalTypeName, claim);
  }

  private static MessageEnvelope<BodyClaimEnvelopePayload> _wrapInClaimEnvelope(
      MessageBodyClaim claim, string originalTypeName) {
    var sentinel = new BodyClaimEnvelopePayload(claim, "application/json", originalTypeName);
    return new MessageEnvelope<BodyClaimEnvelopePayload> {
      MessageId = MessageId.New(),
      Payload = sentinel,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox }
    };
  }

  private static MessageEnvelope<JsonElement> _createJsonEnvelope(MessageId messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{\"x\":1}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  // ============================================================
  // Test doubles
  // ============================================================

  /// <summary>In-memory body store with delete-completion signals for fire-and-forget cleanup assertions.</summary>
  private sealed class TestBodyStore(string providerName) : IMessageBodyStore {
    private readonly ConcurrentDictionary<string, byte[]> _bodies = new();
    private int _deleteCallCount;

    public string ProviderName { get; } = providerName;
    public bool ThrowOnDelete { get; init; }
    public int DeleteCallCount => Volatile.Read(ref _deleteCallCount);
    /// <summary>Completes when DeleteAsync has been invoked (before any simulated failure surfaces).</summary>
    public TaskCompletionSource DeleteObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool ContainsBody(string storageKey) => _bodies.ContainsKey(storageKey);

    public Task<MessageBodyClaim> UploadAsync(
        ReadOnlyMemory<byte> body, string contentType,
        MessageBodyUploadOptions? options = null, CancellationToken cancellationToken = default) {
      var key = $"test://{Guid.NewGuid():N}";
      var copy = body.ToArray();
      _bodies[key] = copy;
      var hash = "sha256-" + Convert.ToHexString(SHA256.HashData(copy));
      return Task.FromResult(new MessageBodyClaim(
        ProviderName, key, copy.Length, hash, contentType, DateTimeOffset.UtcNow));
    }

    public Task<ReadOnlyMemory<byte>> DownloadAsync(
        MessageBodyClaim claim, MessageBodyDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
      => Task.FromResult<ReadOnlyMemory<byte>>(_bodies[claim.StorageKey]);

    public Task DeleteAsync(
        MessageBodyClaim claim, MessageBodyDeleteOptions? options = null,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _deleteCallCount);
      if (ThrowOnDelete) {
        DeleteObserved.TrySetResult();
        throw new InvalidOperationException("simulated delete failure");
      }
      _bodies.TryRemove(claim.StorageKey, out _);
      DeleteObserved.TrySetResult();
      return Task.CompletedTask;
    }
  }

  /// <summary>Logger that completes a signal when a log message contains the given fragment.</summary>
  private sealed class SignalOnLogLogger(string fragment) : ILogger<TransportConsumerWorker> {
    public TaskCompletionSource Signaled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (formatter(state, exception).Contains(fragment, StringComparison.Ordinal)) {
        Signaled.TrySetResult();
      }
    }
  }

  /// <summary>Transport that captures the batch handler and delivers batches on demand.</summary>
  private sealed class OffloadTransport : ITransport {
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;

    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      _batchHandler = batchHandler;
      return Task.FromResult<ISubscription>(new OffloadSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull
      => throw new NotSupportedException();

    public Task DeliverBatchAsync(IReadOnlyList<TransportMessage> messages)
      => _batchHandler is null
        ? throw new InvalidOperationException("No batch handler subscribed yet")
        : _batchHandler(messages, CancellationToken.None);
  }

  private sealed class OffloadSubscription : ISubscription {
    public bool IsActive => true;

    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() {
      // Nothing to release — test double.
    }
  }
}
