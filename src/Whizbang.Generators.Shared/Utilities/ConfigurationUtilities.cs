using System;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// Utilities for reading MSBuild properties from analyzer configuration.
/// Used by generators to read user configuration such as table naming options.
/// </summary>
/// <docs>extending/source-generators/configuration</docs>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/ConfigurationUtilitiesTests.cs</tests>
public static class ConfigurationUtilities {
  /// <summary>
  /// MSBuild property name for enabling/disabling table name suffix stripping.
  /// Default: true
  /// </summary>
  public const string STRIP_TABLE_NAME_SUFFIXES_PROPERTY = "build_property.WhizbangStripTableNameSuffixes";

  /// <summary>
  /// MSBuild property name for comma-separated list of suffixes to strip.
  /// Default: "ReadModel,Model,Projection,Dto,View"
  /// </summary>
  public const string TABLE_NAME_SUFFIXES_TO_STRIP_PROPERTY = "build_property.WhizbangTableNameSuffixesToStrip";

  /// <summary>
  /// MSBuild property name for overriding the maximum identifier length.
  /// Default: Uses provider's default (e.g., 63 for PostgreSQL).
  /// Only set this for edge cases like custom PostgreSQL builds with different NAMEDATALEN.
  /// </summary>
  public const string MAX_IDENTIFIER_LENGTH_PROPERTY = "build_property.WhizbangMaxIdentifierLength";

  /// <summary>
  /// Reads table name configuration from MSBuild properties.
  /// Falls back to TableNameConfig.Default if properties are not set.
  /// </summary>
  /// <param name="globalOptions">The analyzer config options containing MSBuild properties</param>
  /// <returns>A TableNameConfig based on the MSBuild properties</returns>
  public static TableNameConfig GetTableNameConfig(AnalyzerConfigOptions globalOptions) {
    if (globalOptions is null) {
      return TableNameConfig.Default;
    }

    // Read StripTableNameSuffixes (default: true)
    var stripSuffixes = true;
    if (globalOptions.TryGetValue(STRIP_TABLE_NAME_SUFFIXES_PROPERTY, out var stripValue)) {
      stripSuffixes = string.Equals(stripValue, "true", StringComparison.OrdinalIgnoreCase);
    }

    // Read TableNameSuffixesToStrip (default from TableNameConfig.Default)
    string[] suffixesToStrip = TableNameConfig.Default.SuffixesToStrip;
    if (globalOptions.TryGetValue(TABLE_NAME_SUFFIXES_TO_STRIP_PROPERTY, out var suffixesValue) &&
        !string.IsNullOrWhiteSpace(suffixesValue)) {
      suffixesToStrip = ParseSuffixList(suffixesValue);
    }

    return new TableNameConfig(stripSuffixes, suffixesToStrip);
  }

  /// <summary>
  /// Parses a comma-separated list of suffixes into an array.
  /// Trims whitespace from each suffix and filters out empty entries.
  /// </summary>
  /// <param name="suffixList">Comma-separated list like "Model,Projection,Dto"</param>
  /// <returns>Array of trimmed, non-empty suffixes</returns>
  public static string[] ParseSuffixList(string suffixList) {
    if (string.IsNullOrWhiteSpace(suffixList)) {
      return [];
    }

    var parts = suffixList.Split(',');
    var result = new System.Collections.Generic.List<string>();

    foreach (var part in parts) {
      var trimmed = part.Trim();
      if (!string.IsNullOrEmpty(trimmed)) {
        result.Add(trimmed);
      }
    }

    return [.. result];
  }

  /// <summary>
  /// Helper method for use in incremental generator pipelines.
  /// Creates a selector that extracts TableNameConfig from the options provider.
  /// </summary>
  /// <example>
  /// var tableNameConfig = context.AnalyzerConfigOptionsProvider.Select(
  ///     ConfigurationUtilities.SelectTableNameConfig
  /// );
  /// </example>
  public static TableNameConfig SelectTableNameConfig(
      AnalyzerConfigOptionsProvider provider,
      System.Threading.CancellationToken cancellationToken) {
    return GetTableNameConfig(provider.GlobalOptions);
  }

  /// <summary>
  /// Reads the optional max identifier length override from MSBuild properties.
  /// Returns null if not set, meaning the provider's default should be used.
  /// </summary>
  /// <param name="globalOptions">The analyzer config options containing MSBuild properties</param>
  /// <returns>The override value if set and valid, otherwise null</returns>
  public static int? GetMaxIdentifierLengthOverride(AnalyzerConfigOptions globalOptions) {
    if (globalOptions is null) {
      return null;
    }

    if (globalOptions.TryGetValue(MAX_IDENTIFIER_LENGTH_PROPERTY, out var lengthValue) &&
        !string.IsNullOrWhiteSpace(lengthValue) &&
        int.TryParse(lengthValue, out var parsed) &&
        parsed > 0) {
      return parsed;
    }

    return null;
  }

  /// <summary>
  /// Helper method for use in incremental generator pipelines.
  /// Creates a selector that extracts the max identifier length override from the options provider.
  /// </summary>
  /// <example>
  /// var maxIdLengthOverride = context.AnalyzerConfigOptionsProvider.Select(
  ///     ConfigurationUtilities.SelectMaxIdentifierLengthOverride
  /// );
  /// </example>
  public static int? SelectMaxIdentifierLengthOverride(
      AnalyzerConfigOptionsProvider provider,
      System.Threading.CancellationToken cancellationToken) {
    return GetMaxIdentifierLengthOverride(provider.GlobalOptions);
  }
}
