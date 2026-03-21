using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="MessageTagProcessor"/>.
/// Validates hook orchestration, priority ordering, and payload modification.
/// </summary>
/// <tests>Whizbang.Core/Tags/MessageTagProcessor.cs</tests>
[Category("Core")]
[Category("Tags")]
public class MessageTagProcessorTests {

  [Test]
  public async Task ProcessAsync_WithNoHooks_CompletesSuccessfullyAsync() {
    // Arrange
    var options = new TagOptions();
    var processor = new MessageTagProcessor(options);
    var context = _createProcessContext<SignalTagAttribute>(
      new SignalTagAttribute { Tag = "test" },
      new { OrderId = "123" }
    );

    // Act & Assert - no exception means success
    await processor.ProcessAsync(context, CancellationToken.None);
  }

  [Test]
  public async Task ProcessAsync_InvokesMatchingHookAsync() {
    // Arrange
    var hook = new TrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, TrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(TrackingHook) ? hook : null);
    var context = _createProcessContext<SignalTagAttribute>(
      new SignalTagAttribute { Tag = "order-created" },
      new { OrderId = "123" }
    );

    // Act
    await processor.ProcessAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(hook.InvokedCount).IsEqualTo(1);
    await Assert.That(hook.LastContext?.Attribute.Tag).IsEqualTo("order-created");
  }

  [Test]
  public async Task ProcessAsync_DoesNotInvokeNonMatchingHookAsync() {
    // Arrange
    var telemetryHook = new TelemetryTrackingHook();
    var options = new TagOptions();
    options.UseHook<TelemetryTagAttribute, TelemetryTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(TelemetryTrackingHook) ? telemetryHook : null);
    var context = _createProcessContext<SignalTagAttribute>(
      new SignalTagAttribute { Tag = "test" },
      new { }
    );

    // Act
    await processor.ProcessAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(telemetryHook.InvokedCount).IsEqualTo(0);
  }

  [Test]
  public async Task ProcessAsync_InvokesUniversalHookForAnyTagAsync() {
    // Arrange
    var universalHook = new UniversalTrackingHook();
    var options = new TagOptions();
    options.UseUniversalHook<UniversalTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(UniversalTrackingHook) ? universalHook : null);
    var context = _createProcessContext<SignalTagAttribute>(
      new SignalTagAttribute { Tag = "test" },
      new { }
    );

    // Act
    await processor.ProcessAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(universalHook.InvokedCount).IsEqualTo(1);
  }

  [Test]
  public async Task ProcessAsync_InvokesHooksInPriorityOrderAsync() {
    // Arrange
    var executionOrder = new List<string>();
    var hook1 = new OrderTrackingHook("Hook1", executionOrder);
    var hook2 = new OrderTrackingHook("Hook2", executionOrder);
    var hook3 = new OrderTrackingHook("Hook3", executionOrder);

    var options = new TagOptions();
    options
      .UseHook<SignalTagAttribute, OrderTrackingHook>(priority: 500)   // Last
      .UseHook<SignalTagAttribute, OrderTrackingHook>(priority: -100)  // First
      .UseHook<SignalTagAttribute, OrderTrackingHook>(priority: 50);   // Middle

    // Hooks execute in priority order: -100, 50, 500
    // So resolver returns in that order: Hook2 (-100), Hook3 (50), Hook1 (500)
    var hookIndex = 0;
    var hooks = new[] { hook2, hook3, hook1 };
    var processor = new MessageTagProcessor(options, _ => hooks[hookIndex++]);

    var context = _createProcessContext<SignalTagAttribute>(
      new SignalTagAttribute { Tag = "test" },
      new { }
    );

    // Act
    await processor.ProcessAsync(context, CancellationToken.None);

    // Assert - hooks should execute in priority order: -100, 50, 500
    await Assert.That(executionOrder.Count).IsEqualTo(3);
    await Assert.That(executionOrder[0]).IsEqualTo("Hook2"); // priority -100
    await Assert.That(executionOrder[1]).IsEqualTo("Hook3"); // priority 50
    await Assert.That(executionOrder[2]).IsEqualTo("Hook1"); // priority 500
  }

  [Test]
  public async Task ProcessAsync_PassesModifiedPayloadToNextHookAsync() {
    // Arrange
    var payloadModifyingHook = new PayloadModifyingHook();
    var receivingHook = new PayloadReceivingHook();

    var options = new TagOptions();
    options
      .UseHook<SignalTagAttribute, PayloadModifyingHook>(priority: -100)  // First
      .UseHook<SignalTagAttribute, PayloadReceivingHook>(priority: 100);   // Second

    var hookIndex = 0;
    object?[] hooks = [payloadModifyingHook, receivingHook];
    var processor = new MessageTagProcessor(options, _ => hooks[hookIndex++]);

    var context = _createProcessContext<SignalTagAttribute>(
      new SignalTagAttribute { Tag = "test" },
      new { Original = true }
    );

    // Act
    await processor.ProcessAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(receivingHook.ReceivedPayload).IsNotNull();
    await Assert.That(receivingHook.ReceivedPayload!.Value.TryGetProperty("Modified", out var modified)).IsTrue();
    await Assert.That(modified.GetBoolean()).IsTrue();
  }

  [Test]
  public async Task ProcessAsync_PassesCancellationTokenToHooksAsync() {
    // Arrange
    var hook = new CancellationTrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, CancellationTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(CancellationTrackingHook) ? hook : null);
    var cts = new CancellationTokenSource();
    var context = _createProcessContext<SignalTagAttribute>(
      new SignalTagAttribute { Tag = "test" },
      new { }
    );

    // Act
    await processor.ProcessAsync(context, cts.Token);

    // Assert
    await Assert.That(hook.ReceivedToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task ProcessAsync_PassesScopeToHooksAsync() {
    // Arrange
    var hook = new ScopeTrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, ScopeTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(ScopeTrackingHook) ? hook : null);
    var scopeContext = new ImmutableScopeContext(
      new SecurityExtraction {
        Scope = new Whizbang.Core.Lenses.PerspectiveScope { TenantId = "tenant-123" },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>(),
        Source = "Test"
      },
      shouldPropagate: true);
    var context = _createProcessContext<SignalTagAttribute>(
      new SignalTagAttribute { Tag = "test" },
      new { },
      scopeContext
    );

    // Act
    await processor.ProcessAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(hook.ReceivedScope is not null).IsTrue();
    await Assert.That(hook.ReceivedScope!.Scope?.TenantId).IsEqualTo("tenant-123");
  }

  [Test]
  public async Task ProcessAsync_WhenHookReturnsNull_PreservesOriginalPayloadAsync() {
    // Arrange
    var passThroughHook = new PassThroughHook();
    var receivingHook = new PayloadReceivingHook();

    var options = new TagOptions();
    options
      .UseHook<SignalTagAttribute, PassThroughHook>(priority: -100)
      .UseHook<SignalTagAttribute, PayloadReceivingHook>(priority: 100);

    var hookIndex = 0;
    object?[] hooks = [passThroughHook, receivingHook];
    var processor = new MessageTagProcessor(options, _ => hooks[hookIndex++]);

    var originalPayload = new { Original = true };
    var context = _createProcessContext<SignalTagAttribute>(
      new SignalTagAttribute { Tag = "test" },
      originalPayload
    );

    // Act
    await processor.ProcessAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(receivingHook.ReceivedPayload).IsNotNull();
    await Assert.That(receivingHook.ReceivedPayload!.Value.TryGetProperty("Original", out var original)).IsTrue();
    await Assert.That(original.GetBoolean()).IsTrue();
  }

  // Helper to create process context
  private static TagContext<TAttribute> _createProcessContext<TAttribute>(
      TAttribute attribute,
      object payloadData,
      IScopeContext? scope = null,
      LifecycleStage stage = LifecycleStage.AfterReceptorCompletion)
      where TAttribute : MessageTagAttribute {
    return new TagContext<TAttribute> {
      Attribute = attribute,
      Message = payloadData,
      MessageType = payloadData.GetType(),
      Payload = JsonSerializer.SerializeToElement(payloadData),
      Scope = scope,
      Stage = stage
    };
  }

  // Test hook implementations
  private sealed class TrackingHook : IMessageTagHook<SignalTagAttribute> {
    public int InvokedCount { get; private set; }
    public TagContext<SignalTagAttribute>? LastContext { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> context,
        CancellationToken _) {
      InvokedCount++;
      LastContext = context;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class TelemetryTrackingHook : IMessageTagHook<TelemetryTagAttribute> {
    public int InvokedCount { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<TelemetryTagAttribute> _,
        CancellationToken __) {
      InvokedCount++;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class UniversalTrackingHook : IMessageTagHook<MessageTagAttribute> {
    public int InvokedCount { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<MessageTagAttribute> _,
        CancellationToken __) {
      InvokedCount++;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class OrderTrackingHook(string name, List<string> executionOrder) : IMessageTagHook<SignalTagAttribute> {
    private readonly string _name = name;
    private readonly List<string> _executionOrder = executionOrder;

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> _,
        CancellationToken __) {
      _executionOrder.Add(_name);
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class PayloadModifyingHook : IMessageTagHook<SignalTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> _,
        CancellationToken __) {
      var modified = new { Modified = true };
      return ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(modified));
    }
  }

  private sealed class PayloadReceivingHook : IMessageTagHook<SignalTagAttribute> {
    public JsonElement? ReceivedPayload { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> context,
        CancellationToken _) {
      ReceivedPayload = context.Payload;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class CancellationTrackingHook : IMessageTagHook<SignalTagAttribute> {
    public CancellationToken ReceivedToken { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> _,
        CancellationToken ct) {
      ReceivedToken = ct;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class ScopeTrackingHook : IMessageTagHook<SignalTagAttribute> {
    public IScopeContext? ReceivedScope { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> context,
        CancellationToken _) {
      ReceivedScope = context.Scope;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class PassThroughHook : IMessageTagHook<SignalTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> _,
        CancellationToken __) {
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  // Additional hook types for coverage
  private sealed class MetricTrackingHook : IMessageTagHook<MetricTagAttribute> {
    public int InvokedCount { get; private set; }
    public TagContext<MetricTagAttribute>? LastContext { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<MetricTagAttribute> context,
        CancellationToken _) {
      InvokedCount++;
      LastContext = context;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  #region ProcessTagsAsync Tests

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_WithNoHookResolver_ReturnsEarlyAsync() {
    // Arrange
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "test-tag");
    MessageTagRegistry.Register(registry, priority: 100);

    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, TrackingHook>();
    var processor = new MessageTagProcessor(options, hookResolver: null);
    var message = new TaggedTestMessage("123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.AfterReceptorCompletion);

    // Assert - should return early without error (no hook resolver)
    // No exception means success - verified by reaching this point
    await Assert.That(MessageTagRegistry.Count).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_WithNoTags_DoesNothingAsync() {
    // Arrange
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry(); // No registrations
    MessageTagRegistry.Register(registry, priority: 100);

    var hook = new TrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, TrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(TrackingHook) ? hook : null);
    var message = new TaggedTestMessage("123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.AfterReceptorCompletion);

    // Assert - hook should not be invoked
    await Assert.That(hook.InvokedCount).IsEqualTo(0);
  }

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_WithMatchingTag_InvokesHookAsync() {
    // Arrange
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "order-created");
    MessageTagRegistry.Register(registry, priority: 100);

    var hook = new TrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, TrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(TrackingHook) ? hook : null);
    var message = new TaggedTestMessage("123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.AfterReceptorCompletion);

    // Assert
    await Assert.That(hook.InvokedCount).IsEqualTo(1);
    await Assert.That(hook.LastContext?.Attribute.Tag).IsEqualTo("order-created");
  }

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_WithMultipleTags_ProcessesAllAsync() {
    // Arrange
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "order-created");
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(MetricTagAttribute), "order-metric", metricName: "orders.created");
    MessageTagRegistry.Register(registry, priority: 100);

    var notificationHook = new TrackingHook();
    var metricHook = new MetricTrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, TrackingHook>();
    options.UseHook<MetricTagAttribute, MetricTrackingHook>();

    var processor = new MessageTagProcessor(options, type => {
      if (type == typeof(TrackingHook)) {
        return notificationHook;
      }
      if (type == typeof(MetricTrackingHook)) {
        return metricHook;
      }
      return null;
    });

    var message = new TaggedTestMessage("123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.AfterReceptorCompletion);

    // Assert - both hooks should be invoked
    await Assert.That(notificationHook.InvokedCount).IsEqualTo(1);
    await Assert.That(metricHook.InvokedCount).IsEqualTo(1);
  }

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_BuildsPayloadFromMessageAsync() {
    // Arrange
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "test-tag");
    MessageTagRegistry.Register(registry, priority: 100);

    var hook = new PayloadReceivingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, PayloadReceivingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(PayloadReceivingHook) ? hook : null);
    var message = new TaggedTestMessage("order-123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.AfterReceptorCompletion);

    // Assert - payload should contain message data
    await Assert.That(hook.ReceivedPayload).IsNotNull();
    await Assert.That(hook.ReceivedPayload!.Value.TryGetProperty("OrderId", out var orderId)).IsTrue();
    await Assert.That(orderId.GetString()).IsEqualTo("order-123");
  }

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_PassesScopeToContextAsync() {
    // Arrange
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "test-tag");
    MessageTagRegistry.Register(registry, priority: 100);

    var hook = new ScopeTrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, ScopeTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(ScopeTrackingHook) ? hook : null);
    var message = new TaggedTestMessage("123");
    var scopeContext = new ImmutableScopeContext(
      new SecurityExtraction {
        Scope = new Whizbang.Core.Lenses.PerspectiveScope { TenantId = "tenant-456" },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>(),
        Source = "Test"
      },
      shouldPropagate: true);

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.AfterReceptorCompletion, scopeContext);

    // Assert
    await Assert.That(hook.ReceivedScope is not null).IsTrue();
    await Assert.That(hook.ReceivedScope!.Scope?.TenantId).IsEqualTo("tenant-456");
  }

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_InvokesHooksInPriorityOrderAsync() {
    // Arrange
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "test-tag");
    MessageTagRegistry.Register(registry, priority: 100);

    var executionOrder = new List<string>();
    var hook1 = new OrderTrackingHook("Hook1", executionOrder);
    var hook2 = new OrderTrackingHook("Hook2", executionOrder);
    var hook3 = new OrderTrackingHook("Hook3", executionOrder);

    var options = new TagOptions();
    options
      .UseHook<SignalTagAttribute, OrderTrackingHook>(priority: 500)
      .UseHook<SignalTagAttribute, OrderTrackingHook>(priority: -100)
      .UseHook<SignalTagAttribute, OrderTrackingHook>(priority: 50);

    var hookIndex = 0;
    var hooks = new[] { hook2, hook3, hook1 }; // Order by priority: -100, 50, 500
    var processor = new MessageTagProcessor(options, _ => hooks[hookIndex++]);

    var message = new TaggedTestMessage("123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.AfterReceptorCompletion);

    // Assert - hooks should execute in priority order
    await Assert.That(executionOrder.Count).IsEqualTo(3);
    await Assert.That(executionOrder[0]).IsEqualTo("Hook2"); // priority -100
    await Assert.That(executionOrder[1]).IsEqualTo("Hook3"); // priority 50
    await Assert.That(executionOrder[2]).IsEqualTo("Hook1"); // priority 500
  }

  // Helper to cleanup registry between tests
  private static void _cleanupRegistry() {
    Whizbang.Core.Registry.AssemblyRegistry<IMessageTagRegistry>.ClearForTesting();
  }

  // Test message type for ProcessTagsAsync tests
  private sealed record TaggedTestMessage(string OrderId);

  // Test registry implementation
  private sealed class TestMessageTagRegistry : IMessageTagRegistry {
    private readonly List<MessageTagRegistration> _registrations = [];

    public void AddRegistration(Type messageType, Type attributeType, string tag, string? metricName = null) {
      _registrations.Add(new MessageTagRegistration {
        MessageType = messageType,
        AttributeType = attributeType,
        Tag = tag,
        PayloadBuilder = msg => {
          // Extract all public properties
          var props = msg.GetType().GetProperties()
            .Where(p => p.CanRead)
            .ToDictionary(p => p.Name, p => p.GetValue(msg));
          return JsonSerializer.SerializeToElement(props);
        },
        AttributeFactory = () => {
          if (attributeType == typeof(SignalTagAttribute)) {
            return new SignalTagAttribute { Tag = tag };
          }
          if (attributeType == typeof(MetricTagAttribute)) {
            return new MetricTagAttribute { Tag = tag, MetricName = metricName ?? tag };
          }
          if (attributeType == typeof(TelemetryTagAttribute)) {
            return new TelemetryTagAttribute { Tag = tag };
          }
          throw new NotSupportedException($"Unsupported attribute type: {attributeType.Name}");
        }
      });
    }

    public IEnumerable<MessageTagRegistration> GetTagsFor(Type messageType) {
      return _registrations.Where(r => r.MessageType == messageType);
    }
  }

  #endregion

  #region Additional Coverage Tests

  [Test]
  public async Task ProcessAsync_InvokesMetricTagHookAsync() {
    // Arrange - covers MetricTagAttribute branch in _invokeHookAsync
    var hook = new MetricTrackingHook();
    var options = new TagOptions();
    options.UseHook<MetricTagAttribute, MetricTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(MetricTrackingHook) ? hook : null);
    var context = _createProcessContext(
      new MetricTagAttribute { Tag = "order-metric", MetricName = "order.total" },
      new { Amount = 99.99 }
    );

    // Act
    await processor.ProcessAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(hook.InvokedCount).IsEqualTo(1);
    await Assert.That(hook.LastContext?.Attribute.MetricName).IsEqualTo("order.total");
  }

  [Test]
  public async Task ProcessAsync_InvokesTelemetryTagHookAsync() {
    // Arrange - covers TelemetryTagAttribute branch in _invokeHookAsync
    var hook = new TelemetryTrackingHook();
    var options = new TagOptions();
    options.UseHook<TelemetryTagAttribute, TelemetryTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(TelemetryTrackingHook) ? hook : null);
    var context = _createProcessContext(
      new TelemetryTagAttribute { Tag = "process-order" },
      new { OrderId = "123" }
    );

    // Act
    await processor.ProcessAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(hook.InvokedCount).IsEqualTo(1);
  }

  [Test]
  public async Task ProcessAsync_WithNullHookResolver_CompletesWithoutErrorAsync() {
    // Arrange - covers null resolver early return path
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, TrackingHook>();
    var processor = new MessageTagProcessor(options, hookResolver: null);
    var context = _createProcessContext<SignalTagAttribute>(
      new SignalTagAttribute { Tag = "test" },
      new { }
    );

    // Act - should complete without invoking any hooks
    await processor.ProcessAsync(context, CancellationToken.None);

    // Assert - verify context wasn't modified (shows processing completed)
    await Assert.That(context.Attribute.Tag).IsEqualTo("test");
  }

  [Test]
  public async Task ProcessAsync_WhenHookResolverReturnsNull_SkipsHookAsync() {
    // Arrange - covers null hookInstance continue path
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, TrackingHook>();
    var processor = new MessageTagProcessor(options, _ => null);
    var context = _createProcessContext<SignalTagAttribute>(
      new SignalTagAttribute { Tag = "test" },
      new { }
    );

    // Act
    await processor.ProcessAsync(context, CancellationToken.None);

    // Assert - verify context wasn't modified (shows processing completed)
    await Assert.That(context.Attribute.Tag).IsEqualTo("test");
  }

  [Test]
  public async Task Constructor_WithNullOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange & Act & Assert
    await Assert.That(() => new MessageTagProcessor(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullScopeFactory_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var options = new TagOptions();

    // Act & Assert
    await Assert.That(() => new MessageTagProcessor(options, scopeFactory: null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  #endregion

  #region ScopeFactory Tests

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_WithScopeFactory_CreatesScope_Async() {
    // Arrange
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "test-tag");
    MessageTagRegistry.Register(registry, priority: 100);

    var hook = new TrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, TrackingHook>();

    // Create a mock scope factory that tracks scope creation
    var scopeFactory = new TrackingScopeFactory(hook);
    var processor = new MessageTagProcessor(options, scopeFactory);
    var message = new TaggedTestMessage("123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.AfterReceptorCompletion);

    // Assert - scope was created and hook was invoked
    // Note: ScopesCreated is 2 because TagLogger creates a scope to resolve ILoggerFactory
    await Assert.That(scopeFactory.ScopesCreated).IsEqualTo(2);
    await Assert.That(hook.InvokedCount).IsEqualTo(1);
  }

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_WithScopeFactory_DisposesScope_Async() {
    // Arrange
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "test-tag");
    MessageTagRegistry.Register(registry, priority: 100);

    var hook = new TrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, TrackingHook>();

    var scopeFactory = new TrackingScopeFactory(hook);
    var processor = new MessageTagProcessor(options, scopeFactory);
    var message = new TaggedTestMessage("123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.AfterReceptorCompletion);

    // Assert - scope was disposed after processing
    await Assert.That(scopeFactory.LastScope?.Disposed).IsTrue();
  }

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_WithScopeFactory_MultipleHooksShareSameScope_Async() {
    // Arrange
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "test-tag");
    MessageTagRegistry.Register(registry, priority: 100);

    var hook1 = new TrackingHook();
    var hook2 = new TrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, TrackingHook>(priority: 0);
    options.UseHook<SignalTagAttribute, TrackingHook>(priority: 10);

    var hookIndex = 0;
    var hooks = new[] { hook1, hook2 };
    var scopeFactory = new TrackingScopeFactory(type => type == typeof(TrackingHook) ? hooks[hookIndex++] : null);
    var processor = new MessageTagProcessor(options, scopeFactory);
    var message = new TaggedTestMessage("123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.AfterReceptorCompletion);

    // Assert - TWO scopes were created (one for TagLogger, one shared for hooks)
    // Note: TagLogger creates a scope to resolve ILoggerFactory, plus one scope for processing
    await Assert.That(scopeFactory.ScopesCreated).IsEqualTo(2);
    await Assert.That(hook1.InvokedCount).IsEqualTo(1);
    await Assert.That(hook2.InvokedCount).IsEqualTo(1);
  }

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_WithNoHooksAndScopeFactory_DoesNotCreateScope_Async() {
    // Arrange
    _cleanupRegistry();
    // Don't register any tags

    var options = new TagOptions();
    // Don't register any hooks

    var scopeFactory = new TrackingScopeFactory(_ => null);
    var processor = new MessageTagProcessor(options, scopeFactory);
    var message = new TaggedTestMessage("123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.AfterReceptorCompletion);

    // Assert - only the logger scope should be created (no processing scope since no tags)
    // Note: TagLogger creates 1 scope to resolve ILoggerFactory for diagnostic logging
    await Assert.That(scopeFactory.ScopesCreated).IsEqualTo(1);
  }

  /// <summary>
  /// Test scope factory that tracks scope creation and disposal.
  /// </summary>
  private sealed class TrackingScopeFactory : IServiceScopeFactory {
    private readonly Func<Type, object?> _resolver;

    public TrackingScopeFactory(TrackingHook hook) {
      _resolver = type => type == typeof(TrackingHook) ? hook : null;
    }

    public TrackingScopeFactory(Func<Type, object?> resolver) {
      _resolver = resolver;
    }

    public int ScopesCreated { get; private set; }
    public TrackingScope? LastScope { get; private set; }

    public IServiceScope CreateScope() {
      ScopesCreated++;
      LastScope = new TrackingScope(_resolver);
      return LastScope;
    }
  }

  private sealed class TrackingScope(Func<Type, object?> resolver) : IServiceScope, IAsyncDisposable {
    private readonly Func<Type, object?> _resolver = resolver;

    public bool Disposed { get; private set; }
    public IServiceProvider ServiceProvider { get; } = new TrackingServiceProvider(resolver);

    public void Dispose() {
      Disposed = true;
    }

    public ValueTask DisposeAsync() {
      Disposed = true;
      return ValueTask.CompletedTask;
    }
  }

  private sealed class TrackingServiceProvider(Func<Type, object?> resolver) : IServiceProvider {
    private readonly Func<Type, object?> _resolver = resolver;

    public object? GetService(Type serviceType) {
      return _resolver(serviceType);
    }
  }

  #endregion

  #region Dispatcher Fallback Tests (Phase 3)

  /// <summary>
  /// Test that processor uses MessageTagHookDispatcherRegistry for custom attribute types.
  /// Custom attributes require generated dispatchers for AOT-compatible hook invocation.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_WithCustomAttribute_CallsDispatcherRegistryAsync() {
    // Arrange
    _cleanupRegistry();
    _cleanupDispatcherRegistry();

    // Register a custom attribute type in the tag registry
    var customRegistry = new CustomAttributeTestRegistry();
    MessageTagRegistry.Register(customRegistry, priority: 100);

    // Register a dispatcher that handles our custom attribute
    var customDispatcher = new TestMessageTagHookDispatcher();
    MessageTagHookDispatcherRegistry.Register(customDispatcher, priority: 100);

    // Register a hook for the custom attribute type
    var customHook = new CustomTagTrackingHook();
    var options = new TagOptions();
    options.UseHook<CustomTestTagAttribute, CustomTagTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(CustomTagTrackingHook) ? customHook : null);

    var message = new CustomTaggedMessage("test-123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(CustomTaggedMessage), LifecycleStage.AfterReceptorCompletion);

    // Assert - dispatcher should have been called to create context
    await Assert.That(customDispatcher.TryCreateContextCallCount).IsGreaterThan(0);
    // Hook should have been invoked
    await Assert.That(customHook.InvokedCount).IsEqualTo(1);
  }

  /// <summary>
  /// Test that processor uses direct dispatch for built-in attribute types (fast path).
  /// Built-in types should NOT call the dispatcher registry.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_WithBuiltInAttribute_UsesDirectDispatchAsync() {
    // Arrange
    _cleanupRegistry();
    _cleanupDispatcherRegistry();

    // Register built-in attribute type
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "test-tag");
    MessageTagRegistry.Register(registry, priority: 100);

    // Register a dispatcher (should NOT be called for built-in types)
    var customDispatcher = new TestMessageTagHookDispatcher();
    MessageTagHookDispatcherRegistry.Register(customDispatcher, priority: 100);

    var hook = new TrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, TrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(TrackingHook) ? hook : null);

    var message = new TaggedTestMessage("123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.AfterReceptorCompletion);

    // Assert - dispatcher should NOT have been called (direct dispatch used)
    await Assert.That(customDispatcher.TryCreateContextCallCount).IsEqualTo(0);
    // Hook should still have been invoked via direct dispatch
    await Assert.That(hook.InvokedCount).IsEqualTo(1);
  }

  /// <summary>
  /// Test that processor falls back to base context when custom attribute has no dispatcher.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_WithUnknownAttribute_FallsBackToBaseContextAsync() {
    // Arrange
    _cleanupRegistry();
    _cleanupDispatcherRegistry();

    // Register an unknown custom attribute type (no dispatcher registered for it)
    var unknownRegistry = new UnknownAttributeTestRegistry();
    MessageTagRegistry.Register(unknownRegistry, priority: 100);

    // Register a universal hook that handles any MessageTagAttribute
    var universalHook = new UniversalTrackingHook();
    var options = new TagOptions();
    options.UseUniversalHook<UniversalTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(UniversalTrackingHook) ? universalHook : null);

    var message = new UnknownTaggedMessage("test");

    // Act
    await processor.ProcessTagsAsync(message, typeof(UnknownTaggedMessage), LifecycleStage.AfterReceptorCompletion);

    // Assert - universal hook should be invoked with base context
    await Assert.That(universalHook.InvokedCount).IsEqualTo(1);
  }

  /// <summary>
  /// Test that custom dispatcher correctly handles TryDispatchAsync.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_WithCustomAttribute_InvokesHookViaDispatcherAsync() {
    // Arrange
    _cleanupRegistry();
    _cleanupDispatcherRegistry();

    // Register custom attribute
    var customRegistry = new CustomAttributeTestRegistry();
    MessageTagRegistry.Register(customRegistry, priority: 100);

    // Register dispatcher with tracking
    var customDispatcher = new TestMessageTagHookDispatcher();
    MessageTagHookDispatcherRegistry.Register(customDispatcher, priority: 100);

    // Register hook
    var customHook = new CustomTagTrackingHook();
    var options = new TagOptions();
    options.UseHook<CustomTestTagAttribute, CustomTagTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(CustomTagTrackingHook) ? customHook : null);

    var message = new CustomTaggedMessage("invoke-test");

    // Act
    await processor.ProcessTagsAsync(message, typeof(CustomTaggedMessage), LifecycleStage.AfterReceptorCompletion);

    // Assert - dispatcher's TryDispatchAsync should have been called
    await Assert.That(customDispatcher.TryDispatchAsyncCallCount).IsGreaterThan(0);
    // And the hook should have been invoked
    await Assert.That(customHook.InvokedCount).IsEqualTo(1);
    await Assert.That(customHook.LastContext?.Attribute.Tag).IsEqualTo("custom-test-tag");
  }

  // Helper to cleanup dispatcher registry between tests
  private static void _cleanupDispatcherRegistry() {
    Whizbang.Core.Registry.AssemblyRegistry<IMessageTagHookDispatcher>.ClearForTesting();
  }

  // Custom test attribute type (simulates JDNext's custom attributes)
  private sealed class CustomTestTagAttribute : MessageTagAttribute {
  }

  // Another custom attribute type with no dispatcher
  private sealed class UnknownTestTagAttribute : MessageTagAttribute {
  }

  // Test message types
  private sealed record CustomTaggedMessage(string Value);
  private sealed record UnknownTaggedMessage(string Value);

  // Test registry for custom attribute
  private sealed class CustomAttributeTestRegistry : IMessageTagRegistry {
    public IEnumerable<MessageTagRegistration> GetTagsFor(Type messageType) {
      if (messageType == typeof(CustomTaggedMessage)) {
        yield return new MessageTagRegistration {
          MessageType = typeof(CustomTaggedMessage),
          AttributeType = typeof(CustomTestTagAttribute),
          Tag = "custom-test-tag",
          PayloadBuilder = msg => {
            var props = msg.GetType().GetProperties()
              .Where(p => p.CanRead)
              .ToDictionary(p => p.Name, p => p.GetValue(msg));
            return JsonSerializer.SerializeToElement(props);
          },
          AttributeFactory = () => new CustomTestTagAttribute { Tag = "custom-test-tag" }
        };
      }
    }
  }

  // Test registry for unknown attribute (no dispatcher will handle this)
  private sealed class UnknownAttributeTestRegistry : IMessageTagRegistry {
    public IEnumerable<MessageTagRegistration> GetTagsFor(Type messageType) {
      if (messageType == typeof(UnknownTaggedMessage)) {
        yield return new MessageTagRegistration {
          MessageType = typeof(UnknownTaggedMessage),
          AttributeType = typeof(UnknownTestTagAttribute),
          Tag = "unknown-test-tag",
          PayloadBuilder = msg => {
            var props = msg.GetType().GetProperties()
              .Where(p => p.CanRead)
              .ToDictionary(p => p.Name, p => p.GetValue(msg));
            return JsonSerializer.SerializeToElement(props);
          },
          AttributeFactory = () => new UnknownTestTagAttribute { Tag = "unknown-test-tag" }
        };
      }
    }
  }

  // Custom hook for custom attribute
  private sealed class CustomTagTrackingHook : IMessageTagHook<CustomTestTagAttribute> {
    public int InvokedCount { get; private set; }
    public TagContext<CustomTestTagAttribute>? LastContext { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<CustomTestTagAttribute> context,
        CancellationToken _) {
      InvokedCount++;
      LastContext = context;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  // Test dispatcher that handles CustomTestTagAttribute
  private sealed class TestMessageTagHookDispatcher : IMessageTagHookDispatcher {
    public int TryCreateContextCallCount { get; private set; }
    public int TryDispatchAsyncCallCount { get; private set; }

    public object? TryCreateContext(
        Type attributeType,
        MessageTagAttribute attribute,
        object message,
        Type messageType,
        JsonElement payload,
        IScopeContext? scope,
        LifecycleStage stage) {
      TryCreateContextCallCount++;

      if (attributeType == typeof(CustomTestTagAttribute) && attribute is CustomTestTagAttribute customAttr) {
        return new TagContext<CustomTestTagAttribute> {
          Attribute = customAttr,
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
        object hookInstance,
        object context,
        Type attributeType,
        CancellationToken ct) {
      TryDispatchAsyncCallCount++;

      if (attributeType == typeof(CustomTestTagAttribute) &&
          hookInstance is IMessageTagHook<CustomTestTagAttribute> hook &&
          context is TagContext<CustomTestTagAttribute> ctx) {
        return await hook.OnTaggedMessageAsync(ctx, ct);
      }

      return null;
    }
  }

  #endregion

  #region Stage Context Tests — Parameterized (All 20 Lifecycle Stages)

  public static LifecycleStage[] AllLifecycleStages()
    => Enum.GetValues<LifecycleStage>();

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task ProcessAsync_PassesStageToHookContext_AllStagesAsync(LifecycleStage stage) {
    // Arrange
    var hook = new StageTrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, StageTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(StageTrackingHook) ? hook : null);
    var context = _createProcessContext<SignalTagAttribute>(
      new SignalTagAttribute { Tag = "test" },
      new { OrderId = "123" },
      stage: stage
    );

    // Act
    await processor.ProcessAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(hook.InvokedCount).IsEqualTo(1);
    await Assert.That(hook.LastContext?.Stage).IsEqualTo(stage);
  }

  [Test]
  [NotInParallel]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task ProcessTagsAsync_PassesStageToHookContext_AllStagesAsync(LifecycleStage stage) {
    // Arrange
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "stage-test");
    MessageTagRegistry.Register(registry, priority: 100);
    var hook = new StageTrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, StageTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(StageTrackingHook) ? hook : null);
    var message = new TaggedTestMessage("123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), stage);

    // Assert
    await Assert.That(hook.InvokedCount).IsEqualTo(1);
    await Assert.That(hook.LastContext?.Stage).IsEqualTo(stage);
  }

  [Test]
  [NotInParallel]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task ProcessTagsAsync_TelemetryTag_PassesStage_AllStagesAsync(LifecycleStage stage) {
    // Arrange
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(TelemetryTagAttribute), "telemetry-stage-test");
    MessageTagRegistry.Register(registry, priority: 100);
    var hook = new TelemetryStageTrackingHook();
    var options = new TagOptions();
    options.UseHook<TelemetryTagAttribute, TelemetryStageTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(TelemetryStageTrackingHook) ? hook : null);
    var message = new TaggedTestMessage("123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), stage);

    // Assert
    await Assert.That(hook.InvokedCount).IsEqualTo(1);
    await Assert.That(hook.LastStage).IsEqualTo(stage);
  }

  [Test]
  [NotInParallel]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task ProcessTagsAsync_MetricTag_PassesStage_AllStagesAsync(LifecycleStage stage) {
    // Arrange
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(MetricTagAttribute), "metric-stage-test");
    MessageTagRegistry.Register(registry, priority: 100);
    var hook = new MetricStageTrackingHook();
    var options = new TagOptions();
    options.UseHook<MetricTagAttribute, MetricStageTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(MetricStageTrackingHook) ? hook : null);
    var message = new TaggedTestMessage("123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), stage);

    // Assert
    await Assert.That(hook.InvokedCount).IsEqualTo(1);
    await Assert.That(hook.LastStage).IsEqualTo(stage);
  }

  #endregion

  #region Stage Context Tests — Scenario Tests

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_MultipleStagesInSequence_HookReceivesCorrectStageEachTimeAsync() {
    // Arrange
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "stage-test");
    MessageTagRegistry.Register(registry, priority: 100);
    var hook = new StageTrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, StageTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(StageTrackingHook) ? hook : null);
    var message = new TaggedTestMessage("123");

    // Act - simulate the full lifecycle: Dispatcher fires first, then lifecycle stages
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.AfterReceptorCompletion);
    await Assert.That(hook.LastContext?.Stage).IsEqualTo(LifecycleStage.AfterReceptorCompletion);

    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.LocalImmediateInline);
    await Assert.That(hook.LastContext?.Stage).IsEqualTo(LifecycleStage.LocalImmediateInline);

    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.PostInboxInline);
    await Assert.That(hook.LastContext?.Stage).IsEqualTo(LifecycleStage.PostInboxInline);

    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.PostPerspectiveInline);
    await Assert.That(hook.LastContext?.Stage).IsEqualTo(LifecycleStage.PostPerspectiveInline);

    // Assert - hook was invoked for each stage
    await Assert.That(hook.InvokedCount).IsEqualTo(4);
    await Assert.That(hook.AllReceivedStages).Contains(LifecycleStage.AfterReceptorCompletion);
    await Assert.That(hook.AllReceivedStages).Contains(LifecycleStage.LocalImmediateInline);
    await Assert.That(hook.AllReceivedStages).Contains(LifecycleStage.PostInboxInline);
    await Assert.That(hook.AllReceivedStages).Contains(LifecycleStage.PostPerspectiveInline);
  }

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_HookCanFilterByStage_OnlyActsOnPostPerspectiveAsync() {
    // Arrange - simulates JDNext's pattern of filtering for PostPerspective only
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "notification-test");
    MessageTagRegistry.Register(registry, priority: 100);
    var hook = new PostPerspectiveOnlyHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, PostPerspectiveOnlyHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(PostPerspectiveOnlyHook) ? hook : null);
    var message = new TaggedTestMessage("123");

    // Act - fire at multiple stages
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.AfterReceptorCompletion);
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.LocalImmediateInline);
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.PostPerspectiveInline);

    // Assert - hook was called 3 times but only "acted" on PostPerspectiveInline
    await Assert.That(hook.TotalCallCount).IsEqualTo(3);
    await Assert.That(hook.ActedCount).IsEqualTo(1);
    await Assert.That(hook.ActedOnStage).IsEqualTo(LifecycleStage.PostPerspectiveInline);
  }

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_CustomAttribute_PassesStageViaDispatcherAsync() {
    // Arrange
    _cleanupRegistry();
    _cleanupDispatcherRegistry();
    var customRegistry = new CustomAttributeTestRegistry();
    MessageTagRegistry.Register(customRegistry, priority: 100);
    var customDispatcher = new TestMessageTagHookDispatcher();
    MessageTagHookDispatcherRegistry.Register(customDispatcher, priority: 100);
    var customHook = new CustomTagTrackingHook();
    var options = new TagOptions();
    options.UseHook<CustomTestTagAttribute, CustomTagTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(CustomTagTrackingHook) ? customHook : null);
    var message = new CustomTaggedMessage("test-stage");

    // Act
    await processor.ProcessTagsAsync(message, typeof(CustomTaggedMessage), LifecycleStage.PostPerspectiveInline);

    // Assert - custom hook receives the stage via dispatcher-created context
    await Assert.That(customHook.InvokedCount).IsEqualTo(1);
    await Assert.That(customHook.LastContext?.Stage).IsEqualTo(LifecycleStage.PostPerspectiveInline);
  }

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_UniversalHook_ReceivesStageAsync() {
    // Arrange
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "universal-stage-test");
    MessageTagRegistry.Register(registry, priority: 100);
    var hook = new UniversalStageTrackingHook();
    var options = new TagOptions();
    options.UseUniversalHook<UniversalStageTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(UniversalStageTrackingHook) ? hook : null);
    var message = new TaggedTestMessage("123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), LifecycleStage.PostPerspectiveInline);

    // Assert
    await Assert.That(hook.InvokedCount).IsEqualTo(1);
    await Assert.That(hook.LastStage).IsEqualTo(LifecycleStage.PostPerspectiveInline);
  }

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_SameEventMultipleStages_ContextIdenticalExceptStageAsync() {
    // Arrange — lock-in: when the same event fires across stages, only Stage changes
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "context-consistency");
    MessageTagRegistry.Register(registry, priority: 100);
    var hook = new StageTrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, StageTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(StageTrackingHook) ? hook : null);
    var message = new TaggedTestMessage("order-42");

    // Act — fire at representative stages from each path
    var stages = new[] {
      LifecycleStage.AfterReceptorCompletion,
      LifecycleStage.LocalImmediateInline,
      LifecycleStage.PreDistributeInline,
      LifecycleStage.PostOutboxAsync,
      LifecycleStage.PostPerspectiveInline
    };
    foreach (var stage in stages) {
      await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), stage);
    }

    // Assert — all contexts share identical Attribute, Message, MessageType, Payload
    await Assert.That(hook.AllReceivedContexts).Count().IsEqualTo(5);
    var first = hook.AllReceivedContexts[0];
    for (var i = 1; i < hook.AllReceivedContexts.Count; i++) {
      var ctx = hook.AllReceivedContexts[i];
      await Assert.That(ctx.Attribute.Tag).IsEqualTo(first.Attribute.Tag);
      await Assert.That(ctx.MessageType).IsEqualTo(first.MessageType);
      await Assert.That(ctx.Message).IsEqualTo(first.Message);
      await Assert.That(ctx.Payload.GetRawText()).IsEqualTo(first.Payload.GetRawText());
    }

    // Assert — each context has a distinct, correct Stage
    await Assert.That(hook.AllReceivedStages).IsEquivalentTo(stages);
  }

  [Test]
  public async Task ProcessAsync_SameEventMultipleStages_ContextIdenticalExceptStageAsync() {
    // Arrange — lock-in via ProcessAsync path: only Stage differs between invocations
    var hook = new StageTrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, StageTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(StageTrackingHook) ? hook : null);
    var attribute = new SignalTagAttribute { Tag = "order-created" };
    var payload = new { OrderId = "abc", Total = 99.99m };

    // Act — fire at every lifecycle stage
    var allStages = Enum.GetValues<LifecycleStage>();
    foreach (var stage in allStages) {
      var context = _createProcessContext<SignalTagAttribute>(attribute, payload, stage: stage);
      await processor.ProcessAsync(context, CancellationToken.None);
    }

    // Assert — invoked exactly once per stage
    await Assert.That(hook.AllReceivedContexts).Count().IsEqualTo(allStages.Length);

    // Assert — all contexts share identical Attribute, Message, MessageType, Payload
    var first = hook.AllReceivedContexts[0];
    for (var i = 1; i < hook.AllReceivedContexts.Count; i++) {
      var ctx = hook.AllReceivedContexts[i];
      await Assert.That(ctx.Attribute.Tag).IsEqualTo(first.Attribute.Tag);
      await Assert.That(ctx.MessageType).IsEqualTo(first.MessageType);
      await Assert.That(ctx.Payload.GetRawText()).IsEqualTo(first.Payload.GetRawText());
    }

    // Assert — every stage received exactly once, in order
    await Assert.That(hook.AllReceivedStages).IsEquivalentTo(allStages);
  }

  [Test]
  [NotInParallel]
  public async Task ProcessTagsAsync_AllStagesFired_ReceivesAllUniqueStagesAsync() {
    // Arrange — lock-in: all stages (20 lifecycle + AfterReceptorCompletion) produce
    // exactly one invocation each with unique stages
    _cleanupRegistry();
    var registry = new TestMessageTagRegistry();
    registry.AddRegistration(typeof(TaggedTestMessage), typeof(SignalTagAttribute), "all-stages");
    MessageTagRegistry.Register(registry, priority: 100);
    var hook = new StageTrackingHook();
    var options = new TagOptions();
    options.UseHook<SignalTagAttribute, StageTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(StageTrackingHook) ? hook : null);
    var message = new TaggedTestMessage("123");

    // Act — fire at every lifecycle stage
    var allStages = Enum.GetValues<LifecycleStage>();
    foreach (var stage in allStages) {
      await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), stage);
    }

    // Assert — one invocation per stage, each with a unique stage value
    await Assert.That(hook.InvokedCount).IsEqualTo(allStages.Length);
    await Assert.That(hook.AllReceivedStages.Distinct().Count()).IsEqualTo(allStages.Length);
    await Assert.That(hook.AllReceivedStages).IsEquivalentTo(allStages);
  }

  #endregion

  #region Stage Hook Helpers

  // Stage-tracking hook for SignalTagAttribute
  private sealed class StageTrackingHook : IMessageTagHook<SignalTagAttribute> {
    public int InvokedCount { get; private set; }
    public TagContext<SignalTagAttribute>? LastContext { get; private set; }
    public List<LifecycleStage> AllReceivedStages { get; } = [];
    public List<TagContext<SignalTagAttribute>> AllReceivedContexts { get; } = [];

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> context,
        CancellationToken _) {
      InvokedCount++;
      LastContext = context;
      AllReceivedStages.Add(context.Stage);
      AllReceivedContexts.Add(context);
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  // Hook that only acts on PostPerspectiveInline (simulates JDNext notification pattern)
  private sealed class PostPerspectiveOnlyHook : IMessageTagHook<SignalTagAttribute> {
    public int TotalCallCount { get; private set; }
    public int ActedCount { get; private set; }
    public LifecycleStage? ActedOnStage { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> context,
        CancellationToken _) {
      TotalCallCount++;

      // Only act on PostPerspectiveInline — the JDNext pattern
      if (context.Stage == LifecycleStage.PostPerspectiveInline) {
        ActedCount++;
        ActedOnStage = context.Stage;
      }

      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  // Telemetry stage-tracking hook
  private sealed class TelemetryStageTrackingHook : IMessageTagHook<TelemetryTagAttribute> {
    public int InvokedCount { get; private set; }
    public LifecycleStage? LastStage { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<TelemetryTagAttribute> context,
        CancellationToken _) {
      InvokedCount++;
      LastStage = context.Stage;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  // Metric stage-tracking hook
  private sealed class MetricStageTrackingHook : IMessageTagHook<MetricTagAttribute> {
    public int InvokedCount { get; private set; }
    public LifecycleStage? LastStage { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<MetricTagAttribute> context,
        CancellationToken _) {
      InvokedCount++;
      LastStage = context.Stage;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  // Universal stage-tracking hook
  private sealed class UniversalStageTrackingHook : IMessageTagHook<MessageTagAttribute> {
    public int InvokedCount { get; private set; }
    public LifecycleStage? LastStage { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<MessageTagAttribute> context,
        CancellationToken _) {
      InvokedCount++;
      LastStage = context.Stage;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  #endregion
}
