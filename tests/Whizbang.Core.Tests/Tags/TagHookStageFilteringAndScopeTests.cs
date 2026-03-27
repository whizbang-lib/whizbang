using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Registry;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;

#pragma warning disable RCS1163 // Test fakes commonly have unused params to match interface signatures

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// RED/GREEN tests reproducing the JDNext tag notification failure.
///
/// JDNext registers tag hooks at PostAllPerspectivesAsync:
///   options.Tags.UseHook&lt;NotificationTagAttribute, JdxNotificationTagHook&gt;(
///     fireAt: LifecycleStage.PostAllPerspectivesAsync);
///
/// The hooks read scope from IScopeContextAccessor.ScopeContext (AsyncLocal), NOT from TagContext.Scope.
/// If the hook fires at the wrong stage or scope is null, notifications silently fail.
///
/// These tests verify:
/// 1. Stage filtering: hooks only fire at their registered fireAt stage
/// 2. Scope propagation: hooks resolved from a DI scope can read scope via IScopeContextAccessor
/// 3. ProcessTagsAsync respects fireAt via GetHooksFor(type, stage)
/// </summary>
[Category("Core")]
[Category("Tags")]
public class TagHookStageFilteringAndScopeTests {

  [Before(Test)]
  public void Setup() {
    // Clean up static registry between tests
    AssemblyRegistry<IMessageTagRegistry>.ClearForTesting();
  }

  [After(Test)]
  public void Cleanup() {
    AssemblyRegistry<IMessageTagRegistry>.ClearForTesting();
    // Reset AsyncLocal state
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;
  }

  // ─────────────────────────────────────────────────────────────────────
  // Test 1: ProcessTagsAsync stage filtering — hook registered at PostAllPerspectivesAsync
  //         should NOT fire when invoked at AfterReceptorCompletion
  // ─────────────────────────────────────────────────────────────────────

  [Test]
  [NotInParallel("TagRegistry")]
  public async Task ProcessTagsAsync_HookRegisteredAtPostAllPerspectives_DoesNotFireAtAfterReceptorCompletionAsync() {
    // Arrange — simulate JDNext pattern: hook registered at PostAllPerspectivesAsync
    var registry = new TestTagRegistry();
    registry.AddTag<TaggedTestEvent>(typeof(SignalTagAttribute), "notifications");
    MessageTagRegistry.Register(registry, priority: 50);

    var hook = new CapturingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, CapturingHook>(fireAt: LifecycleStage.PostAllPerspectivesAsync);

    var processor = new MessageTagProcessor(options, type => type == typeof(CapturingHook) ? hook : null);
    var message = new TaggedTestEvent("evt-1", Guid.NewGuid());

    // Act — invoke at AfterReceptorCompletion (NOT the registered stage)
    await processor.ProcessTagsAsync(message, typeof(TaggedTestEvent), LifecycleStage.AfterReceptorCompletion);

    // Assert — hook should NOT fire because stage doesn't match
    await Assert.That(hook.InvocationCount).IsEqualTo(0)
      .Because("Hook registered at PostAllPerspectivesAsync must NOT fire at AfterReceptorCompletion");
  }

  // ─────────────────────────────────────────────────────────────────────
  // Test 2: ProcessTagsAsync stage filtering — hook registered at PostAllPerspectivesAsync
  //         SHOULD fire when invoked at PostAllPerspectivesAsync
  // ─────────────────────────────────────────────────────────────────────

  [Test]
  [NotInParallel("TagRegistry")]
  public async Task ProcessTagsAsync_HookRegisteredAtPostAllPerspectives_FiresAtPostAllPerspectivesAsync() {
    // Arrange
    var registry = new TestTagRegistry();
    registry.AddTag<TaggedTestEvent>(typeof(SignalTagAttribute), "notifications");
    MessageTagRegistry.Register(registry, priority: 50);

    var hook = new CapturingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, CapturingHook>(fireAt: LifecycleStage.PostAllPerspectivesAsync);

    var processor = new MessageTagProcessor(options, type => type == typeof(CapturingHook) ? hook : null);
    var message = new TaggedTestEvent("evt-1", Guid.NewGuid());

    // Act — invoke at the registered stage
    await processor.ProcessTagsAsync(message, typeof(TaggedTestEvent), LifecycleStage.PostAllPerspectivesAsync);

    // Assert — hook SHOULD fire
    await Assert.That(hook.InvocationCount).IsEqualTo(1)
      .Because("Hook registered at PostAllPerspectivesAsync must fire when stage matches");
  }

