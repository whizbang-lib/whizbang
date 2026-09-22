using System.Text.Json;
using TUnit.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Covers <see cref="JsonElementHelper.FromStringArray"/> — untouched by the sibling
/// <c>JsonElementHelperTests</c>, which only exercises <c>FromString</c> / <c>FromInt32</c> /
/// <c>FromBoolean</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/JsonElementHelper.cs</code-under-test>
public class JsonElementHelperCoverageTests {

  [Test]
  public async Task FromStringArray_ContainingNullElement_EmitsJsonNullForThatEntryAsync() {
    // A null entry must round-trip as a JSON null in its own array slot, not be skipped
    // (which would shift every later value into the wrong position) or throw.
    var result = JsonElementHelper.FromStringArray(["first", null!, "third"]);

    await Assert.That(result.ValueKind).IsEqualTo(JsonValueKind.Array);
    var items = result.EnumerateArray().ToList();
    await Assert.That(items.Count).IsEqualTo(3);
    await Assert.That(items[0].GetString()).IsEqualTo("first");
    await Assert.That(items[1].ValueKind).IsEqualTo(JsonValueKind.Null)
      .Because("a null string entry must serialize as JSON null, not be dropped or mis-escaped as the literal text 'null'.");
    await Assert.That(items[2].GetString()).IsEqualTo("third");
  }

  [Test]
  public async Task FromStringArray_WithValuesNeedingEscaping_EscapesEachEntryAsync() {
    var result = JsonElementHelper.FromStringArray(["quote\"here", "tab\there"]);

    var items = result.EnumerateArray().ToList();
    await Assert.That(items[0].GetString()).IsEqualTo("quote\"here");
    await Assert.That(items[1].GetString()).IsEqualTo("tab\there");
  }
}
