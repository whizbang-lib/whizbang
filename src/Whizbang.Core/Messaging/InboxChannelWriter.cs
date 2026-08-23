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
  private readonly TimeProvider _timeProvider;
  private static readonly TimeSpan _leaseRenewalThreshold = TimeSpan.FromSeconds(150); // Half of 300s lease

  /// <summary>
  /// How long an entry may stay tracked before it is treated as no longer in flight.
  /// </summary>
  /// <remarks>
  /// Set beyond the default 300s lease. Nothing removes an entry on the SUCCESS path — completion
  /// runs through the completion channel, and <c>RemoveInFlight</c> means "abandoned without
  /// completing" — so without an upper bound this set grows for the life of the process, and
  /// <see cref="ShouldRenewLease"/> keeps renewing leases for work that finished long ago.
  /// An entry older than the lease cannot legitimately be in flight: the lease has lapsed and the
  /// store will re-issue that row to whoever claims it next.
  /// </remarks>
  private static readonly TimeSpan _inFlightMaxAge = TimeSpan.FromSeconds(600);

  /// <summary>
  /// Initializes a new instance with an unbounded channel.
  /// </summary>
  /// <param name="timeProvider">Clock used to age out stale in-flight entries. Defaults to system time.</param>
  public InboxChannelWriter(TimeProvider? timeProvider = null) {
    _timeProvider = timeProvider ?? TimeProvider.System;
    _channel = Channel.CreateUnbounded<InboxWork>(new UnboundedChannelOptions {
      SingleReader = false,
      SingleWriter = false,
      AllowSynchronousContinuations = false
    });
  }

  /// <summary>
  /// Drops in-flight entries whose leases have lapsed, bounding the set.
  /// </summary>
  /// <remarks>
  /// Runs opportunistically on write rather than on a timer: writes are the only moment the set can
  /// grow, so amortising the sweep there keeps it bounded without adding a background worker. It is
  /// also what stops an entry stranded by a hung or cancelled task from blocking its message
  /// permanently — the failure mode that made an earlier in-memory filter on this path
  /// unrecoverable without restarting the process.
  /// </remarks>
  private void _evictLapsed() {
    var cutoff = _timeProvider.GetUtcNow() - _inFlightMaxAge;

    foreach (var entry in _inFlight) {
      if (entry.Value <= cutoff) {
        _inFlight.TryRemove(entry.Key, out _);
      }
    }
  }

  /// <inheritdoc />
  public ChannelReader<InboxWork> Reader => _channel.Reader;

  /// <inheritdoc />
  public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) {
    _evictLapsed();
    _inFlight.TryAdd(work.MessageId, _timeProvider.GetUtcNow());
    return _channel.Writer.WriteAsync(work, ct);
  }

  /// <inheritdoc />
  public bool TryWrite(InboxWork work) {
    if (_channel.Writer.TryWrite(work)) {
      _evictLapsed();
      _inFlight.TryAdd(work.MessageId, _timeProvider.GetUtcNow());
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
      return _timeProvider.GetUtcNow() - trackedAt > _leaseRenewalThreshold;
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
