using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Wraps IMessagePublishStrategy to add topic pool suffix for test isolation.
/// Transforms "products" → "products-{suffix}", "inventory" → "inventory-{suffix}", etc.
/// </summary>
public sealed class TestInstancePublishStrategy : IMessagePublishStrategy {
  private readonly IMessagePublishStrategy _inner;
  private readonly string _topicPoolSuffix;

  public TestInstancePublishStrategy(IMessagePublishStrategy inner, string topicPoolSuffix) {
    _inner = inner;
    _topicPoolSuffix = topicPoolSuffix;
  }

  public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
    return await _inner.IsReadyAsync(cancellationToken);
  }

  public async Task<MessagePublishResult> PublishAsync(
    OutboxWork work,
    CancellationToken cancellationToken = default) {
    // Modify the destination to add the topic pool suffix
    var suffixedWork = work with {
      Destination = $"{work.Destination}-{_topicPoolSuffix}"
    };

    return await _inner.PublishAsync(suffixedWork, cancellationToken);
  }
}
