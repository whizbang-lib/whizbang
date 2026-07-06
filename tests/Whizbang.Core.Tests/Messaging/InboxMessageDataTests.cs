using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Unit tests for <see cref="InboxMessageData"/> and its custom converter.
/// The converter must WRITE the compact short property names (id/p/h) and READ both
/// the short names AND the long names (MessageId/Payload/Hops) — long names occur when
/// data was serialized through the IMessageEnvelope interface type. Missing properties
/// and null hops must fail loudly with a JsonException naming the property, never
/// produce a half-hydrated instance.
/// </summary>
public class InboxMessageDataTests {
  private static readonly JsonSerializerOptions _jsonOptions = JsonContextRegistry.CreateCombinedOptions();

  [Test]
  public async Task Construction_ExposesAllPropertiesAsync() {
    var messageId = MessageId.From((Guid)TrackedGuid.NewMedo());
    using var doc = JsonDocument.Parse("{\"answer\":42}");
    var data = new InboxMessageData {
      MessageId = messageId,
      Payload = doc.RootElement.Clone(),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }]
    };

    await Assert.That(data.MessageId).IsEqualTo(messageId);
    await Assert.That(data.Payload.GetProperty("answer").GetInt32()).IsEqualTo(42);
    await Assert.That(data.Hops.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Serialize_WritesShortPropertyNamesOnlyAsync() {
    var data = _createData("{\"k\":1}");

    var json = JsonSerializer.Serialize(data, _jsonOptions);

    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    await Assert.That(root.TryGetProperty("id", out _)).IsTrue();
    await Assert.That(root.TryGetProperty("p", out _)).IsTrue();
    await Assert.That(root.TryGetProperty("h", out _)).IsTrue();
    await Assert.That(root.TryGetProperty("MessageId", out _)).IsFalse();
    await Assert.That(root.TryGetProperty("Payload", out _)).IsFalse();
    await Assert.That(root.TryGetProperty("Hops", out _)).IsFalse();
  }

  [Test]
  public async Task RoundTrip_ShortNames_PreservesValuesAsync() {
    var data = _createData("{\"total\":10}");

    var json = JsonSerializer.Serialize(data, _jsonOptions);
    var roundTripped = JsonSerializer.Deserialize<InboxMessageData>(json, _jsonOptions);

    await Assert.That(roundTripped).IsNotNull();
    var messageId = roundTripped?.MessageId ?? default;
    await Assert.That(messageId).IsEqualTo(data.MessageId);
    var payloadText = roundTripped?.Payload.GetRawText() ?? string.Empty;
    await Assert.That(payloadText).IsEqualTo(data.Payload.GetRawText());
    var hopCount = roundTripped?.Hops.Count ?? 0;
    await Assert.That(hopCount).IsEqualTo(1);
  }

  [Test]
  public async Task Deserialize_LongPropertyNames_HydratesSameValuesAsync() {
    var data = _createData("{\"total\":10}");
    var shortJson = JsonSerializer.Serialize(data, _jsonOptions);
    string longJson;
    using (var doc = JsonDocument.Parse(shortJson)) {
      var idRaw = doc.RootElement.GetProperty("id").GetRawText();
      var payloadRaw = doc.RootElement.GetProperty("p").GetRawText();
      var hopsRaw = doc.RootElement.GetProperty("h").GetRawText();
      longJson = $"{{\"MessageId\":{idRaw},\"Payload\":{payloadRaw},\"Hops\":{hopsRaw}}}";
    }

    var fromLongNames = JsonSerializer.Deserialize<InboxMessageData>(longJson, _jsonOptions);

    await Assert.That(fromLongNames).IsNotNull();
    var messageId = fromLongNames?.MessageId ?? default;
    await Assert.That(messageId).IsEqualTo(data.MessageId);
    var payloadText = fromLongNames?.Payload.GetRawText() ?? string.Empty;
    await Assert.That(payloadText).IsEqualTo(data.Payload.GetRawText());
    var hopCount = fromLongNames?.Hops.Count ?? 0;
    await Assert.That(hopCount).IsEqualTo(1);
  }

  [Test]
  public async Task Deserialize_MissingMessageId_ThrowsNamingThePropertyAsync() {
    const string json = "{\"p\":{},\"h\":[]}";

    var message = _captureJsonErrorMessage(json);

    await Assert.That(message).Contains("MessageId");
  }

  [Test]
  public async Task Deserialize_MissingPayload_ThrowsNamingThePropertyAsync() {
    var data = _createData("{}");
    var idJson = JsonSerializer.Serialize(data, _jsonOptions);
    string json;
    using (var doc = JsonDocument.Parse(idJson)) {
      var idRaw = doc.RootElement.GetProperty("id").GetRawText();
      json = $"{{\"id\":{idRaw},\"h\":[]}}";
    }

    var message = _captureJsonErrorMessage(json);

    await Assert.That(message).Contains("Payload");
  }

  [Test]
  public async Task Deserialize_MissingHops_ThrowsNamingThePropertyAsync() {
    var data = _createData("{}");
    var idJson = JsonSerializer.Serialize(data, _jsonOptions);
    string json;
    using (var doc = JsonDocument.Parse(idJson)) {
      var idRaw = doc.RootElement.GetProperty("id").GetRawText();
      json = $"{{\"id\":{idRaw},\"p\":{{}}}}";
    }

    var message = _captureJsonErrorMessage(json);

    await Assert.That(message).Contains("Hops");
  }

  [Test]
  public async Task Deserialize_NullHops_ThrowsAsync() {
    var data = _createData("{}");
    var idJson = JsonSerializer.Serialize(data, _jsonOptions);
    string json;
    using (var doc = JsonDocument.Parse(idJson)) {
      var idRaw = doc.RootElement.GetProperty("id").GetRawText();
      json = $"{{\"id\":{idRaw},\"p\":{{}},\"h\":null}}";
    }

    var message = _captureJsonErrorMessage(json);

    await Assert.That(message).Contains("Hops");
  }

  [Test]
  public async Task Deserialize_NonObjectToken_ThrowsExpectedStartOfObjectAsync() {
    const string json = "[]";

    var message = _captureJsonErrorMessage(json);

    await Assert.That(message).Contains("Expected start of object");
  }

  // ========================================
  // Helpers
  // ========================================

  private static InboxMessageData _createData(string payloadJson) {
    using var doc = JsonDocument.Parse(payloadJson);
    return new InboxMessageData {
      MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()),
      Payload = doc.RootElement.Clone(),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }]
    };
  }

  private static string _captureJsonErrorMessage(string json) {
    try {
      _ = JsonSerializer.Deserialize<InboxMessageData>(json, _jsonOptions);
    } catch (JsonException ex) {
      return ex.Message;
    }
    return string.Empty;
  }
}
