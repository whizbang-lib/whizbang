using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Policies.Tests;

/// <summary>
/// Tests for PolicyContext - the universal context that flows through the entire execution pipeline.
/// PolicyContext provides message information, runtime context, and helpers for policy evaluation.
/// </summary>
public class PolicyContextTests {
  // Test message types
  private record TestMessage(string Value);

  public record CreateOrder {
    [AggregateId]
    public Guid OrderId { get; init; }
    public string ProductName { get; init; } = string.Empty;

    public CreateOrder(Guid orderId, string productName) {
      OrderId = orderId;
      ProductName = productName;
    }
  }

  private record OrderCreated(Guid OrderId, DateTimeOffset CreatedAt);

  // Test types for [AggregateId] attribute tests (must be public for generator)
  public record CreateProduct {
    [AggregateId]
    public Guid ProductId { get; init; }
    public string Name { get; init; } = string.Empty;

    public CreateProduct(Guid productId, string name) {
      ProductId = productId;
      Name = name;
    }
  }

  public record MessageWithoutAttribute(string Value);

  [Test]
  public async Task Constructor_InitializesWithMessage_SetsMessageAndMessageTypeAsync() {
    // Arrange
    var message = new CreateOrder(Guid.NewGuid(), "Widget");

    // Act
    var context = new PolicyContext(message);

    // Assert
    await Assert.That(context.Message).IsEqualTo(message);
    await Assert.That(context.MessageType).IsEqualTo(typeof(CreateOrder));
  }

  [Test]
  public async Task Constructor_InitializesTrail_CreatesEmptyDecisionTrailAsync() {
    // Arrange
    var message = new TestMessage("test");

    // Act
    var context = new PolicyContext(message);

    // Assert
    await Assert.That(context.Trail).IsNotNull();
    await Assert.That(context.Trail.Decisions).IsEmpty();
  }

  [Test]
  public async Task Constructor_WithEnvelope_SetsEnvelopeAsync() {
    // Arrange
    var message = new TestMessage("test");
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Topic = "test-topic",
      StreamKey = "test-stream"
    };

    // Act
    var context = new PolicyContext(message, envelope);

