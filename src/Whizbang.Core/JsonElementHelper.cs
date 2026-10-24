// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;

namespace Whizbang.Core;

/// <summary>
/// AOT-compatible helper methods for creating JsonElement values without reflection.
/// Uses manual JSON construction and JsonElement.Parse for primitive types.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/JsonElementHelperTests.cs</tests>
internal static class JsonElementHelper {
  /// <summary>
  /// Creates a JsonElement from a string value (AOT-compatible).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/JsonElementHelperTests.cs:JsonElementHelper_FromString_WithValidString_ReturnsJsonElementAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/JsonElementHelperTests.cs:JsonElementHelper_FromString_WithNull_ReturnsNullJsonElementAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/JsonElementHelperTests.cs:JsonElementHelper_FromString_WithSpecialCharacters_EscapesCorrectlyAsync</tests>
  public static JsonElement FromString(string? value) {
    if (value == null) {
      return JsonElement.Parse("null");
    }

    // Manual JSON escaping for AOT compatibility
    var escaped = value
      .Replace("\\", "\\\\")
      .Replace("\"", "\\\"")
      .Replace("\n", "\\n")
      .Replace("\r", "\\r")
      .Replace("\t", "\\t")
      .Replace("\b", "\\b")
      .Replace("\f", "\\f");

    return JsonElement.Parse($"\"{escaped}\"");
  }

  /// <summary>
  /// Creates a JsonElement from an integer value (AOT-compatible).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/JsonElementHelperTests.cs:JsonElementHelper_FromInt32_WithValidInt_ReturnsJsonElementAsync</tests>
  public static JsonElement FromInt32(int value) {
    return JsonElement.Parse(value.ToString(CultureInfo.InvariantCulture));
  }

  /// <summary>
  /// Creates a JsonElement from a boolean value (AOT-compatible).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/JsonElementHelperTests.cs:JsonElementHelper_FromBoolean_WithTrue_ReturnsJsonElementAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/JsonElementHelperTests.cs:JsonElementHelper_FromBoolean_WithFalse_ReturnsJsonElementAsync</tests>
  public static JsonElement FromBoolean(bool value) {
    return JsonElement.Parse(value ? "true" : "false");
  }

  /// <summary>
  /// Creates a JsonElement array from a sequence of strings (AOT-compatible).
  /// </summary>
  public static JsonElement FromStringArray(IEnumerable<string> values) {
    ArgumentNullException.ThrowIfNull(values);

    // Build JSON array manually for AOT compatibility
    var escapedValues = values.Select(v => {
      if (v == null) {
        return "null";
      }

      var escaped = v
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n")
        .Replace("\r", "\\r")
        .Replace("\t", "\\t")
        .Replace("\b", "\\b")
        .Replace("\f", "\\f");

      return $"\"{escaped}\"";
    });

    var json = $"[{string.Join(",", escapedValues)}]";
    return JsonElement.Parse(json);
  }
}
