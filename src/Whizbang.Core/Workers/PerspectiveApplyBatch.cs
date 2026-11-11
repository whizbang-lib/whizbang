// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Workers;

/// <summary>
/// The set-based apply of one batch of (stream, perspective) pairs (#2246): every pair's apply lock taken as a set, every
/// pair prepared, the prepared rows written per DbContext through that context's write sink and the flush policy, and
/// every pair completed with its outcome.
/// </summary>
/// <remarks>
/// <para>
/// The locks are the per-pair apply locks of <see cref="IPerspectiveApplyCoordinator"/>, taken in
/// <see cref="PerspectiveApplyLockSet.Order"/> before the first prepare and released after the last completion; the
/// worker's affinity gates for the same pairs are taken before them, in the same order. A pair's failure is its own:
/// an exception from its prepare or completion is recorded on the pair, and a row the database rejects or a sink that
/// fails as a whole is handed to the pair's completion as <see cref="PerspectiveWriteStatus.Failed"/>, which reruns it
/// on the per-pair path.
/// </para>
/// <para>
/// Each prepared write names the DbContext it belongs to (<see cref="PendingPerspectiveWrite.SinkKey"/>), and each
/// context's writes go to the sink registered for it, resolved from a scope of their own; a service with several
/// contexts writes each perspective through its own. A write with no sink registered for its context is failed, and
/// its pair is written on the per-pair path.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#the-batch</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveApplyBatchTests.cs</tests>
internal static class PerspectiveApplyBatch {
  /// <summary>One pair of a batch: what it applies, and how its apply ended.</summary>
  internal sealed class Pair(
      Guid streamId, string perspectiveName, IPerspectiveRunner runner, Guid? lastProcessedEventId,
      IReadOnlyList<MessageEnvelope<IEvent>> events) {
    public Guid StreamId { get; } = streamId;
    public string PerspectiveName { get; } = perspectiveName;
    public IPerspectiveRunner Runner { get; } = runner;
    public Guid? LastProcessedEventId { get; } = lastProcessedEventId;
    public IReadOnlyList<MessageEnvelope<IEvent>> Events { get; } = events;

    /// <summary>The pair's completion, when its apply ended without an exception.</summary>
    public PerspectiveCursorCompletion? Result { get; internal set; }

    /// <summary>The exception the pair's apply ended with, which the caller treats as the per-pair path treats a thrown apply.</summary>
    public Exception? Failure { get; internal set; }

    internal PreparedPerspectiveApply? Prepared { get; set; }
    internal PerspectiveWriteOutcome? Outcome { get; set; }
  }

  /// <summary>The services one batch runs with.</summary>
  /// <param name="ScopeFactory">Creates the scope each DbContext's sink is resolved from.</param>
  /// <param name="Coordinator">The apply coordinator, or null when none is registered.</param>
  /// <param name="StateSource">The state source, or null to read every row per stream.</param>
  /// <param name="FlushPolicy">The flush policy.</param>
  /// <param name="Width">How many pairs are prepared or completed at once.</param>
  /// <param name="Logger">Logs a sink that is missing or failed as a whole.</param>
  internal readonly record struct Services(
      IServiceScopeFactory ScopeFactory,
      IPerspectiveApplyCoordinator? Coordinator,
      IPerspectiveStateSource? StateSource,
      IApplyBatchStrategy FlushPolicy,
      int Width,
      ILogger Logger);

  /// <summary>Applies the batch. Each pair ends with a <see cref="Pair.Result"/> or a <see cref="Pair.Failure"/>.</summary>
  /// <param name="pairs">The pairs, each with at least one event.</param>
  /// <param name="services">The services the batch runs with.</param>
  /// <param name="cancellationToken">Cancels the batch; a cancellation is thrown, never recorded on a pair.</param>
  /// <returns>A task that completes when every pair has ended and the locks are released.</returns>
  internal static Task RunAsync(IReadOnlyList<Pair> pairs, Services services, CancellationToken cancellationToken) =>
    throw new NotImplementedException();
}
