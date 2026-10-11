// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #915, at the transport edge. A consumer that handles only a composite's inner events must be
/// able to store the composite it receives, inline or offloaded, and fan it out; and a message this
/// consumer cannot turn into an inbox row must never be settled as consumed without a trace. It is
/// given dead-letter custody with its body, or, when custody itself is unavailable, the batch fails so
/// the transport does not treat it as handled.
/// </summary>
[Category("Workers")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class TransportConsumerWorkerUnstorableMessageTests {

  private const string PROVIDER_KEY = "mem";
  private const string TOPIC = "items-topic";
  private const string SUBSCRIPTION = "consumer-sub";
  private const string UNKNOWN_DISCRIMINATOR = "Elsewhere.Contracts.EventThisServiceNeverReferenced";

  /// <summary>The composite a publisher sends. Public so the generator registers it like a real one.</summary>
  public sealed class ConsumerSideItemComposite : CompositeEventBase;

  /// <summary>The inner event this consumer handles.</summary>
  public sealed record ConsumerHandledItemEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; }
    public string Name { get; init; } = string.Empty;
  }

  /// <summary>An inner event this consumer does not reference; its discriminator is made unknown on the wire.</summary>
  public sealed record ProducerOnlyItemEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; }
    public string Name { get; init; } = string.Empty;
  }

  /// <summary>An ordinary event used to prove neighbors in the same batch are still stored.</summary>
  public sealed record UnstorableProbeEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; }
    public string Marker { get; init; } = string.Empty;
  }

  // ==========================================================================================
  // Consumer that handles only inner events: composite is stored and fans out
  // ==========================================================================================

  [Test]
  public async Task Composite_ConsumerHandlesOnlyInnerEvent_StoredAndInnerEventFansOutAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var coordinator = new NoOpWorkCoordinator();
    await using var sp = _provider(coordinator, options);
    var (worker, transport) = await _startAsync(sp);

    var streamId = Guid.CreateVersion7();
    var wireJson = _compositeEnvelopeWireJson(options, streamId);
    var envelope = (IMessageEnvelope)JsonSerializer.Deserialize(wireJson, options.GetTypeInfo(typeof(MessageEnvelope<ConsumerSideItemComposite>)))!;

    await transport.DeliverBatchAsync([new TransportMessage(envelope, _compositeEnvelopeType)]);

    await Assert.That(coordinator.DeadLetterImports).IsEmpty();
    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
      .Because("the composite is stored as one ordinary inbox row; fan-out happens at dispatch");
    await _assertStoredCompositeFansOutToHandledEventAsync(coordinator.StoredMessages[0], options, sp, streamId);

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task OffloadedComposite_ConsumerHandlesOnlyInnerEvent_RehydratedStoredAndFansOutAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var store = new BodyStore(PROVIDER_KEY);
    var coordinator = new NoOpWorkCoordinator();
    await using var sp = _provider(coordinator, options,
      s => s.AddKeyedSingleton<IMessageBodyStore>(PROVIDER_KEY, (_, _) => store));
    var (worker, transport) = await _startAsync(sp);

    var streamId = Guid.CreateVersion7();
    var body = Encoding.UTF8.GetBytes(_compositeEnvelopeWireJson(options, streamId));
    var claim = await store.UploadAsync(body, "application/json");
    var claimEnvelope = _claimEnvelope(claim, _compositeEnvelopeType);

    await transport.DeliverBatchAsync([new TransportMessage(claimEnvelope, _compositeEnvelopeType)]);

    await Assert.That(coordinator.DeadLetterImports).IsEmpty()
      .Because("an inner element this consumer cannot resolve must not make the offloaded composite unreadable");
    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1);
    await _assertStoredCompositeFansOutToHandledEventAsync(coordinator.StoredMessages[0], options, sp, streamId);

    await worker.StopAsync(CancellationToken.None);
  }

  // ==========================================================================================
  // A message that cannot become an inbox row is dead-lettered, never silently settled
  // ==========================================================================================

  [Test]
  public async Task SerializationFails_MessageDeadLetteredWithBody_NeighborsStillStoredAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var coordinator = new NoOpWorkCoordinator();
    var serializer = new SelectivelyFailingSerializer(new EnvelopeSerializer(options), failOnMarker: "cannot-serialize");
    await using var sp = _provider(coordinator, options, s => s.AddSingleton<IEnvelopeSerializer>(serializer));
    var (worker, transport) = await _startAsync(sp);

    var bad = _probeEnvelope("cannot-serialize");
    var good = _probeEnvelope("fine");
    var probeType = EnvelopeTypeNameHelper.Format(TypeNameFormatter.Format(typeof(UnstorableProbeEvent)));

    await transport.DeliverBatchAsync([new TransportMessage(bad, probeType), new TransportMessage(good, probeType)]);

    await Assert.That(coordinator.StoredMessages.Select(m => m.MessageId)).IsEquivalentTo([good.MessageId.Value])
      .Because("one unstorable message must not cost its neighbors their inbox rows");
    await Assert.That(coordinator.DeadLetterImports.Count).IsEqualTo(1)
      .Because("a message that cannot be stored must be given dead-letter custody, never skipped and settled");
    var custody = coordinator.DeadLetterImports[0];
    await Assert.That(custody.MessageId).IsEqualTo(bad.MessageId.Value);
    await Assert.That(custody.MessageType).IsEqualTo(probeType);
    await Assert.That(custody.Destination).IsEqualTo($"{TOPIC}/{SUBSCRIPTION}");
    await Assert.That(custody.EnvelopeJson).Contains("cannot-serialize")
      .Because("the body is preserved so the message can be replayed once the consumer is fixed");
    await Assert.That(custody.BrokerReason).IsEqualTo(nameof(MessageFailureReason.SerializationError));
    await Assert.That(custody.BrokerDescription).Contains(SelectivelyFailingSerializer.FAILURE_TEXT);

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SerializationFails_PayloadNoContextKnows_DeadLetteredWithDescriptorAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var coordinator = new NoOpWorkCoordinator();
    var serializer = new SelectivelyFailingSerializer(new EnvelopeSerializer(options), failOnMarker: "cannot-serialize");
    await using var sp = _provider(coordinator, options, s => s.AddSingleton<IEnvelopeSerializer>(serializer));
    var (worker, transport) = await _startAsync(sp);

    // No generated context holds metadata for a private type, so not even the registry can re-serialize it.
    var hidden = new MessageEnvelope<HiddenPayload> {
      MessageId = MessageId.New(),
      Payload = new HiddenPayload("secret-shape"),
      Hops = [_hop(Guid.CreateVersion7())],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    const string hiddenType = "Whizbang.Core.Observability.MessageEnvelope`1[[Elsewhere.Hidden, Elsewhere]], Whizbang.Core";

    await transport.DeliverBatchAsync([new TransportMessage(hidden, hiddenType)]);

    await Assert.That(coordinator.DeadLetterImports.Count).IsEqualTo(1)
      .Because("even a message whose body cannot be written anywhere is given custody, never dropped");
    var custody = coordinator.DeadLetterImports[0];
    await Assert.That(custody.MessageId).IsEqualTo(hidden.MessageId.Value);
    await Assert.That(custody.EnvelopeJson).Contains("\"BodyUnavailable\":true")
      .Because("the record says plainly that it holds a descriptor, not a replayable body");
    await Assert.That(custody.EnvelopeJson).Contains(hidden.MessageId.Value.ToString());
    await Assert.That(custody.EnvelopeJson).Contains(SelectivelyFailingSerializer.FAILURE_TEXT);

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task OffloadedBodyUnreadable_DeadLetteredWithDownloadedBodyAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var store = new BodyStore(PROVIDER_KEY);
    var coordinator = new NoOpWorkCoordinator();
    await using var sp = _provider(coordinator, options,
      s => s.AddKeyedSingleton<IMessageBodyStore>(PROVIDER_KEY, (_, _) => store));
    var (worker, transport) = await _startAsync(sp);

    // Valid JSON, wrong shape: the inner list is a number, so the envelope cannot be read.
    const string unreadable = "{\"id\":\"0198a7a4-1f7e-7000-8000-000000000001\",\"p\":{\"Inner\":42},\"h\":[]}";
    var claim = await store.UploadAsync(Encoding.UTF8.GetBytes(unreadable), "application/json");
    var claimEnvelope = _claimEnvelope(claim, _compositeEnvelopeType);

    await transport.DeliverBatchAsync([new TransportMessage(claimEnvelope, _compositeEnvelopeType)]);

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(0);
    await Assert.That(coordinator.DeadLetterImports.Count).IsEqualTo(1)
      .Because("a body that cannot be rebuilt is dead-lettered deliberately instead of looping to the broker's own queue");
    var custody = coordinator.DeadLetterImports[0];
    await Assert.That(custody.EnvelopeJson).IsEqualTo(unreadable)
      .Because("the downloaded original body, not the claim, is what a replay needs");
    await Assert.That(custody.MessageType).IsEqualTo(_compositeEnvelopeType);
    await Assert.That(custody.MessageId).IsEqualTo(claimEnvelope.MessageId.Value);
    await Assert.That(custody.BrokerReason).IsEqualTo(nameof(MessageFailureReason.SerializationError));

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task OffloadedBodyProviderUnknown_DeadLetteredWithClaimAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var coordinator = new NoOpWorkCoordinator();
    await using var sp = _provider(coordinator, options);   // no body store registered at all
    var (worker, transport) = await _startAsync(sp);

    var claim = new MessageBodyClaim("ghost-provider", "test://missing", 3, "sha256-00", "application/json", DateTimeOffset.UtcNow);
    var claimEnvelope = _claimEnvelope(claim, _compositeEnvelopeType);

    await transport.DeliverBatchAsync([new TransportMessage(claimEnvelope, _compositeEnvelopeType)]);

    await Assert.That(coordinator.DeadLetterImports.Count).IsEqualTo(1);
    var custody = coordinator.DeadLetterImports[0];
    await Assert.That(custody.BrokerReason).IsEqualTo(nameof(MessageFailureReason.BodyClaimProviderUnknown));
    await Assert.That(custody.EnvelopeJson).Contains("test://missing")
      .Because("with no body to download, the claim itself is preserved so the body can still be located");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task CustodyUnavailable_BatchFailsAfterStoringNeighborsAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var coordinator = new NoOpWorkCoordinator { RefuseDeadLetterImports = true };
    var serializer = new SelectivelyFailingSerializer(new EnvelopeSerializer(options), failOnMarker: "cannot-serialize");
    var logger = new CapturingLogger();
    await using var sp = _provider(coordinator, options, s => s.AddSingleton<IEnvelopeSerializer>(serializer));
    var (worker, transport) = await _startAsync(sp, logger);

    var bad = _probeEnvelope("cannot-serialize");
    var good = _probeEnvelope("fine");
    var probeType = EnvelopeTypeNameHelper.Format(TypeNameFormatter.Format(typeof(UnstorableProbeEvent)));

    Exception? reported = null;
    try {
      await transport.DeliverBatchAsync([new TransportMessage(bad, probeType), new TransportMessage(good, probeType)]);
    } catch (TransportBatchFailedException ex) {
      reported = ex;
    }

    await Assert.That(coordinator.StoredMessages.Select(m => m.MessageId)).IsEquivalentTo([good.MessageId.Value])
      .Because("neighbors are stored before the batch is failed, so their redelivery is a harmless duplicate");
    await Assert.That(reported).IsNotNull()
      .Because("with neither an inbox row nor custody, the transport must be told the batch failed so it "
             + "does not settle the message as consumed (#921)");
    await Assert.That(reported!.InnerException!.Message).Contains(bad.MessageId.Value.ToString());
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Critical)).IsTrue();

    await worker.StopAsync(CancellationToken.None);
  }

  // ==========================================================================================
  // Helpers
  // ==========================================================================================

  private sealed record HiddenPayload(string Shape) : IEvent;

  private static readonly string _compositeEnvelopeType =
    EnvelopeTypeNameHelper.Format(TypeNameFormatter.Format(typeof(ConsumerSideItemComposite)));

  /// <summary>
  /// The wire JSON a publisher that references both inner event types produces, with the second inner
  /// element's discriminator replaced by one this consumer has never heard of.
  /// </summary>
  private static string _compositeEnvelopeWireJson(JsonSerializerOptions options, Guid streamId) {
    var envelope = new MessageEnvelope<ConsumerSideItemComposite> {
      MessageId = MessageId.New(),
      Payload = new ConsumerSideItemComposite {
        StreamId = streamId,
        Inner = [
          new ConsumerHandledItemEvent { StreamId = streamId, Name = "handled-here" },
          new ProducerOnlyItemEvent { StreamId = streamId, Name = "not-referenced-here" },
        ],
      },
      Hops = [_hop(streamId)],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    var json = JsonSerializer.Serialize(envelope, options.GetTypeInfo(typeof(MessageEnvelope<ConsumerSideItemComposite>)));
    var producerOnly = typeof(ProducerOnlyItemEvent).FullName!.Replace('+', '.');
    if (!json.Contains(producerOnly, StringComparison.Ordinal)) {
      throw new InvalidOperationException($"Fixture assumption broken: '{producerOnly}' not in {json}");
    }
    return json.Replace(producerOnly, UNKNOWN_DISCRIMINATOR, StringComparison.Ordinal);
  }

  private static async Task _assertStoredCompositeFansOutToHandledEventAsync(
      InboxMessage stored, JsonSerializerOptions options, IServiceProvider sp, Guid streamId) {
    await Assert.That(stored.EnvelopeType).IsEqualTo(_compositeEnvelopeType);

    // Dispatch reads the stored row back by its type name, as the inbox dispatch worker does.
    var serializer = new EnvelopeSerializer(options);
    var composite = (ICompositeEvent)serializer.DeserializeMessage((MessageEnvelope<JsonElement>)stored.Envelope, stored.MessageType);
    var result = CompositeInboxFanout.TryExpand(composite, stored.Envelope, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Count).IsEqualTo(1)
      .Because("the inner event this consumer handles fans out; the one it cannot resolve is skipped");
    var child = result.Children[0];
    await Assert.That(child.MessageType).Contains(nameof(ConsumerHandledItemEvent));
    await Assert.That(child.StreamId).IsEqualTo(streamId);
    await Assert.That(child.Envelope.Payload.GetRawText()).Contains("handled-here");
    await Assert.That(result.UnsubscribedChildren).IsEqualTo(1);
  }

  private static MessageEnvelope<UnstorableProbeEvent> _probeEnvelope(string marker) {
    var streamId = Guid.CreateVersion7();
    return new MessageEnvelope<UnstorableProbeEvent> {
      MessageId = MessageId.New(),
      Payload = new UnstorableProbeEvent { StreamId = streamId, Marker = marker },
      Hops = [_hop(streamId)],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
  }

  private static MessageHop _hop(Guid streamId) => new() {
    Type = HopType.Current,
    Timestamp = DateTimeOffset.UtcNow,
    ServiceInstance = ServiceInstanceInfo.Unknown,
    Metadata = new Dictionary<string, JsonElement> {
      ["AggregateId"] = JsonSerializer.SerializeToElement(streamId.ToString()),
    },
  };

  private static MessageEnvelope<BodyClaimEnvelopePayload> _claimEnvelope(MessageBodyClaim claim, string originalTypeName) => new() {
    MessageId = MessageId.New(),
    Payload = new BodyClaimEnvelopePayload(claim, "application/json", originalTypeName),
    Hops = [_hop(Guid.CreateVersion7())],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
  };

  private static ServiceProvider _provider(
      NoOpWorkCoordinator coordinator, JsonSerializerOptions options, Action<ServiceCollection>? configure = null) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton(options);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(options));
    configure?.Invoke(services);
    return services.BuildServiceProvider();
  }

  private static async Task<(TransportConsumerWorker Worker, BatchTransport Transport)> _startAsync(
      IServiceProvider sp, ILogger<TransportConsumerWorker>? logger = null) {
    var transport = new BatchTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination(TOPIC, SUBSCRIPTION));
    var worker = new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance, parallelizeStreams: false),
      metrics: null,
      logger: logger ?? NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      routingOptions: Options.Create(new RoutingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      claimWorkerOptions: Options.Create(new ClaimWorkerOptions()),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      controlClass: Options.Create(new ControlClassOptions()));
    await worker.StartAsync(CancellationToken.None);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
    return (worker, transport);
  }

  /// <summary>Delegates to a real serializer, failing only for a payload that carries the marker.</summary>
  private sealed class SelectivelyFailingSerializer(IEnvelopeSerializer inner, string failOnMarker) : IEnvelopeSerializer {
    public const string FAILURE_TEXT = "JsonTypeInfo metadata for this payload was not provided by the resolver chain";

    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) =>
      (envelope.Payload is UnstorableProbeEvent probe && probe.Marker == failOnMarker) || envelope.Payload is HiddenPayload
        ? throw new NotSupportedException(FAILURE_TEXT)
        : inner.SerializeEnvelope(envelope);

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) =>
      inner.DeserializeMessage(jsonEnvelope, messageTypeName);
  }

  private sealed class CapturingLogger : ILogger<TransportConsumerWorker> {
    private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();
    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => [.. _entries];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      => _entries.Enqueue((logLevel, formatter(state, exception), exception));
  }

  private sealed class BodyStore(string providerName) : IMessageBodyStore {
    private readonly ConcurrentDictionary<string, byte[]> _bodies = new();
    public string ProviderName { get; } = providerName;

    public Task<MessageBodyClaim> UploadAsync(ReadOnlyMemory<byte> body, string contentType,
        MessageBodyUploadOptions? options = null, CancellationToken cancellationToken = default) {
      var key = $"test://{Guid.CreateVersion7():N}";
      var copy = body.ToArray();
      _bodies[key] = copy;
      return Task.FromResult(new MessageBodyClaim(
        ProviderName, key, copy.Length, "sha256-" + Convert.ToHexString(SHA256.HashData(copy)), contentType, DateTimeOffset.UtcNow));
    }

    public Task<ReadOnlyMemory<byte>> DownloadAsync(MessageBodyClaim claim, MessageBodyDownloadOptions? options = null,
        CancellationToken cancellationToken = default) => Task.FromResult<ReadOnlyMemory<byte>>(_bodies[claim.StorageKey]);

    public Task DeleteAsync(MessageBodyClaim claim, MessageBodyDeleteOptions? options = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private sealed class BatchTransport : ITransport {
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) {
      _batchHandler = batchHandler;
      return Task.FromResult<ISubscription>(new Subscription());
    }
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull
      => throw new NotSupportedException();
    public Task DeliverBatchAsync(IReadOnlyList<TransportMessage> messages) =>
      _batchHandler is null ? throw new InvalidOperationException("No batch handler subscribed yet") : _batchHandler(messages, CancellationToken.None);
  }

  private sealed class Subscription : ISubscription {
    public bool IsActive => true;
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() { }
  }
}
