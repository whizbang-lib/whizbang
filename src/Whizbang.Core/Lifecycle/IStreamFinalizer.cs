// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// Seals a stream on the developer's say-so: the stream's lifecycle row becomes <c>Sealed</c>, with the time and the
/// reason, and every later append to the stream is refused (WS-3 S4). Both Postgres drivers register an implementation.
/// </summary>
/// <remarks>
/// <para>
/// Sealing is the first step of a stream's end of life. A sealed stream keeps every event it holds and stays readable;
/// it only stops growing. A <see cref="Attributes.TerminalEventAttribute">terminal event</see> seals its stream the same
/// way, in the transaction that stores it.
/// </para>
/// <para>
/// Finalizing is idempotent: finalizing a sealed stream changes nothing and returns the time and reason it was first
/// sealed with (<see cref="StreamFinalizeStatus.AlreadySealed"/>). A stream with no Sourced event (one never written, or
/// an ephemeral stream, which ends by its time to live) has nothing to seal
/// (<see cref="StreamFinalizeStatus.NothingToSeal"/>).
/// </para>
/// </remarks>
/// <docs>fundamentals/events/stream-end-of-life#finalizing-a-stream</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamSealingTests.cs</tests>
public interface IStreamFinalizer {
  /// <summary>Seals <paramref name="streamId"/> with <paramref name="reason"/>.</summary>
  /// <param name="streamId">The stream to seal. <see cref="Guid.Empty"/> is "no stream" and is refused.</param>
  /// <param name="reason">Why the stream ends; recorded on the lifecycle row and carried by every refusal. Required.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <returns>What the call did, and the time and reason the stream is sealed with.</returns>
  /// <exception cref="ArgumentException"><paramref name="streamId"/> is empty, or <paramref name="reason"/> is null,
  /// empty or white space.</exception>
  Task<StreamFinalizeResult> FinalizeAsync(Guid streamId, string reason, CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IStreamFinalizer.FinalizeAsync"/> did.</summary>
/// <docs>fundamentals/events/stream-end-of-life#finalizing-a-stream</docs>
public enum StreamFinalizeStatus {
  /// <summary>The stream was sealed by this call.</summary>
  Sealed = 0,

  /// <summary>The stream was already sealed; nothing changed, and the result carries the first seal's time and reason.</summary>
  AlreadySealed = 1,

  /// <summary>The stream holds no Sourced event (never written, or ephemeral), so there is nothing to seal.</summary>
  NothingToSeal = 2,
}

/// <summary>The outcome of <see cref="IStreamFinalizer.FinalizeAsync"/>.</summary>
/// <param name="StreamId">The stream.</param>
/// <param name="Status">What the call did.</param>
/// <param name="SealedAt">When the stream was sealed; null when there was nothing to seal.</param>
/// <param name="Reason">The reason the stream is sealed with; null when there was nothing to seal.</param>
/// <docs>fundamentals/events/stream-end-of-life#finalizing-a-stream</docs>
public sealed record StreamFinalizeResult(Guid StreamId, StreamFinalizeStatus Status, DateTimeOffset? SealedAt, string? Reason);
