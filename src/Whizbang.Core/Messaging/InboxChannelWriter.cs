using System.Collections.Concurrent;
using System.Diagnostics;
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
  // Track seen message IDs with timestamp for cooldown-based dedup.
  // Messages stay in the set for a cooldown period after processing
  // to prevent re-queuing while the completion flush is in transit.
  private readonly ConcurrentDictionary<Guid, long> _seen = new();
  private static readonly long _cooldownTicks = TimeSpan.FromSeconds(30).Ticks;

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
    if (!_tryAddOrExpired(work.MessageId)) {
      return ValueTask.CompletedTask; // Debounced — already seen or in cooldown
    }

    var result = _channel.Writer.WriteAsync(work, ct);
    SignalNewInboxWorkAvailable();
    return result;
  }

  /// <inheritdoc />
  public bool TryWrite(InboxWork work) {
    if (!_tryAddOrExpired(work.MessageId)) {
      return true; // Debounced (return true = "handled")
    }

    var written = _channel.Writer.TryWrite(work);
    if (written) {
      SignalNewInboxWorkAvailable();
    }
    return written;
  }

  /// <inheritdoc />
  public void MarkProcessed(Guid messageId) {
    // Don't remove — set cooldown timestamp. The message stays debounced
    // for _cooldownTicks after processing to prevent re-queuing while
    // the completion flush is in transit to the DB.
    _seen[messageId] = Stopwatch.GetTimestamp();
  }

  /// <summary>
  /// Returns true if the message should be queued (new or cooldown expired).
  /// Returns false if the message is already queued or in cooldown.
  /// </summary>
  private bool _tryAddOrExpired(Guid messageId) {
    if (_seen.TryAdd(messageId, Stopwatch.GetTimestamp())) {
      return true; // New message — queue it
    }

    // Already seen — check if cooldown expired
    if (_seen.TryGetValue(messageId, out var timestamp)) {
      var elapsed = Stopwatch.GetTimestamp() - timestamp;
      if (elapsed > _cooldownTicks) {
        // Cooldown expired — allow re-queue (update timestamp)
        _seen[messageId] = Stopwatch.GetTimestamp();
        return true;
      }
    }

    return false; // Still in cooldown — debounce
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
