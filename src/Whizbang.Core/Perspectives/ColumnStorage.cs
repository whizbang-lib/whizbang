// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// PostgreSQL's storage strategy for a column's values, the <c>SET STORAGE</c> of
/// <c>ALTER TABLE … ALTER COLUMN</c>.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-storage</docs>
public enum ColumnStorage {
  /// <summary>Leave the column's storage as its type defines it (<c>EXTENDED</c> for jsonb and text).</summary>
  Default = 0,

  /// <summary>Kept inline and uncompressed. Only for fixed-length types; PostgreSQL refuses it for jsonb.</summary>
  Plain,

  /// <summary>Kept inline, compressed when the row is too large, moved out of line only as a last resort.</summary>
  Main,

  /// <summary>Moved out of line uncompressed when the row is too large; fastest substring access.</summary>
  External,

  /// <summary>Compressed first, then moved out of line: PostgreSQL's default for variable-length types.</summary>
  Extended,
}
