using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The Dapper perspective schema fills a physical column from the document for rows written before the
/// column existed, with the same per-type extraction the EF Core schema uses, so both drivers agree.
/// </summary>
public class PerspectiveSchemaBackfillTests {
  private static string _schema(string storageMode) {
    var source = $$"""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace MyApp.Perspectives;

      [PerspectiveStorage(FieldStorageMode.{{storageMode}})]
      public record OrderModel {
        public Guid Id { get; set; }
        [PhysicalField] public string Sku { get; set; } = string.Empty;
        [PhysicalField] public decimal Amount { get; set; }
        [PhysicalField] public DateTimeOffset PlacedAt { get; set; }
        [VectorField(3)] public float[]? Embedding { get; set; }
      }

      public class OrderPerspective : IPerspectiveFor<OrderModel, OrderPlaced> {
        public OrderModel Apply(OrderModel currentData, OrderPlaced @event) => currentData;
      }

      public record OrderPlaced : IEvent;
      """;
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);
    return GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs")!;
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Extracted_EachPhysicalColumn_IsBackfilledFromTheDocumentAsync() {
    var sql = _schema("Extracted");

    await Assert.That(sql).Contains("SET sku = (data ->> 'Sku') WHERE sku IS NULL AND jsonb_typeof(data -> 'Sku') <> 'null';");
    await Assert.That(sql).Contains("SET amount = (data ->> 'Amount')::numeric WHERE amount IS NULL");
    await Assert.That(sql).Contains("SET placed_at = (TIMESTAMPTZ 'epoch' + (data ->> 'PlacedAt')::bigint * INTERVAL '1 microsecond')")
      .Because("an instant is stored in the document as microseconds since the epoch, and rebuilt exactly");
    await Assert.That(sql).DoesNotContain("SET embedding")
      .Because("a vector's column encoding is not something the document can reproduce");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Extracted_TheBackfillFollowsTheTableItFillsAsync() {
    var sql = _schema("Extracted");

    await Assert.That(sql.IndexOf("SET sku =", StringComparison.Ordinal))
      .IsGreaterThan(sql.IndexOf("CREATE TABLE", StringComparison.Ordinal))
      .Because("the backfill is post-table DDL, so a column-copy migration runs it against the swapped-in table");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Split_NothingIsBackfilledAsync() {
    var sql = _schema("Split");

    await Assert.That(sql).DoesNotContain("UPDATE ")
      .Because("in split mode the column is the only copy; the document has nothing to fill it from");
  }
}
