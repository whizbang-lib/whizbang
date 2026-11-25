// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;

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
/// converter and JSON reader/writer. Outside a document the property keeps the column the complex id had,
/// <c>&lt;column&gt;_Value</c>, as <see cref="WhizbangIdColumnConvention"/> keeps it on a context with the extension. A
/// collection of ids is left as Entity Framework discovered it.
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

  /// <summary>
  /// Gives a context without the Whizbang options extension the framework's query postprocessor, which the extension
  /// would have registered, so an <c>ExecuteUpdate</c> on it sets a strongly-typed id inside a document in the form the
  /// fleet reads, decided when the statement runs (<see cref="StrongIdSetterFormRewriter"/>), and an id predicate takes
  /// either stored form (#2518). The generated <c>OnConfiguring</c> calls it.
  /// </summary>
  /// <remarks>
  /// Nothing is changed on options that already have a query postprocessor of their own or of the extension, on options
  /// for a provider that is not relational, or on frozen options: a pooled context's options cannot be changed by
  /// <c>OnConfiguring</c>, so a pooled context needs the extension (<c>UseWhizbangFunctions</c>), which the generated
  /// registration always adds.
  /// </remarks>
  /// <param name="optionsBuilder">The builder <c>OnConfiguring</c> is given.</param>
  /// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdDocumentStorageTests.cs:Convention_ExecuteUpdateWhileTheGateIsClosed_SetsAnIdInTheObjectFormAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdDocumentStorageTests.cs:Configure_LeavesOptionsWithAPostprocessorOrFrozen_AsTheyAreAsync</tests>
  public static void Configure(DbContextOptionsBuilder optionsBuilder) {
    ArgumentNullException.ThrowIfNull(optionsBuilder);
    var options = optionsBuilder.Options;
    var replaced = options.FindExtension<CoreOptionsExtension>()?.ReplacedServices;
    if (options.IsFrozen
        || !options.Extensions.OfType<RelationalOptionsExtension>().Any()
        || replaced?.Keys.Any(k => k.Item1 == typeof(IQueryTranslationPostprocessorFactory)) == true) {
      return;
    }
    optionsBuilder.ReplaceService<IQueryTranslationPostprocessorFactory, ContainmentPostprocessorFactory>();
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
      } else if (!type.IsMappedToJson() && WhizbangIdColumnConvention.KeptColumn(property) is { } column) {
        // Outside a document the id keeps the column it had as a complex type (WhizbangIdColumnConvention).
        property.SetColumnName(column);
      }
    }
  }
}
