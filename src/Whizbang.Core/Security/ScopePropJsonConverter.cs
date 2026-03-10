using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Core.Security;

/// <summary>
/// JSON converter for <see cref="ScopeProp"/> enum that uses 2-character abbreviated names.
/// Reduces wire size in message hops while maintaining readability.
/// </summary>
/// <docs>core-concepts/scope-propagation</docs>
/// <tests>Whizbang.Core.Tests/Security/ScopeDeltaTests.cs:ScopePropJsonConverter</tests>
/// <remarks>
/// <para>Abbreviation mapping:</para>
/// <list type="table">
///   <listheader><term>Enum</term><description>Abbreviated</description></listheader>
///   <item><term>Scope</term><description>Sc</description></item>
///   <item><term>Roles</term><description>Ro</description></item>
///   <item><term>Perms</term><description>Pe</description></item>
///   <item><term>Principals</term><description>Pr</description></item>
///   <item><term>Claims</term><description>Cl</description></item>
///   <item><term>Actual</term><description>Ac</description></item>
///   <item><term>Effective</term><description>Ef</description></item>
///   <item><term>Type</term><description>Ty</description></item>
/// </list>
/// <para>Deserialization also supports full enum names for backward compatibility.</para>
/// </remarks>
public sealed class ScopePropJsonConverter : JsonConverter<ScopeProp> {
  private static readonly Dictionary<ScopeProp, string> _toShort = new() {
    [ScopeProp.Scope] = "Sc",
    [ScopeProp.Roles] = "Ro",
    [ScopeProp.Perms] = "Pe",
    [ScopeProp.Principals] = "Pr",
    [ScopeProp.Claims] = "Cl",
    [ScopeProp.Actual] = "Ac",
    [ScopeProp.Effective] = "Ef",
    [ScopeProp.Type] = "Ty"
  };

  private static readonly Dictionary<string, ScopeProp> _fromShort =
      _toShort.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

  /// <inheritdoc />
  public override ScopeProp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    var str = reader.GetString();
    if (string.IsNullOrEmpty(str)) {
      throw new JsonException("ScopeProp value cannot be null or empty");
    }

    // Try abbreviated form first
    if (_fromShort.TryGetValue(str, out var prop)) {
      return prop;
    }

    // Fall back to full enum name for backward compatibility
    if (Enum.TryParse<ScopeProp>(str, ignoreCase: true, out var parsed)) {
      return parsed;
    }

    throw new JsonException($"Unknown ScopeProp value: {str}");
  }

  /// <inheritdoc />
  public override void Write(Utf8JsonWriter writer, ScopeProp value, JsonSerializerOptions options) {
    if (_toShort.TryGetValue(value, out var abbrev)) {
      writer.WriteStringValue(abbrev);
    } else {
      // Shouldn't happen if enum is complete, but fall back to name
      writer.WriteStringValue(value.ToString());
    }
  }

  /// <inheritdoc />
  public override ScopeProp ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    var str = reader.GetString();
    if (string.IsNullOrEmpty(str)) {
      throw new JsonException("ScopeProp property name cannot be null or empty");
    }

    // Try abbreviated form first
    if (_fromShort.TryGetValue(str, out var prop)) {
      return prop;
    }

    // Fall back to full enum name for backward compatibility
    if (Enum.TryParse<ScopeProp>(str, ignoreCase: true, out var parsed)) {
      return parsed;
    }

    throw new JsonException($"Unknown ScopeProp property name: {str}");
  }

  /// <inheritdoc />
  public override void WriteAsPropertyName(Utf8JsonWriter writer, ScopeProp value, JsonSerializerOptions options) {
    if (_toShort.TryGetValue(value, out var abbrev)) {
      writer.WritePropertyName(abbrev);
    } else {
      // Shouldn't happen if enum is complete, but fall back to name
      writer.WritePropertyName(value.ToString());
    }
  }
}
