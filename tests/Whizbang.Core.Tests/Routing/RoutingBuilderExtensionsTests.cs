using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for RoutingBuilderExtensions.
/// Verifies that WithRouting() correctly registers routing options and discovery services.
/// </summary>
public class RoutingBuilderExtensionsTests {
  #region WithRouting Registration

  [Test]
  public async Task WithRouting_RegistersRoutingOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangBuilder(services);

    // Act
    builder.WithRouting(routing => routing.OwnDomains("myapp.orders.commands"));

    // Assert
    var provider = services.BuildServiceProvider();
    var options = provider.GetService<IOptions<RoutingOptions>>();
    await Assert.That(options).IsNotNull();
    await Assert.That(options!.Value.OwnedDomains).Contains("myapp.orders.commands");
  }

  [Test]
  public async Task WithRouting_RegistersEventSubscriptionDiscoveryAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangBuilder(services);

    // Act
    builder.WithRouting(routing => routing.SubscribeTo("myapp.events"));

    // Assert
    var provider = services.BuildServiceProvider();
    var discovery = provider.GetService<EventSubscriptionDiscovery>();
    await Assert.That(discovery).IsNotNull();
  }

  [Test]
  public async Task WithRouting_ConfiguresOwnDomainsCorrectlyAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangBuilder(services);

    // Act
    builder.WithRouting(routing => routing.OwnDomains("myapp.orders.commands", "myapp.users.commands"));

    // Assert
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<RoutingOptions>>();
    await Assert.That(options.Value.OwnedDomains.Count).IsEqualTo(2);
    await Assert.That(options.Value.OwnedDomains).Contains("myapp.orders.commands");
    await Assert.That(options.Value.OwnedDomains).Contains("myapp.users.commands");
  }

  [Test]
  public async Task WithRouting_ConfiguresSubscribeToCorrectlyAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangBuilder(services);

    // Act
    builder.WithRouting(routing => routing.SubscribeTo("myapp.orders.events", "myapp.users.events"));

    // Assert
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<RoutingOptions>>();
    await Assert.That(options.Value.SubscribedNamespaces.Count).IsEqualTo(2);
    await Assert.That(options.Value.SubscribedNamespaces).Contains("myapp.orders.events");
    await Assert.That(options.Value.SubscribedNamespaces).Contains("myapp.users.events");
  }

  [Test]
  public async Task WithRouting_ConfiguresSharedTopicInboxAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangBuilder(services);

    // Act
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands")
             .Inbox.UseSharedTopic("whizbang.inbox");
    });

    // Assert
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<RoutingOptions>>();
    await Assert.That(options.Value.InboxStrategy).IsTypeOf<SharedTopicInboxStrategy>();
  }

  [Test]
  public async Task WithRouting_ConfiguresDomainTopicInboxAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangBuilder(services);

    // Act
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands")
             .Inbox.UseDomainTopics(".inbox");
    });

    // Assert
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<RoutingOptions>>();
    await Assert.That(options.Value.InboxStrategy).IsTypeOf<DomainTopicInboxStrategy>();
  }

  #endregion

  #region Chaining

  [Test]
  public async Task WithRouting_ReturnsSameBuilderForChainingAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangBuilder(services);

    // Act
    var result = builder.WithRouting(_ => { });

    // Assert
    await Assert.That(result).IsSameReferenceAs(builder);
  }

  #endregion

  #region Argument Validation

  [Test]
  public async Task WithRouting_WithNullBuilder_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    WhizbangBuilder? builder = null;

    // Act & Assert
    await Assert.That(() => builder!.WithRouting(_ => { }))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task WithRouting_WithNullConfigure_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangBuilder(services);

    // Act & Assert
    await Assert.That(() => builder.WithRouting(null!))
      .Throws<ArgumentNullException>();
  }

  #endregion

  #region Owned + subscribed namespace guard (issue #636)

  /// <summary>
  /// A namespace that is both owned and manually subscribed is a contradiction the framework cannot
  /// honor: event discovery strips owned namespaces from the subscription set on purpose, so the
  /// declared subscription was silently never created and a broker with no subscriber discards the
  /// messages. The options factory refuses at first resolution, the same seam
  /// <c>ThrowIfRetirementIncomplete</c> uses, so the contradiction fails startup loudly.
  /// </summary>
  [Test]
  public async Task WithRouting_OwnedAndSubscribedNamespace_FailsAtFirstResolutionAsync() {
    var services = new ServiceCollection();
    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => routing
      .OwnDomains("app.contracts.identity")
      .SubscribeTo("app.contracts.events")
      .SubscribeTo("app.contracts.identity"));
    await using var provider = services.BuildServiceProvider();

    await Assert.That(() => provider.GetRequiredService<IOptions<RoutingOptions>>())
      .Throws<InvalidOperationException>()
      .WithMessageContaining("app.contracts.identity");
  }

  [Test]
  public async Task WithRouting_OwnedAndAbsorbedNamespace_ResolvesAsync() {
    // Absorbing is the declared way to say "create the binding even though I own it" — it is the
    // remedy the refusal points at, so it must not itself be refused.
    var services = new ServiceCollection();
    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => routing
      .OwnDomains("app.contracts.identity")
      .SubscribeTo("app.contracts.identity")
      .AbsorbNamespaces("app.contracts.identity"));
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(options.AbsorbedNamespaces).Contains("app.contracts.identity");
  }

  #endregion
}
