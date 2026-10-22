// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.SystemEvents.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Which <see cref="ISystemEventEmitter"/> a composed service gets (#1621). Wherever an event store is
/// registered, the framework registers the real <see cref="SystemEventEmitter"/> with TryAdd, so the
/// <see cref="SystemEventOptions"/> defaults decide what is stored and a host's own registration still
/// wins. Without an event store, the <see cref="NullSystemEventEmitter"/> stays.
/// </summary>
/// <remarks>
/// The default is a singleton that appends each record through an event store resolved in a scope of
/// its own, so it never captures a scoped store, works the same for a singleton store, and does not
/// depend on the scope of the caller that asked for the record.
/// </remarks>
/// <docs>fundamentals/events/system-events#emitter</docs>
/// <code-under-test>src/Whizbang.Core/SystemEvents/DefaultSystemEventEmitter.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/ServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventServiceCollectionExtensions.cs</code-under-test>
[Category("SystemEvents")]
[Category("DependencyInjection")]
public class SystemEventEmitterRegistrationTests {
  private static readonly DateTimeOffset _fixedTime = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task AddWhizbang_WithASingletonEventStore_StoresTheKindsTheOptionsEnableAsync() {
    var log = new AppendLog();
    var services = new ServiceCollection();
    services.AddWhizbang();
    services.AddSingleton<IEventStore>(new RecordingStore(log));

    var emitter = services.BuildServiceProvider().GetRequiredService<ISystemEventEmitter>();
    await emitter.EmitAsync(_permissionChanged());
    await emitter.EmitAsync(_scopeEstablished());

    await Assert.That(emitter).IsTypeOf<SystemEventEmitter>()
      .Because("a service with an event store must store the records its options enable without registering the emitter itself");
    await Assert.That(log.Payloads).Count().IsEqualTo(1);
    await Assert.That(log.Payloads[0]).IsTypeOf<PermissionChanged>();
    await Assert.That(log.StreamIds[0]).IsEqualTo(SystemEventStreams.StreamId);
  }

  [Test]
  public async Task AddWhizbang_WithAScopedEventStoreRegisteredAfterIt_NeverCapturesTheStoreAsync() {
    var log = new AppendLog();
    var created = new List<RecordingStore>();
    var services = new ServiceCollection();
    services.AddWhizbang();
    services.AddScoped<IEventStore>(_ => {
      var store = new RecordingStore(log);
      created.Add(store);
      return store;
    });
    var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    var emitter = provider.GetRequiredService<ISystemEventEmitter>();
    await emitter.EmitAsync(_permissionChanged());
    await emitter.EmitAsync(_permissionChanged());

    await Assert.That(log.Payloads).Count().IsEqualTo(2);
    await Assert.That(created).Count().IsEqualTo(2)
      .Because("each record is appended through a store resolved in a scope of its own, never one held by a singleton");
    await Assert.That(created.All(store => store.Disposed)).IsTrue()
      .Because("the scope that supplied the store is disposed once the record is appended");
  }

  [Test]
  public async Task AddWhizbang_TheDefaultEmitter_OutlivesTheScopeItWasResolvedInAsync() {
    var log = new AppendLog();
    var services = new ServiceCollection();
    services.AddWhizbang();
    services.AddScoped<IEventStore>(_ => new RecordingStore(log));
    var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    ISystemEventEmitter emitter;
    await using (var scope = provider.CreateAsyncScope()) {
      emitter = scope.ServiceProvider.GetRequiredService<ISystemEventEmitter>();
    }
    await emitter.EmitAsync(_permissionChanged());

    await Assert.That(log.Payloads).Count().IsEqualTo(1)
      .Because("a fire-and-forget record can finish after the request that raised it has ended");
  }

  [Test]
  public async Task AddWhizbang_WithNoEventStore_KeepsTheNullEmitterAsync() {
    var services = new ServiceCollection();
    services.AddWhizbang();

    var emitter = services.BuildServiceProvider().GetRequiredService<ISystemEventEmitter>();

    await Assert.That(emitter).IsTypeOf<NullSystemEventEmitter>();
  }

