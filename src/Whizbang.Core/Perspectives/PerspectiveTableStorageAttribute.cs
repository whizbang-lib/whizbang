// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Storage options for a perspective's table as a whole: the compression of its <c>data</c> document, the row
/// size past which PostgreSQL starts compressing and moving values out of line, and when autovacuum runs.
/// </summary>
/// <remarks>
/// <para>
/// Applied by the schema pass on every start, and only where the table differs, so a restart changes
/// nothing. The compression settings affect rows written from then on; rows already stored keep their layout
/// until they are rewritten. A setting removed from the attribute stays on the table until it is reset by hand.
/// </para>
/// <para>
/// The autovacuum settings matter on a large table that is updated or deleted from often. The server's default
/// trigger is a fraction of the table, so a table of millions of rows waits for hundreds of thousands of dead
/// rows before it is vacuumed, and grows meanwhile. A scale factor of 0 with a fixed threshold vacuums it after
/// the same number of changes at any size. Each autovacuum setting is negative by default, which keeps the
/// server's value.
/// </para>
/// <para>
/// Pairs with <see cref="PhysicalFieldAttribute.Storage"/> on a small jsonb filter column: the document can
/// be compressed with LZ4 and moved out of line when it is large, while the filter column stays in the row
/// where a filter reads it without a second lookup.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [PerspectiveTableStorage(DataCompression = ColumnCompression.Lz4)]
/// public record OrderModel {
///   [PhysicalField(Storage = ColumnStorage.Main, MaxBytes = 1024)]
///   public Dictionary&lt;string, string[]&gt; Filters { get; init; } = [];
/// }
/// </code>
/// </example>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-storage</docs>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class PerspectiveTableStorageAttribute : Attribute {
  /// <summary>The compression method for the <c>data</c> column.</summary>
  public ColumnCompression DataCompression { get; init; }

  /// <summary>
  /// The table's <c>toast_tuple_target</c>: the row size, in bytes, past which PostgreSQL compresses and moves
  /// values out of line. Between 128 and 8160; -1 or 0 leaves the server's default (about 2 KB).
  /// </summary>
  /// <remarks>
  /// Raising it keeps larger rows entirely inline, at the cost of fewer rows per page; lowering it pushes a
  /// large document out sooner and keeps the rows a scan walks through small.
  /// </remarks>
  public int ToastTupleTarget { get; init; } = -1;

  /// <summary>
  /// The table's <c>autovacuum_vacuum_threshold</c>: the number of updated or deleted rows, added to the scale
  /// factor's share, that starts a vacuum. 0 or more; negative keeps the server's value.
  /// </summary>
  public int AutovacuumVacuumThreshold { get; init; } = -1;

  /// <summary>
  /// The table's <c>autovacuum_vacuum_scale_factor</c>: the fraction of the table, added to the threshold, that
  /// starts a vacuum. Between 0 and 100; negative keeps the server's value. Use 0 with a threshold on a large table.
  /// </summary>
  public double AutovacuumVacuumScaleFactor { get; init; } = -1;

  /// <summary>
  /// The table's <c>autovacuum_vacuum_insert_threshold</c>: the number of inserted rows that starts a vacuum,
  /// which keeps the visibility map current on an append-heavy table. 0 or more; negative keeps the server's value.
  /// </summary>
  public int AutovacuumInsertThreshold { get; init; } = -1;

  /// <summary>
  /// The table's <c>autovacuum_vacuum_insert_scale_factor</c>. Between 0 and 100; negative keeps the server's value.
  /// </summary>
  public double AutovacuumInsertScaleFactor { get; init; } = -1;

  /// <summary>
  /// <c>toast.autovacuum_vacuum_threshold</c>, for the TOAST relation that holds large documents out of line.
  /// 0 or more; negative keeps the server's value.
  /// </summary>
  public int ToastAutovacuumVacuumThreshold { get; init; } = -1;

  /// <summary>
  /// <c>toast.autovacuum_vacuum_scale_factor</c>. Between 0 and 100; negative keeps the server's value.
  /// </summary>
  public double ToastAutovacuumVacuumScaleFactor { get; init; } = -1;
}
