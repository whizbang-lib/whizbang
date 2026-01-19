// Template snippets for StreamKey code generation.
// These are valid C# methods containing #region blocks that get extracted
// and used as templates during code generation.

using System;
using Whizbang.Generators.Templates.Placeholders;

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>
/// Contains template snippets for stream key extractor code generation.
/// Each #region contains a code snippet that gets extracted and has placeholders replaced.
/// </summary>
public class StreamKeySnippets {

  /// <summary>
  /// Example method showing snippet structure for dispatch routing.
  /// The actual snippets are extracted from #region blocks.
  /// </summary>
  public string DispatchExample() {
    #region DISPATCH_CASE
    if (@event is __EVENT_TYPE__ e__INDEX__) {
      return Extract(e__INDEX__);
    }
    #endregion

    return string.Empty;
  }

  /// <summary>
  /// Example method showing snippet structure for extractor method with nullable check.
  /// </summary>
  public string ExtractorWithNullCheckExample() {
    #region EXTRACTOR_NULLABLE
    /// <summary>
    /// Extract stream key from __EVENT_NAME__.
    /// </summary>
    public static string Extract(__EVENT_TYPE__ @event) {
      var key = @event.__PROPERTY_NAME__;
      if (key is null) {
        throw new System.InvalidOperationException("Stream key '__PROPERTY_NAME__' on __EVENT_NAME__ cannot be null.");
      }
      if (key is string str && string.IsNullOrWhiteSpace(str)) {
        throw new System.InvalidOperationException("Stream key '__PROPERTY_NAME__' on __EVENT_NAME__ cannot be empty.");
      }
      return key.ToString()!;
    }
    #endregion

    return string.Empty;
  }

  /// <summary>
  /// Example method showing snippet structure for extractor method without null check (for non-nullable types).
  /// </summary>
  public string ExtractorWithoutNullCheckExample() {
    #region EXTRACTOR_NON_NULLABLE
    /// <summary>
    /// Extract stream key from __EVENT_NAME__.
    /// </summary>
    public static string Extract(__EVENT_TYPE__ @event) {
      var key = @event.__PROPERTY_NAME__;
      return key.ToString()!;
    }
    #endregion

    return string.Empty;
  }
}
