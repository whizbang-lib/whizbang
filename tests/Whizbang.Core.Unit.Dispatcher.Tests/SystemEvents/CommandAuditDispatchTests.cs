// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tests.Security;
using Whizbang.Core.Unit.Dispatcher.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// #2470: with command audit enabled, a command a receptor handles emits one <see cref="CommandAudited"/>, on every
/// path a receptor handles it: a local invoke, a send, and inbox delivery. Proven through the real generated
/// dispatcher and the real receptor invoker, never by calling the audit code directly, because the defect was a
/// registration nothing ran.
/// </summary>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Security/ReceptorInterceptorGate.cs</code-under-test>
[Category("SystemEvents")]
[Category("Dispatcher")]
public class CommandAuditDispatchTests {
  [Test]
  public async Task LocalInvoke_CommandAuditEnabled_EmitsOneCommandAuditedAsync() {
    var (dispatcher, _, sink) = _create(o => o.EnableCommandAudit());
    var command = new CmdAuditDispatchCommand(TrackedGuid.New());

    await dispatcher.LocalInvokeAsync<CmdAuditDispatchHandled>(command);

    var audited = sink.CommandAudits;
    await Assert.That(audited.Count).IsEqualTo(1)
      .Because("the command's one receptor handled it once, so it is audited once");
    await Assert.That(audited[0].CommandType).IsEqualTo(nameof(CmdAuditDispatchCommand));
    await Assert.That(audited[0].ReceptorName).IsEqualTo(nameof(CmdAuditDispatchCommandReceptor));
    await Assert.That(audited[0].CommandBody.GetRawText()).Contains(command.Id.ToString());
  }

  [Test]
  public async Task Send_CommandAuditEnabled_EmitsOneCommandAuditedAsync() {
    var (dispatcher, _, sink) = _create(o => o.EnableCommandAudit());
    var command = new CmdAuditDispatchCommand(TrackedGuid.New());

    await dispatcher.SendAsync(command);

    await Assert.That(sink.CommandAudits.Count).IsEqualTo(1);
    await Assert.That(sink.CommandAudits[0].CommandType).IsEqualTo(nameof(CmdAuditDispatchCommand));
  }

  [Test]
  public async Task InboxDelivery_CommandAuditEnabled_EmitsOneCommandAuditedAsync() {
    var (_, provider, sink) = _create(o => o.EnableCommandAudit());
    var command = new CmdAuditDispatchCommand(TrackedGuid.New());
    await using var scope = provider.CreateAsyncScope();
    var invoker = scope.ServiceProvider.GetRequiredService<IReceptorInvoker>();

    // The stage inbox delivery runs a receptor declared without [FireAt] at
    await invoker.InvokeAsync(_inboxEnvelope(command), LifecycleStage.PostInboxDetached);

    await Assert.That(sink.CommandAudits.Count).IsEqualTo(1);
    await Assert.That(sink.CommandAudits[0].ReceptorName).IsEqualTo(nameof(CmdAuditDispatchCommandReceptor));
  }

  [Test]
  public async Task LocalInvoke_SyncReceptor_EmitsOneCommandAuditedAsync() {
    var (dispatcher, _, sink) = _create(o => o.EnableCommandAudit());
    var command = new CmdAuditDispatchSyncCommand(TrackedGuid.New());

    await dispatcher.LocalInvokeAsync<CmdAuditDispatchSyncResult>(command);

    await Assert.That(sink.CommandAudits.Count).IsEqualTo(1);
    await Assert.That(sink.CommandAudits[0].ReceptorName).IsEqualTo(nameof(CmdAuditDispatchSyncCommandReceptor));
  }

  /// <summary>
  /// The guard for the defect class behind #2470: a service registered and never resolved. Every service
  /// <see cref="SystemEventServiceCollectionExtensions.AddSystemEventAuditing"/> adds must be resolved when commands
  /// and events are dispatched, unless it is listed below with the path that does resolve it. An open-generic
  /// registration cannot be shown to have a caller at all, so it fails outright.
  /// </summary>
  [Test]
  public async Task EveryServiceAddSystemEventAuditingRegisters_IsResolvedOnARealDispatchAsync() {
    var sink = new CapturingSink();
    IServiceCollection services = _baseServices(sink);
    var before = services.ToList();
    services.AddSystemEventAuditing(o => o.EnableAudit());
    var added = services.Where(d => !before.Contains(d)).ToList();

    var openGeneric = added.Where(d => d.ServiceType.IsGenericTypeDefinition).Select(d => d.ServiceType.Name).ToList();
    await Assert.That(openGeneric).IsEmpty()
      .Because("an open-generic registration is resolved only when some framework code asks for a closed form; none does");

    var resolved = new ConcurrentDictionary<Type, bool>();
    foreach (var descriptor in added) {
      services.Remove(descriptor);
      services.Add(_recordingResolution(descriptor, resolved));
    }
    await using var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    await dispatcher.LocalInvokeAsync<CmdAuditDispatchHandled>(new CmdAuditDispatchCommand(TrackedGuid.New()));
    await dispatcher.SendAsync(new CmdAuditDispatchCommand(TrackedGuid.New()));
    await dispatcher.PublishAsync(new CmdAuditDispatchEvent(TrackedGuid.New()));
    await using (var scope = provider.CreateAsyncScope()) {
      await scope.ServiceProvider.GetRequiredService<IReceptorInvoker>()
        .InvokeAsync(_inboxEnvelope(new CmdAuditDispatchCommand(TrackedGuid.New())), LifecycleStage.PostInboxDetached);
    }

    var neverResolved = added
      .Select(d => d.ServiceType)
      .Distinct()
      .Where(t => !resolved.ContainsKey(t) && !_resolvedOnOtherPaths.ContainsKey(t))
      .Select(t => t.Name)
      .ToList();
    await Assert.That(neverResolved).IsEmpty()
      .Because("a service the auditing registration adds and no dispatch path resolves is a feature that never runs");
  }

