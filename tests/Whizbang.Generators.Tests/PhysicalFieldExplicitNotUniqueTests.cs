// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// <c>[PhysicalField(Unique = false)]</c> says the same as leaving <c>Unique</c> out: the field's
/// index is an ordinary one in the table DDL, in the EF Core model and in the schema extensions.
/// Only <c>true</c> asks for uniqueness.
/// </summary>
/// <tests>src/Whizbang.Generators/PerspectiveSchemaGenerator.cs</tests>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/EFCorePerspectiveConfigurationGenerator.cs</tests>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</tests>
[Category("SourceGenerators")]
public class PhysicalFieldExplicitNotUniqueTests {
  private const string MODEL = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    [PerspectiveStorage(FieldStorageMode.Split)]
    public class CatalogModel {
      [StreamId]
      public Guid Id { get; set; }

      [PhysicalField(Unique = false)]
      [Indexed]
      public string Sku { get; set; } = "";
    }

    public record CatalogChanged : IEvent;

    public class CatalogPerspective : IPerspectiveFor<CatalogModel, CatalogChanged> {
      public CatalogModel Apply(CatalogModel currentData, CatalogChanged @event) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task TableDdl_IndexIsNotUniqueAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(MODEL);
    var ddl = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");

    await Assert.That(ddl).IsNotNull();
    await Assert.That(ddl).Contains("_sku");
    await Assert.That(ddl).DoesNotContain("UNIQUE");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task EFCoreModel_IndexIsNotUniqueAsync() {
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorWithEFCoreReferencesAsync(MODEL);
    var model = string.Concat(result.GeneratedSources.Select(s => s.SourceText.ToString()));

    await Assert.That(model).Contains("HasIndex(\"sku\")");
    await Assert.That(model).DoesNotContain(".IsUnique()");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task SchemaExtensions_IndexIsNotUniqueAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODEL);
    var extensions = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();

    var skuIndexes = extensions.Split('\n').Where(l => l.Contains("INDEX", StringComparison.Ordinal) && l.Contains("_sku", StringComparison.Ordinal)).ToList();

    await Assert.That(skuIndexes).IsNotEmpty()
      .Because("the control: the field is indexed");
    await Assert.That(skuIndexes.Where(l => l.Contains("UNIQUE", StringComparison.Ordinal))).IsEmpty();
  }
}
