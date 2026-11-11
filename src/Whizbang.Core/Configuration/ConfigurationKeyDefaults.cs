// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;

namespace Whizbang.Core.Configuration;

/// <summary>
/// The value each configuration key takes when nothing sets it, as the startup settings report shows it, marked
/// <c>(default)</c>. Read from <c>configuration-defaults.txt</c>, embedded in Whizbang.Core at build time.
/// </summary>
/// <remarks>
/// The file is recorded, not hand-written: the configuration key manifest test constructs each options type a section
/// binds, reads the value at every manifest key below the section, and fails when the file and the code disagree. So a
/// default changed in code is a default the report shows, and reading it here is a resource stream and a text parse, with
/// no reflection over options types. A key with no recorded default (a value the binder fills in by hand, a named
/// section) is reported as <c>(default)</c> alone.
/// </remarks>
/// <docs>operations/configuration/startup-settings-report#what-it-writes</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/StartupSettingsReportTests.cs</tests>
internal static class ConfigurationKeyDefaults {
  /// <summary>The defaults file's resource name inside Whizbang.Core (the <c>LogicalName</c> in the project file).</summary>
  internal const string RESOURCE_NAME = "Whizbang.Core.Configuration.configuration-defaults.txt";

  private static readonly Lazy<IReadOnlyDictionary<string, string>> _library =
    new(() => FromResource(typeof(ConfigurationKeyDefaults).Assembly, RESOURCE_NAME));

  /// <summary>Every recorded default, keyed by configuration key (ignoring case), parsed once per process.</summary>
  public static IReadOnlyDictionary<string, string> Library => _library.Value;

  /// <summary>Reads a defaults file embedded as a resource.</summary>
  /// <param name="assembly">The assembly holding the resource.</param>
  /// <param name="name">The resource name.</param>
  /// <returns>The defaults it lists.</returns>
  /// <exception cref="InvalidOperationException">The resource is not in the assembly.</exception>
  public static IReadOnlyDictionary<string, string> FromResource(Assembly assembly, string name) {
    using var stream = assembly.GetManifestResourceStream(name)
      ?? throw new InvalidOperationException($"The configuration defaults '{name}' are not embedded in the assembly.");
    using var reader = new StreamReader(stream);
    return Parse(reader.ReadToEnd());
  }

  /// <summary>
  /// Parses defaults text: one <c>key=value</c> per line (the value is everything after the first <c>=</c>); blank lines,
  /// lines starting with <c>#</c>, and lines with no key are skipped.
  /// </summary>
  /// <param name="text">The text.</param>
  /// <returns>The defaults, keyed ignoring case.</returns>
  public static IReadOnlyDictionary<string, string> Parse(string text) {
    var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var raw in text.Split('\n')) {
      var line = raw.TrimEnd('\r');
      var equals = line.IndexOf('=', StringComparison.Ordinal);
      if (line.StartsWith('#') || equals <= 0) {
        continue;
      }
      defaults[line[..equals]] = line[(equals + 1)..];
    }
    return defaults;
  }
}
