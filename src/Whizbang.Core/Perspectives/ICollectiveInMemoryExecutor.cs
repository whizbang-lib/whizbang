// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Non-generic seam for applying a collective spec to a single in-memory model during replay/rebuild. The
/// in-memory twin of <see cref="ICollectiveEventExecutor"/>: where that runs a set-based SQL UPDATE, this
/// mutates one already-materialized model instance. Each implementation closes a concrete <c>TModel</c> at
/// registration and advertises it via <see cref="ModelType"/>, so <see cref="ICollectiveReplayApplier"/> can
/// look up the right one by <see cref="CollectiveApplyEntry.ModelType"/> with no <c>MakeGenericType</c> —
/// AOT-clean by construction, mirroring the SQL executor's registration.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CollectiveReplayRebuildIntegrationTests.cs:Rebuild_FoldsCollectiveEvent_OnlyIntoTheTargetRowAsync</tests>
public interface ICollectiveInMemoryExecutor {
  /// <summary>The closed <c>TModel</c> this executor handles.</summary>
  Type ModelType { get; }

  /// <summary>
  /// If the row (<paramref name="currentModel"/> identified by <paramref name="streamId"/>) is in the spec's
  /// cohort, apply the spec's setters and return the mutated model; otherwise return it unchanged.
  /// </summary>
  /// <param name="spec">An <c>ICollectiveSpec&lt;TModel&gt;</c> (boxed) produced by a <c>[CollectiveApplyFor]</c> handler.</param>
  /// <param name="currentModel">The single in-memory model being folded during replay.</param>
  /// <param name="streamId">The row's id (used to evaluate a self-referential <c>Where</c>).</param>
  object ApplyToRow(object spec, object currentModel, Guid streamId);

  /// <summary>
  /// The stream id <paramref name="currentModel"/>'s key for <paramref name="fanOut"/> holds (#2621): the value of the key
  /// property <see cref="PerspectiveFanOutEntry.KeySelector"/> names, a <see cref="Guid"/> or a strongly typed id over one,
  /// as its column stores it. Null when the key holds no stream: it is unset, or <see cref="Guid.Empty"/>. A replay reads it
  /// to find the foreign events that target the row, and to fold each only while the row's key holds the event's stream.
  /// </summary>
  /// <param name="fanOut">A fan-out of <see cref="ModelType"/>.</param>
  /// <param name="currentModel">The single in-memory model being folded during replay.</param>
  /// <returns>The stream id the key holds, or null.</returns>
  /// <exception cref="NotSupportedException">The executor was written before replays folded fan-outs.</exception>
  /// <docs>fundamentals/perspectives/multi-stream#fan-out-on-rebuild-and-rewind</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/FanOutReplayApplierTests.cs:AnExecutorWrittenBeforeReplaysFoldedFanOuts_RefusesToReadAKeyAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/FanOutReplayApplierTests.cs:FanOutKeyOf_IsTheStreamTheKeyHolds_AStronglyTypedIdByItsGuidAsync</tests>
  Guid? FanOutKeyOf(PerspectiveFanOutEntry fanOut, object currentModel) =>
    throw new NotSupportedException(
      $"{TypeNameFormatter.DisplayName(GetType())} cannot read a fan-out key, so a replay cannot fold the foreign events that "
      + "target its model's rows. The Postgres drivers' in-memory executor reads it.");
}
