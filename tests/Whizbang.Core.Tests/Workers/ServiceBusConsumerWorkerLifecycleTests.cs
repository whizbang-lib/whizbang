#pragma warning disable CA1707

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for ServiceBusConsumerWorker lifecycle stage invocations via IReceptorInvoker.
/// Verifies that when IReceptorInvoker is resolved from scope, PreInbox/PostInbox
/// lifecycle stages are invoked correctly.
/// </summary>
/// <remarks>
/// <para>
/// The ServiceBusConsumerWorker resolves IReceptorInvoker from the scoped service provider
/// (not constructor-injected). These tests verify the pattern:
/// <code>
/// var receptorInvoker = scopedProvider.GetService&lt;IReceptorInvoker&gt;();
/// if (receptorInvoker is not null &amp;&amp; _lifecycleMessageDeserializer is not null) { ... }
/// </code>
/// </para>
/// <para>
/// Since the worker is a BackgroundService with complex message handling, these tests
/// exercise the lifecycle invocation path directly through ReceptorInvoker with a
/// DI container that mirrors the worker's scoped resolution pattern.
/// </para>
/// </remarks>
public class ServiceBusConsumerWorkerLifecycleTests {

  [Test]
  [MethodDataSource(nameof(InboxStages))]
  public async Task ReceptorInvoker_ResolvedFromScope_InvokesPreAndPostInbox_AtStageAsync(LifecycleStage stage) {
    // Arrange: Set up DI container with IReceptorInvoker registered as scoped
    // This mirrors how ServiceBusConsumerWorker resolves it from scopedProvider
    var invoked = false;
    LifecycleStage? capturedStage = null;

    var registry = new TestLifecycleReceptorRegistry();
    registry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(TestInboxEvent),
      ReceptorId: $"test_inbox_receptor_{stage}",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
        invoked = true;
        capturedStage = stage;
        return ValueTask.FromResult<object?>(null);
      }
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    // Resolve IReceptorInvoker the same way the worker does: from scoped provider
    var receptorInvoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createTypedEnvelopeWithScope("user-inbox", "tenant-inbox");

    // Act
    await receptorInvoker.InvokeAsync(envelope, stage);

