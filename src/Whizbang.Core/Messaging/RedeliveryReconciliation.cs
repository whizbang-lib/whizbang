// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// <param name="RefusedTruncated">The bundle's events deduplication remembers and the event store does not hold that the
/// store refused to restore, because the stream's earlier history was truncated here (#1843).</param>
/// <param name="RefusedForeignOrigin">Of the present events, those with empty origin columns the bundle did not fill,
/// because the stored event's first hop names another service than the bundle's origin (#1844).</param>
/// <param name="OriginsFilled">Of <paramref name="Adopted"/>, the events whose origin was empty and is now the bundle's;
/// the rest already carried the origin and took only its sequence (#2099). Null when the store did not say, in which
/// case every adopted event is taken to have had its origin filled.</param>
/// <docs>resilience/stream-integrity#pre-stamp-history</docs>
public readonly record struct RedeliveredBucketPresence(
  Guid StreamId, string? TenantScope, string EventType, int Present, int Adopted, int RefusedTruncated = 0,
  int RefusedForeignOrigin = 0, int? OriginsFilled = null);

/// <summary>
/// One redelivery bundle as a batch reconcile takes it (#2099): the origin that published it, the origin's service name
/// from its first hop, and its events with their origin sequences, paired by position.
/// </summary>
/// <param name="OriginServiceId">The origin the bundle names.</param>
/// <param name="OriginServiceName">The origin's service name; null when the bundle carries none.</param>
/// <param name="EventIds">The bundle's event ids.</param>
/// <param name="CommitSequences">Each event's origin commit sequence, parallel to <paramref name="EventIds"/>; null when
/// the origin had not stamped it.</param>
/// <docs>resilience/stream-integrity#redelivery-reconciles-run-as-a-batch</docs>
public sealed record RedeliveredBundle(
  Guid OriginServiceId, string? OriginServiceName, IReadOnlyList<Guid> EventIds, IReadOnlyList<long?> CommitSequences);

/// <summary>
/// What a committed redelivery bundle answered, judged when its children commit (#2033): how many of its events the
/// event store already held, how many it did not (the bundle stores them), how many the store refused to restore
/// (#1843), how many present events the bundle can still give an origin, and the buckets this record marked as ones
/// repair cannot change.
/// </summary>
/// <param name="Children">The bundle's distinct events.</param>
/// <param name="Present">Events the event store held at commit.</param>
/// <param name="Absent">Events it did not hold; the bundle stores them.</param>
/// <param name="RefusedTruncated">Events the store refused to restore because the stream was truncated here.</param>
/// <param name="Adoptable">Present events the bundle can give an origin or an origin sequence; the reconcile adopts them.</param>
/// <param name="MarkedRefusedTruncated">Buckets this record marked because the store refused to restore an event of them.</param>
/// <param name="MarkedUnchanged">Buckets this record marked because the bundle changed nothing.</param>
/// <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs</tests>
public sealed record IntegrityBundleAnswer(
  int Children, int Present, int Absent, int RefusedTruncated, int Adoptable,
  IReadOnlyList<IntegrityRepairLedger.DivergenceKey> MarkedRefusedTruncated,
  IReadOnlyList<IntegrityRepairLedger.DivergenceKey> MarkedUnchanged) {
  /// <summary>Every event of the bundle was in the store, or refused by it, at commit: the bundle stored nothing.</summary>
  public bool AllPresent => Present + RefusedTruncated >= Children;
}

