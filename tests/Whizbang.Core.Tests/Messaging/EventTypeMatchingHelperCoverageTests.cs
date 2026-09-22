using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage-round tests for EventTypeMatchingHelper, targeting the null/empty guard, the
/// nested-bracket depth tracking, the runaway-nesting depth guard, and the malformed-bracket
/// fail-safe of <see cref="EventTypeMatchingHelper.ExtractInnerPayloadTypeName"/>, plus the
/// unrecognized-trailing-segment branch of <see cref="EventTypeMatchingHelper.NormalizeTypeName"/>,
/// none of which the main suite (EventTypeMatchingHelperTests, DisabledSubsystemDiscardTests)
/// exercises.
/// </summary>
/// <tests>src/Whizbang.Core/Messaging/EventTypeMatchingHelper.cs</tests>
public class EventTypeMatchingHelperCoverageTests {
  private static string _wrapNTimes(string payload, int layers) {
    var current = payload;
    for (var i = 0; i < layers; i++) {
      current = $"Whizbang.Core.Observability.MessageEnvelope`1[[{current}]], Whizbang.Core";
    }
    return current;
  }

  // A discard policy may look up a type name that was never recorded (null) or recorded as
  // empty; if this guard regressed, that call would throw instead of handing the value back
  // unmatched, the same fail-safe contract NormalizeTypeName gives for null/empty input.
  [Test]
  public async Task ExtractInnerPayloadTypeName_WithNullOrEmpty_ReturnsAsIsAsync() {
    // Act & Assert
    await Assert.That(EventTypeMatchingHelper.ExtractInnerPayloadTypeName(null!)).IsNull();
    await Assert.That(EventTypeMatchingHelper.ExtractInnerPayloadTypeName("")).IsEqualTo("");
  }

  // An inner payload that is itself a generic type carries its own bracket pair; if the scan
  // stopped at the first "]]" instead of tracking nesting depth, it would truncate the payload
  // name mid-generic-argument, and every policy keyed on the inner type would miss it.
  [Test]
  public async Task ExtractInnerPayloadTypeName_WithNestedGenericInnerPayload_TracksBracketDepthAsync() {
    // Arrange
    const string wrapped =
      "Whizbang.Core.Observability.MessageEnvelope`1[[System.Collections.Generic.List`1[[System.String, mscorlib]], MyAssembly]], Whizbang.Core";
    const string expectedInner =
      "System.Collections.Generic.List`1[[System.String, mscorlib]], MyAssembly";

    // Act
    var result = EventTypeMatchingHelper.ExtractInnerPayloadTypeName(wrapped);

    // Assert
    await Assert.That(result).IsEqualTo(expectedInner)
      .Because("the scan must track nested bracket depth instead of stopping at the first ']]' it sees");
  }

  // The depth loop is a runaway-nesting guard, not a design point: a storm of double-wrapping
  // must not spin unbounded. Nine layers exceeds the eight-iteration cap by exactly one, so the
  // result must still carry one layer of wrapping instead of being fully unwrapped.
  [Test]
  public async Task ExtractInnerPayloadTypeName_ExceedingDepthGuard_StopsAfterEightLayersAsync() {
    // Arrange
    const string payload = "Whizbang.Core.Foo, Whizbang.Core";
    var nineLayers = _wrapNTimes(payload, 9);
    var oneLayerRemaining = _wrapNTimes(payload, 1);

    // Act
    var result = EventTypeMatchingHelper.ExtractInnerPayloadTypeName(nineLayers);

    // Assert
    await Assert.That(result).IsEqualTo(oneLayerRemaining)
      .Because("the unwrap loop is bounded at 8 layers -- a runaway-nesting storm must stop " +
               "short of the true payload rather than spin indefinitely");
  }

  // A discard policy requires a positive match; if unbalanced brackets were tolerated, the
  // scan could fabricate a payload name out of garbage instead of admitting it cannot find one.
  [Test]
  public async Task ExtractInnerPayloadTypeName_WithUnterminatedBrackets_FailsSafeToOriginalAsync() {
    // Arrange - opens a wrapper but never closes it
    const string malformed =
      "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Foo, Whizbang.Core";

    // Act
    var result = EventTypeMatchingHelper.ExtractInnerPayloadTypeName(malformed);

    // Assert
    await Assert.That(result).IsEqualTo(malformed)
      .Because("a discard policy keyed on a positive match must never fabricate a payload name " +
               "out of unbalanced brackets -- returning the untouched input keeps the message");
  }

  // Only the recognized Version/Culture/PublicKeyToken segments are assembly noise; if the
  // scan kept skipping past an unrecognized trailing segment instead of stopping, it would
  // silently eat part of a legitimate type name that merely followed a Version= segment.
  [Test]
  public async Task NormalizeTypeName_WithUnrecognizedTrailingSegment_StopsSkippingAtItAsync() {
    // Arrange
    const string typeName = "MyApp.Foo, MyAssembly, Version=1.0.0.0, Flavor=Vanilla";

    // Act
    var result = EventTypeMatchingHelper.NormalizeTypeName(typeName);

    // Assert
    await Assert.That(result).IsEqualTo("MyApp.Foo, MyAssembly, Flavor=Vanilla")
      .Because("an unrecognized trailing segment is part of the type name, not assembly " +
               "metadata, and must survive normalization untouched");
  }
}