  // ─────────────────────────────────────────────────────────────────────
  // Test 3: Two hooks at different stages — each fires ONLY at its stage
  // ─────────────────────────────────────────────────────────────────────

  [Test]
  [NotInParallel("TagRegistry")]
  public async Task ProcessTagsAsync_TwoHooksAtDifferentStages_EachFiresOnlyAtItsStageAsync() {
    // Arrange
    var registry = new TestTagRegistry();
    registry.AddTag<TaggedTestEvent>(typeof(SignalTagAttribute), "notifications");
    MessageTagRegistry.Register(registry, priority: 50);

    var hookA = new CapturingHook();
    var hookB = new CapturingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, CapturingHook>(fireAt: LifecycleStage.PostAllPerspectivesAsync);
    options.UseHook<SignalTagAttribute, CapturingHook>(fireAt: LifecycleStage.AfterReceptorCompletion);

    var hookIndex = 0;
    var hooks = new[] { hookA, hookB };
    var processor = new MessageTagProcessor(options, _ => hooks[hookIndex++]);
    var message = new TaggedTestEvent("evt-1", Guid.NewGuid());

    // Act — invoke at PostAllPerspectivesAsync
    await processor.ProcessTagsAsync(message, typeof(TaggedTestEvent), LifecycleStage.PostAllPerspectivesAsync);

    // Assert — only hookA (PostAllPerspectivesAsync) should fire
    await Assert.That(hookA.InvocationCount).IsEqualTo(1)
      .Because("Hook A is registered at PostAllPerspectivesAsync");
    await Assert.That(hookB.InvocationCount).IsEqualTo(0)
      .Because("Hook B is registered at AfterReceptorCompletion, should not fire at PostAllPerspectivesAsync");
  }

  // ─────────────────────────────────────────────────────────────────────
  // Test 4: Hook with null fireAt (default) fires at ALL stages
  // ─────────────────────────────────────────────────────────────────────

  [Test]
  [NotInParallel("TagRegistry")]
  public async Task ProcessTagsAsync_HookWithNullFireAt_FiresAtAllStagesAsync() {
    // Arrange
    var registry = new TestTagRegistry();
    registry.AddTag<TaggedTestEvent>(typeof(SignalTagAttribute), "notifications");
    MessageTagRegistry.Register(registry, priority: 50);

    var hook = new CapturingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, CapturingHook>(); // default fireAt = null → all stages

    var processor = new MessageTagProcessor(options, type => type == typeof(CapturingHook) ? hook : null);
    var message = new TaggedTestEvent("evt-1", Guid.NewGuid());

    // Act — invoke at PostAllPerspectivesAsync
    await processor.ProcessTagsAsync(message, typeof(TaggedTestEvent), LifecycleStage.PostAllPerspectivesAsync);

    // Assert — hook with null fireAt should fire at any stage
    await Assert.That(hook.InvocationCount).IsEqualTo(1)
      .Because("Hook with null fireAt (all stages) should fire at PostAllPerspectivesAsync");
  }

  // ─────────────────────────────────────────────────────────────────────
  // Test 5: Hook resolved from DI scope can read IScopeContextAccessor
  //         (reproduces the exact JDNext pattern)
  // ─────────────────────────────────────────────────────────────────────

