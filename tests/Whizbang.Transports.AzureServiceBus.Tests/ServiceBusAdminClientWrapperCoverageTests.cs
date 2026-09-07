using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.AzureServiceBus;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Coverage-round-23 targets for <see cref="ServiceBusAdminClientWrapper.GetSubscriptionsAsync"/> --
/// the "list every subscription on a topic" enumerator, the one member of this wrapper the sibling
/// <see cref="ServiceBusAdminClientWrapperTests"/> suite never exercises.
/// </summary>
/// <remarks>
/// Structurally this method is identical to <c>GetRulesAsync</c> (already covered in the sibling
/// suite): it forwards to <c>ServiceBusAdministrationClient.GetSubscriptionsAsync(string,
/// CancellationToken)</c>, which is <c>public virtual</c> on the SDK type -- the same documented
/// mocking seam (protected parameterless constructor + virtual methods) the sibling suite's
/// <c>FakeAdminClient</c> already relies on. None of the target lines require a live namespace: the
/// wrapper never talks to the admin plane itself, it only forwards to whatever
/// <see cref="ServiceBusAdministrationClient"/> instance it was constructed with, and that instance
/// is fully substitutable here -- unlike the connection-retry residue recorded elsewhere (entries
/// AF, AP, BS), which sits downstream of a real management-plane round trip with no such seam.
/// </remarks>
/// <docs>messaging/transports/azure-service-bus#admin-client</docs>
/// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusAdminClientWrapper.cs</tests>
public class ServiceBusAdminClientWrapperCoverageTests {

  // If cross-page enumeration regressed to only the first page, a reconciliation pass that lists a
  // topic's subscriptions to find stale ones would silently stop seeing everything past the first
  // page and leave those subscriptions un-drained.
  [Test]
  public async Task GetSubscriptionsAsync_EnumeratesAllPagesAsync() {
    var fake = new FakeAdminClient();
    // lockDuration must be supplied: the model factory leaves it at TimeSpan.Zero and the
    // SubscriptionProperties setter rejects a non-positive value.
    // SubscriptionProperties validates in its setters, and the model factory's defaults fail
    // several of them: LockDuration must be positive, DefaultMessageTimeToLive and AutoDeleteOnIdle
    // each have a minimum, MaxDeliveryCount must be at least 1, and UserMetadata may not be null.
    // The helper supplies all of them.
    fake.Subscriptions.Add(_subscription("billing-sub"));
    fake.Subscriptions.Add(_subscription("shipping-sub"));
    var wrapper = new ServiceBusAdminClientWrapper(fake);

    var names = new List<string>();
    await foreach (var subscription in wrapper.GetSubscriptionsAsync("orders-topic")) {
      names.Add(subscription.SubscriptionName);
    }

    // The fake serves one subscription per page, so this locks cross-page enumeration.
    await Assert.That(names).Count().IsEqualTo(2);
    await Assert.That(names[0]).IsEqualTo("billing-sub");
    await Assert.That(names[1]).IsEqualTo("shipping-sub");
  }

  // If the wrapper hung or threw instead of completing cleanly on an empty page set, checking a
  // freshly provisioned topic (no subscriptions yet) would break whatever polling or provisioning
  // loop calls this method.
  [Test]
  public async Task GetSubscriptionsAsync_NoSubscriptions_YieldsNothingAsync() {
    var fake = new FakeAdminClient();
    var wrapper = new ServiceBusAdminClientWrapper(fake);

    var count = 0;
    await foreach (var _ in wrapper.GetSubscriptionsAsync("orders-topic")) {
      count++;
    }

    await Assert.That(count).IsEqualTo(0);
  }

  // If the topic name or cancellation token were dropped on the way to the admin client, this
  // would either silently enumerate the wrong topic's subscriptions or ignore a caller's shutdown
  // signal, hanging the enumeration past the point the caller asked it to stop.
  [Test]
  public async Task GetSubscriptionsAsync_PassesTopicNameAndCancellationTokenAsync() {
    using var cts = new CancellationTokenSource();
    var fake = new FakeAdminClient();
    var wrapper = new ServiceBusAdminClientWrapper(fake);

    await foreach (var _ in wrapper.GetSubscriptionsAsync("orders-topic", cts.Token)) {
      // Enumeration forces the lazy iterator to hit the admin client.
    }

    await Assert.That(fake.LastTopicName).IsEqualTo("orders-topic");
    await Assert.That(fake.LastCancellationToken).IsEqualTo(cts.Token);
  }

  // ===== Test doubles =====

  /// <summary>
  /// Mockable ServiceBusAdministrationClient (protected parameterless constructor + virtual
  /// methods) exposing only what <see cref="ServiceBusAdminClientWrapper.GetSubscriptionsAsync"/>
  /// needs -- a fresh, minimal double kept local to this file rather than reusing the sibling
  /// suite's private one.
  /// </summary>
  private sealed class FakeAdminClient : ServiceBusAdministrationClient {
    private static readonly Response _rawResponse = new FakeResponse();

    public List<SubscriptionProperties> Subscriptions { get; } = [];
    public string? LastTopicName { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }

    public override AsyncPageable<SubscriptionProperties> GetSubscriptionsAsync(
        string topicName, CancellationToken cancellationToken = default) {
      LastTopicName = topicName;
      LastCancellationToken = cancellationToken;
      // One subscription per page so callers exercise multi-page enumeration.
      var pages = Subscriptions
        .Select(subscription => Page<SubscriptionProperties>.FromValues([subscription], null, _rawResponse))
        .ToList();
      return AsyncPageable<SubscriptionProperties>.FromPages(pages);
    }
  }

  /// <summary>Minimal Azure.Response for wrapping canned values -- never inspected by the wrapper.</summary>
  private sealed class FakeResponse : Response {
    public override int Status => 200;
    public override string ReasonPhrase => "OK";
    public override Stream? ContentStream { get; set; }
    public override string ClientRequestId { get; set; } = "unit-test";

    public override void Dispose() {
      // No resources to release.
    }

    protected override bool ContainsHeader(string name) => false;

    protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];

    protected override bool TryGetHeader(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) {
      value = null;
      return false;
    }

    protected override bool TryGetHeaderValues(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IEnumerable<string>? values) {
      values = null;
      return false;
    }
  }

  /// <summary>A subscription whose validated TimeSpan properties all satisfy their setters.</summary>
  private static SubscriptionProperties _subscription(string subscriptionName) =>
    ServiceBusModelFactory.SubscriptionProperties(
      topicName: "orders-topic",
      subscriptionName: subscriptionName,
      lockDuration: TimeSpan.FromSeconds(30),
      defaultMessageTimeToLive: TimeSpan.FromDays(1),
      autoDeleteOnIdle: TimeSpan.FromDays(7),
      maxDeliveryCount: 10,
      userMetadata: string.Empty);
}
