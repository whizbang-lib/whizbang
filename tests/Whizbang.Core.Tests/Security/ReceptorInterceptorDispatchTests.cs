// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// #1996: the registered receptor interceptors run immediately before every receptor the generated dispatcher invokes
/// (send, local invoke, publish, and the receptors of a cascaded message). A denied receptor does not run and appends
/// nothing; a host with no interceptor sees no change.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Security/ReceptorInterceptorGate.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/Templates/Snippets/DispatcherSnippets.cs</code-under-test>
[Category("Security")]
[Category("Dispatcher")]
public class ReceptorInterceptorDispatchTests {
  [Test]
  public async Task LocalInvoke_DeniedReceptor_DoesNotRunAndAppendsNothingAsync() {
    var (dispatcher, strategy, _) = _create(new TargetedInterceptor(typeof(GateCommandReceptor), DeniedAction.DeadLetter));
    var command = new GateCommand(TrackedGuid.New());

    var denied = await Assert.ThrowsAsync<ReceptorInvocationDeniedException>(
      async () => await dispatcher.LocalInvokeAsync<GateEmittedEvent>(command));

    await Assert.That(denied!.Action).IsEqualTo(DeniedAction.DeadLetter);
    await Assert.That(denied.ReceptorType).IsEqualTo(typeof(GateCommandReceptor));
    await Assert.That(GateCommandReceptor.Ran.ContainsKey(command.Id)).IsFalse()
      .Because("a denied receptor must not run");
    await Assert.That(strategy.Rows).IsEmpty()
      .Because("the receptor never returned its event, so nothing was appended to the outbox");
  }

  [Test]
  public async Task Send_DeniedReceptor_DoesNotRunAndAppendsNothingAsync() {
    var (dispatcher, strategy, _) = _create(new TargetedInterceptor(typeof(GateCommandReceptor), DeniedAction.Throw));
    var command = new GateCommand(TrackedGuid.New());

    var denied = await Assert.ThrowsAsync<ReceptorInvocationDeniedException>(async () => await dispatcher.SendAsync(command));

    await Assert.That(denied!.Action).IsEqualTo(DeniedAction.Throw);
    await Assert.That(GateCommandReceptor.Ran.ContainsKey(command.Id)).IsFalse();
    await Assert.That(strategy.Rows.Any(r => r.MessageType.Contains(nameof(GateEmittedEvent), StringComparison.Ordinal))).IsFalse();
  }

  [Test]
  public async Task LocalInvoke_DeniedVoidReceptor_DoesNotRunAsync() {
    var (dispatcher, _, _) = _create(new TargetedInterceptor(typeof(GateVoidCommandReceptor), DeniedAction.DeadLetter));
    var command = new GateVoidCommand(TrackedGuid.New());

    await Assert.ThrowsAsync<ReceptorInvocationDeniedException>(async () => await dispatcher.LocalInvokeAsync(command));

    await Assert.That(GateVoidCommandReceptor.Ran.ContainsKey(command.Id)).IsFalse();
  }

  [Test]
  public async Task LocalInvoke_DeniedSyncReceptor_DoesNotRunAsync() {
    var (dispatcher, _, _) = _create(new TargetedInterceptor(typeof(GateSyncCommandReceptor), DeniedAction.DeadLetter));
    var command = new GateSyncCommand(TrackedGuid.New());

    await Assert.ThrowsAsync<ReceptorInvocationDeniedException>(
      async () => await dispatcher.LocalInvokeAsync<GateSyncResult>(command));

    await Assert.That(GateSyncCommandReceptor.Ran.ContainsKey(command.Id)).IsFalse();
  }

  [Test]
  public async Task Publish_DeniedEventReceptor_DoesNotRunAsync() {
    var (dispatcher, _, _) = _create(new TargetedInterceptor(typeof(GatePublishedEventReceptor), DeniedAction.DropQuiet));
    var evt = new GatePublishedEvent(TrackedGuid.New());

    await dispatcher.PublishAsync(evt);

    await Assert.That(GatePublishedEventReceptor.Ran.ContainsKey(evt.Id)).IsFalse()
      .Because("the local receptor of a published event is gated like any other");
  }

