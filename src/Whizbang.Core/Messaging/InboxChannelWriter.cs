using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Default implementation of IInboxChannelWriter using System.Threading.Channels.
/// Creates an unbounded channel for inbox work distribution.
/// Tracks in-flight message IDs to prevent duplicate processing — same pattern as WorkChannelWriter.
/// Thread-safe for concurrent writers and readers.
/// </summary>
/// <docs>messaging/inbox-channel</docs>
/// <tests>tests/Whizbang.Core.Integration.Tests/WorkCoordinatorStrategyChannelIntegrationTests.cs</tests>
public class InboxChannelWriter : IInboxChannelWriter {
  private readonly Channel<InboxWork> _channel;
  private readonly ConcurrentDictionary<Guid, DateTimeOffset> _inFlight = new();
  private static readonly TimeSpan _leaseRenewalThreshold = TimeSpan.FromSeconds(150); // Half of 300s lease

  /// <summary>
  /// Initializes a new instance with an unbounded channel.
  /// </summary>
  public InboxChannelWriter() {
    _channel = Channel.CreateUnbounded<InboxWork>(new UnboundedChannelOptions {
      SingleReader = false,
      SingleWriter = false,
      AllowSynchronousContinuations = false
    });
  }

  /// <inheritdoc />
  public ChannelReader<InboxWork> Reader => _channel.Reader;

  /// <inheritdoc />
  public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) {
    _inFlight.TryAdd(work.MessageId, DateTimeOffset.UtcNow);
    return _channel.Writer.WriteAsync(work, ct);
  }

  /// <inheritdoc />
  public bool TryWrite(InboxWork work) {
    if (_channel.Writer.TryWrite(work)) {
      _inFlight.TryAdd(work.MessageId, DateTimeOffset.UtcNow);
      return true;
    }
    return false;
  }

  /// <inheritdoc />
  public bool IsInFlight(Guid messageId) => _inFlight.ContainsKey(messageId);

  /// <inheritdoc />
  public void RemoveInFlight(Guid messageId) => _inFlight.TryRemove(messageId, out _);

  /// <inheritdoc />
  public bool ShouldRenewLease(Guid messageId) {
    if (_inFlight.TryGetValue(messageId, out var trackedAt)) {
      return DateTimeOffset.UtcNow - trackedAt > _leaseRenewalThreshold;
    }
    return false;
  }

  /// <inheritdoc />
  public void Complete() {
    _channel.Writer.Complete();
  }

  /// <inheritdoc />
  public event Action? OnNewInboxWorkAvailable;

  /// <inheritdoc />
  public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
}
