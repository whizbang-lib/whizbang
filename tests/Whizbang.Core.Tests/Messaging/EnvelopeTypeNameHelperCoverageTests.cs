using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Covers two branches the sibling <c>EnvelopeTypeNameHelperTests</c> leaves untouched:
/// <see cref="EnvelopeTypeNameHelper.ExtractInnerTypeName"/>'s malformed-input fallback (an opening
/// <c>[[</c> with no matching <c>]]</c>), and <see cref="EnvelopeTypeNameHelper.IsBodyClaimEnvelope"/>'s
/// null/empty short-circuit, which no existing test calls at all.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/EnvelopeTypeNameHelper.cs</code-under-test>
public class EnvelopeTypeNameHelperCoverageTests {

  [Test]
  public async Task ExtractInnerTypeName_UnmatchedOpeningBracket_ReturnsNullAsync() {
    // A wire type name with "[[" but no matching "]]" is malformed — the depth-tracking loop must
    // exhaust the string and report "not extractable" rather than throwing an index-out-of-range
    // or returning a truncated/garbage substring.
    var inner = EnvelopeTypeNameHelper.ExtractInnerTypeName("Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Events.Foo, MyApp");

    await Assert.That(inner).IsNull();
  }

  [Test]
  public async Task IsBodyClaimEnvelope_NullTypeName_ReturnsFalseAsync() {
    await Assert.That(EnvelopeTypeNameHelper.IsBodyClaimEnvelope(null)).IsFalse();
  }

  [Test]
  public async Task IsBodyClaimEnvelope_EmptyTypeName_ReturnsFalseAsync() {
    await Assert.That(EnvelopeTypeNameHelper.IsBodyClaimEnvelope("")).IsFalse();
  }
}
