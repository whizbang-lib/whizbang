using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Resilience;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// Binds the transport consumer's options from the section of each transport the host runs
/// (<see cref="TransportConfigurationSection"/>): <c>Whizbang:Transports:&lt;transport&gt;:MessageProcessing</c>,
/// <c>:Batch</c>, <c>:SubscriptionResilience</c> and <c>:Consumer</c>. Applied at resolution time
/// over the values the code set, so configuration overrides per key and an unconfigured key keeps
/// the code value.
/// </summary>
/// <remarks>
/// Each <c>Bind</c> call has a concrete type, so the binder source generator compiles it to typed
/// assignments and no reflection reaches the AOT path.
/// </remarks>
/// <docs>operations/configuration/configuration-reference#transport-sections</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerOptionsConfigurationTests.cs</tests>
internal static class TransportConsumerOptionsBinder {
  internal const string MESSAGE_PROCESSING_SECTION = "MessageProcessing";
  internal const string BATCH_SECTION = "Batch";
  internal const string SUBSCRIPTION_RESILIENCE_SECTION = "SubscriptionResilience";
  internal const string CONSUMER_SECTION = "Consumer";
  internal const string ADDITIONAL_DESTINATIONS_KEY = "AdditionalDestinations";

#pragma warning disable IL2026 // intercepted: the binder source generator compiles these calls to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
  internal static MessageProcessingOptions Bind(IServiceProvider services, MessageProcessingOptions options) {
    foreach (var section in Sections(services, MESSAGE_PROCESSING_SECTION)) {
      section.Bind(options);
    }
    return options;
  }

  internal static TransportBatchOptions Bind(IServiceProvider services, TransportBatchOptions options) {
    foreach (var section in Sections(services, BATCH_SECTION)) {
      section.Bind(options);
    }
    return options;
  }

  internal static SubscriptionResilienceOptions Bind(IServiceProvider services, SubscriptionResilienceOptions options) {
    foreach (var section in Sections(services, SUBSCRIPTION_RESILIENCE_SECTION)) {
      section.Bind(options);
    }
    return options;
  }
#pragma warning restore IL2026

  /// <summary>
  /// Adds each <c>Consumer:AdditionalDestinations:&lt;n&gt;</c> entry (<c>Address</c>, optional
  /// <c>RoutingKey</c>) the consumer does not already subscribe to.
  /// </summary>
  internal static TransportConsumerOptions Bind(IServiceProvider services, TransportConsumerOptions options) {
    foreach (var section in Sections(services, CONSUMER_SECTION)) {
      foreach (var entry in section.GetSection(ADDITIONAL_DESTINATIONS_KEY).GetChildren()) {
        var address = entry["Address"];
        if (string.IsNullOrWhiteSpace(address)) {
          continue;
        }
        var configuredKey = entry["RoutingKey"];
        var routingKey = string.IsNullOrWhiteSpace(configuredKey) ? null : configuredKey;
        if (!options.Destinations.Any(d => d.Address == address && d.RoutingKey == routingKey)) {
          options.Destinations.Add(new TransportDestination(address, routingKey));
        }
      }
    }
    return options;
  }

  /// <summary>
  /// Registers <typeparamref name="T"/> bound over whatever the host registered before this call:
  /// an instance is bound in place, a factory's result is bound (its lifetime kept), and with
  /// nothing registered a default instance is created and bound. A type registration is left as
  /// the host made it — the container constructs it, so there is no code value to bind over.
  /// </summary>
  internal static void AddBound<T>(IServiceCollection services, Func<T> create, Func<IServiceProvider, T, T> bind)
    where T : class {
    var existing = services.LastOrDefault(d => d.ServiceType == typeof(T) && !d.IsKeyedService);
    if (existing is null) {
      services.AddSingleton(sp => bind(sp, create()));
      return;
    }
    if (existing.ImplementationInstance is T instance) {
      services.Remove(existing);
      services.AddSingleton(sp => bind(sp, instance));
      return;
    }
    if (existing.ImplementationFactory is { } factory) {
      services.Remove(existing);
      services.Add(new ServiceDescriptor(typeof(T), sp => bind(sp, (T)factory(sp)), existing.Lifetime));
    }
  }

  /// <summary>
  /// The existing <paramref name="child"/> section of each registered transport, in registration order.
  /// </summary>
  internal static IEnumerable<IConfigurationSection> Sections(IServiceProvider services, string child) {
    // AddTransportConsumer applies TryAddWhizbangDefaults, which guarantees an IConfiguration.
    var root = services.GetRequiredService<IConfiguration>().GetSection(TransportConfigurationSection.ROOT);
    return [.. services.GetServices<TransportConfigurationSection>()
      .Select(transport => root.GetSection(transport.Name).GetSection(child))
      .Where(section => section.Exists())];
  }
}
