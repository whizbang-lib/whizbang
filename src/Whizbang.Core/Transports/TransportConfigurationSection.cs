using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Transports;

/// <summary>
/// Names a transport this host registered, and so the configuration section its consumer
/// reads: <c>Whizbang:Transports:&lt;transport&gt;</c>. A transport's registration call
/// (<c>AddAzureServiceBusTransport</c>, <c>AddRabbitMQTransport</c>) registers its name; the
/// transport consumer then binds <c>MessageProcessing</c>, <c>Batch</c>,
/// <c>SubscriptionResilience</c> and <c>Consumer</c> from that section over the values set in
/// code. A section for a transport the host does not run is never read.
/// </summary>
/// <docs>operations/configuration/configuration-reference#transport-sections</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerOptionsConfigurationTests.cs</tests>
public sealed class TransportConfigurationSection {
  /// <summary>The parent of every transport's section.</summary>
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  public const string ROOT = "Whizbang:Transports";
#pragma warning restore CA1707

  /// <summary>Creates the marker for one transport.</summary>
  /// <param name="name">The transport's section key, e.g. <c>AzureServiceBus</c>.</param>
  public TransportConfigurationSection(string name) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    Name = name;
  }

  /// <summary>The transport's section key, e.g. <c>AzureServiceBus</c>.</summary>
  public string Name { get; }

  /// <summary>The full section path, e.g. <c>Whizbang:Transports:AzureServiceBus</c>.</summary>
  public string Path => $"{ROOT}:{Name}";

  /// <summary>
  /// Registers <paramref name="name"/> as a transport this host runs. Idempotent per name.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="name">The transport's section key, e.g. <c>AzureServiceBus</c>.</param>
  public static void Register(IServiceCollection services, string name) {
    ArgumentNullException.ThrowIfNull(services);
    var section = new TransportConfigurationSection(name);
    // Keyed descriptors are skipped first: reading ImplementationInstance on one throws.
    var registered = services
      .Where(d => !d.IsKeyedService)
      .Select(d => d.ImplementationInstance)
      .OfType<TransportConfigurationSection>()
      .Any(existing => string.Equals(existing.Name, section.Name, StringComparison.Ordinal));
    if (!registered) {
      services.AddSingleton(section);
    }
  }
}
