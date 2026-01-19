namespace Whizbang.Core.Transports;

/// <summary>
/// Defines where a message should be published when created locally.
/// Used by PolicyConfiguration to specify publishing targets.
/// </summary>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_ShouldStoreTransportTypeAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_ShouldStoreDestinationAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_ShouldStoreRoutingKeyAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_ShouldAllowNullRoutingKeyAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_Equality_WithSameValues_ShouldBeEqualAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_Equality_WithDifferentValues_ShouldNotBeEqualAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_WithExpression_ShouldCreateNewInstanceAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_ToString_ShouldContainPropertyValuesAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerPublishingTests.cs:PublishToTargetsAsync_WithSingleTarget_ShouldPublishAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerPublishingTests.cs:PublishToTargetsAsync_WithMultipleTargets_ShouldPublishToAllAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerPublishingTests.cs:PublishToTargetsAsync_WithRoutingKey_ShouldIncludeInDestinationAsync</tests>
public record PublishTarget {
  /// <summary>
  /// Type of transport to publish to
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_ShouldStoreTransportTypeAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_Equality_WithSameValues_ShouldBeEqualAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_WithExpression_ShouldCreateNewInstanceAsync</tests>
  public required TransportType TransportType { get; init; }

  /// <summary>
  /// Destination for the message (topic, queue, exchange)
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_ShouldStoreDestinationAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_Equality_WithDifferentValues_ShouldNotBeEqualAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_WithExpression_ShouldCreateNewInstanceAsync</tests>
  public required string Destination { get; init; }

  /// <summary>
  /// Routing key (RabbitMQ only)
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_ShouldStoreRoutingKeyAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_ShouldAllowNullRoutingKeyAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_Equality_WithSameValues_ShouldBeEqualAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PublishTarget_WithExpression_ShouldCreateNewInstanceAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerPublishingTests.cs:PublishToTargetsAsync_WithRoutingKey_ShouldIncludeInDestinationAsync</tests>
  public string? RoutingKey { get; init; }
}
