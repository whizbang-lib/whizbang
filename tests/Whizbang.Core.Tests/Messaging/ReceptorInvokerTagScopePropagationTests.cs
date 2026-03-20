using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Integration tests verifying that scope context propagates correctly from MessageEnvelope
/// through ReceptorInvoker into tag processing hooks.
/// </summary>
/// <remarks>
/// <para>
/// After lifecycle invoker convergence, ALL lifecycle stages go through ReceptorInvoker,
/// which includes tag processing. Previously, stages handled by ILifecycleInvoker had
/// no tag processing — hooks never fired. These tests prove scope propagation is correct
/// through the full ReceptorInvoker → MessageTagProcessor → hook chain.
/// </para>
/// <para>
/// Key verification points:
/// - context.Scope (explicit parameter passed through tag processing)
/// - ScopeContextAccessor.CurrentContext (static AsyncLocal, flows through CreateAsyncScope)
/// </para>
/// </remarks>
[NotInParallel]
public class ReceptorInvokerTagScopePropagationTests {

  [Before(Test)]
  public void CleanupRegistry() {
    Whizbang.Core.Registry.AssemblyRegistry<IMessageTagRegistry>.ClearForTesting();
  }

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task InvokeAsync_WithScopeDelta_PropagatesScopeToTagHook_ViaContextScope_AtStageAsync(LifecycleStage stage) {
    // Arrange: Register a tag for our test message type, with a hook that captures context.Scope
    var (hook, invoker, envelope) = _setupTagScopeTest(stage, "user-123", "tenant-456");

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert: Hook was invoked and received scope via context.Scope
    await Assert.That(hook.WasInvoked).IsTrue();
    await Assert.That(hook.ContextScope).IsNotNull();
    await Assert.That(hook.ContextScope!.Scope.UserId).IsEqualTo("user-123");
    await Assert.That(hook.ContextScope!.Scope.TenantId).IsEqualTo("tenant-456");
  }

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task InvokeAsync_WithScopeDelta_PropagatesScopeToTagHook_ViaAsyncLocal_AtStageAsync(LifecycleStage stage) {
    // Arrange: Verify scope is also available via ScopeContextAccessor.CurrentContext (AsyncLocal)
    // This proves AsyncLocal flows through MessageTagProcessor.CreateAsyncScope()
    var (hook, invoker, envelope) = _setupTagScopeTest(stage, "user-789", "tenant-012");

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert: Hook captured scope from AsyncLocal (ScopeContextAccessor.CurrentContext)
    await Assert.That(hook.WasInvoked).IsTrue();
    await Assert.That(hook.AsyncLocalScope).IsNotNull();
    await Assert.That(hook.AsyncLocalScope!.Scope.UserId).IsEqualTo("user-789");
    await Assert.That(hook.AsyncLocalScope!.Scope.TenantId).IsEqualTo("tenant-012");
  }

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task InvokeAsync_WithSystemAllTenantsScope_PropagatesScopeToTagHook_AtStageAsync(LifecycleStage stage) {
    // Arrange: Simulate AsSystem().ForAllTenants() — UserId="SYSTEM", TenantId="*"
    // This is the exact scenario that caused errors in downstream hooks after convergence
    var (hook, invoker, envelope) = _setupTagScopeTest(stage, "SYSTEM", TenantConstants.AllTenants);

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert: Hook receives the System/AllTenants scope correctly
    await Assert.That(hook.WasInvoked).IsTrue();
    await Assert.That(hook.ContextScope).IsNotNull();
    await Assert.That(hook.ContextScope!.Scope.UserId).IsEqualTo("SYSTEM");
    await Assert.That(hook.ContextScope!.Scope.TenantId).IsEqualTo(TenantConstants.AllTenants);

    // Also verify via AsyncLocal
    await Assert.That(hook.AsyncLocalScope).IsNotNull();
    await Assert.That(hook.AsyncLocalScope!.Scope.UserId).IsEqualTo("SYSTEM");
    await Assert.That(hook.AsyncLocalScope!.Scope.TenantId).IsEqualTo(TenantConstants.AllTenants);
  }

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task InvokeAsync_WithScopeDelta_ContextScopeAndAsyncLocalScopeAreConsistent_AtStageAsync(LifecycleStage stage) {
    // Arrange: Verify both scope access mechanisms return the same values
    var (hook, invoker, envelope) = _setupTagScopeTest(stage, "consistent-user", "consistent-tenant");

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert: Both mechanisms should return the same scope
    await Assert.That(hook.WasInvoked).IsTrue();
    await Assert.That(hook.ContextScope).IsNotNull();
    await Assert.That(hook.AsyncLocalScope).IsNotNull();
    await Assert.That(hook.ContextScope!.Scope.UserId).IsEqualTo(hook.AsyncLocalScope!.Scope.UserId);
    await Assert.That(hook.ContextScope!.Scope.TenantId).IsEqualTo(hook.AsyncLocalScope!.Scope.TenantId);
  }

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task InvokeAsync_WithoutScopeDelta_TagHookReceivesNullScope_AtStageAsync(LifecycleStage stage) {
    // Arrange: No scope on envelope — hook should receive null scope
    var tagRegistry = new TestMessageTagRegistry();
    tagRegistry.AddRegistration(typeof(TestTaggedEvent), typeof(SignalTagAttribute), "test-tag");
    MessageTagRegistry.Register(tagRegistry, priority: 100);

    var hook = new ScopeCapturingHook();
    var tagOptions = new TagOptions();
    tagOptions.UseHook<SignalTagAttribute, ScopeCapturingHook>(fireAt: stage);

    var receptorRegistry = new TestReceptorRegistry();
    receptorRegistry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(TestTaggedEvent),
      ReceptorId: $"test_tag_no_scope_receptor_{stage}",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => ValueTask.FromResult<object?>(null)
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => {
      // Exempt TestTaggedEvent so security provider doesn't throw for no-scope envelopes.
      // This test focuses on tag hook scope propagation, not security enforcement.
      options.ExemptMessageTypes.Add(typeof(TestTaggedEvent));
    });
    services.AddSingleton<IReceptorRegistry>(receptorRegistry);
    services.AddSingleton(hook);
    services.AddSingleton<IMessageTagProcessor>(sp =>
        new MessageTagProcessor(tagOptions, sp.GetRequiredService<IServiceScopeFactory>()));
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(receptorRegistry, scope.ServiceProvider);
    var envelope = _createTypedEnvelopeWithoutScope();

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert: Hook invoked but with null scope
    await Assert.That(hook.WasInvoked).IsTrue();
    await Assert.That(hook.ContextScope).IsNull();
  }

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task InvokeAsync_WithScopeDelta_ScopeContextAccessorFromNewDIScope_SeesAsyncLocal_AtStageAsync(LifecycleStage stage) {
    // Arrange: This test specifically verifies that IScopeContextAccessor resolved from
    // MessageTagProcessor's NEW DI scope (CreateAsyncScope) can read the AsyncLocal
    // values set by ReceptorInvoker. This is the exact code path that downstream hooks use.
    IScopeContext? hookAccessorScope = null;
    var tagRegistry = new TestMessageTagRegistry();
    tagRegistry.AddRegistration(typeof(TestTaggedEvent), typeof(SignalTagAttribute), "test-tag");
    MessageTagRegistry.Register(tagRegistry, priority: 100);

    var tagOptions = new TagOptions();
    tagOptions.UseHook<SignalTagAttribute, AccessorInjectedHook>(fireAt: stage);

    var receptorRegistry = new TestReceptorRegistry();
    receptorRegistry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(TestTaggedEvent),
      ReceptorId: $"test_tag_di_scope_receptor_{stage}",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => ValueTask.FromResult<object?>(null)
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(receptorRegistry);
    // Register hook as transient so it gets resolved from the NEW scope (like production)
    services.AddTransient<AccessorInjectedHook>();
    // Capture the scope context from the hook when it's invoked
    AccessorInjectedHook.OnInvoked = scope => hookAccessorScope = scope;
    services.AddSingleton<IMessageTagProcessor>(sp =>
        new MessageTagProcessor(tagOptions, sp.GetRequiredService<IServiceScopeFactory>()));
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(receptorRegistry, scope.ServiceProvider);
    var envelope = _createTypedEnvelopeWithScope("di-user", "di-tenant");

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert: Hook resolved from NEW DI scope can still read AsyncLocal scope
    await Assert.That(hookAccessorScope).IsNotNull();
    await Assert.That(hookAccessorScope!.Scope.UserId).IsEqualTo("di-user");
    await Assert.That(hookAccessorScope!.Scope.TenantId).IsEqualTo("di-tenant");
  }

  #region Data Sources

  public static IEnumerable<LifecycleStage> AllLifecycleStages() {
    yield return LifecycleStage.ImmediateAsync;
    yield return LifecycleStage.LocalImmediateAsync;
    yield return LifecycleStage.LocalImmediateInline;
    yield return LifecycleStage.PreDistributeAsync;
    yield return LifecycleStage.PreDistributeInline;
    yield return LifecycleStage.DistributeAsync;
    yield return LifecycleStage.PostDistributeAsync;
    yield return LifecycleStage.PostDistributeInline;
    yield return LifecycleStage.PreOutboxAsync;
    yield return LifecycleStage.PreOutboxInline;
    yield return LifecycleStage.PostOutboxAsync;
    yield return LifecycleStage.PostOutboxInline;
    yield return LifecycleStage.PreInboxAsync;
    yield return LifecycleStage.PreInboxInline;
    yield return LifecycleStage.PostInboxAsync;
    yield return LifecycleStage.PostInboxInline;
    yield return LifecycleStage.PrePerspectiveAsync;
    yield return LifecycleStage.PrePerspectiveInline;
    yield return LifecycleStage.PostPerspectiveAsync;
    yield return LifecycleStage.PostPerspectiveInline;
  }

  #endregion

  #region Setup Helper

  private (ScopeCapturingHook hook, ReceptorInvoker invoker, MessageEnvelope<TestTaggedEvent> envelope)
      _setupTagScopeTest(LifecycleStage stage, string userId, string tenantId) {
    var tagRegistry = new TestMessageTagRegistry();
    tagRegistry.AddRegistration(typeof(TestTaggedEvent), typeof(SignalTagAttribute), "test-tag");
    MessageTagRegistry.Register(tagRegistry, priority: 100);

    var hook = new ScopeCapturingHook();
    var tagOptions = new TagOptions();
    tagOptions.UseHook<SignalTagAttribute, ScopeCapturingHook>(fireAt: stage);

    var receptorRegistry = new TestReceptorRegistry();
    receptorRegistry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(TestTaggedEvent),
      ReceptorId: $"test_tag_scope_receptor_{stage}",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => ValueTask.FromResult<object?>(null)
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(receptorRegistry);
    services.AddSingleton(hook);
    services.AddSingleton<IMessageTagProcessor>(sp =>
        new MessageTagProcessor(tagOptions, sp.GetRequiredService<IServiceScopeFactory>()));
    var serviceProvider = services.BuildServiceProvider();
    var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(receptorRegistry, scope.ServiceProvider);
    var envelope = _createTypedEnvelopeWithScope(userId, tenantId);

    return (hook, invoker, envelope);
  }

  #endregion

  #region Helper Methods

  private static MessageEnvelope<TestTaggedEvent> _createTypedEnvelopeWithScope(string userId, string tenantId) {
    return new MessageEnvelope<TestTaggedEvent> {
      MessageId = MessageId.New(),
      Payload = new TestTaggedEvent("test-event"),
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
      ]
    };
  }

  private static MessageEnvelope<TestTaggedEvent> _createTypedEnvelopeWithoutScope() {
    return new MessageEnvelope<TestTaggedEvent> {
      MessageId = MessageId.New(),
      Payload = new TestTaggedEvent("test-event"),
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
          Scope = null
        }
      ]
    };
  }

  #endregion

  #region Test Types

  /// <summary>
  /// A strongly-typed event with a tag, simulating events like JobTemplateCreatedEvent
  /// that are dispatched via AsSystem().ForAllTenants().
  /// </summary>
  private sealed record TestTaggedEvent(string Name) : IEvent;

  #endregion

  #region Test Doubles

  /// <summary>
  /// Hook that captures scope from both the TagContext.Scope parameter
  /// and the static ScopeContextAccessor.CurrentContext (AsyncLocal).
  /// </summary>
  private sealed class ScopeCapturingHook : IMessageTagHook<SignalTagAttribute> {
    public bool WasInvoked { get; private set; }
    public IScopeContext? ContextScope { get; private set; }
    public IScopeContext? AsyncLocalScope { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> context,
        CancellationToken ct) {
      WasInvoked = true;
      ContextScope = context.Scope;
      AsyncLocalScope = ScopeContextAccessor.CurrentContext;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  /// <summary>
  /// Hook that receives IScopeContextAccessor via DI injection (like production hooks).
  /// Verifies that a NEW DI scope's accessor instance still reads the same AsyncLocal.
  /// </summary>
  private sealed class AccessorInjectedHook(IScopeContextAccessor accessor) : IMessageTagHook<SignalTagAttribute> {
    private readonly IScopeContextAccessor _accessor = accessor;
    public static Action<IScopeContext?>? OnInvoked { get; set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> context,
        CancellationToken ct) {
      OnInvoked?.Invoke(_accessor.Current);
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class TestReceptorRegistry : IReceptorRegistry {
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

  private sealed class TestMessageTagRegistry : IMessageTagRegistry {
    private readonly List<MessageTagRegistration> _registrations = [];

    public void AddRegistration(Type messageType, Type attributeType, string tag) {
      _registrations.Add(new MessageTagRegistration {
        MessageType = messageType,
        AttributeType = attributeType,
        Tag = tag,
        PayloadBuilder = msg => JsonSerializer.SerializeToElement(msg),
        AttributeFactory = () => {
          if (attributeType == typeof(SignalTagAttribute)) {
            return new SignalTagAttribute { Tag = tag };
          }
          throw new NotSupportedException($"Unsupported attribute type: {attributeType.Name}");
        }
      });
    }

    public IEnumerable<MessageTagRegistration> GetTagsFor(Type messageType) {
      return _registrations.FindAll(r => r.MessageType == messageType);
    }
  }

  #endregion
}
