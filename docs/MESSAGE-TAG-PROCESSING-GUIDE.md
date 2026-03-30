# Message Tag Processing Guide

This guide explains how to use the new MessageTagProcessor pipeline in JDNext to automatically process tagged messages after receptor completion.

## Overview

The Message Tag Processing system allows you to:
1. **Tag messages** with attributes like `[NotificationTag]`, `[TelemetryTag]`, `[MetricTag]`, or custom attributes
2. **Register hooks** that automatically fire when tagged messages are processed
3. **Build cross-cutting concerns** (notifications, telemetry, metrics, audit logs) without polluting business logic

## Quick Start

### 1. Configure AddWhizbang with Tag Hooks

```csharp
services.AddWhizbang(options => {
  // Register hooks for built-in tag types
  options.Tags.UseHook<NotificationTagAttribute, SignalRNotificationHook>();
  options.Tags.UseHook<TelemetryTagAttribute, OpenTelemetryHook>();
  options.Tags.UseHook<MetricTagAttribute, PrometheusMetricHook>();

  // Register hooks for custom tag attributes
  options.Tags.UseHook<AuditEventAttribute, AuditLogHook>();

  // Optional: Use universal hook for ALL tag types
  options.Tags.UseUniversalHook<LoggingHook>();

  // Optional: Disable tag processing entirely
  // options.EnableTagProcessing = false;

  // Optional: Process tags during lifecycle stage instead of immediately
  // options.TagProcessingMode = TagProcessingMode.AsLifecycleStage;
});
```

### 2. Tag Your Messages

```csharp
// Notification tag - for real-time notifications
[NotificationTag(Tag = "order-created", Properties = ["OrderId", "CustomerId"])]
public record OrderCreatedEvent(Guid OrderId, Guid CustomerId, decimal Total) : IEvent;

// Telemetry tag - for distributed tracing
[TelemetryTag(Tag = "payment-processed", SpanName = "ProcessPayment", Kind = SpanKind.Internal)]
public record PaymentProcessedEvent(Guid PaymentId, decimal Amount) : IEvent;

// Metric tag - for metrics/counters
[MetricTag(Tag = "orders-metric", MetricName = "orders.created", Type = MetricType.Counter)]
public record OrderCountEvent(Guid OrderId) : IEvent;

// Audit event - for audit logging (inherits from MessageTagAttribute)
[AuditEvent(Reason = "Customer data accessed", Level = AuditLevel.Warning)]
public record CustomerDataViewedEvent(Guid CustomerId, string ViewedBy) : IEvent;
```

### 3. Create a Hook Implementation

```csharp
public class SignalRNotificationHook : IMessageTagHook<NotificationTagAttribute> {
  private readonly IHubContext<NotificationHub> _hubContext;

  public SignalRNotificationHook(IHubContext<NotificationHub> hubContext) {
    _hubContext = hubContext;
  }

  public async ValueTask<JsonElement?> OnTaggedMessageAsync(
      TagContext<NotificationTagAttribute> context,
      CancellationToken ct) {

    // Access the attribute
    var tag = context.Attribute.Tag;  // e.g., "order-created"

    // Access the payload (JSON with extracted properties)
    var payload = context.Payload;

    // Access scope data (tenant, user, etc.)
    var tenantId = context.Scope?["TenantId"];

    // Send notification via SignalR
    await _hubContext.Clients.All.SendAsync(
        "Notification",
        new { Tag = tag, Data = payload },
        ct);

    // Return null to keep original payload, or return modified JsonElement
    return null;
  }
}
```

## Tag Attributes

### Built-in Tag Attributes

| Attribute | Purpose | Key Properties |
|-----------|---------|----------------|
| `NotificationTagAttribute` | Real-time notifications | `Tag`, `Properties`, `IncludeEvent` |
| `TelemetryTagAttribute` | Distributed tracing | `Tag`, `SpanName`, `Kind` |
| `MetricTagAttribute` | Metrics/counters | `Tag`, `MetricName`, `Type` |
| `AuditEventAttribute` | Audit logging | `Reason`, `Level` |

