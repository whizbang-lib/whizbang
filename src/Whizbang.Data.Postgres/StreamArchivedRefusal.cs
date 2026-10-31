// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using Whizbang.Core.Archival;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Turns the store's archived-stream refusal into <see cref="StreamArchivedException"/>: SQLSTATE <c>WHA01</c> when the
/// stream's type refuses appends (<see cref="AppendToArchivedPolicy.Refuse"/>), <c>WHA02</c> when it restores the stream
/// first (<see cref="AppendToArchivedPolicy.AutoRestore"/>), raised by <c>wh_append_event</c> and, for a write its caller
/// awaits, <c>store_outbox_messages</c> (migration 271).
/// </summary>
/// <remarks>
/// The policy is read from the SQLSTATE alone, so no rewording of the message can change what the caller does. The
/// message is kept as the exception's message (the same text a dead-lettered refusal records,
/// <c>Stream {id} is archived through version {n} ...</c>), and the stream and the version are read back from it; the
/// message detail is not used, because Npgsql redacts it unless the connection string opts in.
/// </remarks>
/// <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/StreamArchivedRefusalTests.cs</tests>
internal static class StreamArchivedRefusal {
  /// <summary>The SQLSTATE the store raises for an archived stream whose type refuses appends.</summary>
  internal const string REFUSE_SQL_STATE = "WHA01";

  /// <summary>The SQLSTATE the store raises for an archived stream whose type restores it first.</summary>
  internal const string AUTO_RESTORE_SQL_STATE = "WHA02";

  private const string STREAM_MARKER = "Stream ";
  private const string VERSION_MARKER = " is archived through version ";
  private const int GUID_LENGTH = 36;

  /// <summary>
  /// Runs a store, rethrowing the archived-stream refusal as <see cref="StreamArchivedException"/>. Every other failure
  /// propagates unchanged.
  /// </summary>
  /// <param name="store">The store call.</param>
  /// <returns>A task that completes when the store does.</returns>
  /// <exception cref="StreamArchivedException">The store refused an append to an archived stream.</exception>
  internal static async Task RunAsync(Func<Task> store) {
    try {
      await store().ConfigureAwait(false);
    } catch (PostgresException ex) when (ex.SqlState is REFUSE_SQL_STATE or AUTO_RESTORE_SQL_STATE) {
      throw ToException(ex);
    }
  }

  /// <summary>Builds the typed exception from the store's refusal.</summary>
  /// <param name="refusal">The refusal the store raised.</param>
  /// <returns>The exception, carrying the store's message and the refusal as its inner exception.</returns>
  internal static StreamArchivedException ToException(PostgresException refusal) {
    var text = refusal.MessageText;
    var policy = refusal.SqlState == AUTO_RESTORE_SQL_STATE ? AppendToArchivedPolicy.AutoRestore : AppendToArchivedPolicy.Refuse;
    var versionStart = text.IndexOf(VERSION_MARKER, StringComparison.Ordinal);
    if (versionStart < 0) {
      // A store older or newer than this reader: the message is kept, the fields it does not carry are left empty.
      return new StreamArchivedException(text, Guid.Empty, archivedThroughVersion: null, policy, refusal);
    }
    var digits = text.AsSpan(versionStart + VERSION_MARKER.Length);
    var end = 0;
    while (end < digits.Length && char.IsAsciiDigit(digits[end])) {
      end++;
    }
    return new StreamArchivedException(
      text,
      _streamId(text, versionStart),
      long.TryParse(digits[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : null,
      policy,
      refusal);
  }

  private static Guid _streamId(string text, int versionStart) =>
    versionStart == STREAM_MARKER.Length + GUID_LENGTH
      && text.StartsWith(STREAM_MARKER, StringComparison.Ordinal)
      && Guid.TryParse(text.AsSpan(STREAM_MARKER.Length, GUID_LENGTH), out var id)
      ? id
      : Guid.Empty;
}
