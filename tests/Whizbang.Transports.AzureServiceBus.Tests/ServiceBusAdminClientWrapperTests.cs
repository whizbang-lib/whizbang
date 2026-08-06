using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.AzureServiceBus;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Unit regression locks for <see cref="ServiceBusAdminClientWrapper"/> — the thin
/// IServiceBusAdminClient adapter over the Azure SDK's ServiceBusAdministrationClient.
/// The SDK exposes a documented mocking surface (protected parameterless constructor +
/// virtual methods), so argument pass-through, options construction, Response unwrapping,
/// pageable enumeration, and exception propagation are all asserted without a live
/// namespace.
/// </summary>
[Timeout(10_000)]
public class ServiceBusAdminClientWrapperTests {

  // ===== Constructor =====

  [Test]
  public async Task Constructor_NullAdminClient_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new ServiceBusAdminClientWrapper(adminClient: null!))
      .Throws<ArgumentNullException>();
  }

  // ===== Namespace management =====

  [Test]
  public async Task GetNamespacePropertiesAsync_ReturnsUnwrappedValueAsync() {
    var (wrapper, fake) = _createWrapper();
    fake.NamespaceResult = ServiceBusModelFactory.NamespaceProperties(
      "unit-ns", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, MessagingSku.Standard, 0, "unit-alias");

    var result = await wrapper.GetNamespacePropertiesAsync();

    await Assert.That(result.Name).IsEqualTo("unit-ns");
    await Assert.That(result.Alias).IsEqualTo("unit-alias");
  }

  [Test]
  public async Task GetNamespacePropertiesAsync_PassesCancellationTokenAsync() {
    using var cts = new CancellationTokenSource();
    var (wrapper, fake) = _createWrapper();

    _ = await wrapper.GetNamespacePropertiesAsync(cts.Token);

    await Assert.That(fake.LastCancellationToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task GetNamespacePropertiesAsync_AdminClientThrows_PropagatesExceptionAsync() {
    var (wrapper, fake) = _createWrapper();
    fake.ThrowOnCall = new ServiceBusException(
      "unreachable", ServiceBusFailureReason.ServiceCommunicationProblem);

    await Assert.That(async () => await wrapper.GetNamespacePropertiesAsync())
      .Throws<ServiceBusException>();
  }

  // ===== Topic management =====

  [Test]
  public async Task TopicExistsAsync_TopicExists_ReturnsTrueAndPassesNameAsync() {
    var (wrapper, fake) = _createWrapper();
    fake.TopicExistsResult = true;

    var exists = await wrapper.TopicExistsAsync("orders-topic");

    await Assert.That(exists).IsTrue();
    await Assert.That(fake.LastTopicName).IsEqualTo("orders-topic");
  }

  [Test]
  public async Task TopicExistsAsync_TopicMissing_ReturnsFalseAsync() {
    var (wrapper, fake) = _createWrapper();
    fake.TopicExistsResult = false;

    var exists = await wrapper.TopicExistsAsync("ghost-topic");

    await Assert.That(exists).IsFalse();
  }

  [Test]
  public async Task TopicExistsAsync_AdminClientThrows_PropagatesExceptionAsync() {
    var (wrapper, fake) = _createWrapper();
    fake.ThrowOnCall = new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy);

    await Assert.That(async () => await wrapper.TopicExistsAsync("orders-topic"))
      .Throws<ServiceBusException>();
  }

  [Test]
  public async Task CreateTopicAsync_BuildsOptionsWithSupportOrderingAsync() {
    var (wrapper, fake) = _createWrapper();

    await wrapper.CreateTopicAsync("orders-topic");

    var options = fake.LastCreateTopicOptions;
    await Assert.That(options).IsNotNull();
    await Assert.That(options!.Name).IsEqualTo("orders-topic");
    await Assert.That(options.SupportOrdering).IsTrue()
      .Because("ordering support is the wrapper's provisioning contract for topics");
  }

  // ===== Subscription management =====

  [Test]
  public async Task SubscriptionExistsAsync_PassesBothNamesAndUnwrapsAsync() {
    var (wrapper, fake) = _createWrapper();
    fake.SubscriptionExistsResult = true;

    var exists = await wrapper.SubscriptionExistsAsync("orders-topic", "billing-sub");

    await Assert.That(exists).IsTrue();
    await Assert.That(fake.LastTopicName).IsEqualTo("orders-topic");
    await Assert.That(fake.LastSubscriptionName).IsEqualTo("billing-sub");
  }

  [Test]
  public async Task CreateSubscriptionAsync_WithMaxDeliveryCount_BuildsOptionsAsync() {
    var (wrapper, fake) = _createWrapper();

    await wrapper.CreateSubscriptionAsync("orders-topic", "billing-sub", maxDeliveryCount: 7);

    var options = fake.LastCreateSubscriptionOptions;
    await Assert.That(options).IsNotNull();
    await Assert.That(options!.TopicName).IsEqualTo("orders-topic");
    await Assert.That(options.SubscriptionName).IsEqualTo("billing-sub");
    await Assert.That(options.MaxDeliveryCount).IsEqualTo(7);
    await Assert.That(options.RequiresSession).IsFalse()
      .Because("the two-argument overload must not opt the subscription into sessions");
  }

  [Test]
  public async Task CreateSubscriptionAsync_WithRequiresSession_BuildsOptionsAsync() {
    var (wrapper, fake) = _createWrapper();

    await wrapper.CreateSubscriptionAsync(
      "orders-topic", "billing-sub", requiresSession: true, maxDeliveryCount: 3);

    var options = fake.LastCreateSubscriptionOptions;
    await Assert.That(options).IsNotNull();
    await Assert.That(options!.TopicName).IsEqualTo("orders-topic");
    await Assert.That(options.SubscriptionName).IsEqualTo("billing-sub");
    await Assert.That(options.RequiresSession).IsTrue();
    await Assert.That(options.MaxDeliveryCount).IsEqualTo(3);
  }

  [Test]
  public async Task GetSubscriptionAsync_ReturnsUnwrappedPropertiesAsync() {
    var (wrapper, fake) = _createWrapper();
    fake.SubscriptionResult = ServiceBusModelFactory.SubscriptionProperties(
      topicName: "orders-topic",
      subscriptionName: "billing-sub",
      lockDuration: TimeSpan.FromMinutes(1),
      requiresSession: true,
      defaultMessageTimeToLive: TimeSpan.FromDays(14),
      autoDeleteOnIdle: TimeSpan.FromDays(30),
      maxDeliveryCount: 10,
      userMetadata: string.Empty);

    var result = await wrapper.GetSubscriptionAsync("orders-topic", "billing-sub");

    await Assert.That(result.TopicName).IsEqualTo("orders-topic");
    await Assert.That(result.SubscriptionName).IsEqualTo("billing-sub");
    await Assert.That(result.RequiresSession).IsTrue();
    await Assert.That(fake.LastTopicName).IsEqualTo("orders-topic");
    await Assert.That(fake.LastSubscriptionName).IsEqualTo("billing-sub");
  }

  [Test]
  public async Task DeleteSubscriptionAsync_PassesNamesAsync() {
    var (wrapper, fake) = _createWrapper();

    await wrapper.DeleteSubscriptionAsync("orders-topic", "stale-sub");

    await Assert.That(fake.DeleteSubscriptionCalls).IsEqualTo(1);
    await Assert.That(fake.LastTopicName).IsEqualTo("orders-topic");
    await Assert.That(fake.LastSubscriptionName).IsEqualTo("stale-sub");
  }

  [Test]
  public async Task GetSubscriptionActiveMessageCountAsync_ReturnsActiveMessageCountAsync() {
    var (wrapper, fake) = _createWrapper();
    fake.SubscriptionRuntimeResult = ServiceBusModelFactory.SubscriptionRuntimeProperties(
      topicName: "orders-topic",
      subscriptionName: "billing-sub",
      activeMessageCount: 42);

    var result = await wrapper.GetSubscriptionActiveMessageCountAsync("orders-topic", "billing-sub");

    await Assert.That(result).IsEqualTo(42L);
  }

  [Test]
  public async Task GetSubscriptionActiveMessageCountAsync_PassesArgumentsAndCancellationTokenAsync() {
    using var cts = new CancellationTokenSource();
    var (wrapper, fake) = _createWrapper();

    _ = await wrapper.GetSubscriptionActiveMessageCountAsync("orders-topic", "billing-sub", cts.Token);

    await Assert.That(fake.LastTopicName).IsEqualTo("orders-topic");
    await Assert.That(fake.LastSubscriptionName).IsEqualTo("billing-sub");
    await Assert.That(fake.LastCancellationToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task GetSubscriptionActiveMessageCountAsync_AdminClientThrows_PropagatesExceptionAsync() {
    var (wrapper, fake) = _createWrapper();
    fake.ThrowOnCall = new ServiceBusException(
      "unreachable", ServiceBusFailureReason.ServiceCommunicationProblem);

    await Assert.That(async () => await wrapper.GetSubscriptionActiveMessageCountAsync("orders-topic", "billing-sub"))
      .Throws<ServiceBusException>();
  }

  // ===== Rule management =====

  [Test]
  public async Task GetRulesAsync_EnumeratesAllPagesAsync() {
    var (wrapper, fake) = _createWrapper();
    fake.Rules.Add(ServiceBusModelFactory.RuleProperties("$Default", new TrueRuleFilter()));
    fake.Rules.Add(ServiceBusModelFactory.RuleProperties("rule-a", new TrueRuleFilter()));
    fake.Rules.Add(ServiceBusModelFactory.RuleProperties("rule-b", new TrueRuleFilter()));

    var names = new List<string>();
    await foreach (var rule in wrapper.GetRulesAsync("orders-topic", "billing-sub")) {
      names.Add(rule.Name);
    }

    // The fake serves one rule per page, so this locks cross-page enumeration.
    await Assert.That(names).Count().IsEqualTo(3);
    await Assert.That(names[0]).IsEqualTo("$Default");
    await Assert.That(names[1]).IsEqualTo("rule-a");
    await Assert.That(names[2]).IsEqualTo("rule-b");
    await Assert.That(fake.LastTopicName).IsEqualTo("orders-topic");
    await Assert.That(fake.LastSubscriptionName).IsEqualTo("billing-sub");
  }

  [Test]
  public async Task GetRulesAsync_NoRules_YieldsNothingAsync() {
    var (wrapper, fake) = _createWrapper();

    var count = 0;
    await foreach (var _ in wrapper.GetRulesAsync("orders-topic", "billing-sub")) {
      count++;
    }

    await Assert.That(count).IsEqualTo(0);
  }

  [Test]
  public async Task GetRulesAsync_PassesCancellationTokenAsync() {
    using var cts = new CancellationTokenSource();
    var (wrapper, fake) = _createWrapper();

    await foreach (var _ in wrapper.GetRulesAsync("orders-topic", "billing-sub", cts.Token)) {
      // Enumeration forces the lazy iterator to hit the admin client.
    }

    await Assert.That(fake.LastCancellationToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task DeleteRuleAsync_PassesAllArgumentsAsync() {
    var (wrapper, fake) = _createWrapper();

    await wrapper.DeleteRuleAsync("orders-topic", "billing-sub", "$Default");

    await Assert.That(fake.DeleteRuleCalls).IsEqualTo(1);
    await Assert.That(fake.LastTopicName).IsEqualTo("orders-topic");
    await Assert.That(fake.LastSubscriptionName).IsEqualTo("billing-sub");
    await Assert.That(fake.LastRuleName).IsEqualTo("$Default");
  }

  [Test]
  public async Task CreateRuleAsync_PassesOptionsInstanceThroughAsync() {
    var (wrapper, fake) = _createWrapper();
    var options = new CreateRuleOptions("destination-filter", new SqlRuleFilter("Destination = 'billing'"));

    await wrapper.CreateRuleAsync("orders-topic", "billing-sub", options);

    await Assert.That(fake.LastTopicName).IsEqualTo("orders-topic");
    await Assert.That(fake.LastSubscriptionName).IsEqualTo("billing-sub");
    await Assert.That(ReferenceEquals(fake.LastCreateRuleOptions, options)).IsTrue()
      .Because("the wrapper must pass the caller's rule options through unmodified");
  }

  [Test]
  public async Task CreateRuleAsync_AdminClientThrows_PropagatesExceptionAsync() {
    var (wrapper, fake) = _createWrapper();
    fake.ThrowOnCall = new ServiceBusException(
      "entity exists", ServiceBusFailureReason.MessagingEntityAlreadyExists);
    var options = new CreateRuleOptions("dup-rule", new TrueRuleFilter());

    await Assert.That(async () => await wrapper.CreateRuleAsync("orders-topic", "billing-sub", options))
      .Throws<ServiceBusException>();
  }

  // ===== Helpers =====

  private static (ServiceBusAdminClientWrapper Wrapper, FakeAdminClient Fake) _createWrapper() {
    var fake = new FakeAdminClient();
    return (new ServiceBusAdminClientWrapper(fake), fake);
  }

  // ===== Test doubles =====

  /// <summary>
  /// Mockable ServiceBusAdministrationClient (protected parameterless constructor + virtual
  /// methods) that records arguments and serves canned results without any network access.
  /// When <see cref="ThrowOnCall"/> is set, every operation throws it instead.
  /// </summary>
  private sealed class FakeAdminClient : ServiceBusAdministrationClient {
    private static readonly Response _rawResponse = new FakeResponse();

    public NamespaceProperties NamespaceResult { get; set; } = ServiceBusModelFactory.NamespaceProperties(
      "default-ns", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, MessagingSku.Standard, 0, "default-alias");
    public bool TopicExistsResult { get; set; }
    public bool SubscriptionExistsResult { get; set; }
    public SubscriptionProperties SubscriptionResult { get; set; } = ServiceBusModelFactory.SubscriptionProperties(
      topicName: "default-topic",
      subscriptionName: "default-sub",
      lockDuration: TimeSpan.FromMinutes(1),
      requiresSession: false,
      defaultMessageTimeToLive: TimeSpan.FromDays(14),
      autoDeleteOnIdle: TimeSpan.FromDays(30),
      maxDeliveryCount: 10,
      userMetadata: string.Empty);
    public SubscriptionRuntimeProperties SubscriptionRuntimeResult { get; set; } = ServiceBusModelFactory.SubscriptionRuntimeProperties(
      topicName: "default-topic",
      subscriptionName: "default-sub",
      activeMessageCount: 0);
    public List<RuleProperties> Rules { get; } = [];
    public Exception? ThrowOnCall { get; set; }

    public string? LastTopicName { get; private set; }
    public string? LastSubscriptionName { get; private set; }
    public string? LastRuleName { get; private set; }
    public CreateTopicOptions? LastCreateTopicOptions { get; private set; }
    public CreateSubscriptionOptions? LastCreateSubscriptionOptions { get; private set; }
    public CreateRuleOptions? LastCreateRuleOptions { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }
    public int DeleteSubscriptionCalls { get; private set; }
    public int DeleteRuleCalls { get; private set; }

    public override Task<Response<NamespaceProperties>> GetNamespacePropertiesAsync(
      CancellationToken cancellationToken = default) {
      _throwIfConfigured();
      LastCancellationToken = cancellationToken;
      return Task.FromResult(Response.FromValue(NamespaceResult, _rawResponse));
    }

    public override Task<Response<bool>> TopicExistsAsync(
      string name, CancellationToken cancellationToken = default) {
      _throwIfConfigured();
      LastTopicName = name;
      LastCancellationToken = cancellationToken;
      return Task.FromResult(Response.FromValue(TopicExistsResult, _rawResponse));
    }

    public override Task<Response<TopicProperties>> CreateTopicAsync(
      CreateTopicOptions options, CancellationToken cancellationToken = default) {
      _throwIfConfigured();
      LastCreateTopicOptions = options;
      LastCancellationToken = cancellationToken;
      var created = ServiceBusModelFactory.TopicProperties(
        options.Name, 1024L, false, TimeSpan.FromDays(14), TimeSpan.FromDays(30),
        TimeSpan.FromMinutes(1), true, EntityStatus.Active, false);
      return Task.FromResult(Response.FromValue(created, _rawResponse));
    }

    public override Task<Response<bool>> SubscriptionExistsAsync(
      string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      _throwIfConfigured();
      LastTopicName = topicName;
      LastSubscriptionName = subscriptionName;
      LastCancellationToken = cancellationToken;
      return Task.FromResult(Response.FromValue(SubscriptionExistsResult, _rawResponse));
    }

    public override Task<Response<SubscriptionProperties>> CreateSubscriptionAsync(
      CreateSubscriptionOptions options, CancellationToken cancellationToken = default) {
      _throwIfConfigured();
      LastCreateSubscriptionOptions = options;
      LastCancellationToken = cancellationToken;
      return Task.FromResult(Response.FromValue(SubscriptionResult, _rawResponse));
    }

    public override Task<Response<SubscriptionProperties>> GetSubscriptionAsync(
      string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      _throwIfConfigured();
      LastTopicName = topicName;
      LastSubscriptionName = subscriptionName;
      LastCancellationToken = cancellationToken;
      return Task.FromResult(Response.FromValue(SubscriptionResult, _rawResponse));
    }

    public override Task<Response<SubscriptionRuntimeProperties>> GetSubscriptionRuntimePropertiesAsync(
      string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      _throwIfConfigured();
      LastTopicName = topicName;
      LastSubscriptionName = subscriptionName;
      LastCancellationToken = cancellationToken;
      return Task.FromResult(Response.FromValue(SubscriptionRuntimeResult, _rawResponse));
    }

    public override Task<Response> DeleteSubscriptionAsync(
      string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      _throwIfConfigured();
      DeleteSubscriptionCalls++;
      LastTopicName = topicName;
      LastSubscriptionName = subscriptionName;
      LastCancellationToken = cancellationToken;
      return Task.FromResult(_rawResponse);
    }

    public override AsyncPageable<RuleProperties> GetRulesAsync(
      string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      _throwIfConfigured();
      LastTopicName = topicName;
      LastSubscriptionName = subscriptionName;
      LastCancellationToken = cancellationToken;
      // One rule per page so callers exercise multi-page enumeration.
      var pages = Rules
        .Select(rule => Page<RuleProperties>.FromValues([rule], null, _rawResponse))
        .ToList();
      return AsyncPageable<RuleProperties>.FromPages(pages);
    }

    public override Task<Response> DeleteRuleAsync(
      string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default) {
      _throwIfConfigured();
      DeleteRuleCalls++;
      LastTopicName = topicName;
      LastSubscriptionName = subscriptionName;
      LastRuleName = ruleName;
      LastCancellationToken = cancellationToken;
      return Task.FromResult(_rawResponse);
    }

    public override Task<Response<RuleProperties>> CreateRuleAsync(
      string topicName, string subscriptionName, CreateRuleOptions options, CancellationToken cancellationToken = default) {
      _throwIfConfigured();
      LastTopicName = topicName;
      LastSubscriptionName = subscriptionName;
      LastCreateRuleOptions = options;
      LastCancellationToken = cancellationToken;
      var created = ServiceBusModelFactory.RuleProperties(options.Name, new TrueRuleFilter());
      return Task.FromResult(Response.FromValue(created, _rawResponse));
    }

    private void _throwIfConfigured() {
      if (ThrowOnCall is not null) {
        throw ThrowOnCall;
      }
    }
  }

  /// <summary>Minimal Azure.Response for wrapping canned values — never inspected by the wrapper.</summary>
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
}
