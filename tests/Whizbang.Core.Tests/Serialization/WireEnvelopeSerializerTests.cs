using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// Tests for the wire serialize helper that returns a rich <see cref="SerializationResult"/>
/// (data + size + content type), so callers can measure the serialized size to choose the message
/// body path (inline vs offload) without re-serializing.
/// </summary>
public class WireEnvelopeSerializerTests {
  [Test]
  public async Task Serialize_ReturnsBytesSizeAndContentTypeAsync() {
    var ti = _typeInfo();
    var model = new Wire { Name = "hello" };

    var result = WireEnvelopeSerializer.Serialize(model, ti, SerializationOptions.Default);

    var expected = JsonSerializer.SerializeToUtf8Bytes(model, ti);
    await Assert.That(result.Data.ToArray()).IsEquivalentTo(expected);
    await Assert.That(result.SizeBytes).IsEqualTo(expected.Length);
    await Assert.That(result.ContentType).IsEqualTo("application/json");
  }

  [Test]
  public async Task SizeBytes_TracksDataLengthAsync() {
    var result = new SerializationResult { Data = Encoding.UTF8.GetBytes("abcd") };

    await Assert.That(result.SizeBytes).IsEqualTo(4);
  }

  [Test]
  public async Task Serialize_NullArguments_ThrowAsync() {
    var ti = _typeInfo();
    await Assert.That(() => WireEnvelopeSerializer.Serialize(null!, ti, SerializationOptions.Default)).Throws<ArgumentNullException>();
    await Assert.That(() => WireEnvelopeSerializer.Serialize(new Wire(), null!, SerializationOptions.Default)).Throws<ArgumentNullException>();
    await Assert.That(() => WireEnvelopeSerializer.Serialize(new Wire(), ti, null!)).Throws<ArgumentNullException>();
  }

  private static JsonTypeInfo<Wire> _typeInfo() =>
    (JsonTypeInfo<Wire>)new JsonSerializerOptions { TypeInfoResolver = WireContext.Default }.GetTypeInfo(typeof(Wire));

  public sealed class Wire { public string Name { get; set; } = string.Empty; }
}

[JsonSerializable(typeof(WireEnvelopeSerializerTests.Wire))]
internal sealed partial class WireContext : JsonSerializerContext;
