using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Coverage round 23 tail: <see cref="MessageHopConverter.Read"/>'s two malformed-input guards.
/// Every existing deserialization test (short/long/mixed property names) hands the converter a
/// well-formed JSON object carrying a ServiceInstance, so neither the "not an object" guard nor
/// the "missing ServiceInstance" guard has ever run.
/// </summary>
[Category("Observability")]
public class MessageHopCoverageTests {

  /// <summary>
  /// Operator impact: a corrupted or truncated hop in a persisted envelope (e.g. a JSON array
  /// where an object was expected) must fail loudly with a hop-specific message at the point of
  /// deserialization, not surface as an opaque "expected object, got array" framework error three
  /// stack frames removed from which field was bad.
  /// </summary>
  [Test]
  public async Task Read_RootIsNotAnObject_ThrowsJsonExceptionAsync() {
    var options = InfrastructureJsonContext.Default.Options;

    var thrown = await Assert.That(() => JsonSerializer.Deserialize<MessageHop>("[]", options))
      .Throws<JsonException>();

    await Assert.That(thrown!.Message).Contains("Expected start of object for MessageHop");
  }

  /// <summary>
  /// Operator impact: ServiceInstance is the one truly required field on a hop (everything else
  /// degrades gracefully). A hop record missing it must fail fast with a message naming exactly
  /// what is missing, rather than deserializing into a hop with a garbage/default service
  /// identity that then poisons trace/causation attribution downstream.
  /// </summary>
  [Test]
  public async Task Read_MissingServiceInstance_ThrowsJsonExceptionAsync() {
    var options = InfrastructureJsonContext.Default.Options;
    const string json = """{"to":"TestTopic"}""";

    var thrown = await Assert.That(() => JsonSerializer.Deserialize<MessageHop>(json, options))
      .Throws<JsonException>();

    await Assert.That(thrown!.Message).Contains("Missing required property: ServiceInstance (or si)");
  }
}
