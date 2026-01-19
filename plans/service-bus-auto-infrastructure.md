# Service Bus Infrastructure Auto-Discovery & Provisioning

**Status**: 🔴 Planning
**Phase**: Design
**Started**: 2025-12-01
**Target**: Phase 1 (Core Feature)

---

## Overview

Automatically discover, provision, and configure Azure Service Bus topics and subscriptions for Whizbang applications, with intelligent support for both production (auto-create via Azure Management API) and development (generate Aspire configuration).

---

## Problem Statement

**Current Pain Points:**
1. Manual synchronization between application code and Aspire AppHost configuration
2. No automatic provisioning in production environments
3. Developers must manually track which topics/subscriptions each service needs
4. Easy to get out of sync when adding new event subscriptions

**User Story:**
> As a developer, I want my Whizbang application to automatically discover what Service Bus topics/subscriptions it needs, create them in production if I have permissions, and tell me what Aspire configuration to add in development.

---

## Design

### Architecture

```
┌─────────────────────────────────────────────────────────────┐
│ Application Startup                                          │
│                                                              │
│  ServiceBusInfrastructureInitializer (IHostedService)       │
│         │                                                    │
│         ├─> Auto-discover requirements                      │
│         │   from ServiceBusConsumerOptions                  │
│         │                                                    │
│         ├─> Production Mode?                                │
│         │   ├─> Yes: Use IAzureServiceBusManager           │
│         │   │        to create topics/subscriptions         │
│         │   │        via Azure Management API               │
│         │   │                                               │
│         │   └─> No (Dev): Use AspireConfigurationGenerator  │
│         │                 to log C# code for AppHost        │
│         │                                                    │
│         └─> Report results via ILogger                      │
└─────────────────────────────────────────────────────────────┘
```

### Core Components

#### 1. TopicRequirement (Value Object)
```csharp
namespace Whizbang.Core.Transports.AzureServiceBus;

/// <summary>
/// Represents a required Service Bus topic and subscription pair.
/// </summary>
public sealed record TopicRequirement(
    string TopicName,
    string SubscriptionName
);
```

#### 2. ServiceBusInfrastructureOptions (Configuration)
```csharp
namespace Whizbang.Core.Transports.AzureServiceBus;

public class ServiceBusInfrastructureOptions
{
    /// <summary>
    /// Name of this service (used for generating unique subscription names).
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// Explicitly configured topic requirements.
    /// If empty, will auto-discover from ServiceBusConsumerOptions.
    /// </summary>
    public List<TopicRequirement> RequiredTopics { get; set; } = new();

    /// <summary>
    /// In production, automatically create topics/subscriptions via Azure Management API.
    /// Requires appropriate Azure permissions.
    /// </summary>
    public bool AutoCreateInProduction { get; set; } = true;

    /// <summary>
    /// In development, generate and log Aspire AppHost configuration code.
    /// </summary>
    public bool GenerateAspireConfigInDev { get; set; } = true;

    /// <summary>
    /// Fail startup if topics/subscriptions cannot be created in production.
    /// </summary>
    public bool FailOnProvisioningError { get; set; } = false;
}
```

#### 3. IAzureServiceBusManager (Interface)
```csharp
namespace Whizbang.Core.Transports.AzureServiceBus;

/// <summary>
/// Manages Azure Service Bus topics and subscriptions via Azure Management API.
/// </summary>
public interface IAzureServiceBusManager
{
    /// <summary>
    /// Ensures the specified topic exists, creating it if necessary.
    /// </summary>
    Task<bool> EnsureTopicExistsAsync(string topicName, CancellationToken ct = default);

    /// <summary>
    /// Ensures the specified subscription exists on a topic, creating it if necessary.
    /// </summary>
    Task<bool> EnsureSubscriptionExistsAsync(
        string topicName,
        string subscriptionName,
        CancellationToken ct = default);

    /// <summary>
    /// Checks if management operations are available (requires permissions).
    /// </summary>
    Task<bool> CanManageEntitiesAsync(CancellationToken ct = default);
}
```

#### 4. AzureServiceBusManager (Implementation)
Uses `Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient`:
- CreateTopicIfNotExistsAsync()
- CreateSubscriptionIfNotExistsAsync()
- Handles Azure exceptions gracefully
- Supports retry logic

