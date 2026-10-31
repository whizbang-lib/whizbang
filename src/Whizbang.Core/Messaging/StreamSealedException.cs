// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Thrown by <see cref="IEventStore.AppendAsync{TMessage}(Guid, Observability.MessageEnvelope{TMessage}, CancellationToken)"/>
/// when the stream is sealed: it was finalized (<see cref="Lifecycle.IStreamFinalizer"/>) or a
/// <see cref="Attributes.TerminalEventAttribute">terminal event</see> was stored on it. Nothing is stored.
/// </summary>
/// <remarks>
/// <para>
/// A sealed stream refuses every append, on every path. A write its caller awaits fails with this exception and keeps
/// nothing: a direct append, and an outbox write flushed by the immediate or scoped strategy (the same writes the
/// mixed-stream refusal raises for). A write nobody awaits (a batch flush, a handler commit, the inbox) is dead-lettered
/// instead, with the reason <see cref="MessageFailureReason.StreamSealed"/> and this exception's message as its error
/// text, and the other events of the same write are stored.
/// </para>
/// <para>
/// <see cref="Exception.Message"/> is the store's own wording, the same text a dead-lettered refusal records:
/// <c>Stream {id} was sealed at {sealed_at} and refuses appends. Reason: {reason}</c>.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamSealedRefusalTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:DirectAppend_ToASealedStream_ThrowsStreamSealedException_AndStoresNothingAsync</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
  Justification = "The exception is only meaningful with the stream, the time it was sealed and the reason; the standard constructors would produce one without them.")]
public sealed class StreamSealedException(
  string message, Guid streamId, DateTimeOffset? sealedAt, string? reason, Exception? innerException = null)
    : Exception(message, innerException) {

  /// <summary>The sealed stream.</summary>
  public Guid StreamId { get; } = streamId;

  /// <summary>When the stream was sealed, or null when the store's message did not carry it.</summary>
  public DateTimeOffset? SealedAt { get; } = sealedAt;

  /// <summary>Why the stream was sealed: the finalizer's reason, or the terminal event that sealed it.</summary>
  public string? Reason { get; } = reason;
}
