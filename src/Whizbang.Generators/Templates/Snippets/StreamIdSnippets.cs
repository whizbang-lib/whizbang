// Template snippets for StreamId code generation.
// These are valid C# methods containing #region blocks that get extracted
// and used as templates during code generation.

using System;
using Whizbang.Generators.Templates.Placeholders;

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>
/// Contains template snippets for stream ID extractor code generation.
/// Each #region contains a code snippet that gets extracted and has placeholders replaced.
/// </summary>
public class StreamIdSnippets {

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
    /// Extract stream ID from __EVENT_NAME__.
    /// </summary>
    public static string Extract(__EVENT_TYPE__ @event) {
      var key = @event.__PROPERTY_NAME__;
      if (key is null) {
        throw new System.InvalidOperationException("Stream ID '__PROPERTY_NAME__' on __EVENT_NAME__ cannot be null.");
      }
      if (key is string str && string.IsNullOrWhiteSpace(str)) {
        throw new System.InvalidOperationException("Stream ID '__PROPERTY_NAME__' on __EVENT_NAME__ cannot be empty.");
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
    /// Extract stream ID from __EVENT_NAME__.
    /// </summary>
    public static string Extract(__EVENT_TYPE__ @event) {
      var key = @event.__PROPERTY_NAME__;
      return key.ToString()!;
    }
    #endregion

    return string.Empty;
  }

  /// <summary>
  /// Example method showing snippet structure for TryResolveAsGuid dispatch routing.
  /// </summary>
  public Guid? TryDispatchExample() {
    #region TRY_DISPATCH_CASE
    if (@event is __EVENT_TYPE__ e__INDEX__) {
      return TryExtractAsGuid(e__INDEX__);
    }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing snippet structure for TryExtractAsGuid method for Guid properties.
  /// </summary>
  public Guid? TryExtractorGuidExample() {
    #region TRY_EXTRACTOR_GUID
    /// <summary>
    /// Tries to extract stream ID from __EVENT_NAME__ as a Guid.
    /// Returns null if the key is null or not a valid Guid.
    /// </summary>
    private static global::System.Guid? TryExtractAsGuid(__EVENT_TYPE__ @event) {
      return @event.__PROPERTY_NAME__;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing snippet structure for TryExtractAsGuid method for nullable Guid properties.
  /// </summary>
  public Guid? TryExtractorNullableGuidExample() {
    #region TRY_EXTRACTOR_NULLABLE_GUID
    /// <summary>
    /// Tries to extract stream ID from __EVENT_NAME__ as a Guid.
    /// Returns null if the key is null or not a valid Guid.
    /// </summary>
    private static global::System.Guid? TryExtractAsGuid(__EVENT_TYPE__ @event) {
      return @event.__PROPERTY_NAME__;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing snippet structure for TryExtractAsGuid method for string properties.
  /// </summary>
  public Guid? TryExtractorStringExample() {
    #region TRY_EXTRACTOR_STRING
    /// <summary>
    /// Tries to extract stream ID from __EVENT_NAME__ as a Guid.
    /// Returns null if the key is null, empty, or not a valid Guid.
    /// </summary>
    private static global::System.Guid? TryExtractAsGuid(__EVENT_TYPE__ @event) {
      var key = @event.__PROPERTY_NAME__;
      if (key is null || string.IsNullOrWhiteSpace(key)) {
        return null;
      }
      return global::System.Guid.TryParse(key, out var guid) ? guid : null;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing snippet structure for TryExtractAsGuid method for nullable reference types.
  /// Uses ToString() to get string representation and parse as Guid.
  /// </summary>
  public Guid? TryExtractorOtherExample() {
    #region TRY_EXTRACTOR_OTHER
    /// <summary>
    /// Tries to extract stream ID from __EVENT_NAME__ as a Guid.
    /// Returns null if the key is null or not a valid Guid.
    /// </summary>
    private static global::System.Guid? TryExtractAsGuid(__EVENT_TYPE__ @event) {
      var key = @event.__PROPERTY_NAME__;
      if (key is null) {
        return null;
      }
      var keyString = key.ToString();
      if (string.IsNullOrWhiteSpace(keyString)) {
        return null;
      }
      return global::System.Guid.TryParse(keyString, out var guid) ? guid : null;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing snippet structure for TryExtractAsGuid method for non-nullable value types.
  /// Uses ToString() to get string representation and parse as Guid.
  /// </summary>
  public Guid? TryExtractorValueTypeExample() {
    #region TRY_EXTRACTOR_VALUE_TYPE
    /// <summary>
    /// Tries to extract stream ID from __EVENT_NAME__ as a Guid.
    /// Returns null if the key is not a valid Guid.
    /// </summary>
    private static global::System.Guid? TryExtractAsGuid(__EVENT_TYPE__ @event) {
      var keyString = @event.__PROPERTY_NAME__.ToString();
      if (string.IsNullOrWhiteSpace(keyString)) {
        return null;
      }
      return global::System.Guid.TryParse(keyString, out var guid) ? guid : null;
    }
    #endregion

    return null;
  }

  // ========================================
  // COMMAND SNIPPETS
  // ========================================

  /// <summary>
  /// Example method showing snippet structure for command dispatch routing.
  /// </summary>
  public string CommandDispatchExample() {
    #region COMMAND_DISPATCH_CASE
    if (command is __COMMAND_TYPE__ c__INDEX__) {
      return ExtractFromCommand(c__INDEX__);
    }
    #endregion

    return string.Empty;
  }

  /// <summary>
  /// Example method showing snippet structure for TryResolveAsGuid command dispatch routing.
  /// </summary>
  public Guid? CommandTryDispatchExample() {
    #region COMMAND_TRY_DISPATCH_CASE
    if (command is __COMMAND_TYPE__ c__INDEX__) {
      return TryExtractAsGuidFromCommand(c__INDEX__);
    }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing command extractor with null check.
  /// </summary>
  public string CommandExtractorWithNullCheckExample() {
    #region COMMAND_EXTRACTOR_NULLABLE
    /// <summary>
    /// Extract stream ID from __COMMAND_NAME__.
    /// </summary>
    public static string ExtractFromCommand(__COMMAND_TYPE__ command) {
      var key = command.__PROPERTY_NAME__;
      if (key is null) {
        throw new System.InvalidOperationException("Stream ID '__PROPERTY_NAME__' on __COMMAND_NAME__ cannot be null.");
      }
      if (key is string str && string.IsNullOrWhiteSpace(str)) {
        throw new System.InvalidOperationException("Stream ID '__PROPERTY_NAME__' on __COMMAND_NAME__ cannot be empty.");
      }
      return key.ToString()!;
    }
    #endregion

    return string.Empty;
  }

  /// <summary>
  /// Example method showing command extractor without null check.
  /// </summary>
  public string CommandExtractorWithoutNullCheckExample() {
    #region COMMAND_EXTRACTOR_NON_NULLABLE
    /// <summary>
    /// Extract stream ID from __COMMAND_NAME__.
    /// </summary>
    public static string ExtractFromCommand(__COMMAND_TYPE__ command) {
      var key = command.__PROPERTY_NAME__;
      return key.ToString()!;
    }
    #endregion

    return string.Empty;
  }

  /// <summary>
  /// TryExtractAsGuid for command with Guid property.
  /// </summary>
  public Guid? CommandTryExtractorGuidExample() {
    #region COMMAND_TRY_EXTRACTOR_GUID
    /// <summary>
    /// Tries to extract stream ID from __COMMAND_NAME__ as a Guid.
    /// </summary>
    private static global::System.Guid? TryExtractAsGuidFromCommand(__COMMAND_TYPE__ command) {
      return command.__PROPERTY_NAME__;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// TryExtractAsGuid for command with nullable Guid property.
  /// </summary>
  public Guid? CommandTryExtractorNullableGuidExample() {
    #region COMMAND_TRY_EXTRACTOR_NULLABLE_GUID
    /// <summary>
    /// Tries to extract stream ID from __COMMAND_NAME__ as a Guid.
    /// </summary>
    private static global::System.Guid? TryExtractAsGuidFromCommand(__COMMAND_TYPE__ command) {
      return command.__PROPERTY_NAME__;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// TryExtractAsGuid for command with string property.
  /// </summary>
  public Guid? CommandTryExtractorStringExample() {
    #region COMMAND_TRY_EXTRACTOR_STRING
    /// <summary>
    /// Tries to extract stream ID from __COMMAND_NAME__ as a Guid.
    /// </summary>
    private static global::System.Guid? TryExtractAsGuidFromCommand(__COMMAND_TYPE__ command) {
      var key = command.__PROPERTY_NAME__;
      if (key is null || string.IsNullOrWhiteSpace(key)) {
        return null;
      }
      return global::System.Guid.TryParse(key, out var guid) ? guid : null;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// TryExtractAsGuid for command with other reference type property.
  /// </summary>
  public Guid? CommandTryExtractorOtherExample() {
    #region COMMAND_TRY_EXTRACTOR_OTHER
    /// <summary>
    /// Tries to extract stream ID from __COMMAND_NAME__ as a Guid.
    /// </summary>
    private static global::System.Guid? TryExtractAsGuidFromCommand(__COMMAND_TYPE__ command) {
      var key = command.__PROPERTY_NAME__;
      if (key is null) {
        return null;
      }
      var keyString = key.ToString();
      if (string.IsNullOrWhiteSpace(keyString)) {
        return null;
      }
      return global::System.Guid.TryParse(keyString, out var guid) ? guid : null;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// TryExtractAsGuid for command with non-nullable value type property.
  /// </summary>
  public Guid? CommandTryExtractorValueTypeExample() {
    #region COMMAND_TRY_EXTRACTOR_VALUE_TYPE
    /// <summary>
    /// Tries to extract stream ID from __COMMAND_NAME__ as a Guid.
    /// </summary>
    private static global::System.Guid? TryExtractAsGuidFromCommand(__COMMAND_TYPE__ command) {
      var keyString = command.__PROPERTY_NAME__.ToString();
      if (string.IsNullOrWhiteSpace(keyString)) {
        return null;
      }
      return global::System.Guid.TryParse(keyString, out var guid) ? guid : null;
    }
    #endregion

    return null;
  }
}
