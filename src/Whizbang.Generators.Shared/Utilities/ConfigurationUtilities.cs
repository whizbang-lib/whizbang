using System;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// Utilities for reading MSBuild properties from analyzer configuration.
/// Used by generators to read user configuration such as table naming options.
/// </summary>
/// <docs>source-generators/configuration</docs>
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
      return Array.Empty<string>();
    }

    var parts = suffixList.Split(',');
    var result = new System.Collections.Generic.List<string>();

    foreach (var part in parts) {
      var trimmed = part.Trim();
      if (!string.IsNullOrEmpty(trimmed)) {
        result.Add(trimmed);
      }
    }

    return result.ToArray();
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
}
