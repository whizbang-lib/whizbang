// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Data.EFCore.Postgres.ConventionIds.Fixtures;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Ids.Fixtures;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A module initializer runs when its assembly is first used, not when it is referenced. A model built before any code
/// of the id's assembly has run still maps the id as a scalar (#2518): otherwise Entity Framework caches the object form
/// <c>{"Value": ...}</c> for the life of the process while the atomic upsert writes the UUID string.
/// </summary>
/// <remarks>
/// One test per way a model is built, each over an id from an assembly of its own, so each starts with its id's
/// initializer not yet run whichever runs first.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/WhizbangIdForms.cs</code-under-test>
[Category("Unit")]
[Category("WhizbangId")]
[Category("Shard1")]
public class WhizbangIdModelTimingTests {
  public sealed class ExtensionDocument {
    public UntouchedOrderId OrderId { get; set; }
  }

  public sealed class ExtensionRow {
    public Guid Id { get; set; }
    public ExtensionDocument Data { get; set; } = new();
  }

  public sealed class ConventionDocument {
    public UntouchedConventionOrderId OrderId { get; set; }
  }

  public sealed class ConventionRow {
    public Guid Id { get; set; }
    public ConventionDocument Data { get; set; } = new();
  }

  /// <summary>A context carrying the Whizbang options extension, as every perspective context does.</summary>
  private sealed class ExtensionContext(DbContextOptions options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<ExtensionRow>(entity => {
        entity.ToTable("wh_per_timing");
        entity.HasKey(e => e.Id);
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
      });
  }

  /// <summary>A context without the extension that calls <see cref="WhizbangIdConvention.Apply"/>.</summary>
  private sealed class ConventionContext(DbContextOptions options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<ConventionRow>(entity => {
        entity.ToTable("wh_per_timing");
        entity.HasKey(e => e.Id);
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
      });
      WhizbangIdConvention.Apply(modelBuilder);
    }
  }

  [Test]
  public async Task Id_FromAnAssemblyNothingHasUsed_IsAScalarInTheFirstModelBuiltWithTheExtensionAsync() {
    // Nothing has run the id's module initializer yet. The check goes by name, as WhizbangIdSchemaTimingTests does:
    // the same initializer records the id's Guid conversion and its stored form.
    await Assert.That(_registered(nameof(UntouchedOrderId))).IsFalse();

    await using var context = new ExtensionContext(_options(withExtension: true));
    var orderId = _orderId(context.Model, typeof(ExtensionRow), nameof(ExtensionRow.Data), nameof(ExtensionDocument.OrderId));

    await Assert.That(orderId?.GetTypeMapping()).IsTypeOf<WhizbangIdTypeMapping>();
  }

  [Test]
  public async Task Id_FromAnAssemblyNothingHasUsed_IsAScalarInTheFirstModelBuiltByTheConventionAsync() {
    await Assert.That(_registered(nameof(UntouchedConventionOrderId))).IsFalse();

    await using var context = new ConventionContext(_options(withExtension: false));
    var orderId = _orderId(context.Model, typeof(ConventionRow), nameof(ConventionRow.Data), nameof(ConventionDocument.OrderId));

    await Assert.That(orderId?.GetTypeMapping()).IsTypeOf<WhizbangIdTypeMapping>();
  }

  private static bool _registered(string idName) =>
    WhizbangIdProviderRegistry.GetGuidConversions().Keys.Any(type => type.Name == idName);

  private static IProperty? _orderId(IModel model, Type rowType, string document, string member) =>
    model.FindEntityType(rowType)!.FindComplexProperty(document)!.ComplexType.FindProperty(member);

  private static DbContextOptions _options(bool withExtension) =>
    new DbContextOptionsBuilder()
      .UseNpgsql("Host=localhost;Database=model_only", npgsql => {
        if (withExtension) {
          npgsql.UseWhizbangFunctions();
        }
      })
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;
}
