// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
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
