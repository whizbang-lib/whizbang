namespace Whizbang.Core.Transports.AzureServiceBus;

/// <summary>
/// Represents a required Service Bus topic and subscription pair.
/// Value object with structural equality for caching in infrastructure discovery.
/// </summary>
/// <param name="TopicName">The name of the Service Bus topic</param>
/// <param name="SubscriptionName">The name of the subscription on the topic</param>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/TopicRequirementTests.cs:TopicRequirement_Constructor_SetsPropertiesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/TopicRequirementTests.cs:TopicRequirement_WithSameValues_AreEqualAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/TopicRequirementTests.cs:TopicRequirement_WithDifferentTopicNames_AreNotEqualAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/TopicRequirementTests.cs:TopicRequirement_WithDifferentSubscriptionNames_AreNotEqualAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/TopicRequirementTests.cs:TopicRequirement_ToString_ReturnsFormattedStringAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/TopicRequirementTests.cs:TopicRequirement_WithEmptyTopicName_AllowedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/TopicRequirementTests.cs:TopicRequirement_WithEmptySubscriptionName_AllowedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/TopicRequirementTests.cs:TopicRequirement_Deconstruct_ExtractsValuesAsync</tests>
public sealed record TopicRequirement(
    string TopicName,
    string SubscriptionName
);