  [Test]
  public async Task Cascade_DeniedReceptorOfTheCascadedEvent_DoesNotRunAsync() {
    var (dispatcher, _, _) = _create(new TargetedInterceptor(typeof(GateCascadedEventReceptor), DeniedAction.DropQuiet));
    var command = new GateCascadeCommand(TrackedGuid.New());

    var result = await dispatcher.LocalInvokeAsync<GateCascadedEvent>(command);

    await Assert.That(result).IsNotNull()
      .Because("the command's own receptor is allowed and answers its caller");
    await Assert.That(GateCascadedEventReceptor.Ran.ContainsKey(command.Id)).IsFalse()
      .Because("the receptor of an event a receptor returned is gated like a receptor of a dispatched one");
  }

  [Test]
  public async Task LocalInvoke_DropQuiet_SkipsTheReceptorWithoutThrowingAsync() {
    var (dispatcher, strategy, _) = _create(new TargetedInterceptor(typeof(GateCommandReceptor), DeniedAction.DropQuiet));
    var command = new GateCommand(TrackedGuid.New());

    var result = await dispatcher.LocalInvokeAsync<GateEmittedEvent>(command);

    await Assert.That(result).IsNull()
      .Because("a quietly dropped receptor answers nothing");
    await Assert.That(GateCommandReceptor.Ran.ContainsKey(command.Id)).IsFalse();
    await Assert.That(strategy.Rows).IsEmpty();
  }

  [Test]
  public async Task NoInterceptors_ReceptorRuns_AndTheDispatcherHoldsNoPipelineAsync() {
    var (dispatcher, _, _) = _create();
    var command = new GateCommand(TrackedGuid.New());

    var result = await dispatcher.LocalInvokeAsync<GateEmittedEvent>(command);

    await Assert.That(result.Id).IsEqualTo(command.Id);
    await Assert.That(GateCommandReceptor.Ran.ContainsKey(command.Id)).IsTrue();
    await Assert.That(((Core.Dispatcher)dispatcher).HasReceptorInterceptors).IsFalse()
      .Because("an empty pipeline is dropped when the dispatcher is built, so no message resolves or evaluates it");
  }

  [Test]
  public async Task OnlyAllowingInterceptors_ReceptorRunsAsUsualAsync() {
    var allowing = new TargetedInterceptor(typeof(GateVoidCommandReceptor), DeniedAction.DeadLetter);
    var (dispatcher, _, _) = _create(allowing);
    var command = new GateCommand(TrackedGuid.New());

    var result = await dispatcher.LocalInvokeAsync<GateEmittedEvent>(command);

    await Assert.That(result.Id).IsEqualTo(command.Id);
    await Assert.That(allowing.Seen.Any(s => s.ReceptorType == typeof(GateCommandReceptor))).IsTrue()
      .Because("the interceptor is asked about every receptor, and allows the ones it does not target");
    await Assert.That(((Core.Dispatcher)dispatcher).HasReceptorInterceptors).IsTrue();
  }

  [Test]
  public async Task LocalInvoke_InterceptorSeesTheScopeContextTheReceptorSeesAsync() {
    var interceptor = new TargetedInterceptor(typeof(GateVoidCommandReceptor), DeniedAction.DeadLetter);
    var (dispatcher, _, _) = _create(interceptor);
    ScopeContextAccessor.CurrentContext = _scopeWith("orders:read");
    var command = new GateCommand(TrackedGuid.New());

    await dispatcher.LocalInvokeAsync<GateEmittedEvent>(command);

    var receptorSaw = GateCommandReceptor.Ran[command.Id];
    var interceptorSaw = interceptor.Seen.Single(s => s.ReceptorType == typeof(GateCommandReceptor)).Context;
    await Assert.That(receptorSaw).IsNotNull();
    await Assert.That(ReferenceEquals(interceptorSaw, receptorSaw)).IsTrue()
      .Because("the interceptor decides on the same scope context the receptor reads through IScopeContextAccessor");
  }

