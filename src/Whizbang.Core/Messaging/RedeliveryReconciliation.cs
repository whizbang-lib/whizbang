// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// One (stream, tenant, event type) bucket of a redelivery bundle, as the receiving store found it: how many of the
/// bundle's events it already held, and how many of those adopted the origin identity the bundle carries.
/// </summary>
/// <param name="StreamId">The stream the stored events are on.</param>
/// <param name="TenantScope">The tenant the stored events are scoped to; null for none.</param>
/// <param name="EventType">The stored event type.</param>
/// <param name="Present">The bundle's events this bucket already held.</param>
/// <param name="Adopted">Of those, the events whose empty origin columns the bundle filled.</param>
/// <docs>resilience/stream-integrity#pre-stamp-history</docs>
public readonly record struct RedeliveredBucketPresence(
  Guid StreamId, string? TenantScope, string EventType, int Present, int Adopted);

/// <summary>What reconciling one redelivery bundle changed at the receiver.</summary>
/// <param name="Children">The bundle's children this receiver kept.</param>
/// <param name="Present">Children already in the event store.</param>
/// <param name="Adopted">Present children whose empty origin columns the bundle filled.</param>
/// <param name="MarkedUnhealable">Buckets newly recorded as ones repair cannot change.</param>
/// <docs>resilience/stream-integrity#pre-stamp-history</docs>
public readonly record struct RedeliveryReconciliation(int Children, int Present, int Adopted, int MarkedUnhealable) {
  /// <summary>Nothing was reconciled: no origin, no children, or no store that can answer.</summary>
  public static RedeliveryReconciliation None => default;
}

/// <summary>
/// Reconciles a redelivery bundle with the events the receiver already holds, so that repair converges.
/// </summary>
/// <remarks>
/// <para>
/// A redelivered event the receiver already stores is skipped by the store as a duplicate, so the redelivery cannot
/// change it. That is right for an event stored with its origin identity, and it is what kept history received before
/// the store recorded origins (empty origin columns) from ever verifying: origin-keyed verification counted it as
/// missing, repair asked for it, the redelivery was skipped, and the next check asked again. Here each such event
/// adopts the origin identity the bundle carries (<see cref="IWorkCoordinator.AdoptRedeliveredOriginAsync"/>), which
/// is what the next check needs to count it.
/// </para>
/// <para>
/// When every event of the bundle was already present and none adopted anything, the redelivery changed nothing and a
/// further one would ship the same events again. The buckets it covered are marked as ones repair cannot change
/// (<see cref="IIntegrityRepairLedger.MarkUnhealableBatchAsync"/>): repair stops asking for them, and each is reported
/// once, until its signature changes or it heals.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity#pre-stamp-history</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/RedeliveryReconcilerTests.cs</tests>
public static partial class RedeliveryReconciler {
  /// <summary>
  /// Reconciles a committed redelivery bundle's children with the receiver's event store. Best-effort: a failure is
  /// logged and leaves repair to ask again on its own schedule.
  /// </summary>
  /// <param name="composite">The redelivery bundle.</param>
  /// <param name="children">The children the receiver kept (its subscribed events).</param>
  /// <param name="services">The dispatch scope's services.</param>
  /// <param name="logger">Where the reports go.</param>
  /// <param name="now">The caller's clock.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>What the reconciliation changed.</returns>
  public static async Task<RedeliveryReconciliation> ReconcileAsync(
      IIdentityPreservingComposite composite, IReadOnlyList<InboxMessage> children, IServiceProvider services,
      ILogger logger, DateTimeOffset now, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(composite);
    ArgumentNullException.ThrowIfNull(children);
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(logger);
    var origin = composite.OriginServiceId;
    if (origin == Guid.Empty || children.Count == 0) {
      return RedeliveryReconciliation.None;
    }
    var coordinator = services.GetService<IWorkCoordinator>();
    if (coordinator is null) {
      return RedeliveryReconciliation.None;
    }
    try {
      // A zero sequence is the origin's "not stamped yet" on the wire, never a position.
      var buckets = await coordinator.AdoptRedeliveredOriginAsync(
        origin,
        [.. children.Select(c => c.MessageId)],
        [.. children.Select(c => c.SourceCommitSequence > 0 ? c.SourceCommitSequence : (long?)null)],
        cancellationToken).ConfigureAwait(false);
      if (buckets is null) {
        return RedeliveryReconciliation.None;   // a store that cannot answer: repair behaves as before
      }
      var present = buckets.Sum(b => b.Present);
      var adopted = buckets.Sum(b => b.Adopted);
      var metrics = services.GetService<StreamIntegrityMetrics>();
      if (adopted > 0) {
        metrics?.OriginsAdopted.Add(adopted);
        LogOriginsAdopted(logger, adopted, children.Count, origin);
        return new RedeliveryReconciliation(children.Count, present, adopted, 0);
      }
      // An event the store did not hold is stored by this bundle: the repair is working, nothing to conclude.
      if (present < children.Count) {
        return new RedeliveryReconciliation(children.Count, present, 0, 0);
      }
      var ledger = services.GetService<IIntegrityRepairLedger>() ?? services.GetService<IntegrityRepairLedger>();
      if (ledger is null) {
        return new RedeliveryReconciliation(children.Count, present, 0, 0);
      }
      var marked = await ledger.MarkUnhealableBatchAsync(
        [.. buckets.Select(b => new IntegrityRepairLedger.DivergenceKey(origin, b.TenantScope, b.EventType, b.StreamId))],
        now, cancellationToken).ConfigureAwait(false);
      foreach (var key in marked) {
        LogRepairCannotChangeBucket(logger, key.EventType, key.TenantScope, key.StreamId, origin);
      }
      if (marked.Count > 0) {
        metrics?.RepairsUnhealable.Add(marked.Count);
      }
      return new RedeliveryReconciliation(children.Count, present, 0, marked.Count);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      LogReconcileFailed(logger, children.Count, origin, ex);
      return RedeliveryReconciliation.None;
    }
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Redelivery from origin {OriginServiceId} filled the origin identity of {Adopted} of its {Children} event(s) "
            + "that were stored before origins were recorded; they now verify under that origin")]
  private static partial void LogOriginsAdopted(ILogger logger, int adopted, int children, Guid originServiceId);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Repair cannot change {EventType} (tenant {TenantScope}) on stream {StreamId} from origin {OriginServiceId}: "
            + "a redelivery answered with events that were all already stored under their origin, so it stops being "
            + "requested. It is reported again only if either side's digest changes. Compare the stream on both sides "
            + "(an event stored under another origin or another stream is the usual cause)")]
  private static partial void LogRepairCannotChangeBucket(
    ILogger logger, string eventType, string? tenantScope, Guid streamId, Guid originServiceId);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Reconciling a redelivery of {Children} event(s) from origin {OriginServiceId} with the event store failed; "
            + "repair asks again on its own schedule")]
  private static partial void LogReconcileFailed(ILogger logger, int children, Guid originServiceId, Exception exception);
}
