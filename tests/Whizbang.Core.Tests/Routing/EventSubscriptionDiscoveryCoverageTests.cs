using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Coverage-round-23 target for <see cref="EventSubscriptionDiscoveryExtensions.AddEventSubscriptionDiscovery"/>:
/// the DI registration a host relies on to resolve <see cref="EventSubscriptionDiscovery"/> at
/// transport startup. If this never registers the service (or registers it with the wrong lifetime),
/// the host either fails to start with a DI resolution error, or — if wired manually elsewhere —
/// silently skips the auto-discovered/manual namespace combination this service exists to compute,
/// leaving the service subscribed to nothing.
/// </summary>
public class EventSubscriptionDiscoveryCoverageTests {
  [Test]
  public async Task AddEventSubscriptionDiscovery_RegistersAResolvableSingletonAsync() {
    var services = new ServiceCollection();
    services.AddOptions<RoutingOptions>();

    var result = services.AddEventSubscriptionDiscovery();

    await Assert.That(result).IsSameReferenceAs(services)
      .Because("the extension must return the same collection so callers can keep chaining Add* calls");

    var provider = services.BuildServiceProvider();
    var first = provider.GetRequiredService<EventSubscriptionDiscovery>();
    var second = provider.GetRequiredService<EventSubscriptionDiscovery>();

    await Assert.That(first).IsNotNull()
      .Because("the host must be able to resolve the service it just registered");
    await Assert.That(first).IsSameReferenceAs(second)
      .Because("registered as a singleton — two resolutions from the root provider must be the same instance");
  }
}
