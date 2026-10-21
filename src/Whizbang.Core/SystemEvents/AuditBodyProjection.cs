// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Text.Json;

namespace Whizbang.Core.SystemEvents;

/// <summary>The bounded forms an audit record keeps of a message body.</summary>
/// <remarks>Works on the serialized JSON, so it needs no type information and stays AOT-safe.</remarks>
internal static class AuditBodyProjection {
  /// <summary>
  /// Replaces each named list in <paramref name="body"/> with its length (#1525): a property whose name is in
  /// <paramref name="names"/> (compared without regard to case) and whose value is a JSON array becomes a JSON number.
  /// Every other property, and a body that is not a JSON object, is kept as it is.
  /// </summary>
  /// <param name="body">The serialized command.</param>
  /// <param name="names">The C# names of the properties whose lists are replaced.</param>
  /// <returns>The bounded body.</returns>
  internal static JsonElement ListLengths(JsonElement body, IReadOnlyCollection<string> names) {
    if (body.ValueKind != JsonValueKind.Object) {
      return body;
    }
    var lists = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer)) {
      writer.WriteStartObject();
      foreach (var property in body.EnumerateObject()) {
        if (property.Value.ValueKind == JsonValueKind.Array && lists.Contains(property.Name)) {
          writer.WriteNumber(property.Name, property.Value.GetArrayLength());
        } else {
          property.WriteTo(writer);
        }
      }
      writer.WriteEndObject();
    }
    using var document = JsonDocument.Parse(buffer.WrittenMemory);
    return document.RootElement.Clone();
  }
}