### Attribute Properties

```csharp
[NotificationTag(
    Tag = "order-created",           // Unique identifier for the tag
    Properties = ["OrderId", "Total"], // Properties to extract into payload
    IncludeEvent = true,             // Include full event in payload as "__event"
    ExtraJson = """{"source": "api"}""" // Merge extra JSON into payload
)]
public record OrderCreatedEvent(Guid OrderId, decimal Total, string InternalNote);
```

### Creating Custom Tag Attributes

```csharp
// Custom attribute must inherit from MessageTagAttribute
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public class SlackNotificationAttribute : MessageTagAttribute {
  public string Channel { get; set; } = "#general";
  public string Emoji { get; set; } = ":bell:";
}

// Use it on messages
[SlackNotification(Tag = "deploy-complete", Channel = "#deployments", Emoji = ":rocket:")]
public record DeploymentCompletedEvent(string Version, string Environment) : IEvent;

// Create a hook for it
public class SlackNotificationHook : IMessageTagHook<SlackNotificationAttribute> {
  public async ValueTask<JsonElement?> OnTaggedMessageAsync(
      TagContext<SlackNotificationAttribute> context,
      CancellationToken ct) {

    var channel = context.Attribute.Channel;
    var emoji = context.Attribute.Emoji;
    // Send to Slack...

    return null;
  }
}

// Register the hook
services.AddWhizbang(options => {
  options.Tags.UseHook<SlackNotificationAttribute, SlackNotificationHook>();
});
```

## Hook Interface

```csharp
public interface IMessageTagHook<TAttribute> where TAttribute : MessageTagAttribute {
  /// <summary>
  /// Called when a message with the specified tag attribute is processed.
  /// </summary>
  /// <param name="context">Context containing attribute, message, payload, and scope.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>
  /// Null to keep the original payload, or a new JsonElement to pass to subsequent hooks.
  /// </returns>
  ValueTask<JsonElement?> OnTaggedMessageAsync(
      TagContext<TAttribute> context,
      CancellationToken ct);
}
```

## TagContext Properties

| Property | Type | Description |
|----------|------|-------------|
| `Attribute` | `TAttribute` | The tag attribute instance with configured values |
| `Message` | `object` | The original message object |
| `MessageType` | `Type` | The message's runtime type |
| `Payload` | `JsonElement` | JSON payload with extracted properties |
| `Scope` | `IScopeContext?` | Security scope context (tenant, user, roles, permissions) |
| `Stage` | `LifecycleStage` | The lifecycle stage at which this hook is being invoked |

### Stage-Based Filtering

Hooks fire at **every** lifecycle stage. Use `context.Stage` to decide when to act:

```csharp
public async ValueTask<JsonElement?> OnTaggedMessageAsync(
    TagContext<NotificationTagAttribute> context,
    CancellationToken ct) {
  // Only send notifications after perspective data is committed
  if (context.Stage != LifecycleStage.PostPerspectiveInline)
    return null;

  // Send notification...
  return null;
}
```

The `LifecycleStage` enum has 20 values. The special value `AfterReceptorCompletion` (-1) fires synchronously after the receptor completes, before any lifecycle stages.

## Hook Priority

Hooks execute in ascending priority order (lower values first):

```csharp
options.Tags.UseHook<NotificationTagAttribute, ValidationHook>(priority: -100);  // Runs first
options.Tags.UseHook<NotificationTagAttribute, NotificationHook>(priority: 0);   // Default
options.Tags.UseHook<NotificationTagAttribute, AuditHook>(priority: 500);        // Runs last
```

## Processing Modes

### AfterReceptorCompletion (Default)

Tags are processed immediately after the receptor completes successfully:

```
Message → Receptor → Cascade Events → TAG PROCESSING → Lifecycle Stages
```

### AsLifecycleStage

Tags are processed during lifecycle invocation (use when hooks depend on lifecycle receptors):

```
Message → Receptor → Cascade Events → Lifecycle Stages → TAG PROCESSING
```

