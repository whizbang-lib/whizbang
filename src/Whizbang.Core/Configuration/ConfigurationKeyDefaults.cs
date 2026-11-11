// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Configuration;

/// <summary>
/// The value each configuration key takes when nothing sets it, as the startup settings report shows it. Inert stub.
/// </summary>
/// <docs>operations/configuration/startup-settings-report#what-it-writes</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/StartupSettingsReportTests.cs</tests>
internal static class ConfigurationKeyDefaults {
  /// <summary>Every key's default. Inert stub.</summary>
  public static IReadOnlyDictionary<string, string> Library { get; } = new Dictionary<string, string>();

  /// <summary>Reads embedded defaults. Inert stub.</summary>
  public static IReadOnlyDictionary<string, string> FromResource(System.Reflection.Assembly assembly, string name) {
    _ = (assembly, name);
    return Library;
  }

  /// <summary>Parses defaults text. Inert stub.</summary>
  /// <param name="text">The text.</param>
  /// <returns>The defaults.</returns>
  public static IReadOnlyDictionary<string, string> Parse(string text) {
    _ = text;
    return Library;
  }
}