#### 5. AspireConfigurationGenerator
```csharp
namespace Whizbang.Core.Transports.AzureServiceBus;

/// <summary>
/// Generates Aspire AppHost configuration code for Service Bus topics.
/// </summary>
public static class AspireConfigurationGenerator
{
    public static string GenerateAppHostCode(
        IEnumerable<TopicRequirement> requirements,
        string? serviceName = null)
    {
        // Returns formatted C# code string
    }
}
```

**Output Format:**
```csharp
// === Whizbang Service Bus Configuration ===
// Add this to your AppHost Program.cs:

// Configure topics for bff service
var productsTopic = serviceBus.AddServiceBusTopic("products");
productsTopic.AddServiceBusSubscription("bff-products");

var ordersTopic = serviceBus.AddServiceBusTopic("orders");
ordersTopic.AddServiceBusSubscription("bff-orders");

// ============================================
```

#### 6. ServiceBusInfrastructureInitializer (IHostedService)
```csharp
namespace Whizbang.Core.Transports.AzureServiceBus;

/// <summary>
/// Background service that initializes Service Bus infrastructure on startup.
/// </summary>
public class ServiceBusInfrastructureInitializer : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 1. Auto-discover requirements if not explicitly configured
        // 2. If Production + AutoCreateInProduction: Use IAzureServiceBusManager
        // 3. If Development + GenerateAspireConfigInDev: Log Aspire code
        // 4. Report results via ILogger
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

#### 7. Extension Methods
```csharp
namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceBusInfrastructureExtensions
{
    /// <summary>
    /// Adds Whizbang Service Bus infrastructure auto-discovery and provisioning.
    /// </summary>
    public static IServiceCollection AddWhizbangServiceBusInfrastructure(
        this IServiceCollection services,
        Action<ServiceBusInfrastructureOptions>? configure = null)
    {
        var options = new ServiceBusInfrastructureOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IAzureServiceBusManager, AzureServiceBusManager>();
        services.AddHostedService<ServiceBusInfrastructureInitializer>();

        return services;
    }
}
```

---

## Usage Examples

### Minimal Usage (Auto-Discovery)
```csharp
// Program.cs - BFF API
var consumerOptions = new ServiceBusConsumerOptions();
consumerOptions.Subscriptions.Add(new TopicSubscription("products", "bff-products"));
consumerOptions.Subscriptions.Add(new TopicSubscription("orders", "bff-orders"));
builder.Services.AddSingleton(consumerOptions);

// Auto-discover from consumerOptions
builder.Services.AddWhizbangServiceBusInfrastructure();
```

### Explicit Configuration
```csharp
builder.Services.AddWhizbangServiceBusInfrastructure(options => {
    options.ServiceName = "bff";
    options.RequiredTopics = new List<TopicRequirement>
    {
        new("products", "bff-products"),
        new("orders", "bff-orders")
    };
    options.AutoCreateInProduction = true;
    options.GenerateAspireConfigInDev = true;
    options.FailOnProvisioningError = false;
});
```

### Development Output
```
info: Whizbang.ServiceBus[0]
      Service Bus Infrastructure Discovery
      Service: bff
      Discovered 4 topic/subscription requirements:
        - products/bff-products
        - orders/bff-orders
        - payments/bff-payments
        - shipping/bff-shipping

info: Whizbang.ServiceBus[0]
      === Aspire AppHost Configuration ===
      Add this to ECommerce.AppHost/Program.cs:

      // Service Bus topics for bff service
      var productsTopic = serviceBus.AddServiceBusTopic("products");
      productsTopic.AddServiceBusSubscription("bff-products");

      var ordersTopic = serviceBus.AddServiceBusTopic("orders");
      ordersTopic.AddServiceBusSubscription("bff-orders");

      var paymentsTopic = serviceBus.AddServiceBusTopic("payments");
      paymentsTopic.AddServiceBusSubscription("bff-payments");

      var shippingTopic = serviceBus.AddServiceBusTopic("shipping");
      shippingTopic.AddServiceBusSubscription("bff-shipping");

      ========================================
```

### Production Output
```
info: Whizbang.ServiceBus[0]
      Service Bus Infrastructure Discovery
      Service: bff
      Discovered 4 topic/subscription requirements

