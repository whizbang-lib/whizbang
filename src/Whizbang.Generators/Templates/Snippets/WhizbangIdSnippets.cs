#pragma warning disable IDE1006 // Naming Styles

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>
/// Reusable code snippets for WhizbangId JsonTypeInfo generation.
/// These snippets are extracted and used by the WhizbangIdGenerator.
/// </summary>
internal class WhizbangIdSnippets {

  #region WHIZBANGID_TYPE_CHECK
  if (type == typeof(__FULLY_QUALIFIED_NAME__)) {
    // Create JsonTypeInfo for __TYPE_NAME__ using the generated converter
    var converter = new __FULLY_QUALIFIED_NAME__JsonConverter();
    var jsonTypeInfo = JsonMetadataServices.CreateValueInfo<__FULLY_QUALIFIED_NAME__>(options, converter);
    return jsonTypeInfo;
  }
  #endregion

  #region WHIZBANGID_NULLABLE_TYPE_CHECK
  if (type == typeof(__FULLY_QUALIFIED_NAME__?)) {
    // Create JsonTypeInfo for __TYPE_NAME__? using nullable converter wrapper
    var converter = JsonMetadataServices.GetNullableConverter<__FULLY_QUALIFIED_NAME__>(options);
    var jsonTypeInfo = JsonMetadataServices.CreateValueInfo<__FULLY_QUALIFIED_NAME__?>(options, converter);
    return jsonTypeInfo;
  }
  #endregion

  // Placeholder declarations for IDE validation
  internal class __FULLY_QUALIFIED_NAME__ { }
  internal class __TYPE_NAME__ { }
}

#pragma warning restore IDE1006
