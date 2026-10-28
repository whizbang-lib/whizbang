// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Registry;
using Whizbang.Core.Serialization;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tags;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Cross-path parity for the stream an event is stored on (#1798). One table holds every way an event reaches a
/// service's store; each row produces the event with the producer's own code and stores it with the receiver's own
/// code, and asserts the receiver stores it on the stream its producer stored it on (or names, for a record the
/// producer only ships). A second test folds each stored event through the rebuild's row router, which must keep it on
/// the row live apply and the rewind key it by: the stream it is stored on.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StreamResolutionSitesTests"/> enumerates by machine every composite type and every site that builds an
/// inbox or outbox row, and fails until a new one is added to <see cref="Covered"/> here, so a new delivery path cannot
/// reach the store without a row in this table.
/// </para>
/// <para>
/// Live apply and the rewind take the row's stream from the stored event itself (the work row's <c>stream_id</c> and
/// the stream the rewind reads), so they cannot diverge from the stored stream; the rebuild is the one path that can
/// route an event to another row, and it is asserted here through <see cref="RebuildRowRouting"/>.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Messaging/StreamIdentity.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/CompositeInboxFanout.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/ReceivedInboxMessageBuilder.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/RebuildRowRouting.cs</code-under-test>
[Category("Messaging")]
[NotInParallel("StreamResolutionParity")]
public class StreamResolutionParityTests {
  /// <summary>An event naming its stream, as a domain event's <c>[StreamId]</c> does.</summary>
  public sealed record ParityEvent([property: StreamId] Guid Target, string Note) : IEvent;

  /// <summary>An event naming no stream (an older contract, or a type a raw bundle carries by name only).</summary>
#pragma warning disable WHIZ009 // deliberately names no stream: the shape under test
  public sealed record ParityUnkeyedEvent(string Note) : IEvent;
#pragma warning restore WHIZ009

  /// <summary>A collective, with or without an ordering key.</summary>
  public sealed record ParityCollective : CollectiveEventBase;

  /// <summary>An application composite.</summary>
  public sealed class ParityComposite : CompositeEventBase;

  /// <summary>What one delivery shape produced: the streams its producer stored or named, and the rows the receiver stored.</summary>
  public sealed record Delivery(IReadOnlyList<Guid> ProducerStreams, IReadOnlyList<InboxMessage> Stored);

  /// <summary>
  /// The composite types the table covers, each by a row below. <see cref="StreamResolutionSitesTests"/> holds this
  /// against every composite type the framework defines.
  /// </summary>
  public static readonly IReadOnlyCollection<Type> Covered = [
    typeof(ParityComposite), typeof(CoalescedEventsComposite), typeof(AuditEventsComposite), typeof(RedeliveryComposite),
  ];

  /// <summary>The row-building sites the table covers, by source file, each by a row below.</summary>
  public static readonly IReadOnlyCollection<string> CoveredInboxSites = [
    "src/Whizbang.Core/Workers/ReceivedInboxMessageBuilder.cs",
    "src/Whizbang.Core/Messaging/CompositeInboxFanout.cs",
  ];

  private static readonly Guid _s1 = Guid.Parse("0198f5d2-1111-7000-8000-000000000001");
  private static readonly Guid _s2 = Guid.Parse("0198f5d2-1111-7000-8000-000000000002");
  private static readonly Guid _composite = Guid.Parse("0198f5d2-1111-7000-8000-0000000000c0");
  private static readonly Guid _collectiveStream = Guid.Parse("0198f5d2-1111-7000-8000-0000000000c1");
  private static readonly TenantCollectiveScope _scope = new("tenant-parity");

  private static readonly Dictionary<string, Func<Delivery>> _shapes = new(StringComparer.Ordinal) {
    ["plain event naming its stream"] = _plain(named: true),
    ["plain event naming no stream"] = _plain(named: false),
    ["dead-letter recovery"] = _deadLetterRecovery,
    ["typed composite child naming its own stream"] = () => _typedComposite(new ParityEvent(_s1, "own"), hopNamesStream: true, expected: _s1),
    ["typed composite child naming no stream"] = () => _typedComposite(new ParityUnkeyedEvent("none"), hopNamesStream: true, expected: _composite),
    ["typed child of a composite whose hop names no stream"] = () => _typedComposite(new ParityUnkeyedEvent("none"), hopNamesStream: false, expected: _composite),
    ["coalesced bundle"] = _coalesced(withStreams: true),
    ["coalesced bundle from an older producer"] = _coalesced(withStreams: false),
    ["audit record shipped alone"] = _auditAlone,
    ["audit records shipped in a bundle"] = _auditBundle,
    ["audit record from the event store decorator"] = _auditFromDecorator,
    ["re-delivery bundle"] = _redelivery,
    ["re-delivery bundle from an older origin"] = _redeliveryFromAnOlderOrigin,
    ["collective without an ordering key"] = () => _plainOf(_mintedCollective(), _collectiveStream),
    ["collective with an ordering key"] = () => _plainOf(_keyedCollective(), CollectiveOrdering.StreamIdFor(_scope, "key-1")),
    ["keyed collective inside a composite"] = () => _typedComposite(_keyedCollective(), hopNamesStream: true, expected: CollectiveOrdering.StreamIdFor(_scope, "key-1")),
    ["event published straight to a transport"] = _transportManager,
    ["event published through the dispatcher transport bridge"] = _transportBridge,
  };

  /// <summary>Every delivery shape, by name.</summary>
  public static IEnumerable<string> Shapes() => _shapes.Keys;

  /// <summary>The receiver stores each event on the stream its producer stored it on.</summary>
  [Test]
  [MethodDataSource(nameof(Shapes))]
  public async Task StoredStream_IsTheStreamTheProducerStoredItOnAsync(string shape) {
    var delivery = _shapes[shape]();

    await Assert.That(delivery.Stored.Count).IsEqualTo(delivery.ProducerStreams.Count)
      .Because("setup: the shape stores one row per event its producer produced");
    await Assert.That(delivery.Stored.Select(s => s.StreamId ?? Guid.Empty).ToList()).IsEquivalentTo(delivery.ProducerStreams)
      .Because($"{shape}: a row keyed by the producer's stream must receive the event, so the receiver stores it there");
  }

  /// <summary>
  /// A rebuild folds each stored event onto the row of the stream it is stored on, the row live apply and the rewind
  /// fold it onto: only an upcaster's re-key moves it, and none ran here.
  /// </summary>
  [Test]
  [MethodDataSource(nameof(Shapes))]
  public async Task Rebuild_FoldsEachStoredEventOntoTheRowLiveApplyKeysItByAsync(string shape) {
    var delivery = _shapes[shape]();

    foreach (var stored in delivery.Stored) {
      var asRead = new MessageEnvelope<IEvent> {
        MessageId = new MessageId(stored.MessageId),
        Payload = new ParityUnkeyedEvent(shape),
        Hops = [],
        DispatchContext = new MessageDispatchContext {
          Mode = DispatchModes.Local,
          Source = MessageSource.Local
        },
      };
      await Assert.That(RebuildRowRouting.RowOf(asRead, stored.StreamId!.Value)).IsEqualTo(stored.StreamId.Value)
        .Because($"{shape}: every path folds an event onto the row of the stream it is stored on");
    }
  }

  /// <summary>A shape the table names is one it builds: a typo in a key would otherwise leave a path untested.</summary>
  [Test]
  public async Task Shapes_EachBuildsAtLeastOneStoredEventAsync() {
    foreach (var (shape, build) in _shapes) {
      await Assert.That(build().Stored.Count).IsGreaterThan(0).Because(shape);
    }
  }

  // ── Producers and receivers ────────────────────────────────────────────────────────────────────────────────

  /// <summary>The producer names the stream on the hop with the dispatcher's writer; the receiver reads it on receipt.</summary>
  private static Func<Delivery> _plain(bool named) => () => named
    ? _plainOf(new ParityEvent(_s1, "plain"), _s1)
    : _plainOf(new ParityUnkeyedEvent("plain"), expected: null);

  private static Delivery _plainOf(IMessage payload, Guid? expected) {
    var named = StreamIdExtractorRegistry.GetComposite().ExtractStreamId(payload, payload.GetType());
    var envelope = _wireEnvelope(payload, named is { } stream ? StreamIdentity.HopMetadata(stream) : null);
    // The outbox stores the message on the stream its hop names, or its own id: StreamIdentity.Of, as Dispatcher does.
    var producerStream = StreamIdentity.Of(envelope);
    _assertProducerStream(producerStream, expected ?? envelope.MessageId.Value);
    return new Delivery([producerStream], [_receive(envelope, payload.GetType())]);
  }

  private static void _assertProducerStream(Guid actual, Guid expected) {
    if (actual != expected) {
      throw new InvalidOperationException($"setup: the producer stored the event on {actual}, not {expected}");
    }
  }

  private static Delivery _deadLetterRecovery() {
    // Dead-letter custody keeps the envelope as the wire carried it: JSON.
    var envelope = _wireEnvelope(JsonElement.Parse($$"""{"target":"{{_s1}}","note":"recovered"}"""), StreamIdentity.HopMetadata(_s1));
    var typeName = TypeNameFormatter.Format(typeof(ParityEvent));
    using var provider = new ServiceCollection()
      .AddSingleton<IEventTypeProvider>(new FixedEventTypes(typeof(ParityEvent)))
      .AddSingleton(JsonContextRegistry.CreateCombinedOptions())
      .BuildServiceProvider();
    var wire = JsonSerializer.Serialize(envelope, JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
    var recovered = ReceivedInboxMessageBuilder.BuildRedelivery(provider,
      new DeadLetterRedelivery(Guid.NewGuid(), DeadLetterSourceTable.BROKER, envelope.MessageId.Value, typeName, wire));
    return new Delivery([StreamIdentity.Of(envelope)], [recovered]);
  }

  private static Delivery _typedComposite(IMessage inner, bool hopNamesStream, Guid expected) {
    var composite = new ParityComposite { StreamId = _composite, Inner = [inner] };
    var source = _wireEnvelope(composite, hopNamesStream ? StreamIdentity.HopMetadata(_composite) : null);
    return new Delivery([expected], _fanOut(composite, source));
  }

  private static Func<Delivery> _coalesced(bool withStreams) => () => {
    var singles = new[] { _outboxSingle(_s1), _outboxSingle(_s2) };
    var composite = (CoalescedEventsComposite)CoalesceShipWorker.BuildDefaultComposite(new CoalesceFoldBatch {
      Group = "parity",
      Singles = singles,
      Atomicity = FanoutAtomicity.Independent,
    });
    if (!withStreams) {
      composite = new CoalescedEventsComposite {
        StreamId = composite.StreamId,
        InnerPayloads = composite.InnerPayloads,
        InnerTypeNames = composite.InnerTypeNames,
        InnerEventIds = composite.InnerEventIds,
      };
    }
    var source = _wireEnvelope(composite, metadata: null);
    return new Delivery(withStreams ? [_s1, _s2] : [composite.StreamId, composite.StreamId], _fanOut(composite, source));
  };

  private static OutboxMessage _auditRecord() {
    var audited = _outboxSingle(_s1) with { Scope = new Whizbang.Core.Lenses.PerspectiveScope { TenantId = "tenant-parity" } };
    return AuditOutboxMessageBuilder.TryBuildAuditMessage(audited, new SystemEventOptions().EnableEventAudit())!;
  }

  private static Guid _recordIdOf(OutboxMessage record) => record.Envelope.Payload.GetProperty(nameof(EventAudited.Id)).GetGuid();

  private static Delivery _auditAlone() {
    var record = _auditRecord();
    return new Delivery([_recordIdOf(record)], [_receive(record.Envelope, typeof(EventAudited))]);
  }

  private static Delivery _auditBundle() {
    var records = new[] { _auditRecord(), _auditRecord() };
    var composite = (AuditEventsComposite)SystemEventCoalesceDefaults.BuildAuditComposite(new CoalesceFoldBatch {
      Group = SystemTags.AUDIT,
      Singles = records,
      Atomicity = FanoutAtomicity.Independent,
    });
    return new Delivery([.. records.Select(_recordIdOf)], _fanOut(composite, _wireEnvelope(composite, metadata: null)));
  }

  private static Delivery _auditFromDecorator() {
    var channel = new CaptureChannel();
    var decorator = new AuditingEventStoreDecorator(
      new InertStore(), channel, Options.Create(new SystemEventOptions().EnableEventAudit()),
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      NoOpinionAuditDecisionHook.Instance, logger: NullLogger<AuditingEventStoreDecorator>.Instance);
    decorator.AppendAsync(_s1, new ParityEvent(_s1, "audited")).GetAwaiter().GetResult();
    var record = channel.Queued.Single();
    return new Delivery([_recordIdOf(record)], [_receive(record.Envelope, typeof(EventAudited))]);
  }

  private static Delivery _redelivery() {
    var transport = new CaptureTransport();
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(transport: transport, envelopeSerializer: serializer,
      instanceProvider: new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()), compositeFactory: new CompositeFactory());
    pump.PublishAsync([_redeliveryEvent(_s1, 1), _redeliveryEvent(_s1, 2), _redeliveryEvent(_s2, 1)], topic: "parity", target: "svc")
      .GetAwaiter().GetResult();
    var stored = serializer.Captured.SelectMany(c => _fanOut(c.Payload, c)).ToList();
    return new Delivery([_s1, _s1, _s2], stored);
  }

  private static Delivery _redeliveryFromAnOlderOrigin() {
    var composite = new RedeliveryComposite {
      StreamId = _s1,
      InnerPayloads = [JsonElement.Parse("{\"v\":1}")],
      InnerTypeNames = [TypeNameFormatter.Format(typeof(ParityUnkeyedEvent))],
      InnerEventIds = [Guid.NewGuid()],
    };
    return new Delivery([_s1], _fanOut(composite, _wireEnvelope(composite, metadata: null)));
  }

  private static ParityCollective _mintedCollective() => new() { Scope = _scope, StreamId = _collectiveStream };

  private static ParityCollective _keyedCollective() => new() { Scope = _scope, OrderingKey = "key-1" };

  private static Delivery _transportManager() {
    var transport = new CaptureTransport();
    var manager = new TransportManager(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    manager.AddTransport(TransportType.InProcess, transport);
    manager.PublishToTargetsAsync(new ParityEvent(_s1, "direct"), [new PublishTarget { TransportType = TransportType.InProcess, Destination = "parity" }])
      .GetAwaiter().GetResult();
    var sent = transport.Published.Single();
    return new Delivery([_s1], [_receive(sent, typeof(ParityEvent))]);
  }

  private static Delivery _transportBridge() {
    var transport = new CaptureTransport();
    var bridge = new DispatcherTransportBridge(new Whizbang.Core.Tests.Perspectives.RebuildEventDispatcherDouble(), transport,
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    bridge.PublishToTransportAsync(new ParityEvent(_s1, "bridged"), new TransportDestination("parity")).GetAwaiter().GetResult();
    var sent = transport.Published.Single();
    return new Delivery([_s1], [_receive(sent, typeof(ParityEvent))]);
  }

  // ── Plumbing ───────────────────────────────────────────────────────────────────────────────────────────────

  /// <summary>Stores a received envelope as both consumer workers store it.</summary>
  private static InboxMessage _receive(IMessageEnvelope envelope, Type payloadType) {
    var typeName = TypeNameFormatter.Format(payloadType);
    var json = new MessageEnvelope<JsonElement> {
      MessageId = envelope.MessageId,
      Payload = JsonElement.Parse("{}"),
      Hops = envelope.Hops,
      DispatchContext = envelope.DispatchContext,
    };
    return ReceivedInboxMessageBuilder.Build(
      new ReceivedInboxMessageBuilder.ReceivedEnvelope(envelope, json, EnvelopeTypeNameHelper.Format(typeName), typeName, IsEvent: true),
      priority: 0, guardSite: "Parity.Receive", eventMarkerResolver: null, ephemeralModeResolver: null);
  }

  private static List<InboxMessage> _fanOut(ICompositeEvent composite, IMessageEnvelope source) {
    using var scope = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()))
      .AddSingleton<IStreamIdExtractor>(StreamIdExtractorRegistry.GetComposite())
      .BuildServiceProvider();
    var result = CompositeInboxFanout.TryExpand(composite, source, scope);
    if (result.Outcome != CompositeInboxFanout.FanoutOutcome.Expanded) {
      throw new InvalidOperationException($"setup: the fan-out did not expand ({result.Outcome}: {result.Detail})");
    }
    return [.. result.Children];
  }

  private static MessageEnvelope<TPayload> _wireEnvelope<TPayload>(TPayload payload, IReadOnlyDictionary<string, JsonElement>? metadata) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [new MessageHop {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UnixEpoch,
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Metadata = metadata,
    }],
    DispatchContext = new MessageDispatchContext {
      Mode = DispatchModes.Outbox,
      Source = MessageSource.Outbox
    },
  };

  private static OutboxMessage _outboxSingle(Guid stream) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonElement.Parse($$"""{"target":"{{stream}}","note":"single"}"""),
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UnixEpoch,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Metadata = StreamIdentity.HopMetadata(stream),
      }],
      DispatchContext = new MessageDispatchContext {
        Mode = DispatchModes.Outbox,
        Source = MessageSource.Outbox
      },
    };
    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "parity",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.Hops
      },
      EnvelopeType = EnvelopeTypeNameHelper.Format(TypeNameFormatter.Format(typeof(ParityEvent))),
      StreamId = StreamIdentity.Of(envelope),
      IsEvent = true,
      MessageType = TypeNameFormatter.Format(typeof(ParityEvent)),
    };
  }

  private static RedeliveryEvent _redeliveryEvent(Guid stream, long version) => new() {
    EventId = Guid.NewGuid(),
    StreamId = stream,
    Version = version,
    CommitSequence = version,
    EventType = TypeNameFormatter.Format(typeof(ParityUnkeyedEvent)),
    EventData = "{\"note\":\"repaired\"}",
    Metadata = "{}",
    Scope = null,
    Flags = 0,
  };

  private sealed class FixedEventTypes(params Type[] types) : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => types;
  }

  private sealed class CaptureSerializer : IEnvelopeSerializer {
    public List<IMessageEnvelope<RedeliveryComposite>> Captured { get; } = [];

    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      Captured.Add((IMessageEnvelope<RedeliveryComposite>)envelope);
      return new SerializedEnvelope(
        new MessageEnvelope<JsonElement> {
          MessageId = envelope.MessageId,
          Payload = default,
          Hops = [.. envelope.Hops],
          DispatchContext = envelope.DispatchContext,
        },
        "Envelope", TypeNameFormatter.Format(typeof(RedeliveryComposite)));
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) => throw new NotSupportedException();
  }

  private sealed class CaptureTransport : ITransport {
    public List<IMessageEnvelope> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      lock (Published) {
        Published.Add(envelope);
      }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private sealed class CaptureChannel : IDeferredOutboxChannel {
    public List<OutboxMessage> Queued { get; } = [];
    public ValueTask QueueAsync(OutboxMessage message, CancellationToken ct = default) {
      Queued.Add(message);
      return ValueTask.CompletedTask;
    }
    public IReadOnlyList<OutboxMessage> DrainAll() => [.. Queued];
    public bool HasPending => Queued.Count > 0;
  }

  private sealed class InertStore : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<IEvent>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(0L);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }
}
