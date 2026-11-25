// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Makes every strongly-typed id in a model a scalar mapped by <see cref="WhizbangIdTypeMapping"/>, on a context that
/// does not carry the Whizbang options extension and so never asks <see cref="WhizbangIdTypeMappingSourcePlugin"/> (#2518).
/// </summary>
/// <remarks>
/// <para>
/// Without the type mapping, Entity Framework discovers an id as a complex type of its own and reads a document's id as
/// the object <c>{"Value": ...}</c> only, while the atomic upsert writes the UUID string once the fleet reads it: such a
/// context could not read a row the framework wrote. The generated <c>OnModelCreating</c> calls this after <c>ConfigureWhizbang()</c>, as it calls
/// <see cref="CanonicalTemporalConvention.Apply"/>, so every generated context maps an id the same way however its
/// options were built (a pooled factory registered by hand, for example). On a context with the extension there is no
/// complex id left to replace, and this does nothing.
/// </para>
/// <para>
/// Each id discovered as a complex property is removed and the same member added back as a property, with the id's
/// converter and JSON reader/writer. A collection of ids is left as Entity Framework discovered it.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdDocumentStorageTests.cs</tests>
public static class WhizbangIdConvention {
  /// <summary>Replaces every complex-typed strongly-typed id in the model with a scalar property.</summary>
  /// <param name="modelBuilder">The model being built.</param>
  public static void Apply(ModelBuilder modelBuilder) {
    ArgumentNullException.ThrowIfNull(modelBuilder);
    foreach (var entityType in modelBuilder.Model.GetEntityTypes()) {
      foreach (var complex in entityType.GetComplexProperties().ToList()) {
        _apply(complex.ComplexType);
      }
    }
  }

  private static void _apply(IMutableComplexType type) {
    foreach (var complex in type.GetComplexProperties().ToList()) {
      var idType = Nullable.GetUnderlyingType(complex.ClrType) ?? complex.ClrType;
      if (complex.IsCollection || WhizbangIdTypeMapping.For(idType) is not { } mapping) {
        _apply(complex.ComplexType);
        continue;
      }

      var member = (MemberInfo?)complex.PropertyInfo ?? complex.FieldInfo;
      if (member is null) {
        continue;
      }
      var jsonName = complex.ComplexType.IsMappedToJson() ? complex.GetJsonPropertyName() : null;
      var nullable = complex.IsNullable;
      type.RemoveComplexProperty(complex);

      var property = type.AddProperty(member.Name, complex.ClrType, member);
      property.SetTypeMapping(mapping);
      property.SetValueConverter(mapping.Converter);
      property.SetJsonValueReaderWriterType(mapping.JsonValueReaderWriter!.GetType());
      property.IsNullable = nullable || complex.ClrType != idType;
      if (jsonName is not null) {
        property.SetJsonPropertyName(jsonName);
      }
    }
  }
}
