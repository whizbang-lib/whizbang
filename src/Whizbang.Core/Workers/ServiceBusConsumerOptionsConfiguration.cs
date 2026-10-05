// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// Registers <see cref="ServiceBusConsumerOptions"/> bound from
/// <c>Whizbang:Transports:AzureServiceBus:Consumer:Subscriptions:&lt;n&gt;</c>
/// (<c>TopicName</c>, <c>SubscriptionName</c>, optional <c>DestinationFilter</c>), so a host that
/// resolves <see cref="ServiceBusConsumerWorker"/> from the container can change its subscriptions
/// without a redeploy. <c>AddAzureServiceBusTransport</c> calls it.
/// </summary>
/// <docs>operations/configuration/configuration-reference#transport-sections</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerOptionsConfigurationTests.cs</tests>
public static class ServiceBusConsumerOptionsConfiguration {
  /// <summary>The Service Bus consumer's section.</summary>
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  public const string CONFIGURATION_SECTION = TransportConfigurationSection.ROOT + ":AzureServiceBus:Consumer";
#pragma warning restore CA1707

  /// <summary>
  /// Registers <see cref="ServiceBusConsumerOptions"/>, binding configuration over an instance or
  /// factory the host registered before this call. A configured list with at least one complete
  /// entry REPLACES the code list; a section with none leaves the code list alone.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection for chaining.</returns>
  public static IServiceCollection AddWhizbangServiceBusConsumerOptions(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    services.TryAddEmptyConfiguration();
    TransportConsumerOptionsBinder.AddBound(services, () => new ServiceBusConsumerOptions(), _bind);
    return services;
  }

  private static ServiceBusConsumerOptions _bind(IServiceProvider services, ServiceBusConsumerOptions options) {
    var configured = new List<TopicSubscription>();
    var entries = services.GetRequiredService<IConfiguration>()
      .GetSection(CONFIGURATION_SECTION).GetSection("Subscriptions").GetChildren();
    foreach (var entry in entries) {
      var topic = entry["TopicName"];
      var subscription = entry["SubscriptionName"];
      if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(subscription)) {
        continue;
      }
      var filter = entry["DestinationFilter"];
      configured.Add(new TopicSubscription(topic, subscription, string.IsNullOrWhiteSpace(filter) ? null : filter));
    }
    if (configured.Count > 0) {
      options.Subscriptions = configured;
    }
    return options;
  }
}