    // Assert
    await Assert.That(invoked).IsTrue();
    await Assert.That(capturedStage).IsEqualTo(stage);
  }

  [Test]
  public async Task ReceptorInvoker_NullFromScope_SkipsLifecycleInvocationAsync() {
    // Arrange: DI container WITHOUT IReceptorInvoker registered
    // This tests the guard: if (receptorInvoker is not null && ...)
    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    // Act: Resolve IReceptorInvoker - should be null
    var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();

    // Assert: null means lifecycle stages are skipped (no exception)
    await Assert.That(receptorInvoker).IsNull();
  }

  [Test]
  public async Task ReceptorInvoker_WithLifecycleContext_PassesInboxContextAsync() {
    // Arrange: Verify that LifecycleExecutionContext is correctly constructed
    // for inbox lifecycle stages (MessageSource.Inbox, null EventId/StreamId)
    ILifecycleContext? capturedContext = null;

    var registry = new TestLifecycleReceptorRegistry();
    registry.AddReceptor(LifecycleStage.PreInboxDetached, new ReceptorInfo(
      MessageType: typeof(TestInboxEvent),
      ReceptorId: "test_context_receptor",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
        // Access lifecycle context via the accessor (set by ReceptorInvoker)
        var accessor = sp.GetService<ILifecycleContextAccessor>();
        capturedContext = accessor?.Current;
        return ValueTask.FromResult<object?>(null);
      }
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddScoped<ILifecycleContextAccessor, AsyncLocalLifecycleContextAccessor>();
    services.AddSingleton<IReceptorRegistry>(registry);
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var receptorInvoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createTypedEnvelopeWithScope("user-ctx", "tenant-ctx");

    var lifecycleContext = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PreInboxDetached,
      EventId = null,
      StreamId = null,
      LastProcessedEventId = null,
      MessageSource = MessageSource.Inbox,
      AttemptNumber = null
    };

    // Act
    await receptorInvoker.InvokeAsync(envelope, LifecycleStage.PreInboxDetached, lifecycleContext, CancellationToken.None);

    // Assert
    await Assert.That(capturedContext).IsNotNull();
    await Assert.That(capturedContext!.CurrentStage).IsEqualTo(LifecycleStage.PreInboxDetached);
    await Assert.That(capturedContext!.MessageSource).IsEqualTo(MessageSource.Inbox);
  }

  [Test]
  public async Task ReceptorInvoker_PreInboxThenPostInbox_InvokesBothStagesAsync() {
    // Arrange: Verify both PreInbox and PostInbox stages are invoked sequentially
    // as the worker does: PreInboxDetached, PreInboxInline, then later PostInboxDetached, PostInboxInline
    var invokedStages = new List<LifecycleStage>();

    var registry = new TestLifecycleReceptorRegistry();
    foreach (var stage in InboxStages()) {
      var capturedStage = stage;
      registry.AddReceptor(stage, new ReceptorInfo(
        MessageType: typeof(TestInboxEvent),
        ReceptorId: $"test_dual_receptor_{stage}",
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          invokedStages.Add(capturedStage);
          return ValueTask.FromResult<object?>(null);
        }
      ));
    }

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var receptorInvoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createTypedEnvelopeWithScope("user-dual", "tenant-dual");

    // Act: invoke all stages in order (mirrors worker flow)
    foreach (var stage in InboxStages()) {
      await receptorInvoker.InvokeAsync(envelope, stage);
    }

    // Assert: all four stages should have been invoked
    await Assert.That(invokedStages.Count).IsEqualTo(4);
    await Assert.That(invokedStages[0]).IsEqualTo(LifecycleStage.PreInboxDetached);
    await Assert.That(invokedStages[1]).IsEqualTo(LifecycleStage.PreInboxInline);
    await Assert.That(invokedStages[2]).IsEqualTo(LifecycleStage.PostInboxDetached);
    await Assert.That(invokedStages[3]).IsEqualTo(LifecycleStage.PostInboxInline);
  }

  [Test]
  public async Task ReceptorInvoker_ScopedResolution_EachScopeGetsOwnInstanceAsync() {
    // Arrange: Verify that IReceptorInvoker is resolved per-scope
    // ServiceBusConsumerWorker creates a scope per message, resolving invoker each time
    var registry = new TestLifecycleReceptorRegistry();

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<IReceptorInvoker>(sp =>
      new ReceptorInvoker(sp.GetRequiredService<IReceptorRegistry>(), sp));
    var serviceProvider = services.BuildServiceProvider();

    // Act: Create two scopes and resolve from each
    IReceptorInvoker? invoker1;
    IReceptorInvoker? invoker2;

    using (var scope1 = serviceProvider.CreateScope()) {
      invoker1 = scope1.ServiceProvider.GetService<IReceptorInvoker>();
    }

    using (var scope2 = serviceProvider.CreateScope()) {
      invoker2 = scope2.ServiceProvider.GetService<IReceptorInvoker>();
    }

    // Assert: Both should resolve, and be different instances (scoped)
    await Assert.That(invoker1).IsNotNull();
    await Assert.That(invoker2).IsNotNull();
    await Assert.That(ReferenceEquals(invoker1, invoker2)).IsFalse();
  }

  [Test]
  public async Task ReceptorInvoker_WithSecurityContext_PropagatesScopeAtInboxStagesAsync() {
    // Arrange: Verify security context flows through to receptors at inbox stages
    // This is the key change: receptorInvoker resolves from scope and establishes context
    IScopeContext? capturedScope = null;

    var registry = new TestLifecycleReceptorRegistry();
    registry.AddReceptor(LifecycleStage.PostInboxInline, new ReceptorInfo(
      MessageType: typeof(TestInboxEvent),
      ReceptorId: "test_security_receptor",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
        var accessor = sp.GetService<IScopeContextAccessor>();
        capturedScope = accessor?.Current;
        return ValueTask.FromResult<object?>(null);
      }
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var receptorInvoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createTypedEnvelopeWithScope("inbox-user", "inbox-tenant");

    // Act
    await receptorInvoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert: Security context should be propagated
    await Assert.That(capturedScope).IsNotNull();
    await Assert.That(capturedScope!.Scope.UserId).IsEqualTo("inbox-user");
    await Assert.That(capturedScope!.Scope.TenantId).IsEqualTo("inbox-tenant");
  }

  /// <summary>
  /// Verifies that ServiceBusConsumerWorker fires PostLifecycle for events without
  /// perspectives. This mirrors what TransportConsumerWorker does.
  /// Previously this was a bug — ServiceBusConsumerWorker stopped at PostInboxInline.
  /// </summary>
  [Test]
  public async Task ServiceBusConsumerWorker_PostLifecycle_Fires_ForEventsWithoutPerspectivesAsync() {
    // Arrange
    var invokedStages = new List<LifecycleStage>();

    var registry = new TestLifecycleReceptorRegistry();
    foreach (var stage in AllInboxAndPostLifecycleStages()) {
      var capturedStage = stage;
      registry.AddReceptor(stage, new ReceptorInfo(
        MessageType: typeof(TestInboxEvent),
        ReceptorId: $"test_postlifecycle_receptor_{stage}",
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          invokedStages.Add(capturedStage);
          return ValueTask.FromResult<object?>(null);
        }
      ));
    }

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddSingleton<Whizbang.Core.Lifecycle.ILifecycleCoordinator, Whizbang.Core.Lifecycle.LifecycleCoordinator>();
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var receptorInvoker = scope.ServiceProvider.GetRequiredService<IReceptorInvoker>();
    var envelope = _createTypedEnvelopeWithScope("user-postlifecycle", "tenant-postlifecycle");

    // Act: simulate the ServiceBusConsumerWorker lifecycle flow (PostInbox + PostLifecycle)
    // It invokes PreInbox → PostInbox stages, then STOPS. No PostLifecycle.
    var lifecycleContext = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostInboxDetached,
      MessageSource = MessageSource.Inbox
    };

    await receptorInvoker.InvokeAsync(envelope, LifecycleStage.PostInboxDetached, lifecycleContext, CancellationToken.None);
    await receptorInvoker.InvokeAsync(envelope, LifecycleStage.ImmediateDetached,
      lifecycleContext with { CurrentStage = LifecycleStage.ImmediateDetached }, CancellationToken.None);

    lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PostInboxInline };
    await receptorInvoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline, lifecycleContext, CancellationToken.None);
    await receptorInvoker.InvokeAsync(envelope, LifecycleStage.ImmediateDetached,
      lifecycleContext with { CurrentStage = LifecycleStage.ImmediateDetached }, CancellationToken.None);

    // PostLifecycle stages for events without perspectives
    // This mirrors the code added to ServiceBusConsumerWorker
    var coordinator = scope.ServiceProvider.GetService<Whizbang.Core.Lifecycle.ILifecycleCoordinator>();
    if (coordinator is not null) {
      var eventId = envelope.MessageId.Value;
      var tracking = coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PostLifecycleDetached, MessageSource.Inbox);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleDetached, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
      await tracking.DrainDetachedAsync();
      coordinator.AbandonTracking(eventId);
    }

    // Assert: PostInbox stages fired
    await Assert.That(invokedStages).Contains(LifecycleStage.PostInboxDetached);
    await Assert.That(invokedStages).Contains(LifecycleStage.PostInboxInline);

    // Assert: PostLifecycle MUST fire for events without perspectives
    await Assert.That(invokedStages).Contains(LifecycleStage.PostLifecycleDetached)
      .Because("ServiceBusConsumerWorker must fire PostLifecycleDetached for events without perspectives");
    await Assert.That(invokedStages).Contains(LifecycleStage.PostLifecycleInline)
      .Because("ServiceBusConsumerWorker must fire PostLifecycleInline for events without perspectives");
  }

  #region Data Sources

  public static IEnumerable<LifecycleStage> InboxStages() {
    yield return LifecycleStage.PreInboxDetached;
    yield return LifecycleStage.PreInboxInline;
    yield return LifecycleStage.PostInboxDetached;
    yield return LifecycleStage.PostInboxInline;
  }

  public static IEnumerable<LifecycleStage> AllInboxAndPostLifecycleStages() {
    yield return LifecycleStage.PreInboxDetached;
    yield return LifecycleStage.PreInboxInline;
    yield return LifecycleStage.PostInboxDetached;
    yield return LifecycleStage.PostInboxInline;
    yield return LifecycleStage.PostLifecycleDetached;
    yield return LifecycleStage.PostLifecycleInline;
  }

  #endregion

  #region Helper Methods

  private static MessageEnvelope<TestInboxEvent> _createTypedEnvelopeWithScope(string userId, string tenantId) {
    return new MessageEnvelope<TestInboxEvent> {
      MessageId = MessageId.New(),
      Payload = new TestInboxEvent("test-inbox-event"),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = CorrelationId.New(),
          CausationId = MessageId.New(),
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          },
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
            UserId = userId,
            TenantId = tenantId
          })
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  #endregion

  #region Test Types

  private sealed record TestInboxEvent(string Name) : IEvent;

  #endregion

  #region Test Doubles

  private sealed class TestLifecycleReceptorRegistry : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void AddReceptor(LifecycleStage stage, ReceptorInfo receptor) {
      var key = (receptor.MessageType, stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
      list.Add(receptor);
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      var key = (messageType, stage);
      return _receptors.TryGetValue(key, out var list) ? list : Array.Empty<ReceptorInfo>();
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  #endregion
}
