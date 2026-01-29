using Whizbang.Migrate.Analysis;

namespace Whizbang.Migrate.Tests.Analysis;

/// <summary>
/// Tests for the DomainOwnershipDetector that scans source code for domain patterns.
/// </summary>
/// <tests>Whizbang.Migrate/Analysis/DomainOwnershipDetector.cs:*</tests>
public class DomainOwnershipDetectorTests {
  [Test]
  public async Task DetectAsync_FindsDomainsFromHierarchicalNamespaces_Async() {
    // Arrange - Hierarchical: MyApp.Orders.Events → "orders"
    var sourceCode = """
      namespace MyApp.Orders.Events;

      public record OrderCreated(Guid OrderId, string CustomerId);
      public record OrderUpdated(Guid OrderId, string Description);
      """;

    // Act
    var result = await DomainOwnershipDetector.DetectAsync(sourceCode, "Events/OrderEvents.cs");

    // Assert
    await Assert.That(result.DetectedDomains).Count().IsEqualTo(1);
    await Assert.That(result.DetectedDomains[0].DomainName).IsEqualTo("orders");
  }

  [Test]
  public async Task DetectAsync_FindsDomainsFromFlatNamespaces_Async() {
    // Arrange - Flat: MyApp.Contracts.Commands.CreateOrder → extract from type name
    var sourceCode = """
      namespace MyApp.Contracts.Commands;

      public record CreateOrder(Guid OrderId, string CustomerId);
      public record CancelOrder(Guid OrderId, string Reason);
      """;

    // Act
    var result = await DomainOwnershipDetector.DetectAsync(sourceCode, "Commands/OrderCommands.cs");

    // Assert
    await Assert.That(result.DetectedDomains).Count().IsEqualTo(1);
    await Assert.That(result.DetectedDomains[0].DomainName).IsEqualTo("order");
  }

  [Test]
  public async Task DetectAsync_FindsMultipleDomainsFromSingleFile_Async() {
    // Arrange
    var sourceCode = """
      namespace MyApp.Contracts.Events;

      public record OrderCreated(Guid OrderId);
      public record CustomerCreated(Guid CustomerId);
      public record InventoryReserved(Guid ItemId);
      """;

    // Act
    var result = await DomainOwnershipDetector.DetectAsync(sourceCode, "Events.cs");

    // Assert
    await Assert.That(result.DetectedDomains).Count().IsEqualTo(3);
    await Assert.That(result.DetectedDomains.Select(d => d.DomainName))
        .Contains("order");
    await Assert.That(result.DetectedDomains.Select(d => d.DomainName))
        .Contains("customer");
    await Assert.That(result.DetectedDomains.Select(d => d.DomainName))
        .Contains("inventory");
  }

  [Test]
  public async Task DetectAsync_RanksDomainsByOccurrence_Async() {
    // Arrange
    var sourceCode = """
      namespace MyApp.Contracts.Events;

      public record OrderCreated(Guid OrderId);
      public record OrderUpdated(Guid OrderId);
      public record OrderShipped(Guid OrderId);
      public record CustomerCreated(Guid CustomerId);
      """;

    // Act
    var result = await DomainOwnershipDetector.DetectAsync(sourceCode, "Events.cs");

    // Assert
    await Assert.That(result.MostCommon?.DomainName).IsEqualTo("order");
    await Assert.That(result.MostCommon?.OccurrenceCount).IsEqualTo(3);
  }

  [Test]
  public async Task DetectAsync_ExtractsDomainFromNamespaceSegment_Async() {
    // Arrange - Second-to-last segment when pattern detected
    var sourceCode = """
      namespace Company.Ecommerce.Orders.Events;

      public record OrderPlaced(Guid OrderId);
      """;

    // Act
    var result = await DomainOwnershipDetector.DetectAsync(sourceCode, "Events.cs");

    // Assert
    await Assert.That(result.DetectedDomains[0].DomainName).IsEqualTo("orders");
    await Assert.That(result.DetectedDomains[0].FromNamespace).IsTrue();
  }

  [Test]
  public async Task DetectAsync_ExtractsDomainFromTypeName_WhenNamespaceIsGeneric_Async() {
    // Arrange - Namespace is generic (Contracts), extract from type name
    var sourceCode = """
      namespace MyApp.Contracts.Events;

      public record PaymentProcessed(Guid PaymentId, decimal Amount);
      public record PaymentRefunded(Guid PaymentId, decimal Amount);
      """;

    // Act
    var result = await DomainOwnershipDetector.DetectAsync(sourceCode, "Events.cs");

    // Assert
    await Assert.That(result.DetectedDomains[0].DomainName).IsEqualTo("payment");
    await Assert.That(result.DetectedDomains[0].FromTypeName).IsTrue();
  }

