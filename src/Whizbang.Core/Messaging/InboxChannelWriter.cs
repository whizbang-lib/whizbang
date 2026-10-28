// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  private readonly ConcurrentDictionary<Guid, Held> _inFlight = new();
  private long _heldBytes;
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
  /// also what stops an entry stranded by a hung or canceled task from blocking its message
  /// permanently — the failure mode that made an earlier in-memory filter on this path
  /// unrecoverable without restarting the process.
  /// </remarks>
  private void _evictLapsed() {
    var cutoff = _timeProvider.GetUtcNow() - _inFlightMaxAge;

    // Measured from the last renewal (#1788): a row whose lease is being kept is still held, however long ago it
    // arrived. Only rows nobody renews age out.
    foreach (var entry in _inFlight) {
      if (entry.Value.LastRenewedAt <= cutoff) {
        RemoveInFlight(entry.Key);
      }
    }
  }

  /// <summary>Holds the row unless it is already held; a held row's payload bytes count once (#1793).</summary>
  private void _track(InboxWork work) {
    var held = new Held(work.MessageType, _timeProvider.GetUtcNow(), InboxPayload.Bytes(work.Envelope.Payload));
    if (_inFlight.TryAdd(work.MessageId, held)) {
      Interlocked.Add(ref _heldBytes, held.Bytes);
    }
  }

  /// <inheritdoc />
  public ChannelReader<InboxWork> Reader => _channel.Reader;

  /// <inheritdoc />
  public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) {
    _evictLapsed();
    _track(work);
    return _channel.Writer.WriteAsync(work, ct);
  }

  /// <inheritdoc />
  public bool TryWrite(InboxWork work) {
    if (_channel.Writer.TryWrite(work)) {
      _evictLapsed();
      _track(work);
      return true;
    }
    return false;
  }

  /// <inheritdoc />
  public bool IsInFlight(Guid messageId) => _inFlight.ContainsKey(messageId);

  /// <inheritdoc />
  public void RemoveInFlight(Guid messageId) {
    // Every removal comes through here, so the held bytes are released exactly once per row (#1793).
    if (_inFlight.TryRemove(messageId, out var held)) {
      Interlocked.Add(ref _heldBytes, -held.Bytes);
    }
  }


  /// <inheritdoc />
  public bool ShouldRenewLease(Guid messageId) {
    if (_inFlight.TryGetValue(messageId, out var held)) {
      return _timeProvider.GetUtcNow() - held.TrackedAt > _leaseRenewalThreshold;
    }
    return false;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Messaging/InboxChannelWriterHeldRowsTests.cs</tests>
  public long HeldPayloadBytes => Interlocked.Read(ref _heldBytes);

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Messaging/InboxChannelWriterHeldRowsTests.cs</tests>
  public bool TryBeginDispatch(Guid messageId) {
    if (!_inFlight.TryGetValue(messageId, out var held)) {
      return false;
    }
    lock (held.Gate) {
      if (held.State != InboxHoldState.Queued) {
        return false;
      }
      held.State = InboxHoldState.Dispatching;
      held.Renewals = 0;
      return true;
    }
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Messaging/InboxChannelWriterHeldRowsTests.cs</tests>
  public void MarkCommitPending(Guid messageId) {
    if (_inFlight.TryGetValue(messageId, out var held)) {
      lock (held.Gate) {
        held.State = InboxHoldState.CommitPending;
        held.Renewals = 0;
      }
    }
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Messaging/InboxChannelWriterHeldRowsTests.cs</tests>
  public void EndDispatch(Guid messageId) {
    if (_inFlight.TryGetValue(messageId, out var held) && held.State != InboxHoldState.CommitPending) {
      RemoveInFlight(messageId);
    }
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Messaging/InboxChannelWriterHeldRowsTests.cs</tests>
  public InboxLeaseSweep SweepLeases(TimeSpan renewAfter, int maxRenewals) {
    var now = _timeProvider.GetUtcNow();
    var renew = new List<Guid>();
    var lapsed = new List<LapsedInboxLease>();
    foreach (var entry in _inFlight) {
      var held = entry.Value;
      lock (held.Gate) {
        if (now - held.LastRenewedAt < renewAfter) {
          continue;
        }
        if (held.Renewals >= maxRenewals) {
          RemoveInFlight(entry.Key);
          lapsed.Add(new LapsedInboxLease(entry.Key, held.MessageType, held.State, held.Renewals));
          continue;
        }
        held.Renewals++;
        held.LastRenewedAt = now;
        renew.Add(entry.Key);
      }
    }
    return renew.Count == 0 && lapsed.Count == 0 ? InboxLeaseSweep.Empty : new InboxLeaseSweep(renew, lapsed);
  }

  /// <summary>One row this process holds: where it is, when its lease was last kept, and how many renewals it has used.</summary>
  private sealed class Held(string messageType, DateTimeOffset trackedAt, long bytes) {
    public long Bytes { get; } = bytes;
    public Lock Gate { get; } = new();
    public string MessageType { get; } = messageType;
    public DateTimeOffset TrackedAt { get; } = trackedAt;
    public DateTimeOffset LastRenewedAt { get; set; } = trackedAt;
    public InboxHoldState State { get; set; }
    public int Renewals { get; set; }
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
