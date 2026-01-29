using Whizbang.Migrate.Analysis;

namespace Whizbang.Migrate.Tests.Analysis;

/// <summary>
/// Tests for the StreamIdDetector that scans event types for potential stream ID properties.
/// </summary>
/// <tests>Whizbang.Migrate/Analysis/StreamIdDetector.cs:*</tests>
public class StreamIdDetectorTests {
  [Test]
  public async Task DetectAsync_FindsStreamIdProperty_Async() {
    // Arrange
    var sourceCode = """
      public record OrderCreated(Guid StreamId, string CustomerId);
      public record OrderUpdated(Guid StreamId, string Description);
      public record OrderCancelled(Guid StreamId, string Reason);
      """;

    // Act
    var result = await StreamIdDetector.DetectAsync(sourceCode, "Events/OrderEvents.cs");

    // Assert
    await Assert.That(result.DetectedProperties).Count().IsEqualTo(1);
    await Assert.That(result.DetectedProperties[0].PropertyName).IsEqualTo("StreamId");
    await Assert.That(result.DetectedProperties[0].OccurrenceCount).IsEqualTo(3);
    await Assert.That(result.MostCommon?.PropertyName).IsEqualTo("StreamId");
  }

  [Test]
  public async Task DetectAsync_FindsAggregateIdProperty_Async() {
    // Arrange
    var sourceCode = """
      public record ItemCreated(Guid AggregateId, string Name);
      public record ItemUpdated(Guid AggregateId, string Name);
      """;

    // Act
    var result = await StreamIdDetector.DetectAsync(sourceCode, "Events/ItemEvents.cs");

    // Assert
    await Assert.That(result.DetectedProperties).Count().IsEqualTo(1);
    await Assert.That(result.DetectedProperties[0].PropertyName).IsEqualTo("AggregateId");
    await Assert.That(result.DetectedProperties[0].OccurrenceCount).IsEqualTo(2);
  }

  [Test]
  public async Task DetectAsync_FindsDomainSpecificIdProperty_Async() {
    // Arrange
    // Static methods - no detector instance needed
    var sourceCode = """
      public record OrderCreated(Guid OrderId, string CustomerId);
      public record OrderShipped(Guid OrderId, DateTimeOffset ShippedAt);
      public record OrderDelivered(Guid OrderId, DateTimeOffset DeliveredAt);
      """;

    // Act
    var result = await StreamIdDetector.DetectAsync(sourceCode, "Events/OrderEvents.cs");

    // Assert
    await Assert.That(result.DetectedProperties).Count().IsEqualTo(1);
    await Assert.That(result.DetectedProperties[0].PropertyName).IsEqualTo("OrderId");
    await Assert.That(result.DetectedProperties[0].OccurrenceCount).IsEqualTo(3);
  }

  [Test]
  public async Task DetectAsync_RanksMultiplePropertiesByOccurrence_Async() {
    // Arrange
    // Static methods - no detector instance needed
    var sourceCode = """
      public record OrderCreated(Guid OrderId, string CustomerId);
      public record OrderUpdated(Guid OrderId, string Description);
      public record OrderShipped(Guid OrderId, string Carrier);
      public record OrderCancelled(Guid StreamId, string Reason);
      """;

    // Act
    var result = await StreamIdDetector.DetectAsync(sourceCode, "Events/OrderEvents.cs");

    // Assert
    await Assert.That(result.DetectedProperties).Count().IsEqualTo(2);
    await Assert.That(result.MostCommon?.PropertyName).IsEqualTo("OrderId");
    await Assert.That(result.MostCommon?.OccurrenceCount).IsEqualTo(3);
    await Assert.That(result.DetectedProperties[1].PropertyName).IsEqualTo("StreamId");
    await Assert.That(result.DetectedProperties[1].OccurrenceCount).IsEqualTo(1);
  }

  [Test]
  public async Task DetectAsync_IgnoresNonGuidIdProperties_Async() {
    // Arrange
    // Static methods - no detector instance needed
    var sourceCode = """
      public record OrderCreated(string OrderId, int CustomerId);
      public record OrderUpdated(string OrderId, string Description);
      """;

    // Act
    var result = await StreamIdDetector.DetectAsync(sourceCode, "Events/OrderEvents.cs");

    // Assert
    await Assert.That(result.DetectedProperties).IsEmpty();
    await Assert.That(result.MostCommon).IsNull();
  }

