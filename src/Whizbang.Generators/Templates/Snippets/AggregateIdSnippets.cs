// Template snippets for AggregateId code generation.
// These are valid C# methods containing #region blocks that get extracted
// and used as templates during code generation.

using System;
using Whizbang.Generators.Templates.Placeholders;

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>
/// Contains template snippets for aggregate ID extractor code generation.
/// Each #region contains a code snippet that gets extracted and has placeholders replaced.
/// </summary>
public class AggregateIdSnippets {

  /// <summary>
  /// Example method showing snippet structure for aggregate ID extraction (direct Guid access).
  /// The actual snippet is extracted from the #region block.
  /// </summary>
  public Guid? ExtractorExample() {
    #region EXTRACTOR
    if (messageType == typeof(__MESSAGE_TYPE__)) { var typed = (__MESSAGE_TYPE__)message; return typed.__PROPERTY_NAME__; }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing snippet structure for aggregate ID extraction (via .Value property).
  /// Used for WhizbangId types that wrap Guid with a .Value property.
  /// </summary>
  public Guid? ExtractorWithValueExample() {
    #region EXTRACTOR_WITH_VALUE
    if (messageType == typeof(__MESSAGE_TYPE__)) { var typed = (__MESSAGE_TYPE__)message; return typed.__PROPERTY_NAME__.Value; }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing snippet structure for DI registration.
  /// </summary>
  public void DiRegistrationExample() {
    #region DI_REGISTRATION
/// <summary>
/// Registers the source-generated aggregate ID extractor for zero-reflection extraction.
/// Discovered __COUNT__ message type(s) with [AggregateId] attributes.
/// </summary>
public static IServiceCollection AddWhizbangAggregateIdExtractor(this IServiceCollection services) {
  services.AddSingleton<IAggregateIdExtractor, AggregateIdExtractor>();
  return services;
}
    #endregion
  }
}
