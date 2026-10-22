// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Marker interface that opts a perspective model into <b>strict cross-pod ordering</b> at the
/// storage UPSERT layer.
/// </summary>
/// <remarks>
/// <para>
/// By default, the Postgres atomic UPSERT path is permissive: when an incoming write carries a
/// null <c>metadata.CommitSequence</c> the row is overwritten unconditionally
/// (last-writer-wins). That is a deliberate contract — see
/// <c>CrossPodStaleReadRegressionRaceTests</c> — so the storage layer can trust the
/// <c>PerspectiveWorker</c> stream-affinity gate (v0.740) to serialize writes upstream.
/// </para>
/// <para>
/// Per-item saga streams (<see cref="Whizbang.Sagas.Models.SagaItemModel"/>) ship
/// <c>CommitSequence=null</c> by design (the stamper doesn't cover the per-item fan-out path)
/// and saw a production cross-pod lost-update strand survive even with the stream-affinity
/// gate (a small fraction of per-item rows in a saga reverted to
/// <c>State=Running</c> after their <c>SagaItemCompletedEvent</c> was durable). The reconciler
/// healed the saga but the projection drift persisted on the row.
/// </para>
/// <para>
/// Models that implement this marker get a stricter UPSERT <c>WHERE</c> clause: the row is
/// updated only when the incoming <c>metadata.EventId</c> sorts at or after the stored
/// <c>EventId</c>. UUIDv7 from <see cref="ValueObjects.TrackedGuid.New"/> orders
/// lexicographically by emission time, so "newer event wins" holds with no extra column.
/// Older-EventId writes are dropped.
/// </para>
/// <para>
/// A write AT the stored <c>EventId</c> lands (#1608). A rewind or a rebuild re-folds the stream and records its
/// frontier, which is the event the row already holds whenever a late event sits below the newest one; refusing
/// that write refused the correction, and the late event was reported applied while the row never received it. A
/// redelivery of the row's own event never reaches the store (the runner's idempotency filter drops it), and a
/// live fold of a late event that the guard refuses is re-folded by a rewind instead of being reported applied.
/// </para>
/// <para>
/// This is opt-in because the strict comparison is more restrictive than the legacy contract.
/// Models that have already adopted the framework's stamper-lag forwarding pattern keep working
/// unchanged; only models that explicitly want strand prevention beyond the stream-affinity
/// gate need the marker.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/versioned-apply</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/VersionedApplyTargetTests.cs:StaleEventWrite_OnVersionedTarget_DoesNotRegressTerminalRowAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/VersionedApplyTargetTests.cs:NewerEventWrite_OnVersionedTarget_AdvancesRowAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/VersionedApplyTargetTests.cs:WriteAtTheStoredEventId_OnVersionedTarget_LandsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveLateEventApplyTests.cs:TheLaterEventCommitsFirst_EveryRowStillHoldsBothEventsAsync</tests>
public interface IVersionedApplyTarget;