  [Test]
  public async Task RequirePermission_MissingPermission_DeniesEndToEndAsync() {
    var (dispatcher, _, _) = _create(new DefaultRequirePermissionInterceptor());
    ScopeContextAccessor.CurrentContext = _scopeWith("orders:read");
    var command = new GatePermissionCommand(TrackedGuid.New());

    var denied = await Assert.ThrowsAsync<ReceptorInvocationDeniedException>(
      async () => await dispatcher.LocalInvokeAsync(command));

    await Assert.That(denied!.Reason).Contains("orders:approve");
    await Assert.That(GatePermissionCommandReceptor.Ran.ContainsKey(command.Id)).IsFalse();
  }

  [Test]
  public async Task RequirePermission_PermissionGranted_RunsEndToEndAsync() {
    var (dispatcher, _, _) = _create(new DefaultRequirePermissionInterceptor());
    ScopeContextAccessor.CurrentContext = _scopeWith("orders:approve");
    var command = new GatePermissionCommand(TrackedGuid.New());

    await dispatcher.LocalInvokeAsync(command);

    await Assert.That(GatePermissionCommandReceptor.Ran.ContainsKey(command.Id)).IsTrue();
  }

  /// <summary>A receptor registered at runtime, run by a local invoke, is gated by its instance's type.</summary>
  [Test]
  public async Task LocalInvoke_RuntimeReceptorDroppedQuietly_DoesNotRunAsync() {
    var runtime = new RuntimeGateReceptor<GateRuntimeCommand>();
    var (dispatcher, _, provider) = _create(new TargetedInterceptor(typeof(RuntimeGateReceptor<GateRuntimeCommand>), DeniedAction.DropQuiet));
    provider.GetRequiredService<IReceptorRegistry>().Register(runtime, LifecycleStage.LocalImmediateInline);

    await dispatcher.LocalInvokeAsync(new GateRuntimeCommand(TrackedGuid.New()));

    await Assert.That(runtime.Runs).IsEqualTo(0);
  }

  /// <summary>A receptor registered at runtime, run by a publish, is gated by its instance's type.</summary>
  [Test]
  public async Task Publish_RuntimeReceptorDroppedQuietly_DoesNotRunAsync() {
    var runtime = new RuntimeGateReceptor<GateRuntimeEvent>();
    var (dispatcher, _, provider) = _create(new TargetedInterceptor(typeof(RuntimeGateReceptor<GateRuntimeEvent>), DeniedAction.DropQuiet));
    provider.GetRequiredService<IReceptorRegistry>().Register(runtime, LifecycleStage.LocalImmediateInline);

    await dispatcher.PublishAsync(new GateRuntimeEvent(TrackedGuid.New()));

    await Assert.That(runtime.Runs).IsEqualTo(0);
  }

