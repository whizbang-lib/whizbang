// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Whizbang.Data.Postgres;

/// <summary>Which way the runner has to send a piece of a migration.</summary>
public enum MigrationSegmentKind {
  /// <summary>Sent once, as part of the migration's single command.</summary>
  Plain,

  /// <summary>Sent repeatedly, each send its own command, until it reports no rows.</summary>
  Batch,
}

/// <summary>One piece of a migration, in the order the file wrote it.</summary>
/// <param name="Kind">Whether the runner sends this once or repeatedly.</param>
/// <param name="Sql">The SQL, with the size token already substituted for a batch.</param>
/// <param name="BatchSize">Rows per send for a batch; zero for plain SQL.</param>
public sealed record MigrationSegment(MigrationSegmentKind Kind, string Sql, int BatchSize);

/// <summary>
/// Splits a migration into the pieces the runner sends separately, so a backfill over a large table
/// is bounded by rows rather than by the clock.
/// </summary>
/// <remarks>
/// <para>
/// A migration file is executed as one command and that command carries one timeout, so a statement
/// that rewrites every row of a table cannot finish once the table is large enough. Writing a loop
/// inside the SQL does not help: a <c>DO</c> block is still one command, and the whole loop shares
/// the one budget. The statement has to leave the file and come back repeatedly.
/// </para>
/// <para>
/// A marked region is therefore sent on its own, over and over, until it reports that it changed no
/// rows. Each send is a fresh command with a fresh budget, so the bound that matters becomes the
/// number of rows one send touches — which the migration declares and the author can reason about —
/// rather than how long the whole table takes, which nobody can know in advance. The size of a
/// consumer's table is not knowable when the migration is written.
/// </para>
/// <para>
/// The marker sits next to the statement it bounds, for the same reason the bootstrap markers do: a
/// list kept in code goes stale the first time a migration changes, while a marker is edited by
/// whoever edits the statement.
/// </para>
/// <para>
/// Order is preserved and regions are never grouped. A backfill is marked next to the function it
/// calls, and that function is created by plain SQL above it, so a split that moved regions would
/// call something that does not exist yet.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/MigrationBatchRegionsTests.cs</tests>
/// <docs>operations/infrastructure/migrations</docs>
public static class MigrationBatchRegions {
  /// <summary>Opens a region the runner sends repeatedly. Accepts an optional <c>size=N</c>.</summary>
  public const string BEGIN = "-- @whizbang:batch-begin";

  /// <summary>Closes a region opened by <see cref="BEGIN"/>.</summary>
  public const string END = "-- @whizbang:batch-end";

  /// <summary>
  /// Stands in for the row bound inside a marked region, replaced with the declared size.
  /// </summary>
  /// <remarks>
  /// The runner sends the region as plain text with no parameters, because a migration is a script
  /// rather than a prepared statement, so the bound is substituted rather than bound.
  /// </remarks>
  public const string SIZE_TOKEN = "@whizbang_batch_size";

  /// <summary>Rows per send when a region does not declare one.</summary>
  /// <remarks>
  /// Large enough that a backfill does not spend its time on round trips, small enough that one
  /// send stays well inside any command timeout on a row that carries a sizable document.
  /// </remarks>
  public const int DEFAULT_BATCH_SIZE = 10_000;

  /// <summary>
  /// Splits <paramref name="sql"/> into the segments the runner sends, in file order.
  /// </summary>
  /// <param name="sql">One migration file's text, after schema substitution.</param>
  /// <returns>
  /// The segments. A file with no markers — which is all but a handful — comes back as a single
  /// <see cref="MigrationSegmentKind.Plain"/> segment holding the text unchanged, so the path every
  /// existing migration takes is byte-identical to sending the file directly.
  /// </returns>
  /// <exception cref="InvalidOperationException">
  /// A region is opened and never closed, a close appears with nothing open, or a declared size is
  /// not a positive whole number. Each would otherwise run the very statement the marker bounds.
  /// </exception>
  public static IReadOnlyList<MigrationSegment> Segment(string sql) {
    if (sql is null) {
      return [];
    }

    var hasBegin = sql.Contains(BEGIN, StringComparison.Ordinal);
    var hasEnd = sql.Contains(END, StringComparison.Ordinal);
    if (!hasBegin && !hasEnd) {
      return [new MigrationSegment(MigrationSegmentKind.Plain, sql, 0)];
    }

    var segments = new List<MigrationSegment>();
    var current = new StringBuilder();
    var inside = false;
    var size = DEFAULT_BATCH_SIZE;

    foreach (var line in sql.Split('\n')) {
      var trimmed = line.Trim();

      if (trimmed.StartsWith(BEGIN, StringComparison.Ordinal)) {
        if (inside) {
          throw new InvalidOperationException(
            $"A migration opened a batch region while one was already open. '{BEGIN}' does not nest.");
        }

        _flush(segments, current, MigrationSegmentKind.Plain, 0);
        size = _parseSize(trimmed);
        inside = true;
        continue;
      }

      if (trimmed.StartsWith(END, StringComparison.Ordinal)) {
        if (!inside) {
          throw new InvalidOperationException(
            $"A migration closed a batch region that was never opened. '{END}' needs a matching '{BEGIN}'.");
        }

        _flush(segments, current, MigrationSegmentKind.Batch, size);
        inside = false;
        size = DEFAULT_BATCH_SIZE;
        continue;
      }

      current.Append(line).Append('\n');
    }

    if (inside) {
      throw new InvalidOperationException(
        $"A migration opened a batch region and never closed it. Without '{END}' the statement it "
        + "bounds would run unbounded, which is what the marker exists to prevent.");
    }

    _flush(segments, current, MigrationSegmentKind.Plain, 0);
    return segments;
  }

  /// <summary>Moves what has accumulated into a segment, dropping one that is only whitespace.</summary>
  private static void _flush(
      List<MigrationSegment> segments, StringBuilder current, MigrationSegmentKind kind, int size) {
    var text = current.ToString();
    current.Clear();

    if (string.IsNullOrWhiteSpace(text)) {
      return;
    }

    if (kind == MigrationSegmentKind.Batch) {
      text = text.Replace(SIZE_TOKEN, size.ToString(CultureInfo.InvariantCulture));
    }

    segments.Add(new MigrationSegment(kind, text.TrimEnd('\n'), kind == MigrationSegmentKind.Batch ? size : 0));
  }

  /// <summary>Reads <c>size=N</c> off a begin marker, or supplies the default.</summary>
  private static int _parseSize(string beginLine) {
    var rest = beginLine[BEGIN.Length..].Trim();
    if (rest.Length == 0) {
      return DEFAULT_BATCH_SIZE;
    }

    const string key = "size=";
    if (!rest.StartsWith(key, StringComparison.Ordinal)) {
      throw new InvalidOperationException(
        $"A batch region declared '{rest}', which is not understood. The only setting is '{key}N'.");
    }

    var value = rest[key.Length..].Trim();
    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0) {
      throw new InvalidOperationException(
        $"A batch region declared a size of '{value}'. It has to be a positive whole number of rows: "
        + "a size of zero or less would never finish, because a send that touches no rows ends the loop.");
    }

    return parsed;
  }
}