  [Test]
  [NotInParallel("TagRegistry")]
  public async Task ProcessTagsAsync_HookResolvedFromScopeFactory_CanReadScopeViaAccessorAsync() {
    // Arrange — set up AsyncLocal scope (as ReceptorInvoker does before calling ProcessTagsAsync)
    var scope = _createTestScope("tenant-123", "user-456");
    ScopeContextAccessor.CurrentContext = scope;

    var registry = new TestTagRegistry();
    registry.AddTag<TaggedTestEvent>(typeof(SignalTagAttribute), "notifications");
    MessageTagRegistry.Register(registry, priority: 50);

    // Build DI container with hook + IScopeContextAccessor
    var services = new ServiceCollection();
    services.AddScoped<IScopeContextAccessor, ScopeContextAccessor>();
    services.AddScoped<ScopeCapturingHook>();
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, ScopeCapturingHook>(fireAt: LifecycleStage.PostAllPerspectivesAsync);

    // Use scope factory constructor (production path)
    var processor = new MessageTagProcessor(options, scopeFactory);
    var message = new TaggedTestEvent("evt-1", Guid.NewGuid());

    // Act — invoke at PostAllPerspectivesAsync (the registered stage)
    await processor.ProcessTagsAsync(message, typeof(TaggedTestEvent), LifecycleStage.PostAllPerspectivesAsync, scope);

    // Assert — the hook should have read scope from IScopeContextAccessor.ScopeContext
    var capturedScope = ScopeCapturingHook.LastCapturedScope;
    await Assert.That(capturedScope).IsNotNull()
      .Because("Hook must be able to read scope from IScopeContextAccessor.ScopeContext (AsyncLocal)");
    await Assert.That(capturedScope!.Scope?.TenantId).IsEqualTo("tenant-123")
      .Because("TenantId must propagate through AsyncLocal to hooks in new DI scope");
    await Assert.That(capturedScope.Scope?.UserId).IsEqualTo("user-456")
      .Because("UserId must propagate through AsyncLocal to hooks in new DI scope");
  }

  // ─────────────────────────────────────────────────────────────────────
  // Test 6: Hook resolved from DI scope reads NULL scope when AsyncLocal not set
  //         (proves the silent failure mode)
  // ─────────────────────────────────────────────────────────────────────

  [Test]
  [NotInParallel("TagRegistry")]
  public async Task ProcessTagsAsync_HookResolvedFromScopeFactory_ReturnsNullScopeWhenAsyncLocalNotSetAsync() {
    // Arrange — do NOT set AsyncLocal scope (simulates broken propagation)
    var registry = new TestTagRegistry();
    registry.AddTag<TaggedTestEvent>(typeof(SignalTagAttribute), "notifications");
    MessageTagRegistry.Register(registry, priority: 50);

    var services = new ServiceCollection();
    services.AddScoped<IScopeContextAccessor, ScopeContextAccessor>();
    services.AddScoped<ScopeCapturingHook>();
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, ScopeCapturingHook>(fireAt: LifecycleStage.PostAllPerspectivesAsync);

    var processor = new MessageTagProcessor(options, scopeFactory);
    var message = new TaggedTestEvent("evt-1", Guid.NewGuid());

    // Act — invoke without setting scope (the broken scenario)
    ScopeCapturingHook.Reset();
    await processor.ProcessTagsAsync(message, typeof(TaggedTestEvent), LifecycleStage.PostAllPerspectivesAsync);

    // Assert — scope should be null (proves the silent failure mode)
    await Assert.That(ScopeCapturingHook.LastCapturedScope).IsNull()
      .Because("Without AsyncLocal scope, IScopeContextAccessor.ScopeContext returns null — " +
               "this is the exact failure mode that causes JDNext notifications to silently fail");
  }

  // ─────────────────────────────────────────────────────────────────────
  // Test 7: Custom attribute type (like JDNext's NotificationTagAttribute) goes through
  //         dispatcher registry path. Hook must still fire with correct scope.
  // ─────────────────────────────────────────────────────────────────────

