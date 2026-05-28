using System.Text;

namespace Whizbang.Generators.Utilities;

/// <summary>
/// Mirrors <c>Whizbang.Core.Attributes.AttributeArgNamingConvention</c> for use inside the
/// netstandard2.0 generator project (which cannot reference net10.0 Whizbang.Core).
/// Numeric values MUST stay aligned with the public enum so the generator can cast a
/// Roslyn <c>TypedConstant.Value</c> read from user code directly to this type.
/// </summary>
public enum AttributeArgNamingConvention {
  /// <summary>Capitalize the first character: <c>tagValue</c> → <c>TagValue</c>.</summary>
  PascalCase = 0,
  /// <summary>Leave the parameter name unchanged.</summary>
  Identity = 1,
  /// <summary>Lowercase the first character: <c>TagValue</c> → <c>tagValue</c>.</summary>
  CamelCase = 2,
  /// <summary>Insert <c>_</c> at uppercase boundaries and lowercase: <c>tagValue</c> → <c>tag_value</c>.</summary>
  SnakeCase = 3,
  /// <summary>Insert <c>-</c> at uppercase boundaries and lowercase: <c>tagValue</c> → <c>tag-value</c>.</summary>
  KebabCase = 4,
  /// <summary>Uppercase variant of <see cref="SnakeCase"/>: <c>tagValue</c> → <c>TAG_VALUE</c>.</summary>
  UpperSnake = 5,
}

/// <summary>
/// Pure helper that converts a constructor-parameter name to its corresponding property
/// initializer name using a configurable <see cref="AttributeArgNamingConvention"/>.
/// </summary>
public static class AttributeArgNamingHelper {
  /// <summary>
  /// Applies <paramref name="convention"/> to <paramref name="parameterName"/> to produce
  /// the matching property name on a tag-attribute subclass. Returns the input unchanged
  /// when null or empty.
  /// </summary>
  public static string Convert(string parameterName, AttributeArgNamingConvention convention) {
    if (string.IsNullOrEmpty(parameterName)) {
      return parameterName ?? "";
    }

    return convention switch {
      AttributeArgNamingConvention.Identity => parameterName,
      AttributeArgNamingConvention.PascalCase => _toPascal(parameterName),
      AttributeArgNamingConvention.CamelCase => _toCamel(parameterName),
      AttributeArgNamingConvention.SnakeCase => _splitOnBoundaries(parameterName, '_', upper: false),
      AttributeArgNamingConvention.KebabCase => _splitOnBoundaries(parameterName, '-', upper: false),
      AttributeArgNamingConvention.UpperSnake => _splitOnBoundaries(parameterName, '_', upper: true),
      _ => parameterName,
    };
  }

  private static string _toPascal(string s) =>
    char.IsUpper(s[0]) ? s : char.ToUpperInvariant(s[0]) + s[1..];

  private static string _toCamel(string s) =>
    char.IsLower(s[0]) ? s : char.ToLowerInvariant(s[0]) + s[1..];

  private static string _splitOnBoundaries(string s, char separator, bool upper) {
    // Walk the string, inserting `separator` before each uppercase letter that begins a new
    // token. A "new token" begins when:
    //   - the previous char was lowercase (camel/Pascal boundary: tagValue → tag_value)
    //   - the previous char was uppercase AND the next char is lowercase (acronym boundary:
    //     HTTPRequest → http_request — the 'R' of "Request" begins a new token).
    var sb = new StringBuilder(s.Length + 4);
    for (var i = 0; i < s.Length; i++) {
      var c = s[i];
      if (i > 0 && char.IsUpper(c)) {
        var prev = s[i - 1];
        var next = i + 1 < s.Length ? s[i + 1] : (char?)null;
        var prevWasLower = char.IsLower(prev);
        var nextIsLower = next.HasValue && char.IsLower(next.Value);
        if (prevWasLower || (char.IsUpper(prev) && nextIsLower)) {
          sb.Append(separator);
        }
      }
      sb.Append(upper ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
    }
    return sb.ToString();
  }
}
