using System.Runtime.CompilerServices;
using Azure.Messaging.ServiceBus.Administration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Covers the default interface method body on <see cref="IServiceBusAdminClient"/>.
/// Admin clients that cannot enumerate subscriptions inherit an empty sequence rather
/// than throwing, so callers can iterate unconditionally.
/// </summary>
public class ServiceBusAdminClientDefaultsTests {

  /// <summary>
  /// Implements only the abstract members so <c>GetSubscriptionsAsync</c> stays inherited.
  /// Everything else throws, so a test that strays past the default fails loudly.
  /// </summary>
  private sealed class MinimalAdminClient : IServiceBusAdminClient {
    public Task<NamespaceProperties> GetNamespacePropertiesAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<bool> TopicExistsAsync(string topicName, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task CreateTopicAsync(string topicName, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<bool> SubscriptionExistsAsync(
        string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task CreateSubscriptionAsync(
        string topicName, string subscriptionName, int maxDeliveryCount, TimeSpan lockDuration,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task CreateSubscriptionAsync(
        string topicName, string subscriptionName, bool requiresSession, int maxDeliveryCount,
        TimeSpan lockDuration, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task UpdateSubscriptionLockDurationAsync(
        string topicName, string subscriptionName, TimeSpan lockDuration,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<SubscriptionProperties> GetSubscriptionAsync(
        string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task DeleteSubscriptionAsync(
        string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<long> GetSubscriptionActiveMessageCountAsync(
        string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<RuleProperties> GetRulesAsync(
        string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task DeleteRuleAsync(
        string topicName, string subscriptionName, string ruleName,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task CreateRuleAsync(
        string topicName, string subscriptionName, CreateRuleOptions options,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
  }

  [Test]
  public async Task GetSubscriptionsAsync_WhenNotOverridden_YieldsNoSubscriptionsAsync() {
    IServiceBusAdminClient client = new MinimalAdminClient();

    var count = 0;
    await foreach (var _ in client.GetSubscriptionsAsync("orders")) {
      count++;
    }

    await Assert.That(count).IsEqualTo(0);
  }

  [Test]
  public async Task GetSubscriptionsAsync_WhenNotOverridden_IsSafeToEnumerateTwiceAsync() {
    IServiceBusAdminClient client = new MinimalAdminClient();

    var first = 0;
    await foreach (var _ in client.GetSubscriptionsAsync("orders")) {
      first++;
    }

    var second = 0;
    await foreach (var _ in client.GetSubscriptionsAsync("orders")) {
      second++;
    }

    await Assert.That(first).IsEqualTo(0);
    await Assert.That(second).IsEqualTo(0);
  }

  [Test]
  public async Task GetSubscriptionsAsync_WhenNotOverridden_HonorsCancellationTokenParameterAsync() {
    IServiceBusAdminClient client = new MinimalAdminClient();
    using var cts = new CancellationTokenSource();

    var count = 0;
    await foreach (var _ in client.GetSubscriptionsAsync("orders", cts.Token)) {
      count++;
    }

    await Assert.That(count).IsEqualTo(0);
  }
}
