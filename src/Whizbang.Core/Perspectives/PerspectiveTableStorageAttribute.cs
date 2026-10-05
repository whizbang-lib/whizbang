// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Storage options for a perspective's table as a whole: the compression of its <c>data</c> document and
/// the row size past which PostgreSQL starts compressing and moving values out of line.
/// </summary>
/// <remarks>
/// <para>
/// Applied by the schema pass on every start, and only where the table differs, so a restart changes
/// nothing. Both settings affect rows written from then on; rows already stored keep their layout until
/// they are rewritten.
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
}
