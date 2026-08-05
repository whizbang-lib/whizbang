using System;
using System.IO;
using System.Text.Json;

namespace Whizbang.Core.Serialization;

/// <summary>
/// General, reusable helper that stamps a persisted JSON payload with a
/// <see cref="SerializationVersion"/> so a blob written by an older serialization implementation is
/// detectable on read instead of silently misparsing. Any serialization path that persists JSON
/// (snapshots today; potentially other stores) can use it. Pure: operates only on
/// <see cref="JsonDocument"/>/<see cref="JsonElement"/>.
/// </summary>
/// <remarks>
/// Wire shape: <c>{ "$serializationVersion": &lt;n&gt;, "$payload": &lt;payload json&gt; }</c>.
/// A blob lacking the marker is treated as legacy/unversioned.
/// </remarks>
/// <docs>fundamentals/events/event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Serialization/VersionedJsonEnvelopeTests.cs</tests>
public static class VersionedJsonEnvelope {
  private const string VERSION_PROPERTY = "$serializationVersion";
  private const string PAYLOAD_PROPERTY = "$payload";

  /// <summary>Version returned for a legacy/unversioned blob (one written before versioning).</summary>
  public const int LEGACY = 0;

  /// <summary>Wraps a payload JSON element in an envelope stamped with the serialization version.</summary>
  /// <param name="payload">The serialized payload.</param>
  /// <param name="serializationVersion">The current serialization version to stamp (see <see cref="SerializationVersion.CURRENT"/>).</param>
  public static JsonDocument Wrap(JsonElement payload, int serializationVersion) {
    var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer)) {
      writer.WriteStartObject();
      writer.WriteNumber(VERSION_PROPERTY, serializationVersion);
      writer.WritePropertyName(PAYLOAD_PROPERTY);
      payload.WriteTo(writer);
      writer.WriteEndObject();
    }
    buffer.Position = 0;
    return JsonDocument.Parse(buffer);
  }

  /// <summary>
  /// Reads the stamped serialization version and inner payload from a stored blob. Returns
  /// <c>false</c> for a legacy/unversioned blob (no marker), with <paramref name="version"/> set to
  /// <see cref="LEGACY"/> and <paramref name="payload"/> set to the whole document root.
  /// </summary>
  /// <param name="stored">The stored blob (versioned envelope, or a legacy raw payload).</param>
  /// <param name="version">The stamped version, or <see cref="LEGACY"/> when unversioned.</param>
  /// <param name="payload">The inner payload element (or the whole root for a legacy blob).</param>
  /// <returns><c>true</c> if the blob was a versioned envelope; otherwise <c>false</c>.</returns>
  public static bool TryRead(JsonDocument stored, out int version, out JsonElement payload) {
    ArgumentNullException.ThrowIfNull(stored);

    var root = stored.RootElement;
    if (root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(VERSION_PROPERTY, out var versionElement)
        && root.TryGetProperty(PAYLOAD_PROPERTY, out var payloadElement)
        && versionElement.ValueKind == JsonValueKind.Number) {
      version = versionElement.GetInt32();
      payload = payloadElement;
      return true;
    }

    version = LEGACY;
    payload = root;
    return false;
  }
}
