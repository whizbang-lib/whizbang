using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Receive-side flag derivation for <see cref="TransportConsumerWorker"/>. Transport payloads arrive
/// as <c>JsonElement</c>, so runtime interface checks (<c>payload is ICollectiveEvent</c>) are blind
/// there — the persisted <see cref="EventFlags"/> must come from the compile-time catalog by type
/// name, or every flag-bearing event silently loses its flags at each service boundary (a collective
/// event then never routes to the collective sink on the receiving service; an ephemeral event is
/// never reaped there).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
[Category("Workers")]
public class TransportConsumerWorkerFlagDerivationTests {

  // Marker types for catalog entries. Payloads are delivered as JsonElement, so these exist only to
  // give the catalog a Type + ClrTypeName — the runtime never instantiates them.
  private sealed class TestCollectiveMarker;
  private sealed class TestCompositeMarker;
  private sealed class TestEphemeralMarker;
  private sealed class TestSourcedMarker;
  private sealed class TestTtlMarker;

  private sealed class FakeCatalog : IMessageTypeCatalog {
    private static readonly IReadOnlyList<MessageTypeCatalogEntry> _entries = [
      new(typeof(TestCollectiveMarker), TypeNameFormatter.FormatClrTypeName(typeof(TestCollectiveMarker)), "event", null) { IsCollective = true },
      new(typeof(TestCompositeMarker), TypeNameFormatter.FormatClrTypeName(typeof(TestCompositeMarker)), "event", null) { IsComposite = true },
      new(typeof(TestEphemeralMarker), TypeNameFormatter.FormatClrTypeName(typeof(TestEphemeralMarker)), "event", null) {
        Ephemeral = new EphemeralInfo(Destruction.WhenConsumed, TransientStorage.PersistedRow, -1, -1)
      },
      new(typeof(TestSourcedMarker), TypeNameFormatter.FormatClrTypeName(typeof(TestSourcedMarker)), "event", null),
      new(typeof(TestTtlMarker), TypeNameFormatter.FormatClrTypeName(typeof(TestTtlMarker)), "event", null) {
        Ephemeral = new EphemeralInfo(Destruction.AfterTtl, TransientStorage.PersistedRow, -1, 90)
      },
    ];
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => _entries;
  }

  [Test]
  public async Task Batch_JsonElementCollectivePayload_StampsCollectiveFlagAsync() {
    var stored = await _deliverAsync(typeof(TestCollectiveMarker));
    await Assert.That(stored.Flags & EventFlags.Collective).IsEqualTo(EventFlags.Collective)
      .Because("A collective event received over the transport must keep EventFlags.Collective — " +
               "it is what routes the event to the collective sink on the receiving service.");
  }

  [Test]
  public async Task Batch_JsonElementCompositePayload_StampsCompositeFlagAsync() {
    var stored = await _deliverAsync(typeof(TestCompositeMarker));
    await Assert.That(stored.Flags & EventFlags.Composite).IsEqualTo(EventFlags.Composite);
  }

  [Test]
  public async Task Batch_JsonElementEphemeralPayload_StampsEphemeralFlagAsync() {
    var stored = await _deliverAsync(typeof(TestEphemeralMarker));
    await Assert.That(stored.Flags & EventFlags.Ephemeral).IsEqualTo(EventFlags.Ephemeral)
      .Because("An ephemeral event received over the transport must keep EventFlags.Ephemeral — " +
               "it is what the receiving service's consumption-gated reaper keys on.");
  }

  [Test]
  public async Task Batch_JsonElementAfterTtlPayload_CarriesTtlInMetadataAsync() {
    // The TTL rides the envelope metadata ("ett") so the receiving service's emit chain can
    // materialise ephemeral_expires_at — deriving it from payload.GetType() is blind for
    // transport JsonElement payloads, silently dropping the TTL at every service boundary.
    var stored = await _deliverAsync(typeof(TestTtlMarker));
    await Assert.That(stored.Flags & EventFlags.Ephemeral).IsEqualTo(EventFlags.Ephemeral);
    await Assert.That(stored.Metadata!.EphemeralTtlSeconds).IsEqualTo(90);
  }

  [Test]
  public async Task Batch_JsonElementSourcedPayload_StaysNoneAsync() {
    var stored = await _deliverAsync(typeof(TestSourcedMarker));
    await Assert.That(stored.Flags).IsEqualTo(EventFlags.None);
  }

  // ============================================================
  // Harness
  // ============================================================

  private static async Task<InboxMessage> _deliverAsync(Type payloadType) {
    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    await using var sp = services.BuildServiceProvider();

    var transport = new FlagTransport();
    var worker = _buildWorker(transport, sp);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));
    try {
      var envelope = _createJsonEnvelope(new MessageId(TrackedGuid.NewMedo()));
      // The wire envelope-type string the transport hands over: the worker extracts the payload's
      // assembly-qualified name from between the [[ ]] — the same shape a real broker delivers.
      var envelopeType = $"Whizbang.Core.Messaging.MessageEnvelope`1[[{payloadType.AssemblyQualifiedName}]]";
      await transport.DeliverBatchAsync([new TransportMessage(envelope, envelopeType)]);

      await Assert.That(coordinator.StoredMessages.Count).IsEqualTo(1)
        .Because("The delivered message must land in the inbox for the flag assertion to mean anything.");
      return coordinator.StoredMessages[0];
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  private static TransportConsumerWorker _buildWorker(ITransport transport, IServiceProvider serviceProvider) {
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("flags-topic"));
    return new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      serviceProvider.GetRequiredService<IServiceScopeFactory>(), new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, metrics: null,
      NullLogger<TransportConsumerWorker>.Instance,
      ephemeralModeResolver: new EphemeralModeResolver(new FakeCatalog()),
      eventMarkerResolver: new EventMarkerResolver(new FakeCatalog()),
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider());
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

  private sealed class FlagTransport : ITransport {
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
      return Task.FromResult<ISubscription>(new FlagSubscription());
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

  private sealed class FlagSubscription : ISubscription {
    public bool IsActive => true;

    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() {
      // Nothing to release — test double.
    }
  }
}
