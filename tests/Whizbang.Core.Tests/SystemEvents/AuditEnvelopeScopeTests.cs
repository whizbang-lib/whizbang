using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// What an audit record's own envelope must carry.
/// </summary>
/// <remarks>
/// <para>
/// The emitter extracts the audited event's tenant and user into the audit PAYLOAD, then builds the
/// audit envelope by hand with a bare hop — no scope, no causation, and an unknown service
/// instance. So the record knows who acted while its envelope says nothing, and the row cannot be
/// found by a tenant-scoped read, swept by a tenant export, or traced to what produced it.
/// </para>
/// <para>
/// The scope column is an ACCESS-CONTROL key, not a label: reads filter on
/// <c>scope-&gt;&gt;'t'</c> and <c>scope-&gt;&gt;'u'</c>. That decides the shape here.
/// </para>
/// <list type="bullet">
///   <item><c>t</c> — YES. The audit row belongs to the audited tenant, so tenant reads, exports
///     and deletions reach it. Audit records hold personal data; leaving them outside every tenant
///     partition is a retention problem, not a tidiness one.</item>
///   <item><c>u</c> — NO. Putting the acting user here would hand the SUBJECT of an audit a key to
///     their own audit trail, which is precisely backwards. Their identity stays in the payload,
///     where it is evidence rather than a permission.</item>
///   <item><c>sys</c> — YES. The record is framework-emitted, and that is a separate field from the
///     tenant, so "system-emitted AND belonging to tenant A" is stated directly.</item>
/// </list>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventEmitter.cs</code-under-test>
[Category("SystemEvents")]
public class AuditEnvelopeScopeTests {

  private sealed record _auditedEvent : IEvent { public string Name { get; init; } = ""; }

  private static (SystemEventEmitter Emitter, _captureStore Store) _build() {
    var store = new _captureStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    return (new SystemEventEmitter(options, store), store);
  }

  private static MessageEnvelope<_auditedEvent> _scopedSource(string tenantId, string userId) => new() {
    MessageId = MessageId.New(),
    Payload = new _auditedEvent { Name = "x" },
    Hops = [new MessageHop {
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      Scope = ScopeDelta.FromPerspectiveScope(new PerspectiveScope { TenantId = tenantId, UserId = userId }),
    }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  [Test]
  public async Task TheAuditEnvelopeCarriesTheAuditedTenantAsync() {
    var (emitter, store) = _build();

    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, _scopedSource("tenant-a", "user-1"));

    await Assert.That(store.Envelopes).IsNotEmpty()
      .Because("nothing was emitted, so the assertions below would pass vacuously");
    var scope = store.Envelopes[0].GetCurrentScope();
    await Assert.That(scope).IsNotNull()
      .Because("reads filter on scope->>'t'; an audit row with no scope cannot be returned by a "
             + "tenant-scoped query, swept by a tenant export, or removed by a tenant deletion");
    await Assert.That(scope!.Scope.TenantId).IsEqualTo("tenant-a");
  }

  [Test]
  public async Task TheAuditEnvelopeDoesNotCarryTheActingUserAsync() {
    var (emitter, store) = _build();

    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, _scopedSource("tenant-a", "user-1"));

    var scope = store.Envelopes[0].GetCurrentScope();
    await Assert.That(scope!.Scope.UserId).IsNull()
      .Because("scope->>'u' gates read access, so putting the acting user here hands the SUBJECT "
             + "of an audit record a key to their own audit trail — their identity belongs in the "
             + "payload as evidence, never in the envelope as a permission");
  }

  [Test]
  public async Task TheAuditEnvelopeIsMarkedSystemEmittedAsync() {
    var (emitter, store) = _build();

    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, _scopedSource("tenant-a", "user-1"));

    await Assert.That(store.Envelopes[0].GetCurrentScope()!.Scope.IsSystem).IsTrue()
      .Because("the record is framework-emitted, and saying so alongside the tenant is what keeps "
             + "an absent scope meaning 'lost' everywhere else");
  }

  [Test]
  public async Task TheAuditEnvelopePointsAtWhatItAuditsAsync() {
    var (emitter, store) = _build();
    var source = _scopedSource("tenant-a", "user-1");

    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, source);

    await Assert.That(store.Envelopes[0].Hops[0].CausationId).IsNotNull()
      .Because("an audit record whose whole purpose is to reference another event must record "
             + "which one; without causation the trail cannot be walked backwards");
  }

  [Test]
  public async Task AnUnscopedSourceProducesNoTenantButStillMarksSystemAsync() {
    var (emitter, store) = _build();
    var source = new MessageEnvelope<_auditedEvent> {
      MessageId = MessageId.New(),
      Payload = new _auditedEvent { Name = "x" },
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };

    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, source);

    var scope = store.Envelopes[0].GetCurrentScope();
    await Assert.That(scope!.Scope.TenantId).IsNull()
      .Because("auditing an unscoped event must not invent a tenant — the audit of a control-plane "
             + "event legitimately belongs to no tenant");
    await Assert.That(scope.Scope.IsSystem).IsTrue();
  }


  [Test]
  public async Task TheAuditEnvelopeNamesTheEmittingInstanceAsync() {
    // An audit trail that cannot say which instance wrote a record is missing the one field that
    // makes it forensically useful when instances disagree. ServiceInstanceInfo.Unknown is not a
    // safe default here: it is indistinguishable from an instance that genuinely could not be
    // identified, so every record looks equally untraceable.
    var store = new _captureStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, store, instanceProvider: new _fixedInstance());

    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, _scopedSource("tenant-a", "user-1"));

    var instance = store.Envelopes[0].Hops[0].ServiceInstance;
    await Assert.That(instance.ServiceName).IsEqualTo("audit-emitter-service")
      .Because("the record must name the instance that produced it, or a divergence between "
             + "instances cannot be attributed to either of them");
  }

  [Test]
  public async Task TheAuditEnvelopeStillEmitsWithoutAnInstanceProviderAsync() {
    // The provider is optional: a service with no telemetry identity wired must still produce
    // audit records. Losing the audit entirely would be a far worse failure than an unknown writer.
    var store = new _captureStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, store);

    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, _scopedSource("tenant-a", "user-1"));

    await Assert.That(store.Envelopes).IsNotEmpty()
      .Because("an unwired instance provider must not cost the audit record itself");
    await Assert.That(store.Envelopes[0].Hops[0].ServiceInstance).IsEqualTo(ServiceInstanceInfo.Unknown);
  }

  private sealed class _fixedInstance : IServiceInstanceProvider {
    public Guid InstanceId => Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public string ServiceName => "audit-emitter-service";
    public string HostName => "host-1";
    public int ProcessId => 42;

    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = Guid.Parse("00000000-0000-0000-0000-0000000000a1"),
      ServiceName = "audit-emitter-service",
      HostName = "host-1",
      ProcessId = 42,
    };
  }

  private sealed class _captureStore : IEventStore {
    public List<MessageEnvelope<EventAudited>> Envelopes { get; } = [];

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
      if (envelope is MessageEnvelope<EventAudited> audited) { Envelopes.Add(audited); }
      return Task.CompletedTask;
    }

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
      where TMessage : notnull => Task.CompletedTask;

    // Unused by these tests; the emitter only appends.
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<Whizbang.Core.Messaging.StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      throw new NotSupportedException();
  }
}