  [Test]
  [NotInParallel("TagRegistry")]
  public async Task ProcessTagsAsync_CustomAttribute_HookFiresViaDispatcherRegistryAsync() {
    // Arrange — simulate JDNext's custom NotificationTagAttribute
    var registry = new TestTagRegistry();
    registry.AddCustomTag<TaggedTestEvent>(typeof(TestNotificationTagAttribute), "job-created");
    MessageTagRegistry.Register(registry, priority: 50);

    // Register a dispatcher for the custom attribute type (simulates source-generated dispatcher)
    var dispatcher = new TestNotificationDispatcher();
    MessageTagHookDispatcherRegistry.Register(dispatcher, priority: 100);

    var hook = new CustomAttributeCapturingHook();
    var options = new TagOptions();
    options.UseHook<TestNotificationTagAttribute, CustomAttributeCapturingHook>(
      fireAt: LifecycleStage.PostAllPerspectivesAsync);

    var processor = new MessageTagProcessor(options,
      type => type == typeof(CustomAttributeCapturingHook) ? hook : null);
    var message = new TaggedTestEvent("evt-1", Guid.NewGuid());
    var scope = _createTestScope("tenant-abc", "user-xyz");

    // Act — invoke at the registered stage
    await processor.ProcessTagsAsync(message, typeof(TaggedTestEvent),
      LifecycleStage.PostAllPerspectivesAsync, scope);

    // Assert — hook must fire via the dispatcher registry path
    await Assert.That(hook.InvocationCount).IsEqualTo(1)
      .Because("Custom attribute hook must fire at PostAllPerspectivesAsync via dispatcher registry");
    await Assert.That(hook.LastScope).IsNotNull()
      .Because("Scope must be propagated to custom attribute hook context");
    await Assert.That(hook.LastScope!.Scope?.TenantId).IsEqualTo("tenant-abc");
  }

  [Test]
  [NotInParallel("TagRegistry")]
  public async Task ProcessTagsAsync_CustomAttribute_DoesNotFireAtWrongStageAsync() {
    // Arrange
    var registry = new TestTagRegistry();
    registry.AddCustomTag<TaggedTestEvent>(typeof(TestNotificationTagAttribute), "job-created");
    MessageTagRegistry.Register(registry, priority: 50);

    var dispatcher = new TestNotificationDispatcher();
    MessageTagHookDispatcherRegistry.Register(dispatcher, priority: 100);

    var hook = new CustomAttributeCapturingHook();
    var options = new TagOptions();
    options.UseHook<TestNotificationTagAttribute, CustomAttributeCapturingHook>(
      fireAt: LifecycleStage.PostAllPerspectivesAsync);

    var processor = new MessageTagProcessor(options,
      type => type == typeof(CustomAttributeCapturingHook) ? hook : null);
    var message = new TaggedTestEvent("evt-1", Guid.NewGuid());

    // Act — invoke at WRONG stage
    await processor.ProcessTagsAsync(message, typeof(TaggedTestEvent),
      LifecycleStage.AfterReceptorCompletion);

    // Assert — hook must NOT fire
    await Assert.That(hook.InvocationCount).IsEqualTo(0)
      .Because("Custom attribute hook at PostAllPerspectivesAsync must not fire at AfterReceptorCompletion");
  }

  // ═════════════════════════════════════════════════════════════════════
  // Test Infrastructure
  // ═════════════════════════════════════════════════════════════════════

  [After(Test)]
  public void CleanupDispatcherRegistry() {
    AssemblyRegistry<IMessageTagHookDispatcher>.ClearForTesting();
  }

  private sealed record TaggedTestEvent(string EventId, Guid StreamId);

