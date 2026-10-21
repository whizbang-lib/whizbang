// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Turns the store's mixed-stream refusal (SQLSTATE <c>WH133</c>, raised for a write whose caller awaits it) into
/// <see cref="MixedStreamEventException"/>, so the caller sees a typed error naming the stream and the event.
/// </summary>
/// <remarks>
/// The store's message is kept as the exception's message: it is the same text a dead-lettered refusal records, and it
/// names the stream, the refused event's type and kind, and the kind the stream holds. The stream and event ids are
/// read back from it; the message detail is not used, because Npgsql redacts it unless the connection string opts in.
/// </remarks>
/// <docs>operations/diagnostics/whiz133</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/MixedStreamRefusalTests.cs</tests>
internal static class MixedStreamRefusal {
  /// <summary>The SQLSTATE <c>_wh_refuse_mixed_stream_events</c> raises (migration 223).</summary>
  internal const string SQL_STATE = "WH133";

  private const string STREAM_MARKER = "stream ";
  private const string EVENT_MARKER = "(event ";
  private const int GUID_LENGTH = 36;

  /// <summary>
  /// Runs a store, rethrowing the mixed-stream refusal as <see cref="MixedStreamEventException"/>. Every other
  /// failure propagates unchanged.
  /// </summary>
  /// <param name="store">The store call.</param>
  /// <returns>A task that completes when the store does.</returns>
  /// <exception cref="MixedStreamEventException">The store refused an event the caller awaits.</exception>
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
  internal static MixedStreamEventException ToException(PostgresException refusal) {
    var text = refusal.MessageText;
    return new MixedStreamEventException(
      text,
      _guidAfter(text, text.IndexOf(STREAM_MARKER, StringComparison.Ordinal), STREAM_MARKER.Length),
      _guidAfter(text, text.LastIndexOf(EVENT_MARKER, StringComparison.Ordinal), EVENT_MARKER.Length),
      refusal);
  }

  // The id written right after a marker in the store's message, or Guid.Empty when the message does not carry one
  // there (a store older or newer than this reader). The message itself is kept either way.
  private static Guid _guidAfter(string text, int markerAt, int markerLength) {
    if (markerAt < 0 || markerAt + markerLength + GUID_LENGTH > text.Length) {
      return Guid.Empty;
    }
    return Guid.TryParse(text.AsSpan(markerAt + markerLength, GUID_LENGTH), out var id) ? id : Guid.Empty;
  }
}
