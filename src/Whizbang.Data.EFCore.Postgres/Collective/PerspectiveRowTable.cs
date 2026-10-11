// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Whizbang.Data.EFCore.Postgres.Collective;

/// <summary>
/// Resolves a perspective row type's table from the EF Core model, for the collective paths that write
/// raw SQL against it. A row type the model does not map, or maps to something other than a table, is a
/// configuration error the caller cannot work around, so both are refused here, once, with the reason.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/PerspectiveRowTableTests.cs</tests>
internal static class PerspectiveRowTable {
  /// <summary>Resolves the table and schema <paramref name="rowType"/> is mapped to.</summary>
  /// <param name="model">The model of the <c>DbContext</c> the SQL will run on.</param>
  /// <param name="rowType">The <c>PerspectiveRow&lt;TModel&gt;</c> type.</param>
  /// <param name="modelName">The perspective model's name, for the error messages.</param>
  /// <param name="notMappedHint">What the caller was doing when it needed the table, appended to the not-mapped error.</param>
  /// <returns>The table name, and the schema or null for the connection's default.</returns>
  /// <exception cref="InvalidOperationException">The model does not map the row type, or maps it without a table.</exception>
  internal static (string Table, string? Schema) Resolve(
      IModel model,
      [DynamicallyAccessedMembers(EntityTypeTrimming.MEMBERS)] Type rowType,
      string modelName,
      string notMappedHint) {
    ArgumentNullException.ThrowIfNull(model);
    var entityType = model.FindEntityType(rowType)
      ?? throw new InvalidOperationException(
        $"No EF Core entity is mapped for PerspectiveRow<{modelName}>: {notMappedHint}");
    var table = entityType.GetTableName()
      ?? throw new InvalidOperationException($"PerspectiveRow<{modelName}> has no table name.");
    return (table, entityType.GetSchema());
  }
}
