// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Keeps a strongly-typed id outside a document in the column it had before #2518: <c>&lt;column&gt;_Value</c>, the
/// column Entity Framework gave the id's <c>Value</c> when it mapped the id as a complex type (#2518).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WhizbangIdTypeMappingSourcePlugin"/> maps an id as a <c>uuid</c> scalar wherever Entity Framework meets it,
/// which is what a perspective document needs. Outside a document, a scalar's column is named after the property, so an
/// id an earlier release stored as a complex type (<c>Address_DepotId_Value</c>, inside a table-split complex type)
/// would move to a new column (<c>Address_DepotId</c>): a schema change outside the strong-id gate, which an instance of
/// the earlier release cannot follow during a rolling deploy. This convention keeps the column, so old and new instances
/// read and write the same one and no migration is needed.
/// </para>
/// <para>
/// It leaves a property alone when its column name is configured (<c>HasColumnName</c>, <c>[Column]</c>): that is the
/// opt-in to a column named after the property, a two-step deploy (add the column and copy the values with every
/// instance on a release that reads both, then switch). It also leaves alone a property with a converter of its own
/// (<c>HasConversion</c>), which was a scalar named after the property before #2518 too. An id the model maps to JSON is
/// not a column and is not touched.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#strong-id-columns</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdEntityColumnTests.cs</tests>
public sealed class WhizbangIdColumnConvention : IModelFinalizingConvention {
  /// <summary>The suffix the column of a complex id's <c>Value</c> carries.</summary>
  public const string VALUE_SUFFIX = "_Value";

  /// <inheritdoc/>
  public void ProcessModelFinalizing(
      IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context) {
    ArgumentNullException.ThrowIfNull(modelBuilder);
    foreach (var entityType in modelBuilder.Metadata.GetEntityTypes().Where(e => !e.IsMappedToJson())) {
      _keepColumns(entityType);
    }
  }

  private static void _keepColumns(IConventionTypeBase type) {
    var ids = type.GetDeclaredProperties()
      .Where(p => _isPluginMappedId(p) && p.GetColumnNameConfigurationSource() is null)
      .ToList();
    foreach (var property in ids) {
      if (KeptColumn(property) is { } column) {
        property.Builder.HasColumnName(column);
      }
    }
    foreach (var complex in type.GetDeclaredComplexProperties().Where(c => !c.ComplexType.IsMappedToJson())) {
      _keepColumns(complex.ComplexType);
    }
  }

  /// <summary>
  /// The column a complex id's <c>Value</c> had in the table of the entity the property belongs to:
  /// <c>Depot_DepotId_Value</c> for <c>Depot.DepotId</c>. Null when the entity is not mapped to a table.
  /// </summary>
  /// <param name="property">An id property outside a document.</param>
  /// <returns>The column, or null.</returns>
  internal static string? KeptColumn(IReadOnlyProperty property) {
    ArgumentNullException.ThrowIfNull(property);
    var table = StoreObjectIdentifier.Create(property.DeclaringType.ContainingEntityType, StoreObjectType.Table);
    return table is { } store && property.GetColumnName(store) is { } column ? column + VALUE_SUFFIX : null;
  }

  /// <summary>An id whose mapping came from the plugin, not from a converter the model configured for it.</summary>
  private static bool _isPluginMappedId(IReadOnlyProperty property) =>
    property.GetValueConverter() is null
      && property.GetProviderClrType() is null
      && WhizbangIdTypeMapping.For(Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) is not null;
}

/// <summary>
/// Adds <see cref="WhizbangIdColumnConvention"/> to the convention set of every context that carries the Whizbang
/// options extension, which every generated perspective context does.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#strong-id-columns</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdEntityColumnTests.cs</tests>
public sealed class WhizbangIdColumnConventionSetPlugin : IConventionSetPlugin {
  /// <inheritdoc/>
  public ConventionSet ModifyConventions(ConventionSet conventionSet) {
    ArgumentNullException.ThrowIfNull(conventionSet);
    conventionSet.ModelFinalizingConventions.Add(new WhizbangIdColumnConvention());
    return conventionSet;
  }
}
