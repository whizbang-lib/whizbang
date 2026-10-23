// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Threading.Channels;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Workers;

/// <summary>One lease renewal made through the work coordinator.</summary>
internal sealed record HeldLeaseRenewal(WorkCategory Category, IReadOnlyList<Guid> Ids, int LeaseSeconds, DateTimeOffset At);

/// <summary>
/// A work coordinator for the outbox publisher's not-ready hold (#1606). It keeps each claimed row's lease expiry
/// on the given clock, as <c>wh_outbox.lease_expiry</c> does, and records every renewal; a renewal that arrives
/// after a lease lapsed is recorded in <see cref="Lapsed"/>, because by then another claim could have taken the
/// row and charged it an attempt. Each renewal is also a signal a test can wait on.
/// </summary>
/// <param name="clock">The clock leases are measured on.</param>
/// <param name="claimLease">The lease a row has when it is claimed.</param>
internal sealed class HeldLeaseCoordinator(TimeProvider clock, TimeSpan claimLease) : IWorkCoordinator {
  private static readonly TimeSpan _signal = TimeSpan.FromSeconds(30);
  private readonly ConcurrentDictionary<Guid, DateTimeOffset> _expiry = new();
  private readonly Channel<bool> _renewed = Channel.CreateUnbounded<bool>();
  private int _waitedFor;

  /// <summary>Every renewal made, in order.</summary>
  public ConcurrentQueue<HeldLeaseRenewal> Renewals { get; } = new();

  /// <summary>Rows renewed after their lease had already lapsed.</summary>
  public ConcurrentQueue<Guid> Lapsed { get; } = new();

  /// <summary>How many of the next renewals throw, as an unreachable database would.</summary>
  public int FailingRenewals { get; set; }

  /// <summary>
  /// When set, a renewal signals it on entry and then waits until its cancellation token fires, as a renewal
  /// in flight at shutdown does.
  /// </summary>
  public TaskCompletionSource? BlockRenewalsUntilCanceled { get; set; }

  /// <summary>Records <paramref name="messageId"/> as claimed now, with the claim lease.</summary>
  public void Claim(Guid messageId) => _expiry[messageId] = clock.GetUtcNow() + claimLease;

  /// <summary>When <paramref name="messageId"/>'s lease expires.</summary>
  public DateTimeOffset ExpiryOf(Guid messageId) => _expiry[messageId];

  /// <summary>Waits until <paramref name="total"/> renewals have been made in all.</summary>
  public async Task WaitForRenewalsAsync(int total) {
    while (_waitedFor < total) {
      await _renewed.Reader.ReadAsync().AsTask().WaitAsync(_signal);
      _waitedFor++;
    }
  }

  public Task<int> RenewLeasesAsync(
      WorkCategory category, IReadOnlyList<Guid> ids, int leaseSeconds = 300, CancellationToken cancellationToken = default) {
    if (BlockRenewalsUntilCanceled is { } entered) {
      entered.TrySetResult();
      return _waitForCancellationAsync(cancellationToken);
    }
    if (FailingRenewals > 0) {
      FailingRenewals--;
      return Task.FromException<int>(new InvalidOperationException("database unreachable"));
    }
    var now = clock.GetUtcNow();
    foreach (var id in ids) {
      if (_expiry.TryGetValue(id, out var expiry) && expiry <= now) {
        Lapsed.Enqueue(id);
      }
      _expiry[id] = now + TimeSpan.FromSeconds(leaseSeconds);
    }
    Renewals.Enqueue(new HeldLeaseRenewal(category, [.. ids], leaseSeconds, now));
    _renewed.Writer.TryWrite(true);
    return Task.FromResult(ids.Count);
  }

  private static async Task<int> _waitForCancellationAsync(CancellationToken cancellationToken) {
    var canceled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var _ = cancellationToken.Register(() => canceled.TrySetCanceled(cancellationToken));
    return await canceled.Task;
  }

  public Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
      IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<OutboxBatchRow>>([]);
  public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(Guid.CreateVersion7());
  public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
    Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
  public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
    Task.FromResult<PerspectiveCursorInfo?>(null);
}
