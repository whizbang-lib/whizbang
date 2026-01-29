using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    var context = _createProcessContext<NotificationTagAttribute>(
      new NotificationTagAttribute { Tag = "test" },
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
    options.UseHook<NotificationTagAttribute, TrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(TrackingHook) ? hook : null);
    var context = _createProcessContext<NotificationTagAttribute>(
      new NotificationTagAttribute { Tag = "order-created" },
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
    var context = _createProcessContext<NotificationTagAttribute>(
      new NotificationTagAttribute { Tag = "test" },
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
    var context = _createProcessContext<NotificationTagAttribute>(
      new NotificationTagAttribute { Tag = "test" },
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
      .UseHook<NotificationTagAttribute, OrderTrackingHook>(priority: 500)   // Last
      .UseHook<NotificationTagAttribute, OrderTrackingHook>(priority: -100)  // First
      .UseHook<NotificationTagAttribute, OrderTrackingHook>(priority: 50);   // Middle

    // Hooks execute in priority order: -100, 50, 500
    // So resolver returns in that order: Hook2 (-100), Hook3 (50), Hook1 (500)
    var hookIndex = 0;
    var hooks = new[] { hook2, hook3, hook1 };
    var processor = new MessageTagProcessor(options, _ => hooks[hookIndex++]);

    var context = _createProcessContext<NotificationTagAttribute>(
      new NotificationTagAttribute { Tag = "test" },
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
      .UseHook<NotificationTagAttribute, PayloadModifyingHook>(priority: -100)  // First
      .UseHook<NotificationTagAttribute, PayloadReceivingHook>(priority: 100);   // Second

    var hookIndex = 0;
    object?[] hooks = [payloadModifyingHook, receivingHook];
    var processor = new MessageTagProcessor(options, _ => hooks[hookIndex++]);

    var context = _createProcessContext<NotificationTagAttribute>(
      new NotificationTagAttribute { Tag = "test" },
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
    options.UseHook<NotificationTagAttribute, CancellationTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(CancellationTrackingHook) ? hook : null);
    var cts = new CancellationTokenSource();
    var context = _createProcessContext<NotificationTagAttribute>(
      new NotificationTagAttribute { Tag = "test" },
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
    options.UseHook<NotificationTagAttribute, ScopeTrackingHook>();
    var processor = new MessageTagProcessor(options, type => type == typeof(ScopeTrackingHook) ? hook : null);
    var scope = new Dictionary<string, object?> { ["TenantId"] = "tenant-123" };
    var context = _createProcessContext<NotificationTagAttribute>(
      new NotificationTagAttribute { Tag = "test" },
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
      .UseHook<NotificationTagAttribute, PassThroughHook>(priority: -100)
      .UseHook<NotificationTagAttribute, PayloadReceivingHook>(priority: 100);

    var hookIndex = 0;
    object?[] hooks = [passThroughHook, receivingHook];
    var processor = new MessageTagProcessor(options, _ => hooks[hookIndex++]);

    var originalPayload = new { Original = true };
    var context = _createProcessContext<NotificationTagAttribute>(
      new NotificationTagAttribute { Tag = "test" },
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
  private sealed class TrackingHook : IMessageTagHook<NotificationTagAttribute> {
    public int InvokedCount { get; private set; }
    public TagContext<NotificationTagAttribute>? LastContext { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> context,
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

  private sealed class OrderTrackingHook : IMessageTagHook<NotificationTagAttribute> {
    private readonly string _name;
    private readonly List<string> _executionOrder;

    public OrderTrackingHook(string name, List<string> executionOrder) {
      _name = name;
      _executionOrder = executionOrder;
    }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> _,
        CancellationToken __) {
      _executionOrder.Add(_name);
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class PayloadModifyingHook : IMessageTagHook<NotificationTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> _,
        CancellationToken __) {
      var modified = new { Modified = true };
      return ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(modified));
    }
  }

  private sealed class PayloadReceivingHook : IMessageTagHook<NotificationTagAttribute> {
    public JsonElement? ReceivedPayload { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> context,
        CancellationToken _) {
      ReceivedPayload = context.Payload;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class CancellationTrackingHook : IMessageTagHook<NotificationTagAttribute> {
    public CancellationToken ReceivedToken { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> _,
        CancellationToken ct) {
      ReceivedToken = ct;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class ScopeTrackingHook : IMessageTagHook<NotificationTagAttribute> {
    public IReadOnlyDictionary<string, object?>? ReceivedScope { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> context,
        CancellationToken _) {
      ReceivedScope = context.Scope;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class PassThroughHook : IMessageTagHook<NotificationTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> _,
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
    options.UseHook<NotificationTagAttribute, TrackingHook>();
    var processor = new MessageTagProcessor(options, hookResolver: null);
    var context = _createProcessContext<NotificationTagAttribute>(
      new NotificationTagAttribute { Tag = "test" },
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
    options.UseHook<NotificationTagAttribute, TrackingHook>();
    var processor = new MessageTagProcessor(options, _ => null);
    var context = _createProcessContext<NotificationTagAttribute>(
      new NotificationTagAttribute { Tag = "test" },
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

  #endregion
}
