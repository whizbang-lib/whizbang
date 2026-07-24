using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// AOT-compatible JSON converter for TrackedGuid.
/// Serializes TrackedGuid as a simple UUID string value (like a regular Guid).
/// </summary>
/// <remarks>
/// This converter ensures that TrackedGuid values are serialized as plain UUID strings
/// like "019c7df5-494b-77d6-b994-e7145b796ec0" rather than objects with Value/Metadata properties.
/// This is important for:
/// - PostgreSQL UUID column compatibility
/// - Efficient JSONB queries
/// - Interoperability with other systems expecting standard UUID format
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#tracked-guid</docs>
public sealed class TrackedGuidJsonConverter : JsonConverter<TrackedGuid> {
  /// <inheritdoc/>
  public override TrackedGuid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    var value = reader.GetString();
    if (value == null) {
      return TrackedGuid.Empty;
    }

    if (Guid.TryParse(value, out var guid)) {
      return TrackedGuid.FromExternal(guid);
    }

    return TrackedGuid.Empty;
  }

  /// <inheritdoc/>
  public override void Write(Utf8JsonWriter writer, TrackedGuid value, JsonSerializerOptions options) {
    // Serialize as plain UUID string (implicit conversion to Guid)
    writer.WriteStringValue(((Guid)value).ToString());
  }
}