  /// <summary>
  /// Simple capturing hook that tracks invocations via TagContext.
  /// </summary>
  private sealed class CapturingHook : IMessageTagHook<SignalTagAttribute> {
    public int InvocationCount { get; private set; }
    public IScopeContext? LastScope { get; private set; }
    public LifecycleStage? LastStage { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> context,
        CancellationToken _) {
      InvocationCount++;
      LastScope = context.Scope;
      LastStage = context.Stage;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  /// <summary>
  /// Hook that reads scope from IScopeContextAccessor (exactly like JDNext's JdxNotificationTagHook).
  /// Uses static capture because the hook instance is resolved from DI and we need to inspect it.
  /// </summary>
  private sealed class ScopeCapturingHook(IScopeContextAccessor scopeContextAccessor) : IMessageTagHook<SignalTagAttribute> {
    public static IScopeContext? LastCapturedScope { get; private set; }
    public static int InvocationCount { get; private set; }

    public static void Reset() {
      LastCapturedScope = null;
      InvocationCount = 0;
    }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> context,
        CancellationToken _) {
      InvocationCount++;
      // This is the exact pattern JDNext uses — reads from accessor, NOT from context.Scope
      LastCapturedScope = scopeContextAccessor.ScopeContext;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  /// <summary>
  /// Custom tag attribute simulating JDNext's NotificationTagAttribute.
  /// </summary>
  [AttributeUsage(AttributeTargets.Class)]
  private sealed class TestNotificationTagAttribute : MessageTagAttribute;

  /// <summary>
  /// Hook for custom attribute (like JDNext's JdxNotificationTagHook).
  /// </summary>
  private sealed class CustomAttributeCapturingHook : IMessageTagHook<TestNotificationTagAttribute> {
    public int InvocationCount { get; private set; }
    public IScopeContext? LastScope { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<TestNotificationTagAttribute> context,
        CancellationToken _) {
      InvocationCount++;
      LastScope = context.Scope;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  /// <summary>
  /// Test dispatcher for the custom attribute type (simulates source-generated dispatcher).
  /// </summary>
  private sealed class TestNotificationDispatcher : IMessageTagHookDispatcher {
    public object? TryCreateContext(
        Type attributeType, MessageTagAttribute attribute, object message,
        Type messageType, JsonElement payload, IScopeContext? scope, LifecycleStage stage) {
      if (attributeType == typeof(TestNotificationTagAttribute) && attribute is TestNotificationTagAttribute attr) {
        return new TagContext<TestNotificationTagAttribute> {
          Attribute = attr,
          Message = message,
          MessageType = messageType,
          Payload = payload,
          Scope = scope,
          Stage = stage
        };
      }
      return null;
    }

    public async ValueTask<JsonElement?> TryDispatchAsync(
        object hookInstance, object context, Type attributeType, CancellationToken ct) {
      if (attributeType == typeof(TestNotificationTagAttribute) &&
          hookInstance is IMessageTagHook<TestNotificationTagAttribute> hook &&
          context is TagContext<TestNotificationTagAttribute> typedContext) {
        return await hook.OnTaggedMessageAsync(typedContext, ct);
      }
      return null;
    }
  }

  /// <summary>
  /// Test registry that supports custom attribute types.
  /// </summary>
  private sealed class TestTagRegistry : IMessageTagRegistry {
    private readonly List<MessageTagRegistration> _registrations = [];

    public void AddTag<TMessage>(Type attributeType, string tag) {
      _registrations.Add(new MessageTagRegistration {
        MessageType = typeof(TMessage),
        AttributeType = attributeType,
        Tag = tag,
        PayloadBuilder = msg => JsonSerializer.SerializeToElement(msg),
        AttributeFactory = () => {
          if (attributeType == typeof(SignalTagAttribute)) {
            return new SignalTagAttribute { Tag = tag };
          }
          if (attributeType == typeof(TelemetryTagAttribute)) {
            return new TelemetryTagAttribute { Tag = tag };
          }
          if (attributeType == typeof(MetricTagAttribute)) {
            return new MetricTagAttribute { Tag = tag, MetricName = tag };
          }
          throw new NotSupportedException($"Unsupported: {attributeType.Name}");
        }
      });
    }

    public void AddCustomTag<TMessage>(Type attributeType, string tag) {
      _registrations.Add(new MessageTagRegistration {
        MessageType = typeof(TMessage),
        AttributeType = attributeType,
        Tag = tag,
        PayloadBuilder = msg => JsonSerializer.SerializeToElement(msg),
        AttributeFactory = () => {
          if (attributeType == typeof(TestNotificationTagAttribute)) {
            return new TestNotificationTagAttribute { Tag = tag };
          }
          throw new NotSupportedException($"Unsupported custom attribute: {attributeType.Name}");
        }
      });
    }

    public IEnumerable<MessageTagRegistration> GetTagsFor(Type messageType) =>
      _registrations.Where(r => r.MessageType == messageType);
  }

  private static ImmutableScopeContext _createTestScope(string tenantId, string userId) {
    return new ImmutableScopeContext(
      new SecurityExtraction {
        Scope = new PerspectiveScope { TenantId = tenantId, UserId = userId },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>(),
        Source = "Test"
      },
      shouldPropagate: true);
  }
}
