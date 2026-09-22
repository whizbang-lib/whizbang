using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Coverage for two <see cref="MessageTypeRegistryJson"/> branches
/// <see cref="MessageTypeRegistryJsonTests"/> doesn't reach: <see cref="MessageTypeRegistryJson.JsonArray"/>
/// with more than one element (the primary suite only exercises the null/empty case, so the
/// comma-joining branch never runs), and <see cref="MessageTypeRegistryJson.JsonString"/> called
/// directly with <c>null</c> (every existing caller passes real strings; the null literal only
/// happens today via <c>Serialize</c>'s own inline "null" branch, which bypasses this method
/// entirely). Both feed the JSONB payload the <c>reconcile_message_type_registry</c> PL/pgSQL
/// function parses — a malformed array or a missing null literal there is a payload the reconciler
/// can't read, not just a cosmetic string difference.
/// </summary>
public class MessageTypeRegistryJsonCoverageTests {

  [Test]
  public async Task JsonArray_MultipleValues_JoinsWithCommasAsync() {
    var json = MessageTypeRegistryJson.JsonArray(["OldName", "OlderName"]);

    await Assert.That(json).IsEqualTo("[\"OldName\",\"OlderName\"]")
      .Because("the reconcile function parses this as a JSON array — a missing comma between former names would make the payload unparseable");
  }

  [Test]
  public async Task JsonString_Null_ReturnsTheJsonNullLiteralAsync() {
    var json = MessageTypeRegistryJson.JsonString(null);

    await Assert.That(json).IsEqualTo("null")
      .Because("the reconcile function's JSONB parser needs the literal null token, not a quoted empty string, for an absent PinnedId");
  }
}