  internal static ImmutableScopeContext _scopeWith(params string[] permissions) =>
    new(new SecurityExtraction {
      Scope = new Whizbang.Core.Lenses.PerspectiveScope { TenantId = "tenant-gate", UserId = "user-gate" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(permissions.Select(p => new Permission(p))),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test",
    }, shouldPropagate: true);

  private static (IDispatcher Dispatcher, CapturingStrategy Strategy, IServiceProvider Provider) _create(params IReceptorInterceptor[] interceptors) {
    var strategy = new CapturingStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    foreach (var interceptor in interceptors) {
      services.AddSingleton(interceptor);
    }
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    return (provider.GetRequiredService<IDispatcher>(), strategy, provider);
  }

  /// <summary>Denies one receptor type with the given action, allows every other, and records what it was shown.</summary>
  internal sealed class TargetedInterceptor(Type denied, DeniedAction action) : IReceptorInterceptor {
    public ConcurrentQueue<(Type ReceptorType, IMessageEnvelope Envelope, IScopeContext? Context)> Seen { get; } = new();

    public ValueTask<InterceptorResult> CanInvokeAsync(Type receptorType, IMessageEnvelope envelope, IScopeContext? context,
        CancellationToken cancellationToken = default) {
      Seen.Enqueue((receptorType, envelope, context));
      return new ValueTask<InterceptorResult>(receptorType == denied
        ? InterceptorResult.Deny(action, "denied by the test")
        : InterceptorResult.Allowed);
    }
  }

  internal sealed class CapturingStrategy : IWorkCoordinatorStrategy {
    private readonly ConcurrentQueue<OutboxMessage> _rows = new();
    public IReadOnlyList<OutboxMessage> Rows => [.. _rows];
    public void QueueOutboxMessage(OutboxMessage message) => _rows.Enqueue(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }
}

// Message types and receptors for the interceptor wiring tests (outside the class for source generator discovery).
public record GateCommand([property: StreamId] Guid Id) : ICommand;
public record GateEmittedEvent([property: StreamId] Guid Id) : IEvent;
public record GateVoidCommand([property: StreamId] Guid Id) : ICommand;
public record GateSyncCommand([property: StreamId] Guid Id) : ICommand;
public record GateSyncResult(Guid Id);
public record GatePublishedEvent([property: StreamId] Guid Id) : IEvent;
public record GateCascadeCommand([property: StreamId] Guid Id) : ICommand;
[DefaultRouting(DispatchModes.Local)]
public record GateCascadedEvent([property: StreamId] Guid Id) : IEvent;
public record GatePermissionCommand([property: StreamId] Guid Id) : ICommand;

/// <summary>Returns an event, which the dispatcher appends to the outbox; records the scope context it ran under.</summary>
public class GateCommandReceptor(IScopeContextAccessor accessor) : IReceptor<GateCommand, GateEmittedEvent> {
  public static ConcurrentDictionary<Guid, IScopeContext?> Ran { get; } = new();

  public ValueTask<GateEmittedEvent> HandleAsync(GateCommand message, CancellationToken cancellationToken = default) {
    Ran[message.Id] = accessor.Current;
    return ValueTask.FromResult(new GateEmittedEvent(message.Id));
  }
}

public class GateVoidCommandReceptor : IReceptor<GateVoidCommand> {
  public static ConcurrentDictionary<Guid, bool> Ran { get; } = new();

  public ValueTask HandleAsync(GateVoidCommand message, CancellationToken cancellationToken = default) {
    Ran[message.Id] = true;
    return ValueTask.CompletedTask;
  }
}

public class GateSyncCommandReceptor : ISyncReceptor<GateSyncCommand, GateSyncResult> {
  public static ConcurrentDictionary<Guid, bool> Ran { get; } = new();

  public GateSyncResult Handle(GateSyncCommand message) {
    Ran[message.Id] = true;
    return new GateSyncResult(message.Id);
  }
}

public class GatePublishedEventReceptor : IReceptor<GatePublishedEvent> {
  public static ConcurrentDictionary<Guid, bool> Ran { get; } = new();

  public ValueTask HandleAsync(GatePublishedEvent message, CancellationToken cancellationToken = default) {
    Ran[message.Id] = true;
    return ValueTask.CompletedTask;
  }
}

public class GateCascadeCommandReceptor : IReceptor<GateCascadeCommand, GateCascadedEvent> {
  public ValueTask<GateCascadedEvent> HandleAsync(GateCascadeCommand message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(new GateCascadedEvent(message.Id));
}

public class GateCascadedEventReceptor : IReceptor<GateCascadedEvent> {
  public static ConcurrentDictionary<Guid, bool> Ran { get; } = new();

  public ValueTask HandleAsync(GateCascadedEvent message, CancellationToken cancellationToken = default) {
    Ran[message.Id] = true;
    return ValueTask.CompletedTask;
  }
}

public record GateRuntimeCommand([property: StreamId] Guid Id) : ICommand;
[DefaultRouting(DispatchModes.Local)]
public record GateRuntimeEvent([property: StreamId] Guid Id) : IEvent;

/// <summary>A receptor registered at runtime (open generic, so discovery leaves it alone); counts its runs.</summary>
public class RuntimeGateReceptor<TMessage> : IReceptor<TMessage> where TMessage : IMessage {
  private int _runs;
  public int Runs => Volatile.Read(ref _runs);

  public ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
    Interlocked.Increment(ref _runs);
    return ValueTask.CompletedTask;
  }
}

[RequirePermission("orders:approve")]
public class GatePermissionCommandReceptor : IReceptor<GatePermissionCommand> {
  public static ConcurrentDictionary<Guid, bool> Ran { get; } = new();

  public ValueTask HandleAsync(GatePermissionCommand message, CancellationToken cancellationToken = default) {
    Ran[message.Id] = true;
    return ValueTask.CompletedTask;
  }
}