  [Test]
  public async Task DetectAsync_NormalizesToLowercase_Async() {
    // Arrange
    var sourceCode = """
      namespace MyApp.ORDERS.Events;

      public record OrderCreated(Guid OrderId);
      """;

    // Act
    var result = await DomainOwnershipDetector.DetectAsync(sourceCode, "Events.cs");

    // Assert
    await Assert.That(result.DetectedDomains[0].DomainName).IsEqualTo("orders");
  }

  [Test]
  public async Task DetectAsync_HandlesEmptySourceCode_Async() {
    // Arrange
    var sourceCode = "";

    // Act
    var result = await DomainOwnershipDetector.DetectAsync(sourceCode, "Empty.cs");

    // Assert
    await Assert.That(result.DetectedDomains).IsEmpty();
    await Assert.That(result.HasDetections).IsFalse();
  }

  [Test]
  public async Task DetectFromMultipleSourcesAsync_AggregatesAcrossFiles_Async() {
    // Arrange
    var files = new Dictionary<string, string> {
      ["Orders/OrderEvents.cs"] = """
        namespace MyApp.Orders.Events;
        public record OrderCreated(Guid OrderId);
        public record OrderUpdated(Guid OrderId);
        """,
      ["Inventory/InventoryEvents.cs"] = """
        namespace MyApp.Inventory.Events;
        public record ItemReserved(Guid ItemId);
        """,
      ["Orders/OrderCommands.cs"] = """
        namespace MyApp.Orders.Commands;
        public record CreateOrder(Guid OrderId);
        """
    };

    // Act
    var result = await DomainOwnershipDetector.DetectFromMultipleSourcesAsync(files);

    // Assert
    await Assert.That(result.DetectedDomains).Count().IsEqualTo(2);
    await Assert.That(result.MostCommon?.DomainName).IsEqualTo("orders");
    await Assert.That(result.MostCommon?.OccurrenceCount).IsEqualTo(3);
  }

  [Test]
  public async Task DetectAsync_SkipsGenericNamespaceSegments_Async() {
    // Arrange - Skip segments like "Contracts", "Commands", "Events", "Queries"
    var sourceCode = """
      namespace MyApp.Contracts.Commands;

      public record ProcessPayment(Guid PaymentId);
      """;

    // Act
    var result = await DomainOwnershipDetector.DetectAsync(sourceCode, "Commands.cs");

    // Assert
    // Should extract "payment" from type name, not "contracts" or "commands"
    await Assert.That(result.DetectedDomains[0].DomainName).IsEqualTo("payment");
  }

  [Test]
  public async Task DetectAsync_RecognizesCommandSuffix_Async() {
    // Arrange
    var sourceCode = """
      namespace MyApp.Contracts;

      public record PlaceOrderCommand(Guid OrderId);
      public record CancelOrderCommand(Guid OrderId);
      """;

    // Act
    var result = await DomainOwnershipDetector.DetectAsync(sourceCode, "Commands.cs");

    // Assert
    await Assert.That(result.DetectedDomains[0].DomainName).IsEqualTo("order");
    await Assert.That(result.DetectedDomains[0].OccurrenceCount).IsEqualTo(2);
  }

  [Test]
  public async Task DetectAsync_RecognizesEventSuffix_Async() {
    // Arrange
    var sourceCode = """
      namespace MyApp.Contracts;

      public record OrderPlacedEvent(Guid OrderId);
      public record OrderShippedEvent(Guid OrderId);
      """;

    // Act
    var result = await DomainOwnershipDetector.DetectAsync(sourceCode, "Events.cs");

    // Assert
    await Assert.That(result.DetectedDomains[0].DomainName).IsEqualTo("order");
    await Assert.That(result.DetectedDomains[0].OccurrenceCount).IsEqualTo(2);
  }

  [Test]
  public async Task DetectAsync_HandlesPastTenseEventNames_Async() {
    // Arrange - Names like OrderCreated, OrderShipped (domain = order)
    var sourceCode = """
      namespace MyApp.Events;

      public record OrderCreated(Guid OrderId);
      public record OrderShipped(Guid OrderId);
      public record OrderDelivered(Guid OrderId);
      """;

    // Act
    var result = await DomainOwnershipDetector.DetectAsync(sourceCode, "Events.cs");

    // Assert
    await Assert.That(result.DetectedDomains[0].DomainName).IsEqualTo("order");
    await Assert.That(result.DetectedDomains[0].OccurrenceCount).IsEqualTo(3);
  }
}