  [Test]
  public async Task AddWhizbang_AHostEmitterRegisteredBefore_WinsAndIsTheOnlyRegistrationAsync() {
    var hostEmitter = new NullSystemEventEmitter();
    var services = new ServiceCollection();
    services.AddSingleton<ISystemEventEmitter>(hostEmitter);
    services.AddWhizbang();
    services.AddSingleton<IEventStore>(new RecordingStore(new AppendLog()));

    var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<ISystemEventEmitter>()).IsSameReferenceAs(hostEmitter);
    await Assert.That(services.Count(d => d.ServiceType == typeof(ISystemEventEmitter))).IsEqualTo(1);
  }

  [Test]
  public async Task AddWhizbang_AHostEmitterRegisteredAfter_WinsAsync() {
    var hostEmitter = new NullSystemEventEmitter();
    var services = new ServiceCollection();
    services.AddWhizbang();
    services.AddSingleton<IEventStore>(new RecordingStore(new AppendLog()));
    services.AddSingleton<ISystemEventEmitter>(hostEmitter);

    var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<ISystemEventEmitter>()).IsSameReferenceAs(hostEmitter);
  }

  [Test]
  public async Task AddWhizbang_AHostThatRegistersSystemEventEmitterItself_GetsNoSecondEmitterAsync() {
    var log = new AppendLog();
    var hostEmitter = new SystemEventEmitter(
      Options.Create(new SystemEventOptions()), new RecordingStore(log),
      new FixedInstance(), NullLogger<SystemEventEmitter>.Instance, auditDecisionHook: null);
    var services = new ServiceCollection();
    services.AddSingleton<ISystemEventEmitter>(hostEmitter);
    services.AddWhizbang();
    services.AddSystemEvents();

    var provider = services.BuildServiceProvider();

    await Assert.That(services.Count(d => d.ServiceType == typeof(ISystemEventEmitter))).IsEqualTo(1);
    await Assert.That(provider.GetServices<ISystemEventEmitter>().Single()).IsSameReferenceAs(hostEmitter);
  }

  [Test]
  public async Task AddSystemEvents_AloneWithAnEventStore_RegistersTheSameDefaultAsync() {
    var log = new AppendLog();
    var services = new ServiceCollection();
    services.AddSingleton<IEventStore>(new RecordingStore(log));
    services.AddSystemEvents();

    var emitter = services.BuildServiceProvider().GetRequiredService<ISystemEventEmitter>();
    await emitter.EmitAsync(_permissionChanged());

    await Assert.That(emitter).IsTypeOf<SystemEventEmitter>();
    await Assert.That(log.Payloads).Count().IsEqualTo(1);
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AddWhizbangAndAddSystemEvents_InEitherOrder_RegisterOneEmitterAsync(bool addWhizbangFirst) {
    var services = new ServiceCollection();
    if (addWhizbangFirst) {
      services.AddWhizbang();
      services.AddSystemEvents();
    } else {
      services.AddSystemEvents();
      services.AddWhizbang();
    }

    await Assert.That(services.Count(d => d.ServiceType == typeof(ISystemEventEmitter))).IsEqualTo(1);
  }

  [Test]
  public async Task AddWhizbang_ARecordStoredThroughAnAuditedStore_IsNotItselfAuditedAsync() {
    var log = new AppendLog();
    var channel = new RecordingChannel();
    var services = new ServiceCollection();
    services.AddWhizbang();
    services.AddSingleton<IEventStore>(new RecordingStore(log));
    services.AddSingleton<IDeferredOutboxChannel>(channel);
    services.AddSystemEvents(options => options.EnableEventAudit());

    var emitter = services.BuildServiceProvider().GetRequiredService<ISystemEventEmitter>();
    await emitter.EmitAsync(_permissionChanged());

    await Assert.That(log.Payloads).Count().IsEqualTo(1);
    await Assert.That(channel.Queued).IsEmpty()
      .Because("a system event is excluded from audit, so storing one never raises another record");
  }

  [Test]
  public async Task AddWhizbang_AHookThatAuditsASystemEvent_RaisesOneAuditRecordAndNoMoreAsync() {
    var log = new AppendLog();
    var channel = new RecordingChannel();
    var services = new ServiceCollection();
    services.AddWhizbang();
    services.AddSingleton<IEventStore>(new RecordingStore(log));
    services.AddSingleton<IDeferredOutboxChannel>(channel);
    services.AddSingleton<IAuditDecisionHook>(new AuditEverything());
    services.AddSystemEvents(options => options.EnableEventAudit());

    var emitter = services.BuildServiceProvider().GetRequiredService<ISystemEventEmitter>();
    await emitter.EmitAsync(_permissionChanged());

    await Assert.That(log.Payloads).Count().IsEqualTo(1);
    await Assert.That(channel.Queued).Count().IsEqualTo(1)
      .Because("the audit record is queued to the outbox, and an audit record is never audited, so the chain stops at one");
  }

  [Test]
  public async Task AddWhizbang_AScopedHostHook_IsHonoredWithoutBeingCapturedAsync() {
    var log = new AppendLog();
    var hooksCreated = 0;
    var services = new ServiceCollection();
    services.AddWhizbang();
    services.AddSystemEvents(options => options.EnableEventAudit());
    services.AddSingleton<IEventStore>(new RecordingStore(log));
    services.AddScoped<IAuditDecisionHook>(_ => {
      hooksCreated++;
      return new RefuseEverything();
    });
    var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    var emitter = provider.GetRequiredService<ISystemEventEmitter>();
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, _envelope(new PlainEvent { Name = "refused" }));
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 2, _envelope(new PlainEvent { Name = "refused" }));

    await Assert.That(log.Payloads).IsEmpty()
      .Because("the host's hook refuses every occurrence, and a hook the emitter never consulted would let both through");
    await Assert.That(hooksCreated).IsEqualTo(2)
      .Because("a scoped hook is resolved per record, in that record's own scope, never held by the singleton");
  }

  [Test]
  public async Task AddWhizbang_AScopedHostHookThatNamesTheOccurrence_NamesTheStoredRecordAsync() {
    var log = new AppendLog();
    var services = new ServiceCollection();
    services.AddWhizbang();
    services.AddSystemEvents(options => options.EnableEventAudit());
    services.AddSingleton<IEventStore>(new RecordingStore(log));
    services.AddScoped<IAuditDecisionHook>(_ => new NameEverything());
    var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    var emitter = provider.GetRequiredService<ISystemEventEmitter>();
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, _envelope(new PlainEvent { Name = "named" }));

    await Assert.That(log.Payloads).Count().IsEqualTo(1);
    var audited = (EventAudited)log.Payloads[0]!;
    await Assert.That(audited.ActivityName).IsEqualTo(NameEverything.ACTIVITY_NAME);
  }

  [Test]
  public async Task Create_WhenTheProviderCannotSayWhetherAStoreIsRegistered_ReturnsTheNullEmitterAsync() {
    var emitter = DefaultSystemEventEmitter.Create(new EmptyProvider());

    await Assert.That(emitter).IsTypeOf<NullSystemEventEmitter>();
  }

  // ===== Helpers =====

  private static PermissionChanged _permissionChanged() => new() {
    UserId = "user-1",
    TenantId = "tenant-reg",
    ChangeType = PermissionChangeType.RolesAdded,
    ChangedBy = "admin-user",
    Timestamp = _fixedTime
  };

  private static ScopeContextEstablished _scopeEstablished() => new() {
    Scope = new Whizbang.Core.Lenses.PerspectiveScope { TenantId = "tenant-reg" },
    Roles = new HashSet<string>(),
    Permissions = new HashSet<Whizbang.Core.Security.Permission>(),
    Source = "test",
    Timestamp = _fixedTime
  };

  private static MessageEnvelope<T> _envelope<T>(T payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [
      new MessageHop {
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Type = HopType.Current,
        Timestamp = _fixedTime
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private sealed record PlainEvent : IEvent {
    public required string Name { get; init; }
  }

  private sealed class RefuseEverything : IAuditDecisionHook {
    public AuditDecision Decide(object payload, Type eventType) => AuditDecision.Skip;
  }

  private sealed class NameEverything : IAuditDecisionHook {
    public const string ACTIVITY_NAME = "Named by the scoped hook";

    public AuditDecision Decide(object payload, Type eventType) => AuditDecision.Record(ACTIVITY_NAME);
  }

  private sealed class AppendLog {
    public List<object?> Payloads { get; } = [];
    public List<Guid> StreamIds { get; } = [];
  }

  private sealed class EmptyProvider : IServiceProvider {
    public object? GetService(Type serviceType) => null;
  }

  private sealed class AuditEverything : IAuditDecisionHook {
    public AuditDecision Decide(object payload, Type eventType) => AuditDecision.Record();
  }

  private sealed class FixedInstance : IServiceInstanceProvider {
    public Guid InstanceId => Guid.Parse("00000000-0000-0000-0000-000000001621");
    public string ServiceName => "registration-service";
    public string HostName => "host-1";
    public int ProcessId => 1;

    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class RecordingChannel : IDeferredOutboxChannel {
    public List<OutboxMessage> Queued { get; } = [];

    public ValueTask QueueAsync(OutboxMessage message, CancellationToken ct = default) {
      Queued.Add(message);
      return ValueTask.CompletedTask;
    }

    public IReadOnlyList<OutboxMessage> DrainAll() => [];

    public bool HasPending => Queued.Count > 0;
  }

  private sealed class RecordingStore(AppendLog log) : IEventStore, IDisposable {
    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
      log.StreamIds.Add(streamId);
      log.Payloads.Add(envelope.Payload);
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