/// <summary>What reconciling one redelivery bundle changed at the receiver.</summary>
/// <param name="Children">The bundle's children this receiver kept.</param>
/// <param name="Present">Children already in the event store.</param>
/// <param name="Adopted">Present children whose empty origin columns the bundle filled.</param>
/// <param name="MarkedUnhealable">Buckets newly recorded as ones repair cannot change.</param>
/// <param name="RefusedTruncated">Children the store refused to restore past deduplication (#1843).</param>
/// <param name="RefusedForeignOrigin">Present children with empty origin columns the bundle did not fill, because their
/// first hop names another service than the bundle's origin (#1844).</param>
/// <docs>resilience/stream-integrity#pre-stamp-history</docs>
public readonly record struct RedeliveryReconciliation(
  int Children, int Present, int Adopted, int MarkedUnhealable, int RefusedTruncated = 0, int RefusedForeignOrigin = 0) {
  /// <summary>Nothing was reconciled: no origin, no children, or no store that can answer.</summary>
  public static RedeliveryReconciliation None => default;

  /// <summary>
  /// The reconcile was recorded for the maintainer's integrity delivery drain (#2001) instead of running here; the
  /// counts are those of nothing reconciled yet.
  /// </summary>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RedeliveryReconcileDeferralTests.cs:Reconcile_WithTheQueue_IsRecordedForTheMaintainer_NotRunHereAsync</tests>
  public bool Deferred { get; init; }

  /// <summary>A reconcile recorded for the maintainer's drain.</summary>
  public static RedeliveryReconciliation DeferredToTheMaintainer => new() { Deferred = true };
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
/// <tests>tests/Whizbang.Core.Tests/Messaging/RedeliveryReconcileDeferralTests.cs</tests>
public static partial class RedeliveryReconciler {
  /// <summary>The integrity delivery type a deferred reconcile is recorded under (#2001).</summary>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  public const string DELIVERY_TYPE = "whizbang:redelivery-reconcile";

  /// <summary>
  /// Reconciles a committed redelivery bundle's children with the receiver's event store. Best-effort: a failure is
  /// logged and leaves repair to ask again on its own schedule.
  /// </summary>
  /// <remarks>
  /// Since #2001 the reconcile is integrity work and runs on the maintainer: when the host has the integrity delivery
  /// drain (<see cref="IntegrityDeliveryRegistry"/>) and the coordinator can record deliveries, the reconcile is
  /// recorded with the origin, its name, the children's ids and their origin sequences, and the drain runs it with
  /// exactly those (<see cref="RedeliveryReconciliation.DeferredToTheMaintainer"/> is returned). The bundle's events
  /// are already stored by then: that is ordinary event storage and stays at receipt. Otherwise it reconciles here, as
  /// before. A redelivered bundle with the same children is recorded once. Since #2033 the bundle's answer is recorded
  /// first, here, whether or not the reconcile is deferred (<see cref="IWorkCoordinator.IntegrityRecordBundleAnswerAsync"/>):
  /// its buckets stop waiting, a bundle that changed nothing marks them at once (counted in
  /// <see cref="RedeliveryReconciliation.MarkedUnhealable"/>), and the deferred reconcile judges "changed nothing" by
  /// what the store held at commit.
  /// </remarks>
  /// <param name="composite">The redelivery bundle.</param>
  /// <param name="children">The children the receiver kept (its subscribed events).</param>
  /// <param name="services">The dispatch scope's services.</param>
  /// <param name="logger">Where the reports go.</param>
  /// <param name="now">The caller's clock.</param>
  /// <param name="originServiceName">The origin's service name (<see cref="OriginServiceNameOf"/> of the bundle's
  /// envelope); null when the bundle carries none.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>What the reconciliation changed.</returns>
  public static async Task<RedeliveryReconciliation> ReconcileAsync(
      IIdentityPreservingComposite composite, IReadOnlyList<InboxMessage> children, IServiceProvider services,
      ILogger logger, DateTimeOffset now, string? originServiceName = null, CancellationToken cancellationToken = default) {
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
    Guid[] eventIds = [.. children.Select(c => c.MessageId)];
    // A zero sequence is the origin's "not stamped yet" on the wire, never a position.
    long?[] sequences = [.. children.Select(c => c.SourceCommitSequence > 0 ? c.SourceCommitSequence : (long?)null)];
    // #2033: the answer is recorded now, while the store still shows what the bundle found (its own children are not
    // stored yet), and it is small: the buckets stop waiting, and a bundle that changed nothing is marked at once.
    // #2143: the window the bundle answers; a bucket whose deficit it did not cover is answered but not marked.
    var window = composite.AnsweredWindow;
    var answer = await _recordAnswerAsync(
        coordinator, new BundleAdoption(origin, originServiceName, eventIds, sequences, AllPresentAtCommit: null, window), services,
        logger, now, cancellationToken)
      .ConfigureAwait(false);
    var markedAtCommit = answer is null ? 0 : answer.MarkedRefusedTruncated.Count + answer.MarkedUnchanged.Count;
    if (services.GetService<IntegrityDeliveryRegistry>() is not null) {
      try {
        var payload = _writeDeferred(new BundleAdoption(origin, originServiceName, eventIds, sequences, answer?.AllPresent, window));
        var recorded = await coordinator.EnqueueIntegrityDeliveryAsync(
          IntegrityDeliveryId.For(DELIVERY_TYPE, payload), DELIVERY_TYPE, Encoding.UTF8.GetString(payload), cancellationToken)
          .ConfigureAwait(false);
        if (recorded == IntegrityDeliveryEnqueueResult.Recorded) {
          services.GetService<StreamIntegrityMetrics>()?.IntegrityDeliveriesQueued.Add(1,
            new KeyValuePair<string, object?>("type", DELIVERY_TYPE));
        }
        if (recorded != IntegrityDeliveryEnqueueResult.Unsupported) {
          return RedeliveryReconciliation.DeferredToTheMaintainer with { MarkedUnhealable = markedAtCommit };
        }
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        LogReconcileFailed(logger, children.Count, origin, ex);
        return RedeliveryReconciliation.None;
      }
    }
    var reconciled = await _reconcileCoreAsync(
      coordinator, new BundleAdoption(origin, originServiceName, eventIds, sequences, answer?.AllPresent, window), services, logger,
      now, cancellationToken).ConfigureAwait(false);
    return reconciled with { MarkedUnhealable = reconciled.MarkedUnhealable + markedAtCommit };
  }

  /// <summary>
  /// Records the bundle's answer at commit (<see cref="IWorkCoordinator.IntegrityRecordBundleAnswerAsync"/>) and reports
  /// each bucket it marked once. Null when the store cannot record it or the record failed (logged); the reconcile then
  /// judges the bundle as before.
  /// </summary>
  private static async Task<IntegrityBundleAnswer?> _recordAnswerAsync(
      IWorkCoordinator coordinator, BundleAdoption bundle, IServiceProvider services, ILogger logger, DateTimeOffset now,
      CancellationToken cancellationToken) {
    var (origin, _, eventIds, sequences, _, window) = bundle;
    IntegrityBundleAnswer? answer;
    try {
      answer = await coordinator.IntegrityRecordBundleAnswerAsync(origin, eventIds, sequences, window, now, cancellationToken)
        .ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      LogAnswerRecordFailed(logger, eventIds.Length, origin, ex);
      return null;
    }
    if (answer is null) {
      return null;
    }
    foreach (var key in answer.MarkedRefusedTruncated) {
      LogRepairRefusedTruncatedBucket(logger, key.EventType, key.TenantScope, key.StreamId, origin);
    }
    foreach (var key in answer.MarkedUnchanged) {
      LogRepairCannotChangeBucket(logger, key.EventType, key.TenantScope, key.StreamId, origin);
    }
    var marked = answer.MarkedRefusedTruncated.Count + answer.MarkedUnchanged.Count;
    if (marked > 0) {
      services.GetService<StreamIntegrityMetrics>()?.RepairsUnhealable.Add(marked);
    }
    return answer;
  }

  /// <summary>
  /// The integrity delivery drain's handler for a deferred reconcile: reads the recorded origin, name, event ids and
  /// sequences back and reconciles with exactly those, as the inline path would have.
  /// </summary>
  /// <param name="payload">The recorded reconcile.</param>
  /// <param name="services">A scope's services for this delivery.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>A task that completes when the reconcile is done.</returns>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RedeliveryReconcileDeferralTests.cs:TheDrain_ReconcilesWithExactlyWhatTheInlineReconcilePassesAsync</tests>
  internal static async Task HandleDeferredAsync(string payload, IServiceProvider services, CancellationToken cancellationToken) {
    var bundle = _readDeferred(payload);
    var coordinator = services.GetService<IWorkCoordinator>();
    if (coordinator is null) {
      return;
    }
    var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(RedeliveryReconciler))
      ?? NullLogger.Instance;
    var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
    _ = await _reconcileCoreAsync(coordinator, bundle, services, logger, now, cancellationToken).ConfigureAwait(false);
  }

  // The deferred form, written by hand so Core needs no serializer context for it.
  private static byte[] _writeDeferred(BundleAdoption bundle) {
    var (origin, originServiceName, eventIds, sequences, allPresentAtCommit, window) = bundle;
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer)) {
      writer.WriteStartObject();
      writer.WriteString("originServiceId", origin);
      writer.WriteString("originServiceName", originServiceName);
      writer.WriteStartArray("eventIds");
      foreach (var id in eventIds) {
        writer.WriteStringValue(id);
      }
      writer.WriteEndArray();
      writer.WriteStartArray("commitSequences");
      foreach (var sequence in sequences) {
        if (sequence is { } value) {
          writer.WriteNumberValue(value);
        } else {
          writer.WriteNullValue();
        }
      }
      writer.WriteEndArray();
      // #2033: whether every event was already stored at commit; null when the store could not say.
      if (allPresentAtCommit is { } allPresent) {
        writer.WriteBoolean("allPresentAtCommit", allPresent);
      } else {
        writer.WriteNull("allPresentAtCommit");
      }
      // #2143: the window the bundle answers; absent when the origin did not say (and on a reconcile recorded before).
      if (window is { } answered) {
        writer.WriteStartObject("answeredWindow");
        _writeBound(writer, "from", answered.FromCommitSequence);
        _writeBound(writer, "to", answered.ToCommitSequence);
        // #2149: the streams the bundle carried only in part; absent when it carried every stream whole.
        if (answered.PartialStreamIds is { } partial) {
          writer.WriteStartArray("partialStreams");
          foreach (var stream in partial) {
            writer.WriteStringValue(stream);
          }
          writer.WriteEndArray();
        }
        writer.WriteEndObject();
      }
      writer.WriteEndObject();
    }
    return buffer.ToArray();
  }

  private static void _writeBound(Utf8JsonWriter writer, string name, long? bound) {
    if (bound is { } value) {
      writer.WriteNumber(name, value);
    } else {
      writer.WriteNull(name);
    }
  }

  // #2149: the streams the bundle carried only in part; null when the record names none (and on one recorded before).
  private static List<Guid>? _readPartial(JsonElement window) =>
    window.TryGetProperty("partialStreams", out var partial) ? [.. partial.EnumerateArray().Select(e => e.GetGuid())] : null;

  private static long? _readBound(JsonElement window, string name) {
    var bound = window.GetProperty(name);
    return bound.ValueKind == JsonValueKind.Null ? null : bound.GetInt64();
  }

  /// <summary>What one bundle asks the store to adopt: its origin, the origin's service name, and its events with their
  /// origin sequences, paired by position; and whether every event was already stored when the bundle committed (null
  /// when unknown: a store that could not say, or a reconcile recorded before the commit-time answer existed); and the
  /// window the bundle answers (#2143), null when the origin did not say.</summary>
  private readonly record struct BundleAdoption(
    Guid Origin, string? OriginServiceName, Guid[] EventIds, long?[] Sequences, bool? AllPresentAtCommit,
    RedeliveryAnswerWindow? Window);

  private static BundleAdoption _readDeferred(string payload) {
    using var document = JsonDocument.Parse(payload);
    var root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object) {
      throw new JsonException($"A deferred redelivery reconcile is a JSON object, not {root.ValueKind}.");
    }
    var name = root.GetProperty("originServiceName");
    bool? allPresent = root.TryGetProperty("allPresentAtCommit", out var flag) && flag.ValueKind != JsonValueKind.Null
      ? flag.GetBoolean()
      : null;
    RedeliveryAnswerWindow? window = root.TryGetProperty("answeredWindow", out var answered)
      ? new RedeliveryAnswerWindow(_readBound(answered, "from"), _readBound(answered, "to")) { PartialStreamIds = _readPartial(answered) }
      : null;
    return new BundleAdoption(
      root.GetProperty("originServiceId").GetGuid(),
      name.ValueKind == JsonValueKind.Null ? null : name.GetString(),
      [.. root.GetProperty("eventIds").EnumerateArray().Select(e => e.GetGuid())],
      [.. root.GetProperty("commitSequences").EnumerateArray()
        .Select(e => e.ValueKind == JsonValueKind.Null ? (long?)null : e.GetInt64())],
      allPresent,
      window);
  }

  /// <summary>
  /// The integrity delivery drain's batch handler for deferred reconciles (#2099): reads every recorded reconcile back and
  /// reconciles them in one call (<see cref="IWorkCoordinator.ReconcileRedeliveredBundlesAsync"/>), so each sealed digest
  /// epoch the batch touches is refolded once rather than once per bundle; then concludes each bundle as the single
  /// reconcile does. A store without the batch call reconciles each bundle on its own. A payload that cannot be read, or
  /// a batch call that fails, faults the whole batch, and the drain then handles each delivery on its own, so one bad
  /// delivery fails alone.
  /// </summary>
  /// <param name="payloads">The recorded reconciles, in arrival order.</param>
  /// <param name="services">A scope's services for this batch.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>A task that completes when every reconcile is done.</returns>
  /// <docs>resilience/stream-integrity#redelivery-reconciles-run-as-a-batch</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RedeliveryReconcileBatchTests.cs</tests>
  internal static async Task HandleDeferredBatchAsync(
      IReadOnlyList<string> payloads, IServiceProvider services, CancellationToken cancellationToken) {
    List<BundleAdoption> bundles = [.. payloads.Select(_readDeferred)];
    var coordinator = services.GetService<IWorkCoordinator>();
    if (coordinator is null) {
      return;
    }
    var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(RedeliveryReconciler))
      ?? NullLogger.Instance;
    var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
    var adoptions = await coordinator.ReconcileRedeliveredBundlesAsync(
      [.. bundles.Select(b => new RedeliveredBundle(b.Origin, b.OriginServiceName, b.EventIds, b.Sequences))],
      cancellationToken).ConfigureAwait(false);
    for (var i = 0; i < bundles.Count; i++) {
      _ = adoptions is null
        ? await _reconcileCoreAsync(coordinator, bundles[i], services, logger, now, cancellationToken).ConfigureAwait(false)
        : await _concludeGuardedAsync(bundles[i], adoptions[i], services, logger, now, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>The reconcile itself, shared by the inline path and the maintainer's drain.</summary>
  private static async Task<RedeliveryReconciliation> _reconcileCoreAsync(
      IWorkCoordinator coordinator, BundleAdoption bundle, IServiceProvider services, ILogger logger, DateTimeOffset now,
      CancellationToken cancellationToken) {
    try {
      var buckets = await coordinator.AdoptRedeliveredOriginAsync(
        bundle.Origin, bundle.OriginServiceName, bundle.EventIds, bundle.Sequences, cancellationToken).ConfigureAwait(false);
      if (buckets is null) {
        return RedeliveryReconciliation.None;   // a store that cannot answer: repair behaves as before
      }
      return await _concludeAsync(bundle, buckets, services, logger, now, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      LogReconcileFailed(logger, bundle.EventIds.Length, bundle.Origin, ex);
      return RedeliveryReconciliation.None;
    }
  }

  /// <summary>Concludes one bundle of a batch; a failure is logged for that bundle and the rest still conclude.</summary>
  private static async Task<RedeliveryReconciliation> _concludeGuardedAsync(
      BundleAdoption bundle, IReadOnlyList<RedeliveredBucketPresence> buckets, IServiceProvider services, ILogger logger,
      DateTimeOffset now, CancellationToken cancellationToken) {
    try {
      return await _concludeAsync(bundle, buckets, services, logger, now, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      LogReconcileFailed(logger, bundle.EventIds.Length, bundle.Origin, ex);
      return RedeliveryReconciliation.None;
    }
  }

  /// <summary>What a bundle's adoption means once the store has answered: reports, metrics and the ledger.</summary>
  private static async Task<RedeliveryReconciliation> _concludeAsync(
      BundleAdoption bundle, IReadOnlyList<RedeliveredBucketPresence> buckets, IServiceProvider services, ILogger logger,
      DateTimeOffset now, CancellationToken cancellationToken) {
    var (origin, originServiceName, eventIds, _, allPresentAtCommit, window) = bundle;
    var childCount = eventIds.Length;
    var present = buckets.Sum(b => b.Present);
    var adopted = buckets.Sum(b => b.Adopted);
    var refusedTruncated = buckets.Sum(b => b.RefusedTruncated);
    var refusedForeignOrigin = buckets.Sum(b => b.RefusedForeignOrigin);
    var metrics = services.GetService<StreamIntegrityMetrics>();
    _reportForeignOrigins(logger, metrics, refusedForeignOrigin, childCount, originServiceName, origin);
    _reportAdopted(logger, metrics, buckets, adopted, childCount, origin);
    // A bucket holding an event the store refused to restore (#1843) cannot be repaired by asking again. Otherwise,
    // an event the store did not hold is stored by this bundle: the repair is working, nothing to conclude. Only a
    // bundle that stored nothing and adopted nothing marks the buckets of the events it found present. #2033: what
    // the store held is the commit's answer when there is one, because by now the bundle's own children are stored
    // and read as present.
    List<RedeliveredBucketPresence> refusedBuckets = [.. buckets.Where(b => b.RefusedTruncated > 0)];
    var storedNothing = allPresentAtCommit ?? present + refusedTruncated >= childCount;
    // #2149: a stream the bundle carried only in part may have its missing events in the part it did not carry.
    var partial = window?.PartialStreamIds ?? [];
    List<RedeliveredBucketPresence> unchanged = adopted == 0 && storedNothing
      ? [.. buckets.Where(b => b.RefusedTruncated == 0 && !partial.Contains(b.StreamId))]
      : [];
    var ledger = services.GetService<IIntegrityRepairLedger>() ?? services.GetService<IntegrityRepairLedger>();
    if (ledger is null) {
      return new RedeliveryReconciliation(childCount, present, adopted, 0, refusedTruncated, refusedForeignOrigin);
    }
    // #2029: the bundle answered these buckets. The durable store recorded that at commit; a ledger only the
    // reconcile reaches (the in-memory fallback) learns it here, and its pending answers clear.
    await ledger.MarkAnsweredAsync(
      [.. buckets.Select(b => new IntegrityRepairLedger.DivergenceKey(origin, b.TenantScope, b.EventType, b.StreamId))],
      now, cancellationToken).ConfigureAwait(false);
    if (refusedBuckets.Count + unchanged.Count == 0) {
      return new RedeliveryReconciliation(childCount, present, adopted, 0, refusedTruncated, refusedForeignOrigin);
    }
    var marked = await _markAsync(ledger, origin, refusedBuckets, now,
        key => LogRepairRefusedTruncatedBucket(logger, key.EventType, key.TenantScope, key.StreamId, origin), coverage: null,
        cancellationToken)
      .ConfigureAwait(false);
    marked += await _markAsync(ledger, origin, unchanged, now,
        key => LogRepairCannotChangeBucket(logger, key.EventType, key.TenantScope, key.StreamId, origin),
        window is { } answered ? (answered, eventIds) : null, cancellationToken)
      .ConfigureAwait(false);
    if (marked > 0) {
      metrics?.RepairsUnhealable.Add(marked);
    }
    return new RedeliveryReconciliation(childCount, present, adopted, marked, refusedTruncated, refusedForeignOrigin);
  }

  /// <summary>
  /// Reports what a bundle adopted, once: the events whose origin it filled, and apart from them the events that already
  /// carried the origin and took only its sequence (#2099). Both used to be reported as filling the origin, so a run of
  /// sequence fills on history re-attributed to its owner read as the same origins being filled again and again.
  /// </summary>
  private static void _reportAdopted(
      ILogger logger, StreamIntegrityMetrics? metrics, IReadOnlyList<RedeliveredBucketPresence> buckets, int adopted,
      int children, Guid origin) {
    if (adopted == 0) {
      return;
    }
    metrics?.OriginsAdopted.Add(adopted);
    var originsFilled = buckets.Sum(b => b.OriginsFilled ?? b.Adopted);
    if (originsFilled == adopted) {
      LogOriginsAdopted(logger, adopted, children, origin);
      return;
    }
    LogSequencesAdopted(logger, originsFilled, adopted - originsFilled, children, origin);
  }

  /// <summary>Marks the buckets as ones repair cannot change and reports each newly marked one once.</summary>
  private static async Task<int> _markAsync(
      IIntegrityRepairLedger ledger, Guid origin, List<RedeliveredBucketPresence> buckets, DateTimeOffset now,
      Action<IntegrityRepairLedger.DivergenceKey> report, (RedeliveryAnswerWindow Window, Guid[] EventIds)? coverage,
      CancellationToken cancellationToken) {
    if (buckets.Count == 0) {
      return 0;
    }
    List<IntegrityRepairLedger.DivergenceKey> keys =
      [.. buckets.Select(b => new IntegrityRepairLedger.DivergenceKey(origin, b.TenantScope, b.EventType, b.StreamId))];
    // #2143: with the answered window known, the ledger marks only the buckets whose deficit the answer covered.
    var marked = coverage is { } covered
      ? await ledger.MarkUnhealableBatchAsync(keys, covered.Window, covered.EventIds, now, cancellationToken).ConfigureAwait(false)
      : await ledger.MarkUnhealableBatchAsync(keys, now, cancellationToken).ConfigureAwait(false);
    foreach (var key in marked) {
      report(key);
    }
    return marked.Count;
  }

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Repair cannot restore {EventType} (tenant {TenantScope}) on stream {StreamId} from origin {OriginServiceId}: "
            + "a redelivered event that inbox deduplication remembers is missing from the event store, but the stream's "
            + "earlier history was truncated here, so it could be an event the truncation removed on purpose and is not "
            + "stored again. The bucket stops being requested and is reported again only if either side's digest changes. "
            + "Compare the stream on both sides")]
  private static partial void LogRepairRefusedTruncatedBucket(
    ILogger logger, string eventType, string? tenantScope, Guid streamId, Guid originServiceId);

  /// <summary>
  /// Reports, once per bundle, the stored events the first-hop check did not let adopt the bundle's origin (#1844).
  /// </summary>
  private static void _reportForeignOrigins(
      ILogger logger, StreamIntegrityMetrics? metrics, int refused, int children, string? originServiceName, Guid origin) {
    if (refused == 0) {
      return;
    }
    metrics?.OriginsRefused.Add(refused);
    LogForeignOriginsRefused(logger, refused, children, originServiceName, origin);
  }

  /// <summary>
  /// The service name a redelivery bundle's origin published it under: the service of the envelope's first hop (the
  /// origin's own publish hop). Null when the envelope has no hop or its first hop carries no service identity
  /// (<see cref="ServiceInstanceInfo.Unknown"/>, in any case), which leaves the first-hop check nothing to compare.
  /// </summary>
  /// <remarks>
  /// Two owner decisions (#1844). A bundle that yields no name adopts: with nothing to compare, refusing would leave
  /// history received before origins were recorded counted as missing and requested forever, which is the same
  /// reason a stored first hop without a service identity adopts. The name is returned as written and the store
  /// compares it without case, as the transport consumer compares service names, because one service's name can be
  /// configured with different casing on different hosts, and a case difference must not turn the origin into
  /// another service.
  /// </remarks>
  /// <param name="bundleEnvelope">The redelivery bundle's envelope as received.</param>
  /// <returns>The origin's service name, or null.</returns>
  /// <docs>resilience/stream-integrity#pre-stamp-history</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RedeliveryReconcilerTests.cs</tests>
  public static string? OriginServiceNameOf(IMessageEnvelope bundleEnvelope) {
    ArgumentNullException.ThrowIfNull(bundleEnvelope);
    var hops = bundleEnvelope.Hops;
    if (hops is null || hops.Count == 0) {
      return null;   // read from a wire form without a hop list
    }
    var name = hops[0].ServiceInstance.ServiceName;
    return string.IsNullOrWhiteSpace(name)
      || string.Equals(name, ServiceInstanceInfo.Unknown.ServiceName, StringComparison.OrdinalIgnoreCase)
      ? null
      : name;
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Redelivery from origin {OriginServiceId} filled the origin identity of {Adopted} of its {Children} event(s) "
            + "that were stored before origins were recorded; they now verify under that origin")]
  private static partial void LogOriginsAdopted(ILogger logger, int adopted, int children, Guid originServiceId);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Redelivery from origin {OriginServiceId} filled the origin identity of {OriginsFilled} and only the origin "
            + "sequence of {SequencesFilled} of its {Children} event(s); those already carried the origin (history "
            + "re-attributed to its owner, which records the origin without its sequence). They now verify under that "
            + "origin's sealed epochs")]
  private static partial void LogSequencesAdopted(
    ILogger logger, int originsFilled, int sequencesFilled, int children, Guid originServiceId);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Redelivery from origin {OriginServiceName} ({OriginServiceId}) named itself as the origin of {Refused} of its "
            + "{Children} event(s) that this store holds without an origin, but their first hop names another service, so "
            + "they keep no origin. The usual cause is events this service authored that the origin's store had filed as "
            + "its own before it recorded origins; the origin may report them as a surplus")]
  private static partial void LogForeignOriginsRefused(
    ILogger logger, int refused, int children, string? originServiceName, Guid originServiceId);

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

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Recording the answer of a redelivery of {Children} event(s) from origin {OriginServiceId} failed; its "
            + "buckets wait out their answer timeout, and the reconcile judges the bundle without the commit's view")]
  private static partial void LogAnswerRecordFailed(ILogger logger, int children, Guid originServiceId, Exception exception);
}