info: Whizbang.ServiceBus[0]
      Creating Service Bus infrastructure in Azure...
      ✓ Topic 'products' exists
      ✓ Subscription 'bff-products' exists on 'products'
      ✓ Topic 'orders' exists
      ✓ Subscription 'bff-orders' exists on 'orders'
      ... (4/4 verified or created)

info: Whizbang.ServiceBus[0]
      Service Bus infrastructure ready
```

---

## Implementation Plan (TDD)

### Phase 1: Core Value Objects & Options (RED → GREEN → REFACTOR)

**Test File**: `Whizbang.Core.Tests/Transports/AzureServiceBus/TopicRequirementTests.cs`

```csharp
[Test]
public async Task TopicRequirement_Constructor_SetsPropertiesAsync()
{
    // Arrange & Act
    var requirement = new TopicRequirement("orders", "bff-orders");

    // Assert
    await Assert.That(requirement.TopicName).IsEqualTo("orders");
    await Assert.That(requirement.SubscriptionName).IsEqualTo("bff-orders");
}

[Test]
public async Task TopicRequirement_WithSameValues_AreEqualAsync()
{
    // Value equality for caching
    var req1 = new TopicRequirement("orders", "bff-orders");
    var req2 = new TopicRequirement("orders", "bff-orders");

    await Assert.That(req1).IsEqualTo(req2);
}
```

**Test File**: `Whizbang.Core.Tests/Transports/AzureServiceBus/ServiceBusInfrastructureOptionsTests.cs`

```csharp
[Test]
public async Task ServiceBusInfrastructureOptions_DefaultValues_AreSetAsync()
{
    var options = new ServiceBusInfrastructureOptions();

    await Assert.That(options.ServiceName).IsEqualTo(string.Empty);
    await Assert.That(options.RequiredTopics).IsEmpty();
    await Assert.That(options.AutoCreateInProduction).IsTrue();
    await Assert.That(options.GenerateAspireConfigInDev).IsTrue();
    await Assert.That(options.FailOnProvisioningError).IsFalse();
}
```

### Phase 2: Aspire Configuration Generator (RED → GREEN → REFACTOR)

**Test File**: `Whizbang.Core.Tests/Transports/AzureServiceBus/AspireConfigurationGeneratorTests.cs`

```csharp
[Test]
public async Task GenerateAppHostCode_WithMultipleRequirements_GeneratesCorrectCodeAsync()
{
    var requirements = new[]
    {
        new TopicRequirement("products", "bff-products"),
        new TopicRequirement("orders", "bff-orders")
    };

    var code = AspireConfigurationGenerator.GenerateAppHostCode(requirements, "bff");

    await Assert.That(code).Contains("var productsTopic = serviceBus.AddServiceBusTopic(\"products\");");
    await Assert.That(code).Contains("productsTopic.AddServiceBusSubscription(\"bff-products\");");
}

[Test]
public async Task GenerateAppHostCode_GroupsByTopic_WhenMultipleSubscriptionsAsync()
{
    var requirements = new[]
    {
        new TopicRequirement("orders", "payment-service"),
        new TopicRequirement("orders", "shipping-service"),
        new TopicRequirement("orders", "bff-orders")
    };

    var code = AspireConfigurationGenerator.GenerateAppHostCode(requirements);

    // Should only create ordersTopic once
    await Assert.That(code.Split("AddServiceBusTopic(\"orders\")").Length).IsEqualTo(2);
    await Assert.That(code).Contains("AddServiceBusSubscription(\"payment-service\")");
    await Assert.That(code).Contains("AddServiceBusSubscription(\"shipping-service\")");
}
```

### Phase 3: Azure Management (RED → GREEN → REFACTOR)

**Test File**: `Whizbang.Core.Tests/Transports/AzureServiceBus/AzureServiceBusManagerTests.cs`

Use mocking (Rocks) for Azure SDK:

```csharp
[Test]
public async Task EnsureTopicExistsAsync_WhenTopicExists_ReturnsTrueAsync()
{
    // Arrange
    var mockAdminClient = Rock.Create<ServiceBusAdministrationClient>();
    mockAdminClient.Methods()
        .GetTopicAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult(new TopicProperties("orders")));

    var manager = new AzureServiceBusManager(mockAdminClient.Instance());

    // Act
    var result = await manager.EnsureTopicExistsAsync("orders");

    // Assert
    await Assert.That(result).IsTrue();
}

