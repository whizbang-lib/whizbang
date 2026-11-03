// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Core.Serialization;

/// <summary>
/// The source-generated type information <see cref="JsonContextRegistry"/> holds, for framework code that serializes
/// its own records, so nothing is discovered by reflection. The combined options are rebuilt only when a context
/// registers after they were built.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Serialization/RegisteredJsonTests.cs</tests>
internal static class RegisteredJson {
  private static readonly Lock _gate = new();
  private static JsonSerializerOptions? _options;
  private static long _generation = -1;

  /// <summary>The registered type information for <typeparamref name="T"/>.</summary>
  internal static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options().GetTypeInfo(typeof(T));

  /// <summary>The combined options, current with every context registered so far.</summary>
  internal static JsonSerializerOptions Options() {
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
