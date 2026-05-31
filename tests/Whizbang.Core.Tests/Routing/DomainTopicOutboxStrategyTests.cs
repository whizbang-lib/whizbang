using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Routing;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Direct tests for <see cref="DomainTopicOutboxStrategy"/>. Pins both
/// constructors (custom resolver + default-namespace) and every branch of
/// <c>GetDestination</c>: null-arg guards, the type-name → lowercase
/// routing key, and the delegated topic resolution.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#domain-topic-outbox</docs>
public class DomainTopicOutboxStrategyTests {

  // Test message types for assertion (namespace + name shapes).
  // (Compiler can't see _ on private types, so use real identifiers.)
  private sealed class TenantCreatedEvent { }

  private sealed class _StubTopicResolver(string topic) : ITopicRoutingStrategy {
    public string LastMessageTypeName { get; private set; } = "";
    public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) {
      LastMessageTypeName = messageType.FullName ?? "";
      return topic;
    }
  }

  [Test]
  public async Task Constructor_NullResolver_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new DomainTopicOutboxStrategy(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task DefaultConstructor_UsesNamespaceRoutingStrategyAsync() {
    // The parameterless constructor delegates to NamespaceRoutingStrategy,
    // which derives the topic from the type's namespace.
    var strategy = new DomainTopicOutboxStrategy();

    var destination = strategy.GetDestination(
      typeof(TenantCreatedEvent),
      new HashSet<string>(),
      MessageKind.Event);

    await Assert.That(destination.Address).IsNotNullOrEmpty()
      .Because("namespace routing produces a non-empty topic from the type's namespace");
    await Assert.That(destination.RoutingKey).IsEqualTo("tenantcreatedevent");
  }

  [Test]
  public async Task GetDestination_DelegatesTopicResolutionToResolverAsync() {
    var resolver = new _StubTopicResolver("custom.topic");
    var strategy = new DomainTopicOutboxStrategy(resolver);

    var destination = strategy.GetDestination(
      typeof(TenantCreatedEvent),
      new HashSet<string>(),
      MessageKind.Event);

    await Assert.That(destination.Address).IsEqualTo("custom.topic");
    await Assert.That(resolver.LastMessageTypeName).Contains(nameof(TenantCreatedEvent));
  }

  [Test]
  public async Task GetDestination_RoutingKeyIsLowercaseTypeNameAsync() {
    var resolver = new _StubTopicResolver("any");
    var strategy = new DomainTopicOutboxStrategy(resolver);

    var destination = strategy.GetDestination(
      typeof(TenantCreatedEvent),
      new HashSet<string>(),
      MessageKind.Event);

    // The routing key is messageType.Name.ToLowerInvariant() — simple name,
    // not full name, lowercase regardless of original casing.
    await Assert.That(destination.RoutingKey).IsEqualTo("tenantcreatedevent");
  }

  [Test]
  public async Task GetDestination_NullMessageType_ThrowsArgumentNullExceptionAsync() {
    var strategy = new DomainTopicOutboxStrategy(new _StubTopicResolver("t"));

    await Assert.That(() => strategy.GetDestination(null!, new HashSet<string>(), MessageKind.Event))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task GetDestination_NullOwnedDomains_ThrowsArgumentNullExceptionAsync() {
    var strategy = new DomainTopicOutboxStrategy(new _StubTopicResolver("t"));

    await Assert.That(() => strategy.GetDestination(typeof(TenantCreatedEvent), null!, MessageKind.Event))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task GetDestination_DoesNotCareAboutOwnedDomainsContentAsync() {
    // The outbox strategy returns a destination regardless of ownership —
    // it's an outbound strategy, not a routing filter.
    var resolver = new _StubTopicResolver("t");
    var strategy = new DomainTopicOutboxStrategy(resolver);

    var d1 = strategy.GetDestination(typeof(TenantCreatedEvent), new HashSet<string>(), MessageKind.Event);
    var d2 = strategy.GetDestination(typeof(TenantCreatedEvent), new HashSet<string> { "myapp.users" }, MessageKind.Event);

    await Assert.That(d1.Address).IsEqualTo(d2.Address);
    await Assert.That(d1.RoutingKey).IsEqualTo(d2.RoutingKey);
  }
}
