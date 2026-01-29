using System;
using System.Collections.Generic;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="IMessageTagHook{TAttribute}"/> interface and hook patterns.
/// Validates hook execution, payload modification, and priority ordering.
/// </summary>
/// <tests>Whizbang.Core/Tags/IMessageTagHook.cs</tests>
[Category("Core")]
[Category("Tags")]
public class MessageTagHookTests {

  [Test]
  public async Task IMessageTagHook_IsGenericInterfaceAsync() {
    // Assert
    await Assert.That(typeof(IMessageTagHook<>).IsGenericType).IsTrue();
    await Assert.That(typeof(IMessageTagHook<>).IsInterface).IsTrue();
  }

  [Test]
  public async Task IMessageTagHook_GenericConstraint_RequiresMessageTagAttributeAsync() {
    // Arrange
    var genericArg = typeof(IMessageTagHook<>).GetGenericArguments()[0];
    var constraints = genericArg.GetGenericParameterConstraints();

    // Assert - The constraint should be MessageTagAttribute
    await Assert.That(constraints).IsNotEmpty();
    await Assert.That(constraints[0]).IsEqualTo(typeof(MessageTagAttribute));
  }

  [Test]
  public async Task Hook_ReturnsNull_PassesOriginalPayloadToNextHookAsync() {
    // Arrange
    var hook = new PassThroughHook();
    var context = _createContext("test-tag", new { OrderId = "123" });

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task Hook_ReturnsModifiedPayload_NextHookReceivesModifiedPayloadAsync() {
    // Arrange
    var hook = new PayloadModifyingHook();
    var context = _createContext("test-tag", new { OrderId = "123" });

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.TryGetProperty("modified", out var modified)).IsTrue();
    await Assert.That(modified.GetBoolean()).IsTrue();
  }

  [Test]
  public async Task Hook_CanAccessAttributePropertiesAsync() {
    // Arrange
    var hook = new AttributeAccessingHook();
    var attribute = new NotificationTagAttribute {
      Tag = "order-created",
      Group = "customer-{CustomerId}",
      Priority = NotificationPriority.High,
      Properties = ["OrderId", "CustomerId"]
    };
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = attribute,
      Message = new TestOrderEvent(Guid.NewGuid(), Guid.NewGuid()),
      MessageType = typeof(TestOrderEvent),
      Payload = JsonSerializer.SerializeToElement(new { OrderId = "123" })
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(hook.LastReceivedTag).IsEqualTo("order-created");
    await Assert.That(hook.LastReceivedGroup).IsEqualTo("customer-{CustomerId}");
    await Assert.That(hook.LastReceivedPriority).IsEqualTo(NotificationPriority.High);
  }

  [Test]
  public async Task Hook_CanAccessScopeDataAsync() {
    // Arrange
    var hook = new ScopeAccessingHook();
    var scope = new Dictionary<string, object?> {
      ["TenantId"] = "tenant-123",
      ["UserId"] = "user-456"
    };
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = new NotificationTagAttribute { Tag = "test" },
      Message = new TestOrderEvent(Guid.NewGuid(), Guid.NewGuid()),
      MessageType = typeof(TestOrderEvent),
      Payload = JsonSerializer.SerializeToElement(new { }),
      Scope = scope
    };

