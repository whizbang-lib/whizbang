// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.SystemEvents.Security;

namespace Whizbang.Core.Tests.SystemEvents.Security;

/// <summary>
/// Which security system events are stored (#1599). Denials and permission changes are rare and are the
/// records an auditor asks for, so they are stored by default; grants and scope establishments are
/// per-request and high volume, so they stay opt-in. Each kind has its own switch, honored by
/// <see cref="SystemEventOptions.IsEnabled{TSystemEvent}"/>, so the emitter stores exactly the enabled
/// kinds on <c>$wb-system</c>.
/// </summary>
/// <docs>fundamentals/events/system-events#security-events</docs>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventOptions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventEmitter.cs</code-under-test>
[Category("SystemEvents")]
public class SecuritySystemEventStorageTests {
  private static readonly DateTimeOffset _fixedTime = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task Defaults_StoreDenialsAndPermissionChanges_NotGrantsOrScopeEstablishmentsAsync() {
    var options = new SystemEventOptions();

    await Assert.That(options.AccessDeniedEnabled).IsTrue();
    await Assert.That(options.PermissionChangedEnabled).IsTrue();
    await Assert.That(options.AccessGrantedEnabled).IsFalse();
    await Assert.That(options.ScopeContextEstablishedEnabled).IsFalse();
    await Assert.That(options.IsEnabled<AccessDenied>()).IsTrue();
    await Assert.That(options.IsEnabled<PermissionChanged>()).IsTrue();
    await Assert.That(options.IsEnabled<AccessGranted>()).IsFalse()
      .Because("a grant is recorded on every allowed read; storing it is a deployment's choice");
    await Assert.That(options.IsEnabled<ScopeContextEstablished>()).IsFalse()
      .Because("a scope is established on every request; storing it is a deployment's choice");
  }

  [Test]
  public async Task AccessDeniedEnabled_Off_StopsStoringDenialsOnlyAsync() {
    var options = new SystemEventOptions { AccessDeniedEnabled = false };

    await Assert.That(options.IsEnabled<AccessDenied>()).IsFalse();
    await Assert.That(options.IsEnabled<PermissionChanged>()).IsTrue();
  }

  [Test]
  public async Task PermissionChangedEnabled_Off_StopsStoringPermissionChangesOnlyAsync() {
    var options = new SystemEventOptions { PermissionChangedEnabled = false };

    await Assert.That(options.IsEnabled<PermissionChanged>()).IsFalse();
    await Assert.That(options.IsEnabled<AccessDenied>()).IsTrue();
  }

  [Test]
  public async Task AccessGrantedEnabled_On_StoresGrantsOnlyAsync() {
    var options = new SystemEventOptions { AccessGrantedEnabled = true };

    await Assert.That(options.IsEnabled<AccessGranted>()).IsTrue();
    await Assert.That(options.IsEnabled<ScopeContextEstablished>()).IsFalse();
  }

  [Test]
  public async Task ScopeContextEstablishedEnabled_On_StoresScopeEstablishmentsOnlyAsync() {
    var options = new SystemEventOptions { ScopeContextEstablishedEnabled = true };

    await Assert.That(options.IsEnabled<ScopeContextEstablished>()).IsTrue();
    await Assert.That(options.IsEnabled<AccessGranted>()).IsFalse();
  }

  [Test]
  public async Task EmitAsync_WithDefaults_StoresDenialsAndPermissionChangesOnTheSystemStreamAsync() {
    var (emitter, store) = _build(new SystemEventOptions());

    await emitter.EmitAsync(_denied());
    await emitter.EmitAsync(_granted());
    await emitter.EmitAsync(_permissionChanged());
    await emitter.EmitAsync(_scopeEstablished());

    await Assert.That(store.Payloads).Count().IsEqualTo(2);
    await Assert.That(store.Payloads[0]).IsTypeOf<AccessDenied>();
    await Assert.That(store.Payloads[1]).IsTypeOf<PermissionChanged>();
    await Assert.That(store.StreamIds.All(id => id == SystemEventStreams.StreamId)).IsTrue()
      .Because("security facts are global and stay on $wb-system");
  }

  [Test]
  public async Task EmitAsync_WithEverySwitchFlipped_StoresGrantsAndScopeEstablishmentsOnlyAsync() {
    var (emitter, store) = _build(new SystemEventOptions {
      AccessDeniedEnabled = false,
      PermissionChangedEnabled = false,
      AccessGrantedEnabled = true,
      ScopeContextEstablishedEnabled = true,
    });

    await emitter.EmitAsync(_denied());
    await emitter.EmitAsync(_granted());
    await emitter.EmitAsync(_permissionChanged());
    await emitter.EmitAsync(_scopeEstablished());

    await Assert.That(store.Payloads).Count().IsEqualTo(2);
    await Assert.That(store.Payloads[0]).IsTypeOf<AccessGranted>();
    await Assert.That(store.Payloads[1]).IsTypeOf<ScopeContextEstablished>();
    await Assert.That(store.StreamIds.All(id => id == SystemEventStreams.StreamId)).IsTrue();
  }

  // ===== Helpers =====

  private static (SystemEventEmitter Emitter, RecordingStore Store) _build(SystemEventOptions options) {
    var store = new RecordingStore();
    var emitter = new SystemEventEmitter(
      Options.Create(options), store,
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      logger: NullLogger<SystemEventEmitter>.Instance, auditDecisionHook: null);
    return (emitter, store);
  }

  private static AccessDenied _denied() => new() {
    ResourceType = "Order",
    RequiredPermission = Permission.Read("orders"),
    CallerPermissions = new HashSet<Permission>(),
    CallerRoles = new HashSet<string>(),
    Scope = new PerspectiveScope { TenantId = "tenant-sec" },
    Reason = AccessDenialReason.InsufficientPermission,
    Timestamp = _fixedTime
  };

  private static AccessGranted _granted() => new() {
    ResourceType = "Order",
    UsedPermission = Permission.Read("orders"),
    AccessFilter = ScopeFilters.Tenant,
    Scope = new PerspectiveScope { TenantId = "tenant-sec" },
    Timestamp = _fixedTime
  };

  private static PermissionChanged _permissionChanged() => new() {
    UserId = "user-1",
    TenantId = "tenant-sec",
    ChangeType = PermissionChangeType.RolesAdded,
    ChangedBy = "admin-user",
    Timestamp = _fixedTime
  };

  private static ScopeContextEstablished _scopeEstablished() => new() {
    Scope = new PerspectiveScope { TenantId = "tenant-sec" },
    Roles = new HashSet<string>(),
    Permissions = new HashSet<Permission>(),
    Source = "test",
    Timestamp = _fixedTime
  };

  private sealed class RecordingStore : IEventStore {
    public List<object?> Payloads { get; } = [];
    public List<Guid> StreamIds { get; } = [];

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
      StreamIds.Add(streamId);
      Payloads.Add(envelope.Payload);
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
