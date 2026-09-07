using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Targeted coverage for <see cref="RoutingOptionsConfigurationBinder"/>'s blank-entry guard in
/// the <c>Whizbang:Routing:CommandNamespacesToInbox</c> array binding — a branch none of the
/// existing <c>NamespaceOutboxStrategyTests</c> / <c>SharedInboxRetirementTests</c> /
/// <c>DefaultNamespaceTopologyTests</c> configuration-binding tests exercise (every entry they
/// supply is non-blank).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Routing/RoutingOptionsConfigurationBinder.cs</code-under-test>
public class RoutingOptionsConfigurationBinderCoverageTests {

  [Test]
  public async Task Apply_BlankArrayEntry_IsSkippedRatherThanCrashingStartupOrFlippingAllNamespacesAsync() {
    // JSON config arrays surface as GetChildren() entries; a trailing comma, an unset environment
    // variable substituted into one slot, or a manually-edited appsettings.json can produce a
    // blank/whitespace entry alongside real ones. If this guard were missing,
    // RouteCommandNamespaceToInbox(string.Empty) would either throw (crashing startup, per its own
    // ArgumentException.ThrowIfNullOrWhiteSpace contract) or — worse — silently register a
    // namespace that matches nothing while still stepping the all-namespaces DEFAULT aside.
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Routing:CommandNamespacesToInbox:0"] = "myapp.orders.commands",
        ["Whizbang:Routing:CommandNamespacesToInbox:1"] = "   ",
        ["Whizbang:Routing:CommandNamespacesToInbox:2"] = "myapp.billing.commands",
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    new WhizbangBuilder(services).WithRouting(r => r.Outbox.UseNamespaceRouting());

    using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(options.CommandNamespacesToInbox).Contains("myapp.orders.commands");
    await Assert.That(options.CommandNamespacesToInbox).Contains("myapp.billing.commands");
    await Assert.That(options.CommandNamespacesToInbox.Count).IsEqualTo(2)
      .Because("the blank entry must contribute nothing — not an empty-string namespace, not a third flipped entry");
    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsFalse()
      .Because("naming real namespaces still steps the all-namespaces default aside even when a blank slot sits among them");
  }
}