```csharp
services.AddWhizbang(options => {
  options.TagProcessingMode = TagProcessingMode.AsLifecycleStage;
});
```

## How It Works (Auto-Registration)

The source generator automatically:

1. **Discovers tagged messages** at compile time
2. **Generates a registry** implementing `IMessageTagRegistry`
3. **Auto-registers via `[ModuleInitializer]`** before `Main()` runs

No manual registration needed - just tag your messages and register hooks!

```csharp
// Generated code (you don't write this):
[ModuleInitializer]
internal static void Initialize() {
  MessageTagRegistry.Register(GeneratedMessageTagRegistry_YourAssembly.Instance, priority: 100);
}
```

## Multi-Assembly Support

Tags can be defined in different assemblies (e.g., contracts vs services):

- **Contracts assembly**: Define tagged messages, priority 100 (checked first)
- **Services assembly**: Define handlers, priority 1000

Both assemblies' registries are combined at runtime via `AssemblyRegistry<IMessageTagRegistry>`.

## Best Practices

1. **Keep hooks lightweight** - Don't do heavy processing; queue work if needed
2. **Use properties wisely** - Only extract what you need into the payload
3. **Handle failures gracefully** - Hook failures shouldn't break message processing
4. **Use scope for context** - Pass tenant/user/correlation info via scope, not hardcoding
5. **Test hooks independently** - Unit test hooks with mock TagContext

## Example: Complete Notification System

```csharp
// 1. Define the event with notification tag
[NotificationTag(Tag = "order-status-changed", Properties = ["OrderId", "NewStatus"])]
public record OrderStatusChangedEvent(
    Guid OrderId,
    OrderStatus NewStatus,
    OrderStatus OldStatus) : IEvent;

// 2. Create the hook
public class OrderNotificationHook : IMessageTagHook<NotificationTagAttribute> {
  private readonly IHubContext<CustomerHub> _hub;
  private readonly INotificationService _notifications;

  public OrderNotificationHook(
      IHubContext<CustomerHub> hub,
      INotificationService notifications) {
    _hub = hub;
    _notifications = notifications;
  }

  public async ValueTask<JsonElement?> OnTaggedMessageAsync(
      TagContext<NotificationTagAttribute> context,
      CancellationToken ct) {

    var orderId = context.Payload.GetProperty("OrderId").GetGuid();
    var status = context.Payload.GetProperty("NewStatus").GetString();
    var customerId = context.Scope?["CustomerId"] as Guid?;

    // Send real-time update
    if (customerId.HasValue) {
      await _hub.Clients.User(customerId.Value.ToString())
          .SendAsync("OrderStatusUpdate", new { orderId, status }, ct);
    }

    // Queue push notification
    await _notifications.QueuePushNotificationAsync(
        customerId,
        $"Order {orderId} is now {status}",
        ct);

    return null;
  }
}

// 3. Register in startup
services.AddWhizbang(options => {
  options.Tags.UseHook<NotificationTagAttribute, OrderNotificationHook>();
});
```

## Troubleshooting

### Tags not being processed?

1. Verify `EnableTagProcessing` is true (default)
2. Check that hooks are registered with `UseHook<>`
3. Ensure message type is `public` (private types are not discovered)
4. Verify the attribute inherits from `MessageTagAttribute`

### Hook not firing?

1. Check hook is registered for the correct attribute type
2. Verify hook is registered with DI (automatically done by UseHook)
3. Check `TagProcessingMode` - if using `AsLifecycleStage`, hooks fire later

### Hook firing multiple times?

This is expected. `ProcessTagsAsync` is called from multiple places (Dispatcher, ReceptorInvoker, PerspectiveWorker) at different lifecycle stages. Use `context.Stage` to filter:

```csharp
if (context.Stage != LifecycleStage.PostPerspectiveInline)
  return null; // Skip — only act when data is committed
```

### Multi-assembly issues?

1. Ensure both assemblies reference `Whizbang.Generators`
2. Check that `[ModuleInitializer]` is running (add Console.WriteLine to verify)
3. Contracts assembly should use priority 100, services priority 1000
