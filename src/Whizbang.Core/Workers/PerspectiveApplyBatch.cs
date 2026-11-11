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
internal static partial class PerspectiveApplyBatch {
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
  internal static async Task RunAsync(IReadOnlyList<Pair> pairs, Services services, CancellationToken cancellationToken) {
    if (pairs.Count == 0) {
      return;
    }
    await using var locks = await PerspectiveApplyLockSet.AcquireAsync(
      services.Coordinator, pairs.Select(p => (p.StreamId, p.PerspectiveName)), cancellationToken).ConfigureAwait(false);

    var batchState = services.StateSource?.BeginBatch([.. pairs.Select(p => new PerspectiveStateRequest(
      p.StreamId, p.PerspectiveName, p.Events[0].MessageId.Value,
      PerspectiveStateRequest.IsKnownNew(p.LastProcessedEventId, p.Events), [.. p.Events.Select(e => e.MessageId.Value)]))]);
    var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, services.Width), CancellationToken = cancellationToken };

    await Parallel.ForEachAsync(pairs, parallel, async (pair, ct) => {
      try {
        pair.Prepared = await pair.Runner.PrepareAsync(
          pair.StreamId, pair.PerspectiveName, pair.LastProcessedEventId, pair.Events, batchState, ct).ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        pair.Failure = ex;
      }
    }).ConfigureAwait(false);

    await _flushAsync(pairs, services, cancellationToken).ConfigureAwait(false);

    await Parallel.ForEachAsync(pairs, parallel, async (pair, ct) => {
      if (pair.Failure is not null) {
        return;
      }
      try {
        pair.Result = await pair.Runner.CompleteAsync(pair.Prepared!, pair.Outcome, ct).ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        pair.Failure = ex;
      }
    }).ConfigureAwait(false);
  }

  /// <summary>Writes the prepared rows, one sink per DbContext, and records each pair's outcome.</summary>
  private static async Task _flushAsync(IReadOnlyList<Pair> pairs, Services services, CancellationToken cancellationToken) {
    var bySink = pairs
      .Where(p => p.Failure is null && p.Prepared?.Write is not null)
      .GroupBy(p => p.Prepared!.Write!.SinkKey ?? _defaultSink);
    foreach (var group in bySink) {
      var members = group.ToList();
      var writes = members.ConvertAll(p => p.Prepared!.Write!);
      IReadOnlyList<PerspectiveWriteOutcome> outcomes;
      var scope = services.ScopeFactory.CreateAsyncScope();
      await using (scope.ConfigureAwait(false)) {
        var sink = ReferenceEquals(group.Key, _defaultSink)
          ? scope.ServiceProvider.GetService<IPerspectiveWriteSink>()
          : scope.ServiceProvider.GetKeyedService<IPerspectiveWriteSink>(group.Key);
        if (sink is null) {
          var missing = new InvalidOperationException(
            $"No {nameof(IPerspectiveWriteSink)} is registered for {_describe(group.Key)}, so its rows are written per stream.");
          LogSinkUnavailable(services.Logger, missing, _describe(group.Key), writes.Count);
          outcomes = [.. writes.Select(_ => PerspectiveWriteOutcome.Failed(missing))];
        } else {
          outcomes = await _writeAsync(sink, writes, group.Key, services, cancellationToken).ConfigureAwait(false);
        }
      }
      for (var i = 0; i < members.Count; i++) {
        members[i].Outcome = outcomes[i];
      }
    }
  }

  private static async Task<IReadOnlyList<PerspectiveWriteOutcome>> _writeAsync(
      IPerspectiveWriteSink sink, List<PendingPerspectiveWrite> writes, object key, Services services,
      CancellationToken cancellationToken) {
    try {
      var outcomes = await services.FlushPolicy.FlushAsync(sink, writes, cancellationToken).ConfigureAwait(false);
      if (outcomes.Count != writes.Count) {
        throw new InvalidOperationException(
          $"The flush of {_describe(key)} answered {outcomes.Count} outcome(s) for {writes.Count} write(s).");
      }
      return outcomes;
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // The whole statement group failed (a connection lost, a policy that broke its contract): every row is applied
      // again on its own, where the failure is met, or not, exactly as on the per-pair path.
      LogSinkFailed(services.Logger, ex, _describe(key), writes.Count);
      return [.. writes.Select(_ => PerspectiveWriteOutcome.Failed(ex))];
    }
  }

  /// <summary>The group key of writes that name no DbContext: the sink registered without a key.</summary>
  private static readonly object _defaultSink = new();

  private static string _describe(object key) {
    if (ReferenceEquals(key, _defaultSink)) {
      return "the default write sink";
    }
    return key is Type type ? TypeNameFormatter.DisplayName(type) : key.ToString()!;
  }

  [LoggerMessage(EventId = 2246, Level = LogLevel.Warning,
    Message = "Set-based perspective write: no write sink for {Sink}; its {WriteCount} row(s) are written per stream")]
  private static partial void LogSinkUnavailable(ILogger logger, Exception exception, string sink, int writeCount);

  [LoggerMessage(EventId = 2247, Level = LogLevel.Warning,
    Message = "Set-based perspective write: the write through {Sink} failed as a whole; its {WriteCount} row(s) are written per stream")]
  private static partial void LogSinkFailed(ILogger logger, Exception exception, string sink, int writeCount);
}
