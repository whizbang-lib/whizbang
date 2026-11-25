// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Names the database schema a framework write targets, so a strongly-typed id in the documents it serializes takes the
/// form that schema's gate allows rather than the process's (#2518, <see cref="WhizbangIdDocumentForm.WritingTo"/>).
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveUpsertStrongIdFormTests.cs</tests>
internal static class StrongIdDocumentTarget {
  /// <summary>Targets the schema a table lives in, in the database the context is connected to.</summary>
  /// <param name="context">The context that writes.</param>
  /// <param name="schema">The table's schema; null is <c>public</c>.</param>
  /// <returns>The scope; disposing it restores the target before.</returns>
  internal static IDisposable Of(DbContext context, string? schema) =>
    WhizbangIdDocumentForm.WritingTo(context.Database.IsRelational() ? context.Database.GetDbConnection().Database : null, schema);

  /// <summary>Targets the schema an entity type's table lives in.</summary>
  /// <param name="context">The context that writes.</param>
  /// <param name="rowType">The entity type written.</param>
  /// <returns>The scope; disposing it restores the target before.</returns>
  internal static IDisposable Of(
      DbContext context, [DynamicallyAccessedMembers(EntityTypeTrimming.MEMBERS)] Type rowType) =>
    Of(context, context.Model.FindEntityType(rowType)?.GetSchema());
}
