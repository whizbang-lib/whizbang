// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.MultiStream;

/// <summary>
/// Serializes a run's command and scope with the source-generated type information the registry holds, so nothing is
/// discovered by reflection. The combined options are rebuilt only when a context registers after they were built.
/// </summary>
internal static class MultiStreamJson {
  private static readonly Lock _gate = new();
  private static JsonSerializerOptions? _options;
  private static long _generation = -1;

  internal static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)_currentOptions().GetTypeInfo(typeof(T));

  internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, TypeInfo<T>());

  /// <summary>
  /// Serializes a multi-stream command with its stream list emptied. The streams are recorded one per row and each
  /// stream's handler is given its stream id, so the payload every stream reads back must not carry them: a payload
  /// listing them all was read once per stream, and a run's cost grew with the square of its stream count. Emptied,
  /// not removed, so a command whose stream list is required still reads back.
  /// </summary>
  internal static string SerializeWithoutStreams<T>(T value) where T : IMultiStreamCommand {
    var node = JsonSerializer.SerializeToNode(value, TypeInfo<T>());
    if (node is JsonObject json) {
      foreach (var name in json.Select(property => property.Key).Where(_namesTheStreamList).ToList()) {
        json[name] = new JsonArray();
      }
    }
    return node?.ToJsonString() ?? "null";
  }

  // The command's stream list, under whatever naming policy the options apply (StreamIds, streamIds, stream_ids)
  private static bool _namesTheStreamList(string jsonName) =>
    string.Equals(jsonName.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal),
      nameof(IMultiStreamCommand.StreamIds), StringComparison.OrdinalIgnoreCase);

  internal static T? Deserialize<T>(string json) => JsonSerializer.Deserialize(json, TypeInfo<T>());

  private static JsonSerializerOptions _currentOptions() {
    lock (_gate) {
      var generation = JsonContextRegistry.Generation;
      // The registry's generation only grows from zero, so the -1 start builds the options on first use.
      if (generation != _generation) {
        _options = JsonContextRegistry.CreateCombinedOptions();
        _generation = generation;
      }
      return _options!;
    }
  }
}
