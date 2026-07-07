using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for <see cref="MessageTypeRegistryJson"/> — the shared, AOT-safe serializer that builds the JSONB
/// payload for the reconcile_message_type_registry function. Extracted so the Dapper + EFCore populators and the
/// generated turnkey path share one definition.
/// </summary>
public class MessageTypeRegistryJsonTests {
  [Test]
  public async Task Serialize_PinnedEntryWithFormerNames_EmitsExpectedShapeAsync() {
    var entries = new[] {
      new MessageTypeCatalogEntry(typeof(string), "MyApp.OrderContracts+Placed", "event", "11111111-1111-1111-1111-111111111111")
        { FormerNames = ["MyApp.OrderContracts+Created"] },
    };

    var json = MessageTypeRegistryJson.Serialize(entries);

    // Round-trips as valid JSON with the exact field names the SQL function reads.
    using var doc = JsonDocument.Parse(json);
    var e = doc.RootElement[0];
    await Assert.That(e.GetProperty("ClrTypeName").GetString()).IsEqualTo("MyApp.OrderContracts+Placed");
    await Assert.That(e.GetProperty("PinnedId").GetString()).IsEqualTo("11111111-1111-1111-1111-111111111111");
    await Assert.That(e.GetProperty("Kind").GetString()).IsEqualTo("event");
    await Assert.That(e.GetProperty("FormerNames")[0].GetString()).IsEqualTo("MyApp.OrderContracts+Created");
  }

  [Test]
  public async Task Serialize_UnpinnedEntry_EmitsNullPinnedIdAndEmptyFormerNamesAsync() {
    var entries = new[] {
      new MessageTypeCatalogEntry(typeof(string), "MyApp.Unpinned", "command", null),
    };

    var json = MessageTypeRegistryJson.Serialize(entries);

    using var doc = JsonDocument.Parse(json);
    var e = doc.RootElement[0];
    await Assert.That(e.GetProperty("PinnedId").ValueKind).IsEqualTo(JsonValueKind.Null);
    await Assert.That(e.GetProperty("FormerNames").GetArrayLength()).IsEqualTo(0);
  }

  [Test]
  public async Task Serialize_EmptyCatalog_EmitsEmptyArrayAsync() {
    var json = MessageTypeRegistryJson.Serialize([]);
    await Assert.That(json).IsEqualTo("[]");
  }

  [Test]
  public async Task JsonString_EscapesQuotesBackslashesAndControlCharsAsync() {
    var json = MessageTypeRegistryJson.JsonString("a\"b\\c\td");
    // Valid JSON string literal that decodes back to the original.
    var decoded = JsonSerializer.Deserialize<string>(json);
    await Assert.That(decoded).IsEqualTo("a\"b\\c\td");
  }

  [Test]
  public async Task JsonArray_NullOrEmpty_EmitsEmptyArrayAsync() {
    await Assert.That(MessageTypeRegistryJson.JsonArray([])).IsEqualTo("[]");
    await Assert.That(MessageTypeRegistryJson.JsonArray(null!)).IsEqualTo("[]");
  }
}
