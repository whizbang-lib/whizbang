// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A Split model registers its promoted columns and the copy that puts them into a projected document, so a query
/// ending in <c>Select(r =&gt; r.Data)</c> returns them filled (#2304). A model whose document still holds its
/// promoted fields registers nothing.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</code-under-test>
/// <docs>fundamentals/perspectives/physical-fields#reading-promoted-fields-back</docs>
[Category("Unit")]
public class SplitDocumentProjectionGenerationTests {
  private const string REGISTER = "Whizbang.Data.EFCore.Postgres.QueryTranslation.SplitDocumentProjectionRegistry.Register<";

  private static string _source(string storage, string model) => $$"""
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    {{storage}}
    {{model}}

    public class OrderPerspective(IPerspectiveStore<OrderDto> store)
      : IPerspectiveFor<OrderDto, OrderCreated> {
      public OrderDto Apply(OrderDto currentData, OrderCreated @event) => currentData;
    }

    public record OrderCreated : IEvent;

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  private const string SPLIT = "[PerspectiveStorage(FieldStorageMode.Split)]";
  private const string EXTRACTED = "[PerspectiveStorage(FieldStorageMode.Extracted)]";

  private const string CLASS_MODEL = """
    public class OrderDto {
      [StreamId] public Guid OrderId { get; set; }
      [PhysicalField] [Indexed] public string Status { get; set; } = string.Empty;
      [PhysicalField] public int? Rank { get; set; }
      public string Name { get; set; } = string.Empty;
    }
    """;

  private const string RECORD_MODEL = """
    public record OrderDto {
      [StreamId] public Guid OrderId { get; init; }
      [PhysicalField] [Indexed] public string Status { get; init; } = string.Empty;
      public string Name { get; init; } = string.Empty;
    }
    """;

  private static async Task<string> _generatedAsync(string storage, string model) {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(_source(storage, model));
    return string.Concat(result.GeneratedSources.Select(s => s.SourceText.ToString()));
  }

  [Test]
  public async Task SplitClass_RegistersItsColumnsAndAssignsThemInPlaceAsync() {
    var code = await _generatedAsync(SPLIT, CLASS_MODEL);

    await Assert.That(code).Contains(REGISTER + "global::TestApp.OrderDto>(");
    await Assert.That(code).Contains("new string[] { \"status\", \"rank\" }");
    await Assert.That(code).Contains("data.Status = _status;")
      .Because("a class with setters is assigned in place");
    await Assert.That(code).Contains("data.Rank = _rank ?? data.Rank;")
      .Because("a null column of a nullable type keeps the value the document holds");
  }

  [Test]
  public async Task SplitRecord_CopiesTheColumnsWithAWithExpressionAsync() {
    var code = await _generatedAsync(SPLIT, RECORD_MODEL);

    await Assert.That(code).Contains(REGISTER + "global::TestApp.OrderDto>(");
    await Assert.That(code).Contains("data = data with { Status = _status };")
      .Because("a record's init-only properties are set with a with expression");
  }

  [Test]
  public async Task ExtractedModel_RegistersNothingAsync() {
    var code = await _generatedAsync(EXTRACTED, CLASS_MODEL);

    await Assert.That(code).DoesNotContain(REGISTER)
      .Because("an Extracted document still holds its promoted fields");
  }

  [Test]
  public async Task ModelWithoutPromotedFields_RegistersNothingAsync() {
    var code = await _generatedAsync("", """
      public class OrderDto {
        [StreamId] public Guid OrderId { get; set; }
        public string Name { get; set; } = string.Empty;
      }
      """);

    await Assert.That(code).DoesNotContain(REGISTER);
  }
}
