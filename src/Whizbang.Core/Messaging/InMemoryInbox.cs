using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// In-memory implementation of IInbox for testing and single-process scenarios.
/// Thread-safe using ConcurrentDictionary.
/// NOT suitable for production use across multiple processes.
/// </summary>
public class InMemoryInbox : IInbox {
  private readonly ConcurrentDictionary<MessageId, InboxRecord> _processed = new();

  /// <inheritdoc />
  public Task<bool> HasProcessedAsync(MessageId messageId, CancellationToken cancellationToken = default) {
    return Task.FromResult(_processed.ContainsKey(messageId));
  }

  /// <inheritdoc />
  public Task MarkProcessedAsync(MessageId messageId, string handlerName, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(handlerName);

    _processed.TryAdd(messageId, new InboxRecord(handlerName, DateTimeOffset.UtcNow));
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task CleanupExpiredAsync(TimeSpan retention, CancellationToken cancellationToken = default) {
    var cutoff = DateTimeOffset.UtcNow - retention;
    var expiredKeys = _processed
      .Where(kvp => kvp.Value.ProcessedAt < cutoff)
      .Select(kvp => kvp.Key)
      .ToList();

    foreach (var key in expiredKeys) {
      _processed.TryRemove(key, out _);
    }

    return Task.CompletedTask;
  }

  private record InboxRecord(string HandlerName, DateTimeOffset ProcessedAt);
}
