// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Turns the store's sealed-stream refusal (SQLSTATE <c>WHS01</c>, raised by <c>wh_append_event</c> for a direct append
/// to a sealed stream) into <see cref="StreamSealedException"/>, so the caller sees a typed error naming the stream, when
/// it was sealed and why.
/// </summary>
/// <remarks>
/// The store's message is kept as the exception's message: it is the same text a dead-lettered refusal records,
/// <c>Stream {id} was sealed at {sealed_at} and refuses appends. Reason: {reason}</c>. The three fields are read back from
/// it (the reason last, so it may hold any text); the message detail is not used, because Npgsql redacts it unless the
/// connection string opts in.
/// </remarks>
/// <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamSealedRefusalTests.cs</tests>
internal static class StreamSealedRefusal {
  /// <summary>The SQLSTATE <c>wh_append_event</c> raises for a sealed stream (migration 266).</summary>
  internal const string SQL_STATE = "WHS01";

  private const string STREAM_MARKER = "Stream ";
  private const string SEALED_AT_MARKER = " was sealed at ";
  private const string REASON_MARKER = " and refuses appends. Reason: ";
  private const string SEALED_AT_FORMAT = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";
  private const int GUID_LENGTH = 36;

  /// <summary>
  /// Runs a store, rethrowing the sealed-stream refusal as <see cref="StreamSealedException"/>. Every other failure
  /// propagates unchanged.
  /// </summary>
  /// <param name="store">The store call.</param>
  /// <returns>A task that completes when the store does.</returns>
  /// <exception cref="StreamSealedException">The store refused an append to a sealed stream.</exception>
  internal static async Task RunAsync(Func<Task> store) {
    try {
      await store().ConfigureAwait(false);
    } catch (PostgresException ex) when (ex.SqlState == SQL_STATE) {
      throw ToException(ex);
    }
  }

  /// <summary>Builds the typed exception from the store's refusal.</summary>
  /// <param name="refusal">The refusal the store raised.</param>
  /// <returns>The exception, carrying the store's message and the refusal as its inner exception.</returns>
  internal static StreamSealedException ToException(PostgresException refusal) {
    var text = refusal.MessageText;
    var sealedAtStart = text.IndexOf(SEALED_AT_MARKER, StringComparison.Ordinal);
    var reasonStart = sealedAtStart < 0 ? -1 : text.IndexOf(REASON_MARKER, sealedAtStart, StringComparison.Ordinal);
    if (reasonStart < 0) {
      // A store older or newer than this reader: the message is kept, the fields it does not carry are left empty.
      return new StreamSealedException(text, Guid.Empty, sealedAt: null, reason: null, refusal);
    }
    var sealedAtText = text[(sealedAtStart + SEALED_AT_MARKER.Length)..reasonStart];
    return new StreamSealedException(
      text,
      _streamId(text),
      DateTimeOffset.TryParseExact(sealedAtText, SEALED_AT_FORMAT, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var sealedAt) ? sealedAt : null,
      text[(reasonStart + REASON_MARKER.Length)..],
      refusal);
  }

  private static Guid _streamId(string text) =>
    // Called only for a message that holds both later markers, so it is long enough for an id after the first.
    text.StartsWith(STREAM_MARKER, StringComparison.Ordinal)
      && Guid.TryParse(text.AsSpan(STREAM_MARKER.Length, GUID_LENGTH), out var id)
      ? id
      : Guid.Empty;
}
