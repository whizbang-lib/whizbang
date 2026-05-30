using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Routing;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Direct tests for <see cref="DomainTopicInboxStrategy"/>: both constructor
/// flavors (default ".inbox" suffix and custom) and every branch of
/// <c>GetSubscription</c> — primary-domain selection, the
/// service-name fallback when no domains are owned, the topic-as-filter
/// invariant (no FilterExpression / Metadata).
/// </summary>
/// <docs>fundamentals/dispatcher/routing#domain-topic-inbox</docs>
public class DomainTopicInboxStrategyTests {

  [Test]
  public async Task Constructor_NullSuffix_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new DomainTopicInboxStrategy(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task DefaultConstructor_UsesDotInboxSuffixAsync() {
    var strategy = new DomainTopicInboxStrategy();
    var subscription = strategy.GetSubscription(
      new HashSet<string> { "orders" },
      serviceName: "orderservice",
      MessageKind.Event);

    await Assert.That(subscription.Topic).IsEqualTo("orders.inbox");
  }

  [Test]
  public async Task GetSubscription_PrimaryDomain_AppendsCustomSuffixAsync() {
    var strategy = new DomainTopicInboxStrategy(".in");

    var subscription = strategy.GetSubscription(
      new HashSet<string> { "billing" },
      serviceName: "billingservice",
      MessageKind.Event);

    await Assert.That(subscription.Topic).IsEqualTo("billing.in");
  }

  [Test]
  public async Task GetSubscription_NoOwnedDomains_FallsBackToServiceNameAsync() {
    // Empty ownedDomains → service name becomes the primary domain.
    var strategy = new DomainTopicInboxStrategy(".inbox");

    var subscription = strategy.GetSubscription(
      new HashSet<string>(),
      serviceName: "auth",
      MessageKind.Event);

    await Assert.That(subscription.Topic).IsEqualTo("auth.inbox");
  }

  [Test]
  public async Task GetSubscription_MultipleOwnedDomains_UsesFirstAsync() {
    // The strategy picks the FIRST domain in the set as the primary.
    // (HashSet doesn't guarantee order; we accept either of the candidates
    // here since the contract is "first in set" not "first added".)
    var domains = new HashSet<string> { "orders", "invoicing" };
    var strategy = new DomainTopicInboxStrategy(".inbox");

    var subscription = strategy.GetSubscription(domains, "any", MessageKind.Event);

    var primary = domains.First();
    await Assert.That(subscription.Topic).IsEqualTo($"{primary}.inbox");
  }

  [Test]
  public async Task GetSubscription_FilterExpressionIsNull_TopicIsTheFilterAsync() {
    var strategy = new DomainTopicInboxStrategy(".inbox");
    var subscription = strategy.GetSubscription(
      new HashSet<string> { "orders" },
      "orderservice",
      MessageKind.Event);

    // The topic IS the filter — no expression-based filtering needed.
    await Assert.That(subscription.FilterExpression).IsNull();
    await Assert.That(subscription.Metadata).IsNull();
  }

  [Test]
  public async Task GetSubscription_NullOwnedDomains_ThrowsArgumentNullExceptionAsync() {
    var strategy = new DomainTopicInboxStrategy(".inbox");
    await Assert.That(() => strategy.GetSubscription(null!, "any", MessageKind.Event))
      .Throws<ArgumentNullException>();
  }
}