  /// <summary>
  /// Services <see cref="SystemEventServiceCollectionExtensions.AddSystemEventAuditing"/> adds that a dispatch with no
  /// transport and no event store does not reach, each with the path that resolves it.
  /// </summary>
  private static readonly Dictionary<Type, string> _resolvedOnOtherPaths = new() {
    [typeof(ITransportPublishFilter)] = "read by the transport publish path, which this host has no transport for",
    [typeof(IAuditDecisionHook)] = "read by the event audit decorator and the default emitter's per-record sink; this "
      + "host has no event store, so it has neither",
  };

  private static ServiceDescriptor _recordingResolution(ServiceDescriptor descriptor, ConcurrentDictionary<Type, bool> resolved) {
    var serviceType = descriptor.ServiceType;
    object create(IServiceProvider sp) {
      resolved[serviceType] = true;
      if (descriptor.ImplementationInstance is { } instance) {
        return instance;
      }
      return descriptor.ImplementationFactory is { } factory
        ? factory(sp)
        : ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
    }
    return new ServiceDescriptor(serviceType, create, descriptor.Lifetime);
  }

  internal static MessageEnvelope<TMessage> _inboxEnvelope<TMessage>(TMessage message) => new() {
    MessageId = MessageId.New(),
    Payload = message,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
  };

  internal static (IDispatcher Dispatcher, IServiceProvider Provider, CapturingSink Sink) _create(
      Action<SystemEventOptions> configure, params IReceptorInterceptor[] interceptors) {
    var sink = new CapturingSink();
    var services = _baseServices(sink);
    services.AddSystemEventAuditing(configure);
    foreach (var interceptor in interceptors) {
      services.AddSingleton(interceptor);
    }
    var provider = services.BuildServiceProvider();
    return (provider.GetRequiredService<IDispatcher>(), provider, sink);
  }

  private static ServiceCollection _baseServices(CapturingSink sink) {
    var services = new ServiceCollection();
    // The framework defaults and the instance identity come first, so what AddSystemEventAuditing adds is its own
    services.TryAddWhizbangDefaults();
    services.AddWhizbangInstanceIdentity();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => new ReceptorInterceptorDispatchTests.CapturingStrategy());
    // The real emitter, writing to a sink that keeps what it is given: registered first, so AddSystemEvents keeps it
    services.AddSingleton<ISystemEventEmitter>(sp => SystemEventEmitter.ForSink(
      sp.GetRequiredService<IOptions<SystemEventOptions>>(), sink,
      sp.GetRequiredService<IServiceInstanceProvider>(), NullLogger<SystemEventEmitter>.Instance));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services;
  }

  /// <summary>Keeps every system event appended through it.</summary>
  internal sealed class CapturingSink : ISystemEventSink, ISystemEventSinkSession {
    private readonly ConcurrentQueue<object> _appended = new();

    public IReadOnlyList<CommandAudited> CommandAudits => [.. _appended.OfType<CommandAudited>()];

    public IAuditDecisionHook? AuditDecisionHook => null;

    public ISystemEventSinkSession Open() => this;

    public Task AppendAsync<TSystemEvent>(Guid streamId, MessageEnvelope<TSystemEvent> envelope, CancellationToken cancellationToken) {
      _appended.Enqueue(envelope.Payload!);
      return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}

// Message types and receptors for the command audit dispatch tests (outside the class for source generator discovery).
public record CmdAuditDispatchCommand([property: StreamId] Guid Id) : ICommand;
public record CmdAuditDispatchHandled([property: StreamId] Guid Id) : IEvent;
public record CmdAuditDispatchSyncCommand([property: StreamId] Guid Id) : ICommand;
public record CmdAuditDispatchSyncResult(Guid Id);
[DefaultRouting(DispatchModes.Local)]
public record CmdAuditDispatchEvent([property: StreamId] Guid Id) : IEvent;

public class CmdAuditDispatchCommandReceptor : IReceptor<CmdAuditDispatchCommand, CmdAuditDispatchHandled> {
  public ValueTask<CmdAuditDispatchHandled> HandleAsync(CmdAuditDispatchCommand message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(new CmdAuditDispatchHandled(message.Id));
}

public class CmdAuditDispatchSyncCommandReceptor : ISyncReceptor<CmdAuditDispatchSyncCommand, CmdAuditDispatchSyncResult> {
  public CmdAuditDispatchSyncResult Handle(CmdAuditDispatchSyncCommand message) => new(message.Id);
}

public class CmdAuditDispatchEventReceptor : IReceptor<CmdAuditDispatchEvent> {
  public ValueTask HandleAsync(CmdAuditDispatchEvent message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
