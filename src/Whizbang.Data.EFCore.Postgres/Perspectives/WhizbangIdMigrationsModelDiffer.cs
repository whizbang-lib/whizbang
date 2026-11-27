// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Scaffolds the move of a strongly-typed id's column from <c>&lt;column&gt;_Value</c>, the column an earlier release's
/// complex id held its value in, to <c>&lt;column&gt;</c> as a rename of each column to its own id's (#2518).
/// </summary>
/// <remarks>
/// <para>
/// Entity Framework pairs a dropped column with an added one by column name, then by property name, then by shape alone.
/// The old column belongs to a property named <c>Value</c> and the new one to a property named after the id, so every id
/// column falls through to the shape match, and two ids of one shape are paired in name order: a table with
/// <c>Route_From_Value</c> and <c>Route_FromHub_Value</c> scaffolded <c>Route_From_Value -&gt; Route_FromHub</c> and
/// <c>Route_FromHub_Value -&gt; Route_From</c>, which swaps the two ids' values with no error.
/// </para>
/// <para>
/// After Entity Framework's diff, each table's renames from <c>&lt;column&gt;_Value</c> are paired by name instead: when
/// every one of them has a new column of its own name among those renames, each goes to that column. A pair is moved only
/// between columns of one store type and nullability, so no alteration Entity Framework planned for the pairing it chose
/// is left behind; otherwise <c>migrations add</c> fails, naming the columns, rather than scaffold a rename that could
/// move a value to another id.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#strong-id-columns</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdEntityColumnTests.cs:MigrationsAdd_PairsEachValueColumnWithItsOwnId_NeverSwappingTwoOfOneShapeAsync</tests>
[SuppressMessage("Usage", "EF1001:Internal EF Core API usage",
  Justification = "The model differ has no public base; the pairing it chose is corrected after its own diff, which is kept whole.")]
internal sealed class WhizbangIdMigrationsModelDiffer(
    IRelationalTypeMappingSource typeMappingSource,
    IMigrationsAnnotationProvider migrationsAnnotationProvider,
    IRelationalAnnotationProvider relationalAnnotationProvider,
    IRowIdentityMapFactory rowIdentityMapFactory,
    CommandBatchPreparerDependencies commandBatchPreparerDependencies)
    : MigrationsModelDiffer(
      typeMappingSource, migrationsAnnotationProvider, relationalAnnotationProvider, rowIdentityMapFactory,
      commandBatchPreparerDependencies) {
  /// <summary>The suffix Entity Framework gives the column of a complex id's <c>Value</c>.</summary>
  private const string VALUE_SUFFIX = "_Value";

  /// <inheritdoc/>
  public override IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target) {
    var operations = base.GetDifferences(source, target);
    if (source is null || target is null) {
      return operations;
    }
    foreach (var table in operations.OfType<RenameColumnOperation>().GroupBy(r => (r.Schema, r.Table))) {
      _pairByName([.. table], source, target);
    }
    return operations;
  }

  private static void _pairByName(List<RenameColumnOperation> renames, IRelationalModel source, IRelationalModel target) {
    var fromValue = renames.Where(r => r.Name.EndsWith(VALUE_SUFFIX, StringComparison.Ordinal)).ToList();
    var newNames = fromValue.Select(r => r.NewName).ToHashSet(StringComparer.Ordinal);
    if (fromValue.Count == 0
        || !fromValue.All(r => newNames.Contains(r.Name[..^VALUE_SUFFIX.Length]))
        || fromValue.All(r => r.NewName == r.Name[..^VALUE_SUFFIX.Length])) {
      return;
    }
    var (schema, table) = (fromValue[0].Schema, fromValue[0].Table);
    foreach (var rename in fromValue) {
      var ownName = rename.Name[..^VALUE_SUFFIX.Length];
      var was = _column(source, schema, table, rename.Name);
      var becomes = _column(target, schema, table, ownName);
      if (was is null || becomes is null || was.StoreType != becomes.StoreType || was.IsNullable != becomes.IsNullable) {
        throw new InvalidOperationException(
          $"Entity Framework paired {table}.{rename.Name} with {rename.NewName}, not with {ownName}, and the two cannot be "
          + "paired by name because their store types or nullability differ. Write this migration's renames by hand, "
          + $"each '<column>{VALUE_SUFFIX}' to '<column>', so no strongly-typed id takes another's values (#2518).");
      }
      rename.NewName = ownName;
    }
  }

  private static IColumn? _column(IRelationalModel model, string? schema, string table, string column) =>
    model.FindTable(table, schema)?.FindColumn(column);
}
