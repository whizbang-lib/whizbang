// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Thrown when storing an event would make one stream hold both ephemeral and Sourced events (diagnostic
/// <c>WHIZ133</c>), on a write whose caller awaits the store.
/// </summary>
/// <remarks>
/// <para>
/// One ephemeral event marks its whole stream state-based, so every rebuild and rewind then skips the
/// stream's Sourced history without a word. A stream therefore never mixes the two kinds, in either order,
/// and there is no opt-in. Publish the ephemeral events on a stream of their own.
/// </para>
/// <para>
/// The refusal is made where events are copied into the event store. A write the caller awaits (the
/// immediate and scoped strategies' flush, which the dispatcher awaits when publishing) fails with this
/// exception and keeps nothing of the write. Background writes (the batch flushes, the handler commit and
/// the inbox) dead-letter only the refused row instead, with the reason
/// <see cref="MessageFailureReason.MixedStreamRefused"/>, so the other streams in the same write are
/// stored and nothing is redelivered forever.
/// </para>
/// <para>
/// <see cref="Exception.Message"/> is the store's own wording, the same text a dead-lettered row records:
/// it names the stream, the refused event's type and kind, and the kind the stream already holds.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz133</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamGuardSqlTests.cs:Store_DirectCommitOfASourcedEventOnAnEphemeralStream_ThrowsMixedStreamEventExceptionAsync</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
  Justification = "The exception is only meaningful with the stream and the event it refused; the standard constructors would produce one without them.")]
public sealed class MixedStreamEventException(string message, Guid streamId, Guid eventId, Exception? innerException = null)
    : Exception(message, innerException) {

  /// <summary>The diagnostic id of the refusal, which every message and dead-letter reason starts with.</summary>
  public const string DIAGNOSTIC_ID = "WHIZ133";

  /// <summary>The stream the event was refused on.</summary>
  public Guid StreamId { get; } = streamId;

  /// <summary>The refused event's id.</summary>
  public Guid EventId { get; } = eventId;
}
