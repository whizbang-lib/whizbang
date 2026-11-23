// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Ids.Fixtures;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A module initializer runs when its assembly is first used, not when it is referenced. A model built before any code
/// of the id's assembly has run still maps the id as a scalar (#2518): otherwise Entity Framework caches the object form
/// <c>{"Value": ...}</c> for the life of the process while the atomic upsert writes the UUID string.
/// </summary>
/// <code-under-test>src/Whizbang.Core/WhizbangIdForms.cs</code-under-test>
[Category("Unit")]
[Category("WhizbangId")]
[Category("Shard1")]
public class WhizbangIdModelTimingTests {
  private const string UNTOUCHED_ID_NAME = nameof(UntouchedOrderId);

  public sealed class TimingDocument {
    public UntouchedOrderId OrderId { get; set; }
  }

  public sealed class TimingRow {
    public Guid Id { get; set; }
    public TimingDocument Data { get; set; } = new();
  }

  private abstract class TimingContext(DbContextOptions options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) => _configure(modelBuilder);

    protected static void _configure(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<TimingRow>(entity => {
        entity.ToTable("wh_per_timing");
        entity.HasKey(e => e.Id);
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
      });
  }

  /// <summary>A context carrying the Whizbang options extension, as every perspective context does.</summary>
  private sealed class ExtensionContext(DbContextOptions options) : TimingContext(options);

  /// <summary>A context without the extension that calls <see cref="WhizbangIdConvention.Apply"/>.</summary>
  private sealed class ConventionContext(DbContextOptions options) : TimingContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      _configure(modelBuilder);
      WhizbangIdConvention.Apply(modelBuilder);
    }
  }

  [Test]
  public async Task Id_FromAnAssemblyNothingHasUsed_IsAScalarInTheFirstModelBuiltAsync() {
    // Nothing has run the id's module initializer yet. The check goes by name, as WhizbangIdSchemaTimingTests does:
    // the same initializer records the id's Guid conversion and its stored form.
    await Assert.That(WhizbangIdProviderRegistry.GetGuidConversions().Keys.Any(type => type.Name == UNTOUCHED_ID_NAME))
      .IsFalse();

    await using var extension = new ExtensionContext(_options(withExtension: true));
    await Assert.That(_orderId(extension.Model)?.GetTypeMapping()).IsTypeOf<WhizbangIdTypeMapping>();

    await using var convention = new ConventionContext(_options(withExtension: false));
    await Assert.That(_orderId(convention.Model)?.GetTypeMapping()).IsTypeOf<WhizbangIdTypeMapping>();
  }

  private static IProperty? _orderId(IModel model) =>
    model.FindEntityType(typeof(TimingRow))!.FindComplexProperty(nameof(TimingRow.Data))!.ComplexType
      .FindProperty(nameof(TimingDocument.OrderId));

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
