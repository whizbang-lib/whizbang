// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.Security;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.SystemEvents.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Where each kind of system event is stored (#1555). An audit record is a ledger entry about a
/// domain event, so the emitter writes it on the tenant's audit ledger exactly as the outbox path
/// does; only the global kinds (security and advisory) stay on <c>$wb-system</c>. Whatever the
/// stream, the envelope is built one way.
/// </summary>
/// <docs>fundamentals/events/system-events#stream</docs>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventEmitter.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventStream.cs</code-under-test>
[Category("SystemEvents")]
public class SystemEventStreamRoutingTests {
  private static readonly DateTimeOffset _fixedTime = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task EmitEventAudited_AppendsToTheTenantsAuditLedgerAsync() {
    var (emitter, store) = _build(new SystemEventOptions().EnableEventAudit());

    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 3, _scopedEnvelope("tenant-ledger-a"));

    await Assert.That(store.StreamIds).Count().IsEqualTo(1);
    await Assert.That(store.StreamIds[0]).IsEqualTo(AuditLedger.StreamFor("tenant-ledger-a"))
      .Because("an audit record is a ledger entry; the emitter writes it where the outbox path writes it");
    await Assert.That(store.StreamIds[0]).IsNotEqualTo(SystemEventStreams.StreamId);
  }

  [Test]
  public async Task EmitCommandAudited_AppendsToTheTenantsAuditLedgerAsync() {
    var (emitter, store) = _build(new SystemEventOptions().EnableCommandAudit());
    var context = new RoutingMessageContext();
    context.Metadata["TenantId"] = "tenant-ledger-b";

    await emitter.EmitCommandAuditedAsync("Command", "Response", "Receptor", context);

    await Assert.That(store.StreamIds.Single()).IsEqualTo(AuditLedger.StreamFor("tenant-ledger-b"));
  }

  [Test]
  public async Task EmitCommandAudited_WithoutATenant_AppendsToTheNoTenantLedgerAsync() {
    var (emitter, store) = _build(new SystemEventOptions().EnableCommandAudit());

    await emitter.EmitCommandAuditedAsync("Command", "Response", "Receptor", context: null);

    await Assert.That(store.StreamIds.Single()).IsEqualTo(AuditLedger.StreamFor(null))
      .Because("a record with no tenant has one ledger of its own, never the global stream");
  }

  [Test]
  public async Task EmitAsync_AnAuditRecordGivenDirectly_StillGoesToTheLedgerAsync() {
    var (emitter, store) = _build(new SystemEventOptions().EnableAudit());

    await emitter.EmitAsync(_eventAudited("tenant-direct"));

    await Assert.That(store.StreamIds.Single()).IsEqualTo(AuditLedger.StreamFor("tenant-direct"))
      .Because("$wb-system keeps only the global kinds, whichever method carried the audit record");
  }

  [Test]
  public async Task EmitAsync_AnAdvisory_StaysOnTheGlobalSystemStreamAsync() {
    var (emitter, store) = _build(new SystemEventOptions().EnablePerspectiveEvents());

    await emitter.EmitAsync(_advisory());

    await Assert.That(store.StreamIds.Single()).IsEqualTo(SystemEventStreams.StreamId)
      .Because("an advisory is a fact about the database, not about a tenant");
  }

  [Test]
  public async Task TheLedgerEnvelope_IsBuiltTheSameWayAsTheGlobalOneAsync() {
    var (emitter, store) = _build(new SystemEventOptions().EnableEventAudit().EnablePerspectiveEvents());

    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, _scopedEnvelope("tenant-shape"));
    await emitter.EmitAsync(_advisory());

    var ledger = (MessageEnvelope<EventAudited>)store.Envelopes[0];
    var global = (MessageEnvelope<PerspectiveIndexAdvised>)store.Envelopes[1];
    await Assert.That(ledger.Priority).IsEqualTo(WorkPriority.IDLE)
      .Because("the audit band still applies on the ledger path");
    await Assert.That(ledger.Hops).Count().IsEqualTo(1);
    await Assert.That(ledger.Hops[0].Type).IsEqualTo(HopType.Current);
    await Assert.That(ledger.GetCurrentScope()?.Scope.TenantId).IsEqualTo("tenant-shape")
      .Because("the record's own hop carries its tenant, as on the global path");
    await Assert.That(ledger.DispatchContext.Mode).IsEqualTo(global.DispatchContext.Mode);
    await Assert.That(ledger.DispatchContext.Source).IsEqualTo(global.DispatchContext.Source);
  }

  [Test]
  public async Task StreamFor_NamesTheLedgerForAuditKindsAndTheGlobalStreamForTheRestAsync() {
    var commandAudited = new CommandAudited {
      Id = Guid.CreateVersion7(),
      CommandType = "C",
      CommandBody = JsonSerializer.SerializeToElement(1),
      Timestamp = _fixedTime,
      ReceptorName = "R",
      ResponseType = "S",
      TenantId = "tenant-cmd"
    };
    var denied = new AccessDenied {
      ResourceType = "Order",
      RequiredPermission = new Permission("orders:read"),
      CallerPermissions = new HashSet<Permission>(),
      CallerRoles = new HashSet<string>(),
      Scope = new PerspectiveScope { TenantId = "tenant-sec" },
      Reason = AccessDenialReason.InsufficientPermission,
      Timestamp = _fixedTime
    };

    await Assert.That(SystemEventStreams.StreamFor(_eventAudited("tenant-evt"))).IsEqualTo(AuditLedger.StreamFor("tenant-evt"));
    await Assert.That(SystemEventStreams.StreamFor(commandAudited)).IsEqualTo(AuditLedger.StreamFor("tenant-cmd"));
    await Assert.That(SystemEventStreams.StreamFor(denied)).IsEqualTo(SystemEventStreams.StreamId)
      .Because("a security fact stays on the global stream even when it carries a tenant");
    await Assert.That(SystemEventStreams.StreamFor(_advisory())).IsEqualTo(SystemEventStreams.StreamId);
  }

  // ===== Helpers =====

  private static (SystemEventEmitter Emitter, RoutingStore Store) _build(SystemEventOptions options) {
    var store = new RoutingStore();
    var emitter = new SystemEventEmitter(
      Options.Create(options), store,
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      logger: NullLogger<SystemEventEmitter>.Instance, auditDecisionHook: null);
    return (emitter, store);
  }

  private static EventAudited _eventAudited(string tenant) => new() {
    Id = Guid.CreateVersion7(),
    OriginalEventType = "E",
    OriginalStreamId = "s",
    OriginalStreamPosition = 1,
    OriginalBody = null,
    Timestamp = _fixedTime,
    TenantId = tenant
  };

  private static PerspectiveIndexAdvised _advisory() => new() {
    ModelName = "M",
    TableName = "wh_per_m",
    TableSizeBytes = 2,
    ThresholdBytes = 1,
    Exposure = "Filterable",
    UnindexedFields = ["Name"]
  };

  private static MessageEnvelope<string> _scopedEnvelope(string tenant) => new() {
    MessageId = MessageId.New(),
    Payload = "payload",
    Hops = [
      new MessageHop {
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Type = HopType.Current,
        Timestamp = _fixedTime,
        Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = tenant, UserId = "user-1" })
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private sealed class RoutingMessageContext : IMessageContext {
    public MessageId MessageId { get; init; } = MessageId.New();
    public CorrelationId CorrelationId { get; init; } = new(Guid.CreateVersion7());
    public MessageId CausationId { get; init; } = MessageId.New();
    public DateTimeOffset Timestamp { get; init; } = _fixedTime;
    public string? UserId => null;
    public Dictionary<string, object> Metadata { get; } = [];
    public string? TenantId => null;
    public IScopeContext? ScopeContext => null;
    public ICallerInfo? CallerInfo => null;
    IReadOnlyDictionary<string, object> IMessageContext.Metadata => Metadata;
  }

  private sealed class RoutingStore : IEventStore {
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