    // Assert
    await Assert.That(context.Envelope).IsEqualTo(envelope);
  }

  [Test]
  public async Task Constructor_WithServiceProvider_SetsServicesAsync() {
    // Arrange
    var services = new ServiceCollection()
        .AddSingleton<ITestService, TestService>()
        .BuildServiceProvider();
    var message = new TestMessage("test");

    // Act
    var context = new PolicyContext(message, services: services);

    // Assert
    await Assert.That(context.Services).IsEqualTo(services);
  }

  [Test]
  public async Task Constructor_WithEnvironment_SetsEnvironmentAsync() {
    // Arrange
    var message = new TestMessage("test");

    // Act
    var context = new PolicyContext(message, environment: "production");

    // Assert
    await Assert.That(context.Environment).IsEqualTo("production");
  }

  [Test]
  public async Task Constructor_SetsExecutionTime_ToApproximatelyNowAsync() {
    // Arrange
    var before = DateTimeOffset.UtcNow;
    var message = new TestMessage("test");

    // Act
    var context = new PolicyContext(message);
    var after = DateTimeOffset.UtcNow;

    // Assert
    await Assert.That(context.ExecutionTime).IsGreaterThanOrEqualTo(before);
    await Assert.That(context.ExecutionTime).IsLessThanOrEqualTo(after);
  }

  [Test]
  public async Task GetService_ReturnsService_WhenServiceProviderSetAsync() {
    // Arrange
    var services = new ServiceCollection()
        .AddSingleton<ITestService, TestService>()
        .BuildServiceProvider();
    var message = new TestMessage("test");
    var context = new PolicyContext(message, services: services);

    // Act
    var service = context.GetService<ITestService>();

    // Assert
    await Assert.That(service).IsNotNull();
    await Assert.That(service).IsTypeOf<TestService>();
  }

  [Test]
  public async Task GetService_ThrowsException_WhenServiceProviderNotSetAsync() {
    // Arrange
    var message = new TestMessage("test");
    var context = new PolicyContext(message);

    // Act & Assert
    await Assert.That(() => context.GetService<ITestService>())
        .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task GetMetadata_ReturnsValue_WhenKeyExistsAsync() {
    // Arrange
    var message = new TestMessage("test");
    var metadata = new Dictionary<string, object> {
      ["tenant"] = "acme-corp",
      ["priority"] = 5
    };
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Metadata = metadata
    };
    var context = new PolicyContext(message, envelope);

    // Act
    var tenant = context.GetMetadata("tenant");
    var priority = context.GetMetadata("priority");

    // Assert
    await Assert.That(tenant).IsEqualTo("acme-corp");
    await Assert.That(priority).IsEqualTo(5);
  }

  [Test]
  public async Task GetMetadata_ReturnsNull_WhenKeyDoesNotExistAsync() {
    // Arrange
    var message = new TestMessage("test");
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Metadata = new Dictionary<string, object>()
    };
    var context = new PolicyContext(message, envelope);

    // Act
    var result = context.GetMetadata("nonexistent");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetMetadata_ReturnsNull_WhenEnvelopeNotSetAsync() {
    // Arrange
    var message = new TestMessage("test");
    var context = new PolicyContext(message);

    // Act
    var result = context.GetMetadata("any-key");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task HasTag_ReturnsTrue_WhenTagExistsInMetadataAsync() {
    // Arrange
    var message = new TestMessage("test");
    var metadata = new Dictionary<string, object> {
      ["tags"] = new[] { "high-priority", "customer-vip", "region-us-west" }
    };
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Metadata = metadata
    };
    var context = new PolicyContext(message, envelope);

    // Act & Assert
    await Assert.That(context.HasTag("high-priority")).IsTrue();
    await Assert.That(context.HasTag("customer-vip")).IsTrue();
    await Assert.That(context.HasTag("region-us-west")).IsTrue();
  }

  [Test]
  public async Task HasTag_ReturnsFalse_WhenTagDoesNotExistAsync() {
    // Arrange
    var message = new TestMessage("test");
    var metadata = new Dictionary<string, object> {
      ["tags"] = new[] { "high-priority" }
    };
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Metadata = metadata
    };
    var context = new PolicyContext(message, envelope);

    // Act & Assert
    await Assert.That(context.HasTag("low-priority")).IsFalse();
  }

  [Test]
  public async Task HasTag_ReturnsFalse_WhenNoTagsInMetadataAsync() {
    // Arrange
    var message = new TestMessage("test");
    var context = new PolicyContext(message);

    // Act & Assert
    await Assert.That(context.HasTag("any-tag")).IsFalse();
  }

  [Test]
  public async Task HasFlag_ReturnsTrue_WhenFlagIsSetAsync() {
    // Arrange
    var message = new TestMessage("test");
    var metadata = new Dictionary<string, object> {
      ["flags"] = WhizbangFlags.LoadTesting | WhizbangFlags.VerboseLogging
    };
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Metadata = metadata
    };
    var context = new PolicyContext(message, envelope);

    // Act & Assert
    await Assert.That(context.HasFlag(WhizbangFlags.LoadTesting)).IsTrue();
    await Assert.That(context.HasFlag(WhizbangFlags.VerboseLogging)).IsTrue();
  }

  [Test]
  public async Task HasFlag_ReturnsFalse_WhenFlagIsNotSetAsync() {
    // Arrange
    var message = new TestMessage("test");
    var metadata = new Dictionary<string, object> {
      ["flags"] = WhizbangFlags.LoadTesting
    };
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Metadata = metadata
    };
    var context = new PolicyContext(message, envelope);

    // Act & Assert
    await Assert.That(context.HasFlag(WhizbangFlags.DryRun)).IsFalse();
  }

  [Test]
  public async Task HasFlag_ReturnsFalse_WhenNoFlagsInMetadataAsync() {
    // Arrange
    var message = new TestMessage("test");
    var context = new PolicyContext(message);

    // Act & Assert
    await Assert.That(context.HasFlag(WhizbangFlags.LoadTesting)).IsFalse();
  }

  [Test]
  public async Task MatchesAggregate_ReturnsTrue_WhenMessageIsForSpecifiedAggregateTypeAsync() {
    // Arrange
    var message = new CreateOrder(Guid.NewGuid(), "Widget");
    var context = new PolicyContext(message);

    // Act
    var matches = context.MatchesAggregate<Order>();

    // Assert
    await Assert.That(matches).IsTrue();
  }

  [Test]
  public async Task MatchesAggregate_ReturnsFalse_WhenMessageIsForDifferentAggregateTypeAsync() {
    // Arrange
    var message = new CreateOrder(Guid.NewGuid(), "Widget");
    var context = new PolicyContext(message);

    // Act
    var matches = context.MatchesAggregate<Customer>();

    // Assert
    await Assert.That(matches).IsFalse();
  }

  [Test]
  public async Task GetAggregateId_WithAggregateIdAttribute_UsesGeneratedExtractorAsync() {
    // RED PHASE: This test will FAIL until generator is implemented
    // Arrange
    var productId = Guid.NewGuid();
    var message = new CreateProduct(productId, "New Product");
    var context = new PolicyContext(message);

    // Act - Should use generated extractor, not reflection
    var aggregateId = context.GetAggregateId();

    // Assert
    await Assert.That(aggregateId).IsEqualTo(productId);
  }

  [Test]
  public async Task GetAggregateId_WithoutAggregateIdAttribute_ThrowsHelpfulExceptionAsync() {
    // RED PHASE: This test will FAIL until generator is implemented
    // Arrange
    var message = new MessageWithoutAttribute("test");
    var context = new PolicyContext(message);

    // Act & Assert
    var exception = await Assert.That(() => context.GetAggregateId())
        .Throws<InvalidOperationException>();

    await Assert.That(exception.Message).Contains("does not have a property marked with [AggregateId]");
  }

  [Test]
  public async Task GetAggregateId_ReturnsId_WhenMessageContainsAggregateIdAsync() {
    // Arrange
    var orderId = Guid.NewGuid();
    var message = new CreateOrder(orderId, "Widget");
    var context = new PolicyContext(message);

    // Act
    var aggregateId = context.GetAggregateId();

    // Assert
    await Assert.That(aggregateId).IsEqualTo(orderId);
  }

  [Test]
  public async Task GetAggregateId_ThrowsException_WhenMessageDoesNotContainAggregateIdAsync() {
    // Arrange
    var message = new TestMessage("test");
    var context = new PolicyContext(message);

    // Act & Assert
    await Assert.That(() => context.GetAggregateId())
        .Throws<InvalidOperationException>();
  }

  // Test helper types
  private interface ITestService { }
  private class TestService : ITestService { }

  // Mock aggregate types for testing MatchesAggregate
  private class Order { }
  private class Customer { }
}

