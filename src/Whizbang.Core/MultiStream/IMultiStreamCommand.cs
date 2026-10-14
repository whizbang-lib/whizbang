// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.MultiStream;

/// <summary>
/// A command that names a set of streams and is worked one stream at a time, each stream on the instance that owns it.
/// Whichever instance receives the command records a run with one entry per stream; every instance of the service then
/// claims only the run's streams it owns, by the same rule that decides who processes each stream's live work, and calls
/// the command's <see cref="IMultiStreamCommandHandler{TCommand}"/> once per stream. Register the handler with
/// <see cref="MultiStreamCommandServiceCollectionExtensions.AddMultiStreamCommandHandler{TCommand, THandler}"/>.
/// </summary>
/// <remarks>
/// The handler can run more than once for a stream (a redelivered claim after an instance dies, or two instances reading
/// different partition assignments during a rebalance), so it must leave the same result when repeated.
/// </remarks>
/// <docs>fundamentals/dispatcher/multi-stream-commands</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandIntakeTests.cs:HandleAsync_RecordsOneRunCarryingTheCommandAndItsStreamsAsync</tests>
public interface IMultiStreamCommand : ICommand {
  /// <summary>
  /// The run's identity, chosen by the caller (a new <c>TrackedGuid</c>). Recording is idempotent on it, so a command
  /// delivered twice makes one run, and the caller reads the run's progress by it. Must not be empty.
  /// </summary>
  Guid RunId { get; }

  /// <summary>
  /// The streams to work. Must not be empty: an empty list is refused, never read as "every stream". A stream named more
  /// than once is worked once.
  /// </summary>
  IReadOnlyList<Guid> StreamIds { get; }
}
