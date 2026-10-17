// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Security;

namespace Whizbang.Core.MultiStream;

/// <summary>
/// The receptor that takes a multi-stream command in. It validates the command and records a run with one work item per
/// stream; it runs no stream itself. Every instance then claims and works the streams it owns. Registered at the local
/// dispatch, PreOutbox and PostInbox stages, so the command is recorded whether it is dispatched locally or arrives from
/// the transport; recording is idempotent on the run id, so both make one run.
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <param name="store">Where the run is recorded.</param>
/// <param name="logger">Logger.</param>
/// <docs>fundamentals/dispatcher/multi-stream-commands#intake</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandIntakeTests.cs:HandleAsync_RecordsOneRunCarryingTheCommandAndItsStreamsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandIntakeTests.cs:HandleAsync_AnEmptyStreamList_IsRefusedAndRecordsNothingAsync</tests>
public sealed partial class MultiStreamCommandIntake<TCommand>(
    IMultiStreamCommandStore store,
    ILogger<MultiStreamCommandIntake<TCommand>> logger) : IReceptor<TCommand> where TCommand : IMultiStreamCommand {
  private readonly IMultiStreamCommandStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly ILogger<MultiStreamCommandIntake<TCommand>> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

  /// <inheritdoc/>
  /// <exception cref="ArgumentException">The command has no run id, no streams, or an empty stream id.</exception>
  /// <exception cref="InvalidOperationException">No multi-stream command store is registered.</exception>
  public async ValueTask HandleAsync(TCommand message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    if (message.RunId == Guid.Empty) {
      throw new ArgumentException("A multi-stream command needs a run id: the run is recorded and read by it.", nameof(message));
    }
    var streams = _distinctStreams(message.StreamIds);
    if (!_store.IsConfigured) {
      throw new InvalidOperationException(
        $"{MultiStreamCommandNames.Of(typeof(TCommand))} cannot be accepted: no multi-stream command store is registered, so no stream would ever run.");
    }

    var run = new MultiStreamRun(
      message.RunId,
      MultiStreamCommandNames.Of(typeof(TCommand)),
      MultiStreamJson.Serialize(message),
      streams,
      MultiStreamScope.Capture(ScopeContextAccessor.CurrentContext));
    if (await _store.RecordRunAsync(run, cancellationToken).ConfigureAwait(false)) {
      LogRecorded(_logger, message.RunId, run.CommandType, streams.Count);
    } else {
      LogAlreadyRecorded(_logger, message.RunId, run.CommandType);
    }
  }

  private static List<Guid> _distinctStreams(IReadOnlyList<Guid>? streamIds) {
    if (streamIds is null || streamIds.Count == 0) {
      throw new ArgumentException(
        "A multi-stream command must name at least one stream. An empty list is refused, never read as every stream.", nameof(streamIds));
    }
    var seen = new HashSet<Guid>();
    var streams = new List<Guid>(streamIds.Count);
    foreach (var streamId in streamIds) {
      if (streamId == Guid.Empty) {
        throw new ArgumentException("A multi-stream command named an empty stream id.", nameof(streamIds));
      }
      if (seen.Add(streamId)) {
        streams.Add(streamId);
      }
    }
    return streams;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Recorded multi-stream run {RunId} of {CommandType} over {StreamCount} streams.")]
  static partial void LogRecorded(ILogger logger, Guid runId, string commandType, int streamCount);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Multi-stream run {RunId} of {CommandType} was already recorded; the delivery is a repeat.")]
  static partial void LogAlreadyRecorded(ILogger logger, Guid runId, string commandType);
}
