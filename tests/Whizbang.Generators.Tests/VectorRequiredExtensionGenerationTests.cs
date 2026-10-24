// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The generated code creates pgvector through one runtime helper, which names the feature and the fix when the
/// server refuses, rather than three inline <c>CREATE EXTENSION vector</c> statements that fail with a raw
/// exception. It also registers the context's schema requirements, so the startup verdict judges pgvector as an
/// explicit requirement of every vector column.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</code-under-test>
/// <docs>fundamentals/perspectives/vector-fields#pgvector-extension</docs>
[Category("Unit")]
public class VectorRequiredExtensionGenerationTests {
  private const string ENSURE = "global::Whizbang.Data.Postgres.PostgresRequiredExtension.EnsureAsync(";
  private const string SOURCE = "global::Whizbang.Core.Capabilities.FixedCapabilityRequirementSource(";

  private static string _source(bool vector) => $$"""
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record ProductDto {
      public Guid Id { get; init; }
      public string Name { get; init; } = string.Empty;
      {{(vector ? "[VectorField(1536)]\n      [Indexed]\n      public float[]? Embedding { get; init; }" : "")}}
    }

    public class ProductPerspective(IPerspectiveStore<ProductDto> store)
      : IPerspectiveFor<ProductDto, ProductCreated> {
      public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
    }

    public record ProductCreated : IEvent;

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _generatedAsync(bool vector) {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(_source(vector));
    return string.Concat(result.GeneratedSources.Select(s => s.SourceText.ToString()));
  }

  [Test]
  public async Task VectorFields_CreatePgvectorThroughTheHelperAtEverySite_AndNeverInlineAsync() {
    var code = await _generatedAsync(vector: true);

    await Assert.That(code).DoesNotContain("CREATE EXTENSION vector")
      .Because("an inline CREATE EXTENSION fails with a raw exception that names neither the feature nor the fix");
    await Assert.That(Regex.Count(code, Regex.Escape(ENSURE + "tempConn, \"vector\", "))).IsEqualTo(2)
      .Because("both data sources create pgvector before Build(), so Npgsql's type scan finds it");
    await Assert.That(code).Contains("await " + ENSURE + "connection, \"vector\", ")
      .Because("the whole-script schema path creates it through the same helper");
    await Assert.That(code).Contains("GetCapabilityRequirements()");
    await Assert.That(code).Contains("PostgresSchemaRequirements.From(new string[] { ")
      .Because("the vector columns are passed as a literal array");
    await Assert.That(code).Contains(".embedding\"")
      .Because("the requirement names each vector column, so the verdict says which column needs pgvector");
  }

  [Test]
  public async Task EveryContext_RegistersItsSchemaRequirementsAsASourceAsync() {
    var code = await _generatedAsync(vector: true);

    await Assert.That(code).Contains(
      "services.AddSingleton<global::Whizbang.Core.Capabilities.ICapabilityRequirementSource>(new " + SOURCE)
      .Because("the startup verdict reads the schema's requirements through the requirement-source seam");
  }

  [Test]
  public async Task NoVectorFields_NeverTouchPgvectorAsync() {
    var code = await _generatedAsync(vector: false);

    await Assert.That(code).DoesNotContain(ENSURE);
    await Assert.That(code).DoesNotContain("CREATE EXTENSION vector");
    await Assert.That(code).Contains(SOURCE).Because("the context still declares its schema's other requirements");
    await Assert.That(code).Contains("PostgresSchemaRequirements.From(System.Array.Empty<string>(), GetPerspectiveEntries())")
      .Because("the perspectives' optional-extension blocks are read from the very SQL the schema pass runs (pg_trgm, implicitly)");
  }
}
