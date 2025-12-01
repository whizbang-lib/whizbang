namespace Whizbang.Core.Transports.AzureServiceBus;

/// <summary>
/// Represents a required Service Bus topic and subscription pair.
/// Value object with structural equality for caching in infrastructure discovery.
/// </summary>
/// <param name="TopicName">The name of the Service Bus topic</param>
/// <param name="SubscriptionName">The name of the subscription on the topic</param>
public sealed record TopicRequirement(
    string TopicName,
    string SubscriptionName
);
