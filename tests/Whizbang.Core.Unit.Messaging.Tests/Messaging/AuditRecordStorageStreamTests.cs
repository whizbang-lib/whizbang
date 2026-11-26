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
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Registry;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Design lock for where an audit record is stored (#2331, owner decision 2026-11-25): the tenant's audit ledger
/// (<see cref="AuditLedger.StreamFor"/>) is the lane a record travels and is ordered on, and the record is STORED on a
/// stream of its own, its own id. Never on the ledger, which would fold a tenant's whole trail into one perspective
/// row and put every append of the tenant on one stream lock; never on the audited event's stream, which is the
/// domain's. Each writer is locked here: the outbox path, a coalesced bundle, the event store decorator, and the
/// emitter that stores a record locally.
/// </summary>
/// <docs>fundamentals/security/audit-logging#record-storage</docs>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditOutboxMessageBuilder.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventCoalesceDefaults.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditingEventStoreDecorator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventEmitter.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventStream.cs</code-under-test>
[Category("Messaging")]
[NotInParallel("StreamIdExtractorRegistry")] // shares the process-wide stream id extractor registry the fan-out reads
public class AuditRecordStorageStreamTests {
  private const string TENANT = "tenant-audit-storage";

  /// <summary>An audited domain event naming its stream.</summary>
  public sealed record StorageAuditedEvent([property: StreamId] Guid Target, string Note) : IEvent;

  private static readonly Guid _auditedStreamA = Guid.Parse("0198f5d2-2331-7000-8000-00000000000a");
  private static readonly Guid _auditedStreamB = Guid.Parse("0198f5d2-2331-7000-8000-00000000000b");

  [Test]
  public async Task ARecordShippedAlone_TravelsOnTheLedger_AndIsStoredOnItsOwnIdAsync() {
    var record = _auditRecordOf(_auditedEvent(_auditedStreamA));

    var stored = _receive(record.Envelope);

    await Assert.That(record.StreamId).IsEqualTo(AuditLedger.StreamFor(TENANT))
      .Because("the tenant's ledger is the lane a record travels and is ordered on");
    await _assertStoredOnItsOwnIdAsync(stored.StreamId, _recordIdOf(record), _auditedStreamA);
  }

  [Test]
  public async Task RecordsShippedInABundle_TravelOnTheLedger_AndEachIsStoredOnItsOwnIdAsync() {
    var records = new[] { _auditRecordOf(_auditedEvent(_auditedStreamA)), _auditRecordOf(_auditedEvent(_auditedStreamB)) };
    var composite = (AuditEventsComposite)SystemEventCoalesceDefaults.BuildAuditComposite(new CoalesceFoldBatch {
      Group = SystemTags.AUDIT,
      Singles = records,
      Atomicity = FanoutAtomicity.Independent,
    });

    var stored = _fanOut(composite);

    await Assert.That(composite.StreamId).IsEqualTo(AuditLedger.StreamFor(TENANT))
      .Because("a bundle of one tenant's records rides that tenant's ledger");
    await Assert.That(stored.Count).IsEqualTo(2).Because("setup: one stored row per bundled record");
    await _assertStoredOnItsOwnIdAsync(stored[0].StreamId, _recordIdOf(records[0]), _auditedStreamA);
    await _assertStoredOnItsOwnIdAsync(stored[1].StreamId, _recordIdOf(records[1]), _auditedStreamB);
  }

  [Test]
  public async Task ARecordFromTheEventStoreDecorator_TravelsOnTheLedger_AndIsStoredOnItsOwnIdAsync() {
    var channel = new CaptureChannel();
    var decorator = new AuditingEventStoreDecorator(
      new CaptureStore(), channel, Options.Create(new SystemEventOptions().EnableEventAudit()), _instance(),
      NoOpinionAuditDecisionHook.Instance, logger: NullLogger<AuditingEventStoreDecorator>.Instance,
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance));

    await decorator.AppendAsync(_auditedStreamA, _scopedEnvelope(new StorageAuditedEvent(_auditedStreamA, "decorated")));
    var record = channel.Queued.Single();
    var stored = _receive(record.Envelope);

    await Assert.That(record.StreamId).IsEqualTo(AuditLedger.StreamFor(TENANT))
      .Because("the decorator's record travels on the tenant's ledger, as the outbox path's does");
    await _assertStoredOnItsOwnIdAsync(stored.StreamId, _recordIdOf(record), _auditedStreamA);
  }

  [Test]
  public async Task AnEventAuditTheEmitterStoresLocally_IsStoredOnItsOwnIdAsync() {
    var store = new CaptureStore();
    var emitter = _emitter(new SystemEventOptions().EnableEventAudit(), store);
    var audited = _scopedEnvelope(new StorageAuditedEvent(_auditedStreamA, "emitted"));

    await emitter.EmitEventAuditedAsync(_auditedStreamA, 1, audited);

    var record = (MessageEnvelope<EventAudited>)store.Envelopes.Single();
    await Assert.That(record.Payload.Id).IsEqualTo(AuditLedger.RecordIdFor(audited.MessageId.Value))
      .Because("setup: the record's id derives from the event it audits, as on every other writer");
    await _assertStoredOnItsOwnIdAsync(store.StreamIds.Single(), record.Payload.Id, _auditedStreamA);
  }

  [Test]
  public async Task ACommandAuditTheEmitterStoresLocally_IsStoredOnItsOwnIdAsync() {
    var store = new CaptureStore();
    var emitter = _emitter(new SystemEventOptions().EnableCommandAudit(), store);
    var context = new StorageMessageContext();
    context.Metadata["TenantId"] = TENANT;

    await emitter.EmitCommandAuditedAsync("Command", "Response", "Receptor", context);

    var record = (MessageEnvelope<CommandAudited>)store.Envelopes.Single();
    await _assertStoredOnItsOwnIdAsync(store.StreamIds.Single(), record.Payload.Id, audited: null);
  }

  [Test]
  public async Task AnAuditRecordGivenToEmitAsync_IsStoredOnItsOwnIdAsync() {
    // The command audit recorder emits every command audit this way.
    var store = new CaptureStore();
    var emitter = _emitter(new SystemEventOptions().EnableAudit(), store);
    var record = new CommandAudited {
      Id = TrackedGuid.New(),
      CommandType = "C",
      CommandBody = JsonSerializer.SerializeToElement(1),
      Timestamp = DateTimeOffset.UnixEpoch,
      ReceptorName = "R",
      TenantId = TENANT,
    };

    await emitter.EmitAsync(record);

    await _assertStoredOnItsOwnIdAsync(store.StreamIds.Single(), record.Id, audited: null);
  }

  // ── Assertion ──────────────────────────────────────────────────────────────────────────────────────────────

  private static async Task _assertStoredOnItsOwnIdAsync(Guid? storedOn, Guid recordId, Guid? audited) {
    await Assert.That(storedOn).IsEqualTo(recordId)
      .Because("an audit record is stored on a stream of its own, its own id, so each record is one perspective row");
    await Assert.That(storedOn).IsNotEqualTo(AuditLedger.StreamFor(TENANT))
      .Because("storing on the ledger would fold a tenant's whole trail into one row and one stream lock");
    await Assert.That(storedOn).IsNotEqualTo(SystemEventStreams.StreamId)
      .Because("$wb-system holds only the global kinds");
    if (audited is { } auditedStream) {
      await Assert.That(storedOn).IsNotEqualTo(auditedStream)
        .Because("the audited event's stream is the domain's; a record about it is not part of it");
    }
  }

  // ── Producers and receivers ────────────────────────────────────────────────────────────────────────────────

  private static OutboxMessage _auditRecordOf(OutboxMessage audited) =>
    AuditOutboxMessageBuilder.TryBuildAuditMessage(audited, new SystemEventOptions().EnableEventAudit())
      ?? throw new InvalidOperationException("setup: the probe event must be audited");

  private static Guid _recordIdOf(OutboxMessage record) => record.Envelope.Payload.GetProperty(nameof(EventAudited.Id)).GetGuid();

  /// <summary>Stores a received record as both consumer workers store it.</summary>
  private static InboxMessage _receive(IMessageEnvelope envelope) {
    var typeName = TypeNameFormatter.Format(typeof(EventAudited));
    var json = new MessageEnvelope<JsonElement> {
      MessageId = envelope.MessageId,
      Payload = JsonElement.Parse("{}"),
      Hops = envelope.Hops,
      DispatchContext = envelope.DispatchContext,
    };
    return ReceivedInboxMessageBuilder.Build(
      new ReceivedInboxMessageBuilder.ReceivedEnvelope(envelope, json, EnvelopeTypeNameHelper.Format(typeName), typeName, IsEvent: true),
      priority: 0, guardSite: "AuditStorage.Receive", eventMarkerResolver: null, ephemeralModeResolver: null);
  }

  /// <summary>Expands a received bundle as the receiver does.</summary>
  private static List<InboxMessage> _fanOut(ICompositeEvent composite) {
    var source = new MessageEnvelope<ICompositeEvent> {
      MessageId = MessageId.New(),
      Payload = composite,
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UnixEpoch, ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    using var provider = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()))
      .AddSingleton<IStreamIdExtractor>(StreamIdExtractorRegistry.GetComposite())
      .BuildServiceProvider();
    var result = CompositeInboxFanout.TryExpand(composite, source, provider);
    if (result.Outcome != CompositeInboxFanout.FanoutOutcome.Expanded) {
      throw new InvalidOperationException($"setup: the fan-out did not expand ({result.Outcome}: {result.Detail})");
    }
    return [.. result.Children];
  }

  /// <summary>An audited event's outbox row, stored on <paramref name="stream"/> for <see cref="TENANT"/>.</summary>
  private static OutboxMessage _auditedEvent(Guid stream) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonElement.Parse($$"""{"target":"{{stream}}","note":"audited"}"""),
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UnixEpoch,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Metadata = StreamIdentity.HopMetadata(stream),
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "audit-storage",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = envelope.Hops },
      EnvelopeType = EnvelopeTypeNameHelper.Format(TypeNameFormatter.Format(typeof(StorageAuditedEvent))),
      StreamId = stream,
      IsEvent = true,
      MessageType = TypeNameFormatter.Format(typeof(StorageAuditedEvent)),
      Scope = new PerspectiveScope { TenantId = TENANT },
    };
  }

  private static MessageEnvelope<TPayload> _scopedEnvelope<TPayload>(TPayload payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [new MessageHop {
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UnixEpoch,
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = TENANT, UserId = "user-1" }),
    }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private static SystemEventEmitter _emitter(SystemEventOptions options, IEventStore store) =>
    new(Options.Create(options), store, _instance(), logger: NullLogger<SystemEventEmitter>.Instance, auditDecisionHook: null);

  private static ServiceInstanceProvider _instance() => new(configuration: new ConfigurationBuilder().Build());

  private sealed class StorageMessageContext : IMessageContext {
    public MessageId MessageId { get; init; } = MessageId.New();
    public CorrelationId CorrelationId { get; init; } = new(TrackedGuid.New());
    public MessageId CausationId { get; init; } = MessageId.New();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UnixEpoch;
    public string? UserId => null;
    public Dictionary<string, object> Metadata { get; } = [];
    public string? TenantId => null;
    public IScopeContext? ScopeContext => null;
    public ICallerInfo? CallerInfo => null;
    IReadOnlyDictionary<string, object> IMessageContext.Metadata => Metadata;
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

  /// <summary>An event store that records where each envelope was appended and stores nothing.</summary>
  private sealed class CaptureStore : IEventStore {
    public List<object> Envelopes { get; } = [];
    public List<Guid> StreamIds { get; } = [];

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
      StreamIds.Add(streamId);
      Envelopes.Add(envelope!);
      return Task.CompletedTask;
    }

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
        where TMessage : notnull => throw new NotSupportedException("the emitter always appends an envelope");

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
