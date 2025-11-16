using System.Text.Json;
using System.Text.Json.Serialization;
using Medo;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// AOT-compatible JSON converter for CorrelationId.
/// Serializes CorrelationId using Medo.Uuid7 format for time-ordered UUIDs.
/// </summary>
public sealed class CorrelationIdJsonConverter : JsonConverter<CorrelationId> {
  public override CorrelationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    var uuid7String = reader.GetString()!;
    var uuid7 = Uuid7.Parse(uuid7String);
    return CorrelationId.From(uuid7.ToGuid());
  }

  public override void Write(Utf8JsonWriter writer, CorrelationId value, JsonSerializerOptions options) {
    var uuid7 = new Uuid7(value.Value);
    writer.WriteStringValue(uuid7.ToString());
  }
}
