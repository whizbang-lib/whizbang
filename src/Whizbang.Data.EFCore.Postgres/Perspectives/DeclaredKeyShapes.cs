// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Whizbang.Core.Lenses;
using Whizbang.Data.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// What the readers of every perspective table in a model declare, for the startup pass that brings stored keys to
/// their declared spelling (#1874).
/// </summary>
/// <remarks>
/// <para>
/// The names come from the thing that reads each document, as the temporal rewrite's paths do
/// (<see cref="CanonicalTemporalRewrite"/>): a column Entity Framework maps as a complex property is described by its
/// model, which already honors <c>[JsonPropertyName]</c> and any name the configuration set; a jsonb column it reads as
/// one value is described by the serializer's metadata for the column's type. Neither is reflection.
/// </para>
/// <para>
/// Entity Framework's model does not record whether a member is <see langword="required"/>, and its reader takes a
/// missing key as the member's default. So which members are required is read from the serializer's metadata for the
/// same type, matched by name, together with the members a type shared with an event declares required to
/// <see cref="Whizbang.Core.Serialization.DeclaredRequiredMembers"/> (whose reader tolerates them): the startup pass
/// reports the rows missing one, since that reader cannot refuse them.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/stored-form-migrations#declared-names</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DeclaredKeyShapesTests.cs</tests>
public static class DeclaredKeyShapes {
  private const string JSONB = "jsonb";

  /// <summary>Every perspective table in a model, with what each document column's reader declares.</summary>
  /// <param name="model">The context's model.</param>
  /// <param name="documentOptions">The options every document is read and written with.</param>
  /// <returns>One entry per perspective table with at least one document column, in table order.</returns>
  public static ImmutableArray<DeclaredKeyTable> ForModel(IModel model, JsonSerializerOptions documentOptions) {
    ArgumentNullException.ThrowIfNull(model);
    ArgumentNullException.ThrowIfNull(documentOptions);

    // Every perspective row has a table, and at least its scope column.
    return [.. model.GetEntityTypes()
      .Where(e => _isPerspectiveRow(e.ClrType))
      .Select(e => new DeclaredKeyTable(e.GetTableName()!, _columnsOf(e, documentOptions)))
      .Where(t => t.Columns.Count > 0)
      .OrderBy(t => t.Table, StringComparer.Ordinal)];
  }

  private static bool _isPerspectiveRow(Type type) =>
    type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PerspectiveRow<>);

  private static List<DeclaredKeyColumn> _columnsOf(IEntityType rowType, JsonSerializerOptions documentOptions) {
    var mapped = rowType.GetComplexProperties()
      .Select(c => c.ComplexType)
      .Where(c => c.GetContainerColumnName() is not null)
      .Select(c => new DeclaredKeyColumn(c.GetContainerColumnName()!, _mapped(c, documentOptions)));
    // A number, a string or a dictionary has no declared keys, and the serializer's metadata says so.
    var serialized = rowType.GetProperties()
      .Where(p => p.GetColumnType() == JSONB)
      .Select(p => (Column: p.GetColumnName(), Shape: DeclaredKeyShape.FromSerializer(documentOptions, p.ClrType)))
      .Where(p => p.Shape is not null)
      .Select(p => new DeclaredKeyColumn(p.Column, p.Shape!));
    return [.. mapped, .. serialized];
  }

  /// <summary>A mapped object as Entity Framework reads it, its required members marked from the serializer's metadata.</summary>
  private static DeclaredKeyShape _mapped(IComplexType type, JsonSerializerOptions documentOptions) {
    var required = _requiredNames(type.ClrType, documentOptions);
    var members = new List<DeclaredMember>();
    // Non-null for every property and nested complex property Entity Framework maps into a document, which is the
    // only place this walk runs (the invariant CanonicalTemporalRewrite asserts the same way).
    foreach (var property in type.GetProperties()) {
      var name = property.GetJsonPropertyName()!;
      members.Add(new DeclaredMember(name, required.Contains(name)));
    }
    foreach (var nested in type.GetComplexProperties()) {
      var name = nested.GetJsonPropertyName()!;
      var shape = _mapped(nested.ComplexType, documentOptions);
      members.Add(new DeclaredMember(name, required.Contains(name), nested.IsCollection ? DeclaredKeyShape.ForArray(shape) : shape));
    }
    return DeclaredKeyShape.ForObject(members);
  }

  private static HashSet<string> _requiredNames(Type type, JsonSerializerOptions documentOptions) =>
    DeclaredKeyShape.FromSerializer(documentOptions, type) is { } described
      ? described.Members.Where(m => m.IsRequired).Select(m => m.Name).ToHashSet(StringComparer.Ordinal)
      : [];
}
