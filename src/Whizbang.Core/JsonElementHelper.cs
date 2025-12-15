using System.Text.Json;

namespace Whizbang.Core;

/// <summary>
/// AOT-compatible helper methods for creating JsonElement values without reflection.
/// Uses manual JSON construction and JsonDocument.Parse for primitive types.
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
      return JsonDocument.Parse("null").RootElement.Clone();
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

    return JsonDocument.Parse($"\"{escaped}\"").RootElement.Clone();
  }

  /// <summary>
  /// Creates a JsonElement from an integer value (AOT-compatible).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/JsonElementHelperTests.cs:JsonElementHelper_FromInt32_WithValidInt_ReturnsJsonElementAsync</tests>
  public static JsonElement FromInt32(int value) {
    return JsonDocument.Parse(value.ToString()).RootElement.Clone();
  }

  /// <summary>
  /// Creates a JsonElement from a boolean value (AOT-compatible).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/JsonElementHelperTests.cs:JsonElementHelper_FromBoolean_WithTrue_ReturnsJsonElementAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/JsonElementHelperTests.cs:JsonElementHelper_FromBoolean_WithFalse_ReturnsJsonElementAsync</tests>
  public static JsonElement FromBoolean(bool value) {
    return JsonDocument.Parse(value ? "true" : "false").RootElement.Clone();
  }
}
