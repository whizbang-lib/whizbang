using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Core.Serialization;

/// <summary>
/// Serializes a message envelope (or any value) to wire bytes in one pass, returning a rich
/// <see cref="SerializationResult"/> (bytes + size + content type). This is the single
/// serialize-once point on the publish path, so the body-offload decision can read the size off
/// the result instead of re-serializing. AOT-safe — the caller supplies the source-generated
/// <see cref="JsonTypeInfo"/>.
/// </summary>
/// <docs>event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Serialization/WireEnvelopeSerializerTests.cs</tests>
public static class WireEnvelopeSerializer {
  /// <summary>Serializes <paramref name="value"/> to UTF-8 JSON bytes using the supplied type info.</summary>
  /// <param name="value">The value to serialize (typically a message envelope).</param>
  /// <param name="typeInfo">The source-generated type info for the value's type.</param>
  /// <param name="options">Serialize options (forward-extensible).</param>
  /// <returns>The serialized bytes plus size/content-type metadata.</returns>
  public static SerializationResult Serialize(object value, JsonTypeInfo typeInfo, SerializationOptions options) {
    ArgumentNullException.ThrowIfNull(value);
    ArgumentNullException.ThrowIfNull(typeInfo);
    ArgumentNullException.ThrowIfNull(options);

    var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
    return new SerializationResult { Data = bytes, ContentType = "application/json" };
  }
}
