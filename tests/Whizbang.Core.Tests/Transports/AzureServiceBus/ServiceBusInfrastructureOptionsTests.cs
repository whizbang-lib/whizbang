using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports.AzureServiceBus;

namespace Whizbang.Core.Tests.Transports.AzureServiceBus;

/// <summary>
/// Tests for ServiceBusInfrastructureOptions configuration class.
/// Ensures default values and property setters work correctly.
/// </summary>
public class ServiceBusInfrastructureOptionsTests {
  [Test]
  public async Task ServiceBusInfrastructureOptions_DefaultValues_AreSetAsync() {
    // Arrange & Act
    var options = new ServiceBusInfrastructureOptions();

    // Assert - Verify all default values
    await Assert.That(options.ServiceName).IsEqualTo(string.Empty);
    await Assert.That(options.RequiredTopics).IsEmpty();
    await Assert.That(options.AutoCreateInProduction).IsTrue();
    await Assert.That(options.GenerateAspireConfigInDev).IsTrue();
    await Assert.That(options.FailOnProvisioningError).IsFalse();
  }

  [Test]
  public async Task ServiceBusInfrastructureOptions_ServiceName_CanBeSetAsync() {
    // Arrange
    var options = new ServiceBusInfrastructureOptions();

    // Act
    options.ServiceName = "bff";

    // Assert
    await Assert.That(options.ServiceName).IsEqualTo("bff");
  }

  [Test]
  public async Task ServiceBusInfrastructureOptions_RequiredTopics_CanBeModifiedAsync() {
    // Arrange
    var options = new ServiceBusInfrastructureOptions();
    var requirement = new TopicRequirement("orders", "bff-orders");

    // Act
    options.RequiredTopics.Add(requirement);

    // Assert
    await Assert.That(options.RequiredTopics).HasCount().EqualTo(1);
    await Assert.That(options.RequiredTopics[0]).IsEqualTo(requirement);
  }

  [Test]
  public async Task ServiceBusInfrastructureOptions_AutoCreateInProduction_CanBeDisabledAsync() {
    // Arrange
    var options = new ServiceBusInfrastructureOptions();

    // Act
    options.AutoCreateInProduction = false;

    // Assert
    await Assert.That(options.AutoCreateInProduction).IsFalse();
  }

  [Test]
  public async Task ServiceBusInfrastructureOptions_GenerateAspireConfigInDev_CanBeDisabledAsync() {
    // Arrange
    var options = new ServiceBusInfrastructureOptions();

    // Act
    options.GenerateAspireConfigInDev = false;

    // Assert
    await Assert.That(options.GenerateAspireConfigInDev).IsFalse();
  }

  [Test]
  public async Task ServiceBusInfrastructureOptions_FailOnProvisioningError_CanBeEnabledAsync() {
    // Arrange
    var options = new ServiceBusInfrastructureOptions();

    // Act
    options.FailOnProvisioningError = true;

    // Assert
    await Assert.That(options.FailOnProvisioningError).IsTrue();
  }

  [Test]
  public async Task ServiceBusInfrastructureOptions_RequiredTopics_InitializedAsEmptyListAsync() {
    // Arrange & Act
    var options = new ServiceBusInfrastructureOptions();

    // Assert - Should be initialized (not null) but empty
    await Assert.That(options.RequiredTopics).IsNotNull();
    await Assert.That(options.RequiredTopics).IsEmpty();
  }

  [Test]
  public async Task ServiceBusInfrastructureOptions_MultipleRequiredTopics_CanBeAddedAsync() {
    // Arrange
    var options = new ServiceBusInfrastructureOptions();
    var req1 = new TopicRequirement("orders", "bff-orders");
    var req2 = new TopicRequirement("products", "bff-products");
    var req3 = new TopicRequirement("payments", "bff-payments");

    // Act
    options.RequiredTopics.Add(req1);
    options.RequiredTopics.Add(req2);
    options.RequiredTopics.Add(req3);

    // Assert
    await Assert.That(options.RequiredTopics).HasCount().EqualTo(3);
    await Assert.That(options.RequiredTopics).Contains(req1);
    await Assert.That(options.RequiredTopics).Contains(req2);
    await Assert.That(options.RequiredTopics).Contains(req3);
  }
}
