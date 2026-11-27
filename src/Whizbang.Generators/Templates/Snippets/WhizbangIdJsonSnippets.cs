// Template snippets for the JSON converter a generated WhizbangId carries (#2518).
// Each #region holds members of the generated converter; the generator extracts a region, replaces its placeholders and
// indents it into the converter.

using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>The members of a generated WhizbangId's JSON converter.</summary>
internal sealed class __TYPE_NAME__JsonConverter : JsonConverter<__TYPE_NAME__> {
  #region JSON_READ_EITHER_FORM
  /// <summary>
  /// Reads the UUID string, and the object <c>{"Value": "..."}</c> an Entity Framework perspective stores until its
  /// fleet reads the UUID string (#2518), so a reader on either driver reads a row in either form.
  /// </summary>
  /// <param name="reader">The reader, at the id's token.</param>
  /// <param name="typeToConvert">The id's type.</param>
  /// <param name="options">The serializer options.</param>
  /// <returns>The id.</returns>
  public override __TYPE_NAME__ Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    if (reader.TokenType != JsonTokenType.StartObject) {
      return __TYPE_NAME__.From(Guid.Parse(reader.GetString()!, CultureInfo.InvariantCulture));
    }
    Guid? value = null;
    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
      if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("Value")) {
        reader.Read();
        value = Guid.Parse(reader.GetString()!, CultureInfo.InvariantCulture);
      } else {
        reader.Skip();
      }
    }
    return value is { } found
      ? __TYPE_NAME__.From(found)
      : throw new JsonException("A stored __TYPE_NAME__ object holds no Value");
  }

  #endregion
}

/// <summary>Stands in for the generated id.</summary>
internal readonly struct __TYPE_NAME__ {
  public static __TYPE_NAME__ From(Guid value) => default;
}
