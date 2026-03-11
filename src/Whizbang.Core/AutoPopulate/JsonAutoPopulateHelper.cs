using System.Text.Json;
using System.Text.Json.Nodes;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.AutoPopulate;

/// <summary>
/// AOT-safe helper for populating timestamp properties on JSON payloads.
/// </summary>
/// <remarks>
/// <para>
/// Used by transport workers to set QueuedAt/DeliveredAt timestamps on serialized
/// message payloads (JsonElement) without requiring typed deserialization.
/// </para>
/// <para>
/// Uses <see cref="JsonNode"/> for manipulation, which is fully AOT-compatible
/// and requires no reflection.
/// </para>
/// </remarks>
/// <docs>attributes/auto-populate</docs>
public static class JsonAutoPopulateHelper {
  /// <summary>
  /// Populates timestamp properties on a JSON payload by message type.
  /// </summary>
  /// <param name="payload">The JSON payload to modify.</param>
  /// <param name="messageType">The message type to look up registrations for.</param>
  /// <param name="kind">The timestamp kind to populate (e.g., QueuedAt, DeliveredAt).</param>
  /// <param name="timestamp">The timestamp value to set.</param>
  /// <returns>A new JsonElement with the timestamp properties set, or the original if no registrations match.</returns>
  public static JsonElement PopulateTimestamp(
      JsonElement payload,
      Type messageType,
      TimestampKind kind,
      DateTimeOffset timestamp) {
    var registrations = AutoPopulateRegistry.GetRegistrationsFor(messageType)
        .Where(r => r.PopulateKind == PopulateKind.Timestamp && r.TimestampKind == kind)
        .ToList();

    if (registrations.Count == 0) {
      return payload;
    }

    return _applyTimestamp(payload, registrations, timestamp);
  }

  /// <summary>
  /// Populates timestamp properties on a JSON payload by message type name.
  /// AOT-safe alternative that avoids <c>Type.GetType()</c>.
  /// </summary>
  /// <param name="payload">The JSON payload to modify.</param>
  /// <param name="messageTypeName">The message type name to look up registrations for.</param>
  /// <param name="kind">The timestamp kind to populate (e.g., QueuedAt, DeliveredAt).</param>
  /// <param name="timestamp">The timestamp value to set.</param>
  /// <returns>A new JsonElement with the timestamp properties set, or the original if no registrations match.</returns>
  public static JsonElement PopulateTimestampByName(
      JsonElement payload,
      string messageTypeName,
      TimestampKind kind,
      DateTimeOffset timestamp) {
    var registrations = AutoPopulateRegistry.FindRegistrationsByTypeName(messageTypeName)
        .Where(r => r.PopulateKind == PopulateKind.Timestamp && r.TimestampKind == kind)
        .ToList();

    if (registrations.Count == 0) {
      return payload;
    }

    return _applyTimestamp(payload, registrations, timestamp);
  }

  private static JsonElement _applyTimestamp(
      JsonElement payload,
      List<AutoPopulateRegistration> registrations,
      DateTimeOffset timestamp) {
    var node = JsonNode.Parse(payload.GetRawText());
    if (node is not JsonObject obj) {
      return payload;
    }

    foreach (var reg in registrations) {
      obj[reg.PropertyName] = JsonValue.Create(timestamp);
    }

    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream)) {
      node.WriteTo(writer);
    }
    stream.Position = 0;
    using var doc = JsonDocument.Parse(stream);
    return doc.RootElement.Clone();
  }
}
