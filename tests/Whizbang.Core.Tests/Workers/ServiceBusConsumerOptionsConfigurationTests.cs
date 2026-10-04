using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <see cref="ServiceBusConsumerOptions"/> binds from
/// <c>Whizbang:Transports:AzureServiceBus:Consumer:Subscriptions</c>; a configured list replaces
/// the code list, and an unconfigured host keeps the code list.
/// </summary>
public class ServiceBusConsumerOptionsConfigurationTests {
  private const string SUBSCRIPTIONS = "Whizbang:Transports:AzureServiceBus:Consumer:Subscriptions";

  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
      .Build();

  private static ServiceProvider _provider(IConfiguration configuration, ServiceBusConsumerOptions? codeSet = null) {
    var services = new ServiceCollection();
    services.AddSingleton(configuration);
    if (codeSet is not null) {
      services.AddSingleton(codeSet);
    }
    services.AddWhizbangServiceBusConsumerOptions();
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task ConfiguredSubscriptions_ReplaceTheCodeList_SkippingIncompleteEntriesAsync() {
    var codeSet = new ServiceBusConsumerOptions { Subscriptions = [new TopicSubscription("code-topic", "code-sub")] };
    await using var provider = _provider(_config(
      ($"{SUBSCRIPTIONS}:0:TopicName", "orders"),
      ($"{SUBSCRIPTIONS}:0:SubscriptionName", "orders-sub"),
      ($"{SUBSCRIPTIONS}:0:DestinationFilter", "inventory-service"),
      ($"{SUBSCRIPTIONS}:1:TopicName", "payments"),
      ($"{SUBSCRIPTIONS}:1:SubscriptionName", "payments-sub"),
      ($"{SUBSCRIPTIONS}:2:TopicName", "no-subscription-name"),
      ($"{SUBSCRIPTIONS}:3:SubscriptionName", "no-topic-name")), codeSet);

    var options = provider.GetRequiredService<ServiceBusConsumerOptions>();

    await Assert.That(options).IsSameReferenceAs(codeSet);
    await Assert.That(options.Subscriptions).IsEquivalentTo([
      new TopicSubscription("orders", "orders-sub", "inventory-service"),
      new TopicSubscription("payments", "payments-sub")]);
  }

  [Test]
  public async Task NoConfiguredSubscriptions_KeepTheCodeListAsync() {
    var codeSet = new ServiceBusConsumerOptions { Subscriptions = [new TopicSubscription("code-topic", "code-sub")] };
    await using var provider = _provider(_config(($"{SUBSCRIPTIONS}:0:TopicName", "orders")), codeSet);

    var options = provider.GetRequiredService<ServiceBusConsumerOptions>();

    await Assert.That(options.Subscriptions).IsEquivalentTo([new TopicSubscription("code-topic", "code-sub")])
      .Because("a section with no complete entry must not wipe the subscriptions the code set");
  }

  [Test]
  public async Task NothingRegistered_GetsABoundDefaultInstanceAsync() {
    await using var provider = _provider(_config(
      ($"{SUBSCRIPTIONS}:0:TopicName", "orders"),
      ($"{SUBSCRIPTIONS}:0:SubscriptionName", "orders-sub")));

    var options = provider.GetRequiredService<ServiceBusConsumerOptions>();

    await Assert.That(options.Subscriptions).IsEquivalentTo([new TopicSubscription("orders", "orders-sub")]);
  }

  [Test]
  public async Task AddWhizbangServiceBusConsumerOptions_RejectsNullServicesAsync() {
    await Assert.That(() => ServiceBusConsumerOptionsConfiguration.AddWhizbangServiceBusConsumerOptions(null!))
      .Throws<ArgumentNullException>();
  }
}
