using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Hosting.Azure.ServiceBus.Tests;

/// <summary>
/// Tests for ServiceBusSubscriptionExtensions to ensure TrueFilter and CorrelationFilter rules
/// are correctly configured for the Whizbang inbox pattern.
/// Rules are applied immediately to the Aspire application model, so no Azure connection,
/// emulator, or app run is required - the builder is inspected directly.
/// </summary>
public class ServiceBusSubscriptionExtensionsTests {
  private static readonly string[] _noArgs = [];

  // --- WithTrueFilter ---

  [Test]
  public async Task WithTrueFilter_NullSubscription_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    IResourceBuilder<AzureServiceBusSubscriptionResource>? subscription = null;

    // Act
    var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => {
      subscription!.WithTrueFilter();
      return Task.CompletedTask;
    });

    // Assert
    await Assert.That(ex!.ParamName).IsEqualTo("subscription");
  }

  [Test]
  public async Task WithTrueFilter_AddsDefaultRule_ToSubscriptionAsync() {
    // Arrange
    var subscription = _createSubscription();

    // Act
    subscription.WithTrueFilter();

    // Assert - $Default rule with no explicit filter defaults to TrueFilter (accepts all)
    await Assert.That(subscription.Resource.Rules.Count).IsEqualTo(1);
    await Assert.That(subscription.Resource.Rules[0].Name).IsEqualTo("$Default");
  }

  [Test]
  public async Task WithTrueFilter_ReturnsSameBuilder_ForChainingAsync() {
    // Arrange
    var subscription = _createSubscription();

    // Act
    var result = subscription.WithTrueFilter();

    // Assert
    await Assert.That(ReferenceEquals(result, subscription)).IsTrue();
  }

  // --- WithDestinationFilter ---

  [Test]
  public async Task WithDestinationFilter_NullSubscription_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    IResourceBuilder<AzureServiceBusSubscriptionResource>? subscription = null;

    // Act
    var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => {
      subscription!.WithDestinationFilter("inventory-service");
      return Task.CompletedTask;
    });

    // Assert
    await Assert.That(ex!.ParamName).IsEqualTo("subscription");
  }

  [Test]
  public async Task WithDestinationFilter_NullDestination_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var subscription = _createSubscription();

    // Act
    var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => {
      subscription.WithDestinationFilter(null!);
      return Task.CompletedTask;
    });

    // Assert
    await Assert.That(ex!.ParamName).IsEqualTo("destination");
  }

  [Test]
  public async Task WithDestinationFilter_EmptyDestination_ThrowsArgumentExceptionAsync() {
    // Arrange
    var subscription = _createSubscription();

    // Act
    var ex = await Assert.ThrowsAsync<ArgumentException>(() => {
      subscription.WithDestinationFilter(string.Empty);
      return Task.CompletedTask;
    });

    // Assert
    await Assert.That(ex!.ParamName).IsEqualTo("destination");
  }

  [Test]
  public async Task WithDestinationFilter_WhitespaceDestination_ThrowsArgumentExceptionAsync() {
    // Arrange
    var subscription = _createSubscription();

    // Act
    var ex = await Assert.ThrowsAsync<ArgumentException>(() => {
      subscription.WithDestinationFilter("   ");
      return Task.CompletedTask;
    });

    // Assert
    await Assert.That(ex!.ParamName).IsEqualTo("destination");
  }

  [Test]
  public async Task WithDestinationFilter_AddsCorrelationFilterRule_WithDestinationPropertyAsync() {
    // Arrange
    var subscription = _createSubscription();

    // Act
    subscription.WithDestinationFilter("inventory-service");

    // Assert
    await Assert.That(subscription.Resource.Rules.Count).IsEqualTo(1);

    var rule = subscription.Resource.Rules[0];
    await Assert.That(rule.Name).IsEqualTo("DestinationFilter");
    await Assert.That(rule.CorrelationFilter).IsNotNull();
    await Assert.That(rule.CorrelationFilter!.Properties["Destination"]).IsEqualTo("inventory-service");
  }

  [Test]
  public async Task WithDestinationFilter_ReturnsSameBuilder_ForChainingAsync() {
    // Arrange
    var subscription = _createSubscription();

    // Act
    var result = subscription.WithDestinationFilter("payment-service");

    // Assert
    await Assert.That(ReferenceEquals(result, subscription)).IsTrue();
  }

  [Test]
  public async Task WithTrueFilterAndWithDestinationFilter_Chained_AddBothRulesAsync() {
    // Arrange
    var subscription = _createSubscription();

    // Act
    subscription.WithTrueFilter().WithDestinationFilter("shipping-service");

    // Assert
    await Assert.That(subscription.Resource.Rules.Count).IsEqualTo(2);
    await Assert.That(subscription.Resource.Rules[0].Name).IsEqualTo("$Default");
    await Assert.That(subscription.Resource.Rules[1].Name).IsEqualTo("DestinationFilter");
    await Assert.That(subscription.Resource.Rules[1].CorrelationFilter!.Properties["Destination"]).IsEqualTo("shipping-service");
  }

  // --- helpers ---

  /// <summary>
  /// Builds a topic subscription on an Aspire distributed application model.
  /// The application is never built or run, so nothing connects to Azure or the emulator.
  /// </summary>
  private static IResourceBuilder<AzureServiceBusSubscriptionResource> _createSubscription() {
    var builder = DistributedApplication.CreateBuilder(_noArgs);
    var serviceBus = builder.AddAzureServiceBus("servicebus");
    var topic = serviceBus.AddServiceBusTopic("inbox");
    return topic.AddServiceBusSubscription("sub-inbox-test");
  }
}