    // Act
    await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(hook.LastReceivedTenantId).IsEqualTo("tenant-123");
    await Assert.That(hook.LastReceivedUserId).IsEqualTo("user-456");
  }

  [Test]
  public async Task Hook_CanAccessMessageAndMessageTypeAsync() {
    // Arrange
    var hook = new MessageAccessingHook();
    var message = new TestOrderEvent(Guid.NewGuid(), Guid.NewGuid());
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = new NotificationTagAttribute { Tag = "test" },
      Message = message,
      MessageType = typeof(TestOrderEvent),
      Payload = JsonSerializer.SerializeToElement(new { })
    };

    // Act
    await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(hook.LastReceivedMessage).IsEqualTo(message);
    await Assert.That(hook.LastReceivedMessageType).IsEqualTo(typeof(TestOrderEvent));
  }

  [Test]
  public async Task Hook_CancellationToken_IsPassedThroughAsync() {
    // Arrange
    var hook = new CancellationAwareHook();
    var cts = new CancellationTokenSource();
    var context = _createContext("test", new { });

    // Act
    await hook.OnTaggedMessageAsync(context, cts.Token);

    // Assert
    await Assert.That(hook.LastReceivedCancellationToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task TelemetryHook_CanAccessTelemetrySpecificPropertiesAsync() {
    // Arrange
    var hook = new TelemetryHook();
    var context = new TagContext<TelemetryTagAttribute> {
      Attribute = new TelemetryTagAttribute {
        Tag = "payment-processed",
        SpanName = "ProcessPayment",
        Kind = SpanKind.Internal,
        RecordAsEvent = true
      },
      Message = new { PaymentId = "pay-123" },
      MessageType = typeof(object),
      Payload = JsonSerializer.SerializeToElement(new { })
    };

    // Act
    await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(hook.LastSpanName).IsEqualTo("ProcessPayment");
    await Assert.That(hook.LastSpanKind).IsEqualTo(SpanKind.Internal);
    await Assert.That(hook.LastRecordAsEvent).IsTrue();
  }

  [Test]
  public async Task MetricHook_CanAccessMetricSpecificPropertiesAsync() {
    // Arrange
    var hook = new MetricHook();
    var context = new TagContext<MetricTagAttribute> {
      Attribute = new MetricTagAttribute {
        Tag = "order-amount",
        MetricName = "orders.amount",
        Type = MetricType.Histogram,
        ValueProperty = "TotalAmount",
        Unit = "USD"
      },
      Message = new { OrderId = "ord-123", TotalAmount = 99.99m },
      MessageType = typeof(object),
      Payload = JsonSerializer.SerializeToElement(new { })
    };

    // Act
    await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(hook.LastMetricName).IsEqualTo("orders.amount");
    await Assert.That(hook.LastMetricType).IsEqualTo(MetricType.Histogram);
    await Assert.That(hook.LastValueProperty).IsEqualTo("TotalAmount");
    await Assert.That(hook.LastUnit).IsEqualTo("USD");
  }

  // Helper method to create test context
  private static TagContext<NotificationTagAttribute> _createContext(string tag, object payloadData) {
    return new TagContext<NotificationTagAttribute> {
      Attribute = new NotificationTagAttribute { Tag = tag },
      Message = payloadData,
      MessageType = payloadData.GetType(),
      Payload = JsonSerializer.SerializeToElement(payloadData)
    };
  }

  // Test event type
  private sealed record TestOrderEvent(Guid OrderId, Guid CustomerId);

  // Test hook implementations
  private sealed class PassThroughHook : IMessageTagHook<NotificationTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> _,
        CancellationToken __) {
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class PayloadModifyingHook : IMessageTagHook<NotificationTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> context,
        CancellationToken _) {
      var modified = new Dictionary<string, object> {
        ["original"] = context.Payload,
        ["modified"] = true
      };
      return ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(modified));
    }
  }

  private sealed class AttributeAccessingHook : IMessageTagHook<NotificationTagAttribute> {
    public string? LastReceivedTag { get; private set; }
    public string? LastReceivedGroup { get; private set; }
    public NotificationPriority LastReceivedPriority { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> context,
        CancellationToken _) {
      LastReceivedTag = context.Attribute.Tag;
      LastReceivedGroup = context.Attribute.Group;
      LastReceivedPriority = context.Attribute.Priority;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class ScopeAccessingHook : IMessageTagHook<NotificationTagAttribute> {
    public string? LastReceivedTenantId { get; private set; }
    public string? LastReceivedUserId { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> context,
        CancellationToken _) {
      LastReceivedTenantId = context.Scope?["TenantId"]?.ToString();
      LastReceivedUserId = context.Scope?["UserId"]?.ToString();
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class MessageAccessingHook : IMessageTagHook<NotificationTagAttribute> {
    public object? LastReceivedMessage { get; private set; }
    public Type? LastReceivedMessageType { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> context,
        CancellationToken _) {
      LastReceivedMessage = context.Message;
      LastReceivedMessageType = context.MessageType;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class CancellationAwareHook : IMessageTagHook<NotificationTagAttribute> {
    public CancellationToken LastReceivedCancellationToken { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> context,
        CancellationToken ct) {
      LastReceivedCancellationToken = ct;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class TelemetryHook : IMessageTagHook<TelemetryTagAttribute> {
    public string? LastSpanName { get; private set; }
    public SpanKind LastSpanKind { get; private set; }
    public bool LastRecordAsEvent { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<TelemetryTagAttribute> context,
        CancellationToken _) {
      LastSpanName = context.Attribute.SpanName;
      LastSpanKind = context.Attribute.Kind;
      LastRecordAsEvent = context.Attribute.RecordAsEvent;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class MetricHook : IMessageTagHook<MetricTagAttribute> {
    public string? LastMetricName { get; private set; }
    public MetricType LastMetricType { get; private set; }
    public string? LastValueProperty { get; private set; }
    public string? LastUnit { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<MetricTagAttribute> context,
        CancellationToken _) {
      LastMetricName = context.Attribute.MetricName;
      LastMetricType = context.Attribute.Type;
      LastValueProperty = context.Attribute.ValueProperty;
      LastUnit = context.Attribute.Unit;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }
}
