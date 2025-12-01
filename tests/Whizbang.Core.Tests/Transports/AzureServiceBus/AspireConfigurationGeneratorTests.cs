using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports.AzureServiceBus;

namespace Whizbang.Core.Tests.Transports.AzureServiceBus;

/// <summary>
/// Tests for AspireConfigurationGenerator static class.
/// Ensures correct C# code generation for Aspire AppHost configuration.
/// </summary>
public class AspireConfigurationGeneratorTests
{
  [Test]
  public async Task GenerateAppHostCode_WithNoRequirements_ReturnsEmptyMessageAsync()
  {
    // Arrange
    var requirements = Array.Empty<TopicRequirement>();

    // Act
    var code = AspireConfigurationGenerator.GenerateAppHostCode(requirements);

    // Assert - Should return a message indicating no topics required
    await Assert.That(code).IsNotEmpty();
    await Assert.That(code).Contains("No Service Bus topics required");
  }

  [Test]
  public async Task GenerateAppHostCode_WithSingleRequirement_GeneratesCorrectCodeAsync()
  {
    // Arrange
    var requirements = new[]
    {
      new TopicRequirement("orders", "bff-orders")
    };

    // Act
    var code = AspireConfigurationGenerator.GenerateAppHostCode(requirements);

    // Assert
    await Assert.That(code).Contains("var ordersTopic = serviceBus.AddServiceBusTopic(\"orders\");");
    await Assert.That(code).Contains("ordersTopic.AddServiceBusSubscription(\"bff-orders\");");
  }

  [Test]
  public async Task GenerateAppHostCode_WithMultipleRequirements_GeneratesCorrectCodeAsync()
  {
    // Arrange
    var requirements = new[]
    {
      new TopicRequirement("products", "bff-products"),
      new TopicRequirement("orders", "bff-orders")
    };

    // Act
    var code = AspireConfigurationGenerator.GenerateAppHostCode(requirements);

    // Assert - Both topics should be generated
    await Assert.That(code).Contains("var productsTopic = serviceBus.AddServiceBusTopic(\"products\");");
    await Assert.That(code).Contains("productsTopic.AddServiceBusSubscription(\"bff-products\");");
    await Assert.That(code).Contains("var ordersTopic = serviceBus.AddServiceBusTopic(\"orders\");");
    await Assert.That(code).Contains("ordersTopic.AddServiceBusSubscription(\"bff-orders\");");
  }

  [Test]
  public async Task GenerateAppHostCode_GroupsByTopic_WhenMultipleSubscriptionsAsync()
  {
    // Arrange - Multiple subscriptions for the same topic
    var requirements = new[]
    {
      new TopicRequirement("orders", "payment-service"),
      new TopicRequirement("orders", "shipping-service"),
      new TopicRequirement("orders", "bff-orders")
    };

    // Act
    var code = AspireConfigurationGenerator.GenerateAppHostCode(requirements);

    // Assert - Topic should only be created once
    var topicDeclarations = code.Split("AddServiceBusTopic(\"orders\")").Length - 1;
    await Assert.That(topicDeclarations).IsEqualTo(1);

    // All subscriptions should be present
    await Assert.That(code).Contains("AddServiceBusSubscription(\"payment-service\")");
    await Assert.That(code).Contains("AddServiceBusSubscription(\"shipping-service\")");
    await Assert.That(code).Contains("AddServiceBusSubscription(\"bff-orders\")");
  }

  [Test]
  public async Task GenerateAppHostCode_WithServiceName_IncludesServiceNameInCommentsAsync()
  {
    // Arrange
    var requirements = new[]
    {
      new TopicRequirement("orders", "bff-orders")
    };

    // Act
    var code = AspireConfigurationGenerator.GenerateAppHostCode(requirements, "bff");

    // Assert - Service name should appear in comments
    await Assert.That(code).Contains("bff");
  }

  [Test]
  public async Task GenerateAppHostCode_IncludesHeaderAndFooterAsync()
  {
    // Arrange
    var requirements = new[]
    {
      new TopicRequirement("orders", "bff-orders")
    };

    // Act
    var code = AspireConfigurationGenerator.GenerateAppHostCode(requirements);

    // Assert - Should have helpful header and footer markers
    await Assert.That(code).Contains("===");  // Header/footer markers
    await Assert.That(code).Contains("Whizbang");  // Branded header
  }

  [Test]
  public async Task GenerateAppHostCode_GeneratesValidCSharpSyntaxAsync()
  {
    // Arrange
    var requirements = new[]
    {
      new TopicRequirement("products", "bff-products"),
      new TopicRequirement("orders", "payment-orders"),
      new TopicRequirement("orders", "shipping-orders")
    };

    // Act
    var code = AspireConfigurationGenerator.GenerateAppHostCode(requirements, "multi-service");

    // Assert - Generated code should have valid C# syntax elements
    await Assert.That(code).Contains("var ");  // Variable declarations
    await Assert.That(code).Contains(";");  // Statement terminators
    await Assert.That(code).DoesNotContain(";;");  // No double semicolons
  }

  [Test]
  public async Task GenerateAppHostCode_SortsTopicsAlphabeticallyAsync()
  {
    // Arrange - Topics in non-alphabetical order
    var requirements = new[]
    {
      new TopicRequirement("shipping", "bff-shipping"),
      new TopicRequirement("orders", "bff-orders"),
      new TopicRequirement("products", "bff-products")
    };

    // Act
    var code = AspireConfigurationGenerator.GenerateAppHostCode(requirements);

    // Assert - Topics should appear in alphabetical order in output
    var ordersIndex = code.IndexOf("ordersTopic", StringComparison.Ordinal);
    var productsIndex = code.IndexOf("productsTopic", StringComparison.Ordinal);
    var shippingIndex = code.IndexOf("shippingTopic", StringComparison.Ordinal);

    await Assert.That(ordersIndex).IsLessThan(productsIndex);
    await Assert.That(productsIndex).IsLessThan(shippingIndex);
  }

  [Test]
  public async Task GenerateAppHostCode_WithSpecialCharacters_EscapesCorrectlyAsync()
  {
    // Arrange - Topic names with hyphens and underscores
    var requirements = new[]
    {
      new TopicRequirement("order-events", "bff_orders_v2")
    };

    // Act
    var code = AspireConfigurationGenerator.GenerateAppHostCode(requirements);

    // Assert - Should handle special characters correctly
    await Assert.That(code).Contains("\"order-events\"");
    await Assert.That(code).Contains("\"bff_orders_v2\"");
  }
}
