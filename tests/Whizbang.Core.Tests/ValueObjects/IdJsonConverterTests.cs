using System.Text.Json;
using System.Text.Json.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Covers the source-generated per-Id JsonConverter implementations.
/// Each Id type ships a JsonConverter the WhizbangIdGenerator emits; the
/// converters wrap System.Text.Json Read/Write for AOT compatibility but
/// without explicit fixtures they sat at 0% — only the integration suite
/// hit them indirectly.
/// </summary>
public class IdJsonConverterTests {

  private static JsonSerializerOptions _opts<TConverter>() where TConverter : JsonConverter, new() {
    return new JsonSerializerOptions {
      Converters = { new TConverter() },
    };
  }

  [Test]
  public async Task MessageIdJsonConverter_RoundTrip_PreservesIdAsync() {
    var id = MessageId.New();
    var opts = _opts<MessageIdJsonConverter>();
    var json = JsonSerializer.Serialize(id, opts);
    var roundTripped = JsonSerializer.Deserialize<MessageId>(json, opts);
    await Assert.That(roundTripped).IsEqualTo(id);
  }

  [Test]
  public async Task MessageIdJsonConverter_Write_ProducesUuid7StringAsync() {
    var id = MessageId.New();
    var opts = _opts<MessageIdJsonConverter>();
    var json = JsonSerializer.Serialize(id, opts);
    // Uuid7 hex string format — should start and end with a double quote.
    await Assert.That(json.StartsWith('"')).IsTrue();
    await Assert.That(json.EndsWith('"')).IsTrue();
  }

  [Test]
  public async Task StreamIdJsonConverter_RoundTrip_PreservesIdAsync() {
    var id = StreamId.New();
    var opts = _opts<StreamIdJsonConverter>();
    var json = JsonSerializer.Serialize(id, opts);
    var roundTripped = JsonSerializer.Deserialize<StreamId>(json, opts);
    await Assert.That(roundTripped).IsEqualTo(id);
  }

  [Test]
  public async Task EventIdJsonConverter_RoundTrip_PreservesIdAsync() {
    var id = EventId.New();
    var opts = _opts<EventIdJsonConverter>();
    var json = JsonSerializer.Serialize(id, opts);
    var roundTripped = JsonSerializer.Deserialize<EventId>(json, opts);
    await Assert.That(roundTripped).IsEqualTo(id);
  }

  [Test]
  public async Task CorrelationIdJsonConverter_RoundTrip_PreservesIdAsync() {
    var id = CorrelationId.New();
    var opts = _opts<CorrelationIdJsonConverter>();
    var json = JsonSerializer.Serialize(id, opts);
    var roundTripped = JsonSerializer.Deserialize<CorrelationId>(json, opts);
    await Assert.That(roundTripped).IsEqualTo(id);
  }
}