/// <summary>
/// Placeholder for WhizbangFlags enum (will be implemented separately)
/// </summary>
[Flags]
public enum WhizbangFlags {
  None = 0,
  LoadTesting = 1 << 0,
  DryRun = 1 << 1,
  VerboseLogging = 1 << 4,
  VerboseOtel = 1 << 5,
  IgnoreTimeouts = 1 << 6,
  CursorMode = 1 << 7,
  Breakpoint = 1 << 8,
  Production = 1 << 15,
  Staging = 1 << 16,
  QA = 1 << 17,
  Migration = 1 << 18
}

/// <summary>
/// Placeholder for MessageEnvelope (will be implemented in observability tests)
/// Updated to implement IMessageEnvelope for PolicyContext compatibility.
/// </summary>
public class MessageEnvelope<TMessage> : IMessageEnvelope {
  public MessageId MessageId { get; init; }
  public TMessage Payload { get; init; } = default!;
  public string Topic { get; init; } = string.Empty;
  public string StreamKey { get; init; } = string.Empty;
  public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();

  // IMessageEnvelope implementation
  public List<MessageHop> Hops { get; init; } = new();
  public void AddHop(MessageHop hop) => Hops.Add(hop);
  public DateTimeOffset GetMessageTimestamp() => Hops.FirstOrDefault()?.Timestamp ?? DateTimeOffset.UtcNow;
  public CorrelationId? GetCorrelationId() => Hops.FirstOrDefault()?.CorrelationId;
  public MessageId? GetCausationId() => Hops.FirstOrDefault()?.CausationId;
  public object? GetMetadata(string key) => Metadata.TryGetValue(key, out var value) ? value : null;
}
