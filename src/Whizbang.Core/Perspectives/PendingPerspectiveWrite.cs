// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>What a pending perspective write does to its row.</summary>
/// <docs>fundamentals/perspectives/write-modes</docs>
public enum PerspectiveWriteOperation {
  /// <summary>Write the row whatever its version is, subject only to the ordering guard (an unchecked version).</summary>
  Upsert = 0,

  /// <summary>Insert a row the apply read as absent; a row that exists by the time of the write is a conflict.</summary>
  Insert = 1,

  /// <summary>Update the row on the exact version the apply read, subject to the ordering guard.</summary>
  Update = 2,

  /// <summary>Delete the row.</summary>
  Purge = 3,
}

/// <summary>
/// One physical column's value in a pending write, already in the form the column is written with.
/// </summary>
/// <param name="Column">The column name: a plain identifier, written double-quoted.</param>
/// <param name="Value">The value, or null for a SQL null.</param>
/// <param name="StoreType">
/// The column's store type (for instance <c>integer</c>, <c>text</c> or <c>vector(3)</c>), or null when it is not
/// known. A set-based update names it to type a value that has no target column to take its type from.
/// </param>
/// <param name="IsJsonDocument">
/// Whether <paramref name="Value"/> is JSON text produced by the column's own converter, to be written as
/// <c>jsonb</c>.
/// </param>
/// <docs>fundamentals/perspectives/write-modes</docs>
public readonly record struct PerspectivePhysicalValue(string Column, object? Value, string? StoreType, bool IsJsonDocument);

/// <summary>
/// The values a pending upsert, insert or update writes into its row, serialized and stamped exactly as the
/// per-stream write would write them.
/// </summary>
/// <remarks>
/// A perspective document has one writer rule: whichever path writes a row writes the same bytes
/// (<c>ai-docs/perspective-stored-forms.md</c>). These values are therefore produced by the driver, from the same
/// serialization profile, apply hooks and expiry rule the per-stream write uses, and the sink only places them.
/// </remarks>
/// <param name="Data">The model document, as JSON text.</param>
/// <param name="Metadata">The row metadata, as JSON text; the ordering guard reads it.</param>
/// <param name="Scope">The row scope, as JSON text.</param>
/// <param name="ForceUpdateScope">Whether an update replaces the stored scope (a scope event) or keeps it.</param>
/// <param name="OrdersOnEventId">Whether the ordering guard orders on the event id rather than the commit sequence.</param>
/// <param name="CreatedAt">The business time an inserted row is created at.</param>
/// <param name="UpdatedAt">The business time the write stamps, unless <paramref name="SuppressActivity"/>.</param>
/// <param name="SuppressActivity">Whether an update keeps the stored business time (a non-activity event).</param>
/// <param name="BumpVersion">Whether an update moves the row's <c>version</c> column.</param>
/// <param name="ExpiresAt">The row's expiry, or null when the model has no row time to live.</param>
/// <param name="PhysicalValues">The physical columns written alongside the document, in column order.</param>
/// <docs>fundamentals/perspectives/write-modes</docs>
public sealed record PerspectiveRowValues(
    string Data,
    string Metadata,
    string Scope,
    bool ForceUpdateScope,
    bool OrdersOnEventId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool SuppressActivity,
    bool BumpVersion,
    DateTime? ExpiresAt,
    IReadOnlyList<PerspectivePhysicalValue> PhysicalValues);

/// <summary>
/// A perspective row write, prepared and waiting to be written by an <see cref="IPerspectiveWriteSink"/> together
/// with the other writes of its batch.
/// </summary>
/// <remarks>
/// The operation follows from the version the apply read: an unchecked version upserts, an absent row is inserted,
/// a present row is updated on that version. A purge carries no row values. The two are built only through
/// <see cref="Write"/> and <see cref="Purge"/>, so a write whose operation disagrees with its version cannot be made.
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PendingPerspectiveWriteTests.cs</tests>
public sealed class PendingPerspectiveWrite {
  private PendingPerspectiveWrite(
      string table, Guid streamId, PerspectiveWriteOperation operation, PerspectiveRowVersion expectedVersion,
      PerspectiveRowValues? row) {
    Table = table;
    StreamId = streamId;
    Operation = operation;
    ExpectedVersion = expectedVersion;
    Row = row;
  }

  /// <summary>The schema-qualified, quoted perspective table the row lives in.</summary>
  public string Table { get; }

  /// <summary>The stream, which is the row's id.</summary>
  public Guid StreamId { get; }

  /// <summary>What the write does.</summary>
  public PerspectiveWriteOperation Operation { get; }

  /// <summary>The version the apply read: unchecked, absent, or the row's <c>xmin</c>.</summary>
  public PerspectiveRowVersion ExpectedVersion { get; }

  /// <summary>The values written, or null for a purge.</summary>
  public PerspectiveRowValues? Row { get; }

  /// <summary>
  /// The key the write sink of the row's DbContext is registered under (the context's type for the Entity Framework
  /// driver), or null for the sink registered without a key. A batch writes each context's rows through that context's
  /// own sink, so a service with several contexts writes every perspective through its own.
  /// </summary>
  /// <docs>fundamentals/perspectives/write-modes#the-batch</docs>
  public object? SinkKey { get; private init; }

  /// <summary>The same write, for the sink registered under <paramref name="sinkKey"/>.</summary>
  /// <param name="sinkKey">The key of the sink of the row's DbContext.</param>
  /// <returns>The write, keyed to that sink.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PendingPerspectiveWriteTests.cs:ForSink_KeysTheSameWriteToTheContextsSinkAsync</tests>
  public PendingPerspectiveWrite ForSink(object sinkKey) {
    ArgumentNullException.ThrowIfNull(sinkKey);
    return new PendingPerspectiveWrite(Table, StreamId, Operation, ExpectedVersion, Row) { SinkKey = sinkKey };
  }

  /// <summary>An upsert, insert or update of a row, decided by the version the apply read.</summary>
  /// <param name="table">The schema-qualified, quoted perspective table.</param>
  /// <param name="streamId">The stream, which is the row's id.</param>
  /// <param name="expectedVersion">The version the apply read.</param>
  /// <param name="row">The values to write.</param>
  /// <returns>The pending write.</returns>
  public static PendingPerspectiveWrite Write(
      string table, Guid streamId, PerspectiveRowVersion expectedVersion, PerspectiveRowValues row) {
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    ArgumentNullException.ThrowIfNull(row);
    var operation = expectedVersion.State switch {
      PerspectiveRowVersionState.Absent => PerspectiveWriteOperation.Insert,
      PerspectiveRowVersionState.Present => PerspectiveWriteOperation.Update,
      _ => PerspectiveWriteOperation.Upsert,
    };
    return new PendingPerspectiveWrite(table, streamId, operation, expectedVersion, row);
  }

  /// <summary>A delete of the row, whatever its version.</summary>
  /// <param name="table">The schema-qualified, quoted perspective table.</param>
  /// <param name="streamId">The stream, which is the row's id.</param>
  /// <returns>The pending purge.</returns>
  public static PendingPerspectiveWrite Purge(string table, Guid streamId) {
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    return new PendingPerspectiveWrite(table, streamId, PerspectiveWriteOperation.Purge, PerspectiveRowVersion.Unchecked, null);
  }
}
