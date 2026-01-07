using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Whizbang.Core;

/// <summary>
/// Provides methods for formatting type names according to TypeQualification flags.
/// </summary>
/// <remarks>
/// This class offers flexible type name formatting based on individual component flags,
/// allowing precise control over how type names appear in generated code, logs, and diagnostics.
/// </remarks>
/// <docs>core-concepts/type-formatting</docs>
public static class TypeFormatter {
  /// <summary>
  /// Formats a type name according to the specified qualification flags.
  /// </summary>
  /// <param name="type">The type to format.</param>
  /// <param name="qualification">Flags controlling which components to include.</param>
  /// <returns>A formatted type name string.</returns>
  /// <remarks>
  /// Flags can be combined to create custom formats. For example:
  /// - TypeQualification.Simple returns just "TypeName"
  /// - TypeQualification.FullyQualified returns "Namespace.TypeName, Assembly"
  /// - TypeQualification.GlobalQualified returns "global::Namespace.TypeName"
  /// </remarks>
  public static string FormatType(Type type, TypeQualification qualification) {
    ArgumentNullException.ThrowIfNull(type);

    // If no flags are set, return empty string
    if (qualification == TypeQualification.None) {
      return string.Empty;
    }

    var result = new StringBuilder();

    // Step 1: Add global:: prefix if requested
    if (qualification.HasFlag(TypeQualification.GlobalPrefix)) {
      result.Append("global::");
    }

    // Step 2: Add namespace if requested
    if (qualification.HasFlag(TypeQualification.Namespace)) {
      if (!string.IsNullOrEmpty(type.Namespace)) {
        result.Append(type.Namespace);

        // Add separator before type name if type name will be included
        if (qualification.HasFlag(TypeQualification.TypeName)) {
          result.Append('.');
        }
      }
    }

    // Step 3: Add type name if requested
    if (qualification.HasFlag(TypeQualification.TypeName)) {
      result.Append(type.Name);
    }

    // Step 4: Add assembly information if requested
    if (qualification.HasFlag(TypeQualification.Assembly)) {
      var assemblyName = type.Assembly.GetName();

      // Add comma separator before assembly if we have content
      if (result.Length > 0) {
        result.Append(", ");
      }

      result.Append(assemblyName.Name);

      // Step 5: Add version, culture, and public key token if requested
      if (qualification.HasFlag(TypeQualification.Version)) {
        result.Append(CultureInfo.InvariantCulture, $", Version={assemblyName.Version}");
      }

      if (qualification.HasFlag(TypeQualification.Culture)) {
        var culture = assemblyName.CultureName;
        if (string.IsNullOrEmpty(culture)) {
          culture = "neutral";
        }
        result.Append(CultureInfo.InvariantCulture, $", Culture={culture}");
      }

      if (qualification.HasFlag(TypeQualification.PublicKeyToken)) {
        var token = assemblyName.GetPublicKeyToken();
        var tokenString = token == null || token.Length == 0
            ? "null"
            : Convert.ToHexStringLower(token);
        result.Append(CultureInfo.InvariantCulture, $", PublicKeyToken={tokenString}");
      }
    }

    return result.ToString();
  }

  /// <summary>
  /// Parses assembly name from a fully qualified type name string.
  /// </summary>
  /// <param name="fullTypeName">
  /// A fully qualified type name string (e.g., "Namespace.Type, Assembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null").
  /// </param>
  /// <param name="stripVersion">
  /// If true, returns only the assembly name without version/culture/token.
  /// If false, returns the full assembly string including version info.
  /// </param>
  /// <returns>
  /// The assembly name (with or without version info), or empty string if no assembly info is present.
  /// </returns>
  /// <remarks>
  /// This method is useful for parsing type strings returned by Type.AssemblyQualifiedName
  /// or similar sources where assembly information needs to be extracted or cleaned.
  /// </remarks>
  public static string ParseAssemblyName(string fullTypeName, bool stripVersion) {
    if (string.IsNullOrEmpty(fullTypeName)) {
      return string.Empty;
    }

    // Split by comma to separate type name from assembly info
    var parts = fullTypeName.Split(',').Select(p => p.Trim()).ToArray();

    // If there's no comma, there's no assembly info
    if (parts.Length < 2) {
      return string.Empty;
    }

    // First part after comma is the assembly name
    var assemblyName = parts[1];

    if (stripVersion) {
      // Just return the assembly name without version info
      return assemblyName;
    }

    // Return full assembly string (everything after the type name)
    var assemblyParts = parts.Skip(1);
    return string.Join(", ", assemblyParts);
  }
}