[Test]
public async Task EnsureTopicExistsAsync_WhenTopicNotExists_CreatesAndReturnsTrueAsync()
{
    // Test creation flow
}
```

### Phase 4: Hosted Service (RED → GREEN → REFACTOR)

**Test File**: `Whizbang.Core.Tests/Transports/AzureServiceBus/ServiceBusInfrastructureInitializerTests.cs`

```csharp
[Test]
public async Task StartAsync_InProduction_CallsAzureManagerAsync()
{
    // Arrange
    var mockEnv = CreateMockProductionEnvironment();
    var mockManager = Rock.Create<IAzureServiceBusManager>();
    var options = new ServiceBusInfrastructureOptions
    {
        RequiredTopics = new List<TopicRequirement>
        {
            new("orders", "bff-orders")
        },
        AutoCreateInProduction = true
    };

    var initializer = new ServiceBusInfrastructureInitializer(
        mockEnv,
        mockManager.Instance(),
        options,
        Mock.Of<ILogger<ServiceBusInfrastructureInitializer>>()
    );

    // Act
    await initializer.StartAsync(CancellationToken.None);

    // Assert
    mockManager.Verify(m => m.EnsureTopicExistsAsync("orders", Arg.Any<CancellationToken>()));
}

[Test]
public async Task StartAsync_InDevelopment_LogsAspireConfigurationAsync()
{
    // Test logging behavior
}
```

### Phase 5: Auto-Discovery (RED → GREEN → REFACTOR)

**Test File**: `Whizbang.Core.Tests/Transports/AzureServiceBus/ServiceBusInfrastructureAutoDiscoveryTests.cs`

```csharp
[Test]
public async Task AutoDiscover_FromServiceBusConsumerOptions_ExtractsRequirementsAsync()
{
    var consumerOptions = new ServiceBusConsumerOptions();
    consumerOptions.Subscriptions.Add(new TopicSubscription("products", "bff-products"));
    consumerOptions.Subscriptions.Add(new TopicSubscription("orders", "bff-orders"));

    var discovered = ServiceBusInfrastructureDiscovery.DiscoverFromConsumerOptions(consumerOptions);

    await Assert.That(discovered).HasCount().EqualTo(2);
    await Assert.That(discovered).Contains(new TopicRequirement("products", "bff-products"));
    await Assert.That(discovered).Contains(new TopicRequirement("orders", "bff-orders"));
}
```

---

## Dependencies

**New NuGet Package:**
- `Azure.Messaging.ServiceBus.Administration` (for management API in production)

**Existing Dependencies:**
- `Microsoft.Extensions.Hosting.Abstractions` (IHostedService)
- `Microsoft.Extensions.Logging.Abstractions`
- `Azure.Messaging.ServiceBus` (already present for transport)

---

## Testing Strategy

### Unit Tests
- TopicRequirement value equality
- ServiceBusInfrastructureOptions defaults
- AspireConfigurationGenerator code generation
- Auto-discovery logic from ServiceBusConsumerOptions

### Integration Tests (with Mocks)
- AzureServiceBusManager with mocked Azure SDK
- ServiceBusInfrastructureInitializer with mocked dependencies
- Extension method registration

### Manual Integration Testing
- Run BFF.API in development → verify Aspire code is logged
- Run BFF.API in production (or staging) → verify Azure entities created
- Verify existing topics/subscriptions are not recreated

---

## Future Enhancements (Out of Scope for Phase 1)

1. **Publisher Discovery**: Auto-discover topics from `ITransport.PublishAsync` calls
2. **Subscription Rules**: Configure message filtering rules automatically
3. **Dead Letter Queue**: Auto-configure DLQ settings
4. **Metrics**: Report provisioning metrics to telemetry
5. **Interactive Mode**: Prompt developer to approve/reject generated configuration
6. **AppHost File Modification**: Automatically write to AppHost Program.cs (risky!)

---

## Success Criteria

✅ Services can auto-discover their Service Bus requirements
✅ Production: Automatically create topics/subscriptions if permitted
✅ Development: Generate valid Aspire configuration code
✅ All components have comprehensive unit tests (TDD)
✅ Works seamlessly with existing ECommerce sample
✅ Documentation updated with usage examples

---

## Changelog

- **2025-12-01**: Initial design document created
