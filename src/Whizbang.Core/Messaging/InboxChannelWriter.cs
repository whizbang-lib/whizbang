using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Default implementation of IInboxChannelWriter using System.Threading.Channels.
/// Creates an unbounded channel for inbox work distribution.
/// Deduplicates by MessageId — the same message written multiple times (e.g., from
/// repeated interval flushes) is only queued once.
/// Thread-safe for concurrent writers and readers.
/// </summary>
/// <docs>messaging/inbox-channel</docs>
public class InboxChannelWriter : IInboxChannelWriter {
  private readonly Channel<InboxWork> _channel;
  private readonly ConcurrentDictionary<Guid, byte> _seen = new();

  /// <summary>
  /// Initializes a new instance with an unbounded channel and deduplication.
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
    if (!_seen.TryAdd(work.MessageId, 0)) {
      return ValueTask.CompletedTask; // Already seen — debounce
    }

    var result = _channel.Writer.WriteAsync(work, ct);
    SignalNewInboxWorkAvailable();
    return result;
  }

  /// <inheritdoc />
  public bool TryWrite(InboxWork work) {
    if (!_seen.TryAdd(work.MessageId, 0)) {
      return true; // Already seen — debounce (return true = "handled")
    }

    var written = _channel.Writer.TryWrite(work);
    if (written) {
      SignalNewInboxWorkAvailable();
    }
    return written;
  }

  /// <summary>
  /// Removes a message ID from the dedup set after processing.
  /// Called by the publisher worker after processing inbox work.
  /// </summary>
  /// <param name="messageId">The message ID to remove from dedup tracking</param>
  public void MarkProcessed(Guid messageId) {
    _seen.TryRemove(messageId, out _);
  }

  /// <inheritdoc />
  public void Complete() {
    _channel.Writer.Complete();
  }

  /// <inheritdoc />
  public event Action? OnNewInboxWorkAvailable;

  /// <inheritdoc />
  public void SignalNewInboxWorkAvailable() {
    OnNewInboxWorkAvailable?.Invoke();
  }
}
