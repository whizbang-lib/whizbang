namespace Whizbang.Core.Transports.AzureServiceBus;

/// <summary>
/// Configuration options for Service Bus infrastructure auto-discovery and provisioning.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/ServiceBusInfrastructureOptionsTests.cs:ServiceBusInfrastructureOptions_DefaultValues_AreSetAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/ServiceBusInfrastructureOptionsTests.cs:ServiceBusInfrastructureOptions_ServiceName_CanBeSetAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/ServiceBusInfrastructureOptionsTests.cs:ServiceBusInfrastructureOptions_RequiredTopics_CanBeModifiedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/ServiceBusInfrastructureOptionsTests.cs:ServiceBusInfrastructureOptions_AutoCreateInProduction_CanBeDisabledAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/ServiceBusInfrastructureOptionsTests.cs:ServiceBusInfrastructureOptions_GenerateAspireConfigInDev_CanBeDisabledAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/ServiceBusInfrastructureOptionsTests.cs:ServiceBusInfrastructureOptions_FailOnProvisioningError_CanBeEnabledAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/ServiceBusInfrastructureOptionsTests.cs:ServiceBusInfrastructureOptions_RequiredTopics_InitializedAsEmptyListAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/ServiceBusInfrastructureOptionsTests.cs:ServiceBusInfrastructureOptions_MultipleRequiredTopics_CanBeAddedAsync</tests>
public class ServiceBusInfrastructureOptions {
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
