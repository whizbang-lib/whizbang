using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// The strategy-agnostic command-inbox seam (topology arc phase 7):
/// <see cref="ICommandInboxAddressResolver"/> exposes the default (shared) inbox address plus
/// the publish-time flipped-resolution hook, implemented by BOTH built-in command-routing
/// strategies — the transports' DI factories consume the interface instead of type-testing
/// concrete strategies.
/// </summary>
public class CommandInboxAddressResolverTests {
  /// <summary>Exercises the seam THROUGH the interface — the exact consumption shape the
  /// transports' DI factories use (no concrete strategy types).</summary>
  private static async Task<string?> _resolveThroughSeamAsync(
      ICommandInboxAddressResolver resolver, string expectedDefault, string probeNamespace) {
    await Assert.That(resolver.DefaultCommandInboxAddress).IsEqualTo(expectedDefault);
    return resolver.ResolveFlippedCommandInboxAddress(probeNamespace);
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_ImplementsTheSeam_DefaultIsInboxTopic_NeverFlipsAsync() {
    var strategy = new SharedTopicOutboxStrategy("custom-inbox");

    var resolved = await _resolveThroughSeamAsync(strategy, "custom-inbox", "myapp.orders.commands");

    await Assert.That(resolved).IsNull()
      .Because("the shared-topic strategy never flips — every command rides the shared inbox");
    await Assert.That(strategy.ResolveFlippedCommandInboxAddress(null)).IsNull();
  }

  [Test]
  public async Task NamespaceOutboxStrategy_ImplementsTheSeam_DefaultIsSharedInboxTopicAsync() {
    var options = new RoutingOptions().RouteCommandNamespaceToInbox("myapp.orders.commands");
    var strategy = new NamespaceOutboxStrategy(options, "custom-inbox");

    var resolved = await _resolveThroughSeamAsync(strategy, "custom-inbox", "myapp.orders.commands");

    await Assert.That(resolved).IsEqualTo("inbox.myapp.orders.commands");
    await Assert.That(strategy.ResolveFlippedCommandInboxAddress("myapp.users.commands")).IsNull()
      .Because("unflipped namespaces keep the default (legacy shared) inbox address");
  }

  [Test]
  public async Task DomainTopicOutboxStrategy_DoesNotImplementTheSeamAsync() {
    // The "neither" wiring case: strategies without a command-inbox concept stay outside the
    // seam — the DI factories fall back to the default inbox topic with no flip hook.
    IOutboxRoutingStrategy strategy = new DomainTopicOutboxStrategy();

    await Assert.That(strategy is ICommandInboxAddressResolver).IsFalse();
  }
}