  [Test]
  public async Task DetectAsync_RecognizesStronglyTypedIds_Async() {
    // Arrange
    // Static methods - no detector instance needed
    var sourceCode = """
      public record OrderCreated(OrderId OrderId, string CustomerId);
      public record OrderUpdated(OrderId OrderId, string Description);

      public readonly record struct OrderId(Guid Value);
      """;

    // Act
    var result = await StreamIdDetector.DetectAsync(sourceCode, "Events/OrderEvents.cs");

    // Assert
    await Assert.That(result.DetectedProperties).Count().IsEqualTo(1);
    await Assert.That(result.DetectedProperties[0].PropertyName).IsEqualTo("OrderId");
    await Assert.That(result.DetectedProperties[0].IsStronglyTyped).IsTrue();
  }

  [Test]
  public async Task DetectAsync_SkipsNonEventRecords_Async() {
    // Arrange
    // Static methods - no detector instance needed
    var sourceCode = """
      public class OrderService {
        private readonly Guid _orderId;
        public Guid OrderId => _orderId;
      }

      public record OrderCreated(Guid OrderId, string CustomerId);
      """;

    // Act
    var result = await StreamIdDetector.DetectAsync(sourceCode, "Events/OrderEvents.cs");

    // Assert
    // Should only count from the event record, not the service class
    await Assert.That(result.DetectedProperties).Count().IsEqualTo(1);
    await Assert.That(result.DetectedProperties[0].OccurrenceCount).IsEqualTo(1);
  }

  [Test]
  public async Task DetectAsync_HandlesIdPropertyWithExplicitGuidType_Async() {
    // Arrange
    // Static methods - no detector instance needed
    var sourceCode = """
      public record OrderCreated(System.Guid Id, string CustomerId);
      public record OrderUpdated(System.Guid Id, string Description);
      """;

    // Act
    var result = await StreamIdDetector.DetectAsync(sourceCode, "Events/OrderEvents.cs");

    // Assert
    await Assert.That(result.DetectedProperties).Count().IsEqualTo(1);
    await Assert.That(result.DetectedProperties[0].PropertyName).IsEqualTo("Id");
    await Assert.That(result.DetectedProperties[0].OccurrenceCount).IsEqualTo(2);
  }

  [Test]
  public async Task DetectAsync_ReturnsEmptyForNoEvents_Async() {
    // Arrange
    // Static methods - no detector instance needed
    var sourceCode = """
      public class OrderService {
        public void ProcessOrder() { }
      }
      """;

    // Act
    var result = await StreamIdDetector.DetectAsync(sourceCode, "Services/OrderService.cs");

    // Assert
    await Assert.That(result.DetectedProperties).IsEmpty();
    await Assert.That(result.MostCommon).IsNull();
    await Assert.That(result.HasDetections).IsFalse();
  }

  [Test]
  public async Task DetectProjectAsync_AggregatesAcrossMultipleFiles_Async() {
    // Arrange - this would scan a directory, but we'll test the aggregation logic
    // Static methods - no detector instance needed
    var files = new Dictionary<string, string> {
      ["Events/OrderEvents.cs"] = """
        public record OrderCreated(Guid OrderId, string CustomerId);
        public record OrderUpdated(Guid OrderId, string Description);
        """,
      ["Events/CustomerEvents.cs"] = """
        public record CustomerCreated(Guid CustomerId, string Name);
        public record CustomerUpdated(Guid CustomerId, string Name);
        """,
      ["Events/InventoryEvents.cs"] = """
        public record ItemCreated(Guid OrderId, string Name);
        """
    };

    // Act
    var result = await StreamIdDetector.DetectFromMultipleSourcesAsync(files);

    // Assert
    await Assert.That(result.DetectedProperties).Count().IsEqualTo(2);
    await Assert.That(result.MostCommon?.PropertyName).IsEqualTo("OrderId");
    await Assert.That(result.MostCommon?.OccurrenceCount).IsEqualTo(3);
    await Assert.That(result.DetectedProperties.Single(p => p.PropertyName == "CustomerId").OccurrenceCount)
        .IsEqualTo(2);
  }
}
