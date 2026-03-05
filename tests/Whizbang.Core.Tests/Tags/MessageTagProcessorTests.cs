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
    var scope = new Dictionary<string, object?> { ["TenantId"] = "tenant-123" };
    var context = _createProcessContext<SignalTagAttribute>(
      new SignalTagAttribute { Tag = "test" },
      new { },
      scope
    );

    // Act
    await processor.ProcessAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(hook.ReceivedScope is not null).IsTrue();
    var tenantId = hook.ReceivedScope!["TenantId"];
    await Assert.That(tenantId).IsEqualTo("tenant-123");
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
      IReadOnlyDictionary<string, object?>? scope = null)
      where TAttribute : MessageTagAttribute {
    return new TagContext<TAttribute> {
      Attribute = attribute,
      Message = payloadData,
      MessageType = payloadData.GetType(),
      Payload = JsonSerializer.SerializeToElement(payloadData),
      Scope = scope
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

  private sealed class OrderTrackingHook : IMessageTagHook<SignalTagAttribute> {
    private readonly string _name;
    private readonly List<string> _executionOrder;

    public OrderTrackingHook(string name, List<string> executionOrder) {
      _name = name;
      _executionOrder = executionOrder;
    }

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
    public IReadOnlyDictionary<string, object?>? ReceivedScope { get; private set; }

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
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage));

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
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage));

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
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage));

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
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage));

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
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage));

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
    var scope = new Dictionary<string, object?> { ["TenantId"] = "tenant-456" };

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage), scope);

    // Assert
    await Assert.That(hook.ReceivedScope is not null).IsTrue();
    await Assert.That(hook.ReceivedScope!["TenantId"]).IsEqualTo("tenant-456");
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
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage));

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
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage));

    // Assert - scope was created and hook was invoked
    await Assert.That(scopeFactory.ScopesCreated).IsEqualTo(1);
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
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage));

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
    var scopeFactory = new TrackingScopeFactory(type => hooks[hookIndex++]);
    var processor = new MessageTagProcessor(options, scopeFactory);
    var message = new TaggedTestMessage("123");

    // Act
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage));

    // Assert - only ONE scope was created for both hooks
    await Assert.That(scopeFactory.ScopesCreated).IsEqualTo(1);
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
    await processor.ProcessTagsAsync(message, typeof(TaggedTestMessage));

    // Assert - no scope should be created if no tags exist
    await Assert.That(scopeFactory.ScopesCreated).IsEqualTo(0);
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

  private sealed class TrackingScope : IServiceScope, IAsyncDisposable {
    private readonly Func<Type, object?> _resolver;

    public TrackingScope(Func<Type, object?> resolver) {
      _resolver = resolver;
      ServiceProvider = new TrackingServiceProvider(resolver);
    }

    public bool Disposed { get; private set; }
    public IServiceProvider ServiceProvider { get; }

    public void Dispose() {
      Disposed = true;
    }

    public ValueTask DisposeAsync() {
      Disposed = true;
      return ValueTask.CompletedTask;
    }
  }

  private sealed class TrackingServiceProvider : IServiceProvider {
    private readonly Func<Type, object?> _resolver;

    public TrackingServiceProvider(Func<Type, object?> resolver) {
      _resolver = resolver;
    }

    public object? GetService(Type serviceType) {
      return _resolver(serviceType);
    }
  }

  #endregion
}
