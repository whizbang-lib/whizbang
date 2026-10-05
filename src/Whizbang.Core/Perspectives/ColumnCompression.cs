// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The compression method for a column's values, the <c>SET COMPRESSION</c> of
/// <c>ALTER TABLE … ALTER COLUMN</c>.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-storage</docs>
public enum ColumnCompression {
  /// <summary>Leave the column on the server's default, <c>default_toast_compression</c>.</summary>
  Default = 0,

  /// <summary>PostgreSQL's built-in compression.</summary>
  Pglz,

  /// <summary>
  /// LZ4: faster to compress and to read back than pglz, usually at a similar ratio. Needs a server built
  /// with it; on one that is not, the schema pass logs a warning and leaves the column as it was.
  /// </summary>
  Lz4,
}
