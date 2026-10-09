// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Notifications;

namespace Whizbang.Core.Workers;

/// <summary>
/// The claimer's in-memory copy of the published partition assignment (#1254). Refreshed with one keyed read when the
/// assigner announces a publish, or when a claim reports the copy stale; used until its lease runs out; and never in
/// the way of a claim: with no usable copy, the claim ranks itself.
/// </summary>
/// <remarks>
/// <para>
/// The claim path pays nothing for the assignment while the copy is current: <see cref="ForClaimAsync"/> answers from
/// memory, and the claim checks the version it is handed with one read of a one-row table that replaces its own ranking.
/// A refresh happens only after a publish (the NOTIFY), after a claim found the copy superseded, or the first time.
/// </para>
/// <para>
/// The lease is kept as the time left on it by the database's clock, added to this process's clock at the moment of
/// the read, so the two clocks are never compared. A copy past its lease is not presented; the database is the
/// authority either way, and refuses a version that is no longer published or no longer leased.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionAssignmentCacheTests.cs:ForClaim_TheFirstTime_ReadsOnceAndPresentsTheVersionAsync</tests>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionAssignmentCacheTests.cs:ForClaim_AfterTheLeaseRunsOut_PresentsNothingAsync</tests>
public sealed partial class PartitionAssignmentCache : IPartitionAssignmentSource, INotifySubscription {
  /// <summary>The channel the assigner announces a publish on, with payload <c>epoch:revision</c>.</summary>
  public const string CHANNEL = "wh_partition_assignment";

  private readonly IPartitionAssignmentStore _store;
  private readonly TimeProvider _time;
  private readonly ILogger<PartitionAssignmentCache> _logger;
  private readonly Lock _gate = new();
  private PartitionAssignment? _current;
  private DateTimeOffset _expiresAtLocal;
  private int _stale = 1;

  /// <summary>Creates the cache. It reads nothing until the first claim asks.</summary>
  /// <param name="store">Where the assignment is read from.</param>
  /// <param name="logger">Logger.</param>
  /// <param name="timeProvider">The clock the lease is kept against; the system clock by default.</param>
  public PartitionAssignmentCache(
      IPartitionAssignmentStore store, ILogger<PartitionAssignmentCache> logger, TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(logger);
    _store = store;
    _logger = logger;
    _time = timeProvider ?? TimeProvider.System;
  }

  /// <inheritdoc />
  public PartitionAssignment? Current {
    get {
      lock (_gate) {
        return _current;
      }
    }
  }

  /// <inheritdoc />
  public event Action<PartitionAssignment?>? OnAssignmentChanged;

  /// <summary>
  /// Raised after every refresh read, with what was read (null when nothing is published). The signal a caller waits
  /// on to know that a refresh it caused has happened. Must not block.
  /// </summary>
  public event Action<PartitionAssignment?>? OnRefreshed;

  /// <summary>
  /// Raised when the assigner's announcement of a publish arrives, with its <c>epoch:revision</c> payload, before the
  /// copy is marked stale. The signal that this instance heard a publish. Must not block.
  /// </summary>
  public event Action<string>? OnPublishAnnounced;

  /// <inheritdoc />
  public string ChannelName => CHANNEL;

  /// <inheritdoc />
  /// <remarks>A publish marks the copy stale unless it already is this version; the read happens on the next claim.</remarks>
  public void OnNotification(string payload) {
    OnPublishAnnounced?.Invoke(payload);
    var current = Current;
    if (current is not null && string.Equals(payload, PayloadOf(current.Version), StringComparison.Ordinal)) {
      return;
    }
    MarkStale();
  }

  /// <inheritdoc />
  public void MarkStale() => Volatile.Write(ref _stale, 1);

  /// <inheritdoc />
  public async ValueTask<PartitionAssignmentVersion?> ForClaimAsync(Guid instanceId, CancellationToken cancellationToken) {
    if (Interlocked.Exchange(ref _stale, 0) == 1) {
      await _refreshAsync(cancellationToken).ConfigureAwait(false);
    }
    lock (_gate) {
      if (_current is null || _time.GetUtcNow() >= _expiresAtLocal || _current.RankOf(instanceId) is null) {
        return null;
      }
      return _current.Version;
    }
  }

  private async Task _refreshAsync(CancellationToken cancellationToken) {
    PartitionAssignmentRead? read;
    try {
      read = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // Keep the last copy until it expires, as the owner decided, and try again on the next claim.
      MarkStale();
      LogRefreshFailed(_logger, ex);
      return;
    }

    PartitionAssignment? changed = null;
    var raise = false;
    lock (_gate) {
      if (read is null) {
        raise = _current is not null;
        _current = null;
      } else {
        raise = _current?.Version != read.Assignment.Version;
        _current = read.Assignment;
        _expiresAtLocal = _time.GetUtcNow() + read.LeaseRemaining;
        changed = read.Assignment;
      }
    }
    OnRefreshed?.Invoke(read?.Assignment);
    if (raise) {
      OnAssignmentChanged?.Invoke(changed);
    }
  }

  internal static string PayloadOf(PartitionAssignmentVersion version) =>
    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{version.Epoch}:{version.Revision}");

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Reading the partition assignment failed; the cached copy is kept until it expires and the read is retried on the next claim.")]
  static partial void LogRefreshFailed(ILogger logger, Exception exception);
}
