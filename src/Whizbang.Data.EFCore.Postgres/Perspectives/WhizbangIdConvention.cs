// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
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
/// converter and JSON reader/writer. Outside a document its column is named after the property, as on a context with
/// the extension; <c>migrations add</c> scaffolds the move from the complex id's <c>&lt;column&gt;_Value</c> as a rename.
/// A collection of ids is left as Entity Framework discovered it.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdDocumentStorageTests.cs</tests>
public static class WhizbangIdConvention {
  /// <summary>
  /// Replaces every complex-typed strongly-typed id in the model with a scalar property, on a context whose options carry
  /// the framework's query postprocessor: the extension's (<c>UseWhizbangFunctions</c>), or <see cref="Configure"/>'s.
  /// </summary>
  /// <remarks>
  /// Without the postprocessor an <c>ExecuteUpdate</c> would set an id inside a document as its UUID string while the
  /// schema's gate is closed, which an earlier release cannot read, and an update to no id would fail. A context whose
  /// options cannot carry it (a pooled context's options are frozen before <c>OnConfiguring</c> runs, or a hand-written
  /// context that never calls <see cref="Configure"/>) therefore fails here, when its model is built, naming the fix,
  /// rather than write such a document later.
  /// </remarks>
  /// <param name="modelBuilder">The model being built.</param>
  /// <param name="context">The context whose model it is.</param>
  /// <exception cref="InvalidOperationException">The model maps a strongly-typed id and the context's options carry no
  /// Whizbang query postprocessor.</exception>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdDocumentStorageTests.cs:Convention_OnOptionsWithoutThePostprocessor_FailsWhenTheModelIsBuiltAsync</tests>
  public static void Apply(ModelBuilder modelBuilder, DbContext context) {
    ArgumentNullException.ThrowIfNull(modelBuilder);
    ArgumentNullException.ThrowIfNull(context);
    var replaced = 0;
    foreach (var entityType in modelBuilder.Model.GetEntityTypes()) {
      foreach (var complex in entityType.GetComplexProperties().ToList()) {
        replaced += _apply(complex.ComplexType);
      }
    }
    if (replaced > 0 && !CarriesThePostprocessor(context.GetService<IDbContextOptions>())) {
      throw new InvalidOperationException(
        $"{Whizbang.Core.TypeNameFormatter.SimpleName(context.GetType())} maps strongly-typed ids, but its options do not carry Whizbang's query postprocessor, "
        + "so an ExecuteUpdate could write an id in a document in a form the fleet cannot read yet (#2518). Configure it "
        + "with npgsql.UseWhizbangFunctions(), or call WhizbangIdConvention.Configure(optionsBuilder) at the end of its "
        + "OnConfiguring. A pooled context's options cannot be changed in OnConfiguring: it needs UseWhizbangFunctions().");
    }
  }

  /// <summary>Whether options carry the framework's query postprocessor, or one derived from it.</summary>
  /// <param name="options">The options.</param>
  /// <returns>True when they do.</returns>
  internal static bool CarriesThePostprocessor(IDbContextOptions options) =>
    options.FindExtension<CoreOptionsExtension>()?.ReplacedServices?.Any(r =>
      r.Key.Item1 == typeof(IQueryTranslationPostprocessorFactory)
      && typeof(ContainmentPostprocessorFactory).IsAssignableFrom(r.Value)) == true;

  /// <summary>
  /// Gives a context without the Whizbang options extension what the extension would have registered for its strongly-typed
  /// ids (#2518): the framework's query postprocessor, so an <c>ExecuteUpdate</c> sets an id inside a document in the form
  /// the fleet reads, decided when the statement runs (<see cref="StrongIdSetterFormRewriter"/>), and an id predicate takes
  /// either stored form; the migrations model differ that pairs an id's old column by name
  /// (<see cref="WhizbangIdMigrationsModelDiffer"/>); and the interceptor that learns the name the server gives the
  /// database (<see cref="ServerDatabaseNameInterceptor"/>). The generated <c>OnConfiguring</c> calls it last, after the
  /// base context's and <c>OnConfiguringExtended</c>, so it sees the provider wherever it was set.
  /// </summary>
  /// <remarks>
  /// A service the options already replace (the extension's, or the consumer's own) is left as it is, and nothing is
  /// changed on options for a provider that is not relational, or on frozen options: a pooled context's options cannot
  /// be changed by <c>OnConfiguring</c>, so a pooled context needs the extension (<c>UseWhizbangFunctions</c>), which the
  /// generated registration always adds. A context left without the postprocessor fails when its model is built
  /// (<see cref="Apply"/>).
  /// </remarks>
  /// <param name="optionsBuilder">The builder <c>OnConfiguring</c> is given.</param>
  /// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdDocumentStorageTests.cs:Convention_ExecuteUpdateWhileTheGateIsClosed_SetsAnIdInTheObjectFormAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdDocumentStorageTests.cs:Convention_AProviderSetBeforeConfigureInOnConfiguring_GetsThePostprocessorAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdDocumentStorageTests.cs:Configure_LeavesOptionsWithAPostprocessorOrFrozen_AsTheyAreAsync</tests>
  public static void Configure(DbContextOptionsBuilder optionsBuilder) {
    ArgumentNullException.ThrowIfNull(optionsBuilder);
    var options = optionsBuilder.Options;
    if (options.IsFrozen || !options.Extensions.OfType<RelationalOptionsExtension>().Any()) {
      return;
    }
    var replaced = options.FindExtension<CoreOptionsExtension>()?.ReplacedServices;
    if (replaced?.Keys.Any(k => k.Item1 == typeof(IQueryTranslationPostprocessorFactory)) != true) {
      optionsBuilder.ReplaceService<IQueryTranslationPostprocessorFactory, ContainmentPostprocessorFactory>();
    }
    ReplaceMigrationsModelDiffer(optionsBuilder);
    if (options.FindExtension<CoreOptionsExtension>()?.Interceptors?.Contains(ServerDatabaseNameInterceptor.Instance) != true) {
      optionsBuilder.AddInterceptors(ServerDatabaseNameInterceptor.Instance);
    }
  }

  /// <summary>
  /// Gives the options Whizbang's migrations model differ, which pairs a strongly-typed id's old <c>&lt;column&gt;_Value</c>
  /// with <c>&lt;column&gt;</c> by name (<see cref="WhizbangIdMigrationsModelDiffer"/>), unless they already replace the
  /// differ with one of their own.
  /// </summary>
  /// <param name="optionsBuilder">The options builder.</param>
  internal static void ReplaceMigrationsModelDiffer(DbContextOptionsBuilder optionsBuilder) {
    var replaced = optionsBuilder.Options.FindExtension<CoreOptionsExtension>()?.ReplacedServices;
    if (replaced?.Keys.Any(k => k.Item1 == typeof(IMigrationsModelDiffer)) != true) {
      optionsBuilder.ReplaceService<IMigrationsModelDiffer, WhizbangIdMigrationsModelDiffer>();
    }
  }

  private static int _apply(IMutableComplexType type) {
    var replaced = 0;
    foreach (var complex in type.GetComplexProperties().ToList()) {
      var idType = Nullable.GetUnderlyingType(complex.ClrType) ?? complex.ClrType;
      if (complex.IsCollection || WhizbangIdTypeMapping.For(idType) is not { } mapping) {
        replaced += _apply(complex.ComplexType);
        continue;
      }

      var member = (MemberInfo?)complex.PropertyInfo ?? complex.FieldInfo;
      if (member is null) {
        continue;
      }
      replaced++;
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
    return replaced;
  }
}
