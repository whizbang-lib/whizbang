using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

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

  private static MessageHop _hopWithCausation(Guid causation, Guid correlation) => new() {
    Type = HopType.Current,
    ServiceInstance = new ServiceInstanceInfo {
      ServiceName = "TestService",
      InstanceId = Guid.Parse("11111111-2222-4333-8444-555555555555"),
      HostName = "localhost",
      ProcessId = 1234
    },
    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000),
    CausationId = MessageId.From(causation),
    CorrelationId = CorrelationId.FromExternal(correlation),
  };

  /// <summary>
  /// Wire compatibility: hops already persisted or in flight carry causation and correlation ids as the lowercase
  /// hyphenated form. The converter must keep writing exactly that form, so a hop written before and after the
  /// switch to the framework's own UUID handling reads identically.
  /// </summary>
  [Test]
  public async Task Write_CausationAndCorrelation_UseTheLowercaseHyphenatedFormAsync() {
    var options = InfrastructureJsonContext.Default.Options;
    var causation = Guid.Parse("0190A1B2-C3D4-7E5F-8A9B-0C1D2E3F4A5B");
    var correlation = Guid.Parse("3F2504E0-4F89-41D3-9A0C-0305E82C3301");

    var json = JsonSerializer.Serialize(_hopWithCausation(causation, correlation), options);

    await Assert.That(json).Contains("\"ca\":\"0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b\"");
    await Assert.That(json).Contains("\"co\":\"3f2504e0-4f89-41d3-9a0c-0305e82c3301\"");
  }

  /// <summary>
  /// Wire compatibility: the reader accepts every notation the previous UUID parser did (upper case, braces, no
  /// hyphens), so a hop some other producer wrote in any of them still reads.
  /// </summary>
  [Test]
  [Arguments("0190A1B2-C3D4-7E5F-8A9B-0C1D2E3F4A5B")]
  [Arguments("{0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b}")]
  [Arguments("0190a1b2c3d47e5f8a9b0c1d2e3f4a5b")]
  public async Task Read_CausationAndCorrelation_AcceptAnyStandardNotationAsync(string notation) {
    var options = InfrastructureJsonContext.Default.Options;
    var expected = Guid.Parse("0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
    var json = JsonSerializer.Serialize(_hopWithCausation(expected, expected), options)
      .Replace("0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", notation, StringComparison.Ordinal);

    var hop = JsonSerializer.Deserialize<MessageHop>(json, options)!;

    await Assert.That(hop.CausationId!.Value.Value).IsEqualTo(expected);
    await Assert.That(hop.CorrelationId!.Value.Value).IsEqualTo(expected);
  }
}
