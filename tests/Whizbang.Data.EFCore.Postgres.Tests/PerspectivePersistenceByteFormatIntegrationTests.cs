using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// End-to-end byte-format equivalence test for the Path 1 atomic-upsert serialization context.
/// Writes a perspective row through EF Core's standard <c>SaveChanges</c> path, then reads
/// the raw JSONB column back via SQL and compares it semantically (parsed-JSON equality)
/// against what <see cref="PerspectivePersistenceJsonContext"/> would emit for the same TModel.
/// </summary>
/// <remarks>
/// <para>
/// This is the gating regression for slice 22b.3. If EF Core 10's
/// <c>ComplexProperty().ToJson()</c> writer and Path 1 produce semantically-different JSON
/// for the same TModel (different property structure for <c>[WhizbangId]</c> values, for
/// example), then the atomic INSERT…ON CONFLICT DO UPDATE path would write bytes EF's
/// reader can't deserialize — exactly the wall captured in the
/// <c>feedback_ef_complexproperty_tojson_is_walled_garden</c> memory.
/// </para>
/// <para>
/// Semantic equivalence (parsed JSON equality), not byte-identical, is the correct bar:
/// PostgreSQL JSONB parses and re-emits keys in canonical order on write, so the on-disk
/// byte layout is governed by Postgres, not by our serializer. What matters is whether
/// EF's reader can deserialize Path 1's output.
/// </para>
/// </remarks>
public class PerspectivePersistenceByteFormatIntegrationTests : EFCoreTestBase {
  [Test]
  public async Task PathOneWrite_ProducesJsonSemanticallyEquivalentToEFWriteAsync() {
    // Arrange — write an Order through the standard EF strategy.
    await using var context = CreateDbContext();
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();

    var order = new Order {
      OrderId = new TestOrderId(testId),
      Amount = 100.00m,
      Status = "Created"
    };

    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();

    await strategy.UpsertPerspectiveRowAsync(
        context,
        "wh_per_order",
        testId,
        order,
        metadata,
        scope);

    // Act — read EF's JSONB output from the data column, then serialize the SAME order
    // via Path 1's options chain (PerspectivePersistenceJsonContext + MessageJsonContext).
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var efJson = await conn.QuerySingleAsync<string>(
        "SELECT data::text FROM wh_per_order WHERE id = @id",
        new { id = testId });

    var pathOneOptions = PerspectivePersistenceJsonContext.CreateOptions(MessageJsonContext.Default);
    var orderTypeInfo = pathOneOptions.GetTypeInfo(typeof(Order));
    var pathOneJson = JsonSerializer.Serialize(order, orderTypeInfo);

    // Assert — semantic equivalence. Property order may differ between EF and STJ;
    // what matters is the parsed structure.
    var efDoc = JsonDocument.Parse(efJson);
    var pathOneDoc = JsonDocument.Parse(pathOneJson);
    var equivalent = _semanticallyEquivalent(efDoc.RootElement, pathOneDoc.RootElement);

    // Both payloads surfaced via a fail-friendly assertion so the diff is visible on RED.
    await Assert.That(equivalent)
      .IsTrue()
      .Because($"Path 1 JSON must be semantically equivalent to EF's write.\nEF wrote:     {efJson}\nPath 1 wrote: {pathOneJson}");
  }

  /// <summary>
  /// Recursively compares two <see cref="JsonElement"/> instances for semantic equality —
  /// same shape, same property names, same scalar values. Property order is ignored.
  /// Number comparison goes through <c>GetRawText</c> so <c>100.00</c> and <c>100</c>
  /// are NOT considered equal (preserving the integer-vs-decimal distinction that EF
  /// emits for <c>decimal</c> columns).
  /// </summary>
  private static bool _semanticallyEquivalent(JsonElement a, JsonElement b) {
    if (a.ValueKind != b.ValueKind) {
      return false;
    }
    switch (a.ValueKind) {
      case JsonValueKind.Object:
        var aProps = a.EnumerateObject().ToDictionary(static p => p.Name, static p => p.Value);
        var bProps = b.EnumerateObject().ToDictionary(static p => p.Name, static p => p.Value);
        if (aProps.Count != bProps.Count) {
          return false;
        }
        foreach (var kvp in aProps) {
          if (!bProps.TryGetValue(kvp.Key, out var bValue)) {
            return false;
          }
          if (!_semanticallyEquivalent(kvp.Value, bValue)) {
            return false;
          }
        }
        return true;
      case JsonValueKind.Array:
        if (a.GetArrayLength() != b.GetArrayLength()) {
          return false;
        }
        var ai = a.EnumerateArray();
        var bi = b.EnumerateArray();
        while (ai.MoveNext() && bi.MoveNext()) {
          if (!_semanticallyEquivalent(ai.Current, bi.Current)) {
            return false;
          }
        }
        return true;
      case JsonValueKind.String:
        return a.GetString() == b.GetString();
      case JsonValueKind.Number:
        // Preserve numeric formatting differences (decimal vs int) — EF emits 100.00 for
        // decimal-typed columns, Path 1 must too.
        return a.GetRawText() == b.GetRawText();
      case JsonValueKind.True:
      case JsonValueKind.False:
      case JsonValueKind.Null:
        return true;
      default:
        return false;
    }
  }

}
