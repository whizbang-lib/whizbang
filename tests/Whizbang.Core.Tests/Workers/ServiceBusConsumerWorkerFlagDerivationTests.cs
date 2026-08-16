using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Receive-side flag derivation for <see cref="ServiceBusConsumerWorker"/> — the SECOND receive-path
/// worker (the one the Azure Service Bus hosting path registers). Transport payloads arrive as
/// <c>JsonElement</c>, so runtime interface checks are blind here; the persisted
/// <see cref="EventFlags"/> and the ephemeral TTL must come from the compile-time catalog by wire
/// type name — exactly the guarantee <see cref="TransportConsumerWorker"/> already carries. Without
/// it, every flag-bearing event loses its flags at each service boundary served by this worker
/// (a collective event is never routed to the collective sink on the receiving service).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs</code-under-test>
[Category("Workers")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ServiceBusConsumerWorkerFlagDerivationTests {

  // Marker types for catalog entries. Payloads are delivered as JsonElement, so these exist only to
  // give the catalog a Type + ClrTypeName — the runtime never instantiates them.
  private sealed class SbcTestCollectiveMarker;
  private sealed class SbcTestCompositeMarker;
  private sealed class SbcTestEphemeralMarker;
  private sealed class SbcTestSourcedMarker;
  private sealed class SbcTestTtlMarker;

  private sealed class FakeCatalog : IMessageTypeCatalog {
    private static readonly IReadOnlyList<MessageTypeCatalogEntry> _entries = [
      new(typeof(SbcTestCollectiveMarker), TypeNameFormatter.FormatClrTypeName(typeof(SbcTestCollectiveMarker)), "event", null) { IsCollective = true },
      new(typeof(SbcTestCompositeMarker), TypeNameFormatter.FormatClrTypeName(typeof(SbcTestCompositeMarker)), "event", null) { IsComposite = true },
      new(typeof(SbcTestEphemeralMarker), TypeNameFormatter.FormatClrTypeName(typeof(SbcTestEphemeralMarker)), "event", null) {
        Ephemeral = new EphemeralInfo(Destruction.WhenConsumed, TransientStorage.PersistedRow, -1, -1)
      },
      new(typeof(SbcTestSourcedMarker), TypeNameFormatter.FormatClrTypeName(typeof(SbcTestSourcedMarker)), "event", null),
      new(typeof(SbcTestTtlMarker), TypeNameFormatter.FormatClrTypeName(typeof(SbcTestTtlMarker)), "event", null) {
        Ephemeral = new EphemeralInfo(Destruction.AfterTtl, TransientStorage.PersistedRow, -1, 90)
      },
    ];
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => _entries;
  }

  [Test]
  public async Task HandleMessage_JsonElementCollectivePayload_StampsCollectiveFlagAsync() {
    var stored = await _deliverAsync(typeof(SbcTestCollectiveMarker));
    await Assert.That(stored.Flags & EventFlags.Collective).IsEqualTo(EventFlags.Collective)
      .Because("A collective event received over the service bus must keep EventFlags.Collective — " +
               "it is what routes the event to the collective sink on the receiving service.");
  }

  [Test]
  public async Task HandleMessage_JsonElementCompositePayload_StampsCompositeFlagAsync() {
    var stored = await _deliverAsync(typeof(SbcTestCompositeMarker));
    await Assert.That(stored.Flags & EventFlags.Composite).IsEqualTo(EventFlags.Composite);
  }

  [Test]
  public async Task HandleMessage_JsonElementEphemeralPayload_StampsEphemeralFlagAsync() {
    var stored = await _deliverAsync(typeof(SbcTestEphemeralMarker));
    await Assert.That(stored.Flags & EventFlags.Ephemeral).IsEqualTo(EventFlags.Ephemeral)
      .Because("An ephemeral event received over the service bus must keep EventFlags.Ephemeral — " +
               "it is what the receiving service's consumption-gated reaper keys on.");
  }

  [Test]
  public async Task HandleMessage_JsonElementAfterTtlPayload_CarriesTtlInMetadataAsync() {
    var stored = await _deliverAsync(typeof(SbcTestTtlMarker));
    await Assert.That(stored.Flags & EventFlags.Ephemeral).IsEqualTo(EventFlags.Ephemeral);
    await Assert.That(stored.Metadata!.EphemeralTtlSeconds).IsEqualTo(90)
      .Because("The TTL rides the envelope metadata (\"ett\") so the receiving service's emit chain " +
               "can materialise ephemeral_expires_at.");
  }

  [Test]
  public async Task HandleMessage_JsonElementSourcedPayload_StaysNoneAsync() {
    var stored = await _deliverAsync(typeof(SbcTestSourcedMarker));
    await Assert.That(stored.Flags).IsEqualTo(EventFlags.None);
  }

  // ============================================================
  // Harness
  // ============================================================

  private static async Task<InboxMessage> _deliverAsync(Type payloadType) {
    var transport = new FlagSbTransport();
    var strategy = new FlagSbStrategy();
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    var worker = new ServiceBusConsumerWorker(
      transport,
      scopeFactory,
      new JsonSerializerOptions(),
      new TestLogger<ServiceBusConsumerWorker>(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("flags-topic", "flags-sub")]
      },
      eventMarkerResolver: new EventMarkerResolver(new FakeCatalog()),
      ephemeralModeResolver: new EphemeralModeResolver(new FakeCatalog()));

    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));
    try {
      var envelope = _createJsonEnvelope(new MessageId(TrackedGuid.NewMedo()));
      // The wire envelope-type string the ASB transport hands over — the worker extracts the
      // payload's assembly-qualified name from between the [[ ]], the same shape the real broker
      // delivers via the EnvelopeType application property.
      var envelopeType = $"Whizbang.Core.Messaging.MessageEnvelope`1[[{payloadType.AssemblyQualifiedName}]], Whizbang.Core";
      await transport.CapturedBatchHandler!(
        [new TransportMessage(envelope, envelopeType)], CancellationToken.None);

      await Assert.That(strategy.CapturedInboxMessages.Count).IsEqualTo(1)
        .Because("The delivered message must land in the inbox for the flag assertion to mean anything.");
      return strategy.CapturedInboxMessages[0];
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
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

  private sealed class FlagSbTransport : ITransport {
    public Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? CapturedBatchHandler { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      CapturedBatchHandler = batchHandler;
      return Task.FromResult<ISubscription>(new FlagSbSubscription());
    }

    public Task PublishAsync(
        IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull
      => throw new NotSupportedException();
  }

  private sealed class FlagSbSubscription : ISubscription {
    public bool IsActive => true;

    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() {
      // Nothing to release — test double.
    }
  }

  private sealed class FlagSbStrategy : IWorkCoordinatorStrategy {
    public List<InboxMessage> CapturedInboxMessages { get; } = [];

    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) => CapturedInboxMessages.Add(message);
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default)
      => FlushAndGetBatchAsync(flags, ct);

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default)
      => Task.FromResult(new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
  }
}
