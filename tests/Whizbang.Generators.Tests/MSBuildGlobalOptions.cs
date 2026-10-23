// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The build properties a generator sees, in the shape MSBuild delivers them: every property the Whizbang generator
/// packages declare <c>CompilerVisibleProperty</c> is written to the generated global config, set or not, and Roslyn
/// parses that file into the options. A declared property the project never sets arrives as an empty value, not as a
/// missing one (#1660), and the parser trims what a project does set.
/// </summary>
/// <remarks>
/// The declared names are read from the packages' own build files (embedded in this assembly by the project file), so a
/// property added there is delivered here too.
/// A real build also sets <c>ProjectDir</c>; it stays empty here unless a test sets it, so nothing is discovered from
/// the machine running the test.
/// </remarks>
internal static class MSBuildGlobalOptions {
  private static readonly string[] _packages = ["Whizbang.Generators", "Whizbang.Data.EFCore.Postgres.Generators"];

  private const string RESOURCE_PREFIX = "WhizbangBuild/";

  // Roslyn requires a rooted path for a global config; nothing is read from it.
  private const string GLOBAL_CONFIG_PATH = "/consumer/obj/Consumer.GeneratedMSBuildEditorConfig.editorconfig";

  private static readonly Lazy<IReadOnlyList<string>> _declared = new(_readDeclared);

  /// <summary>Every property the generator packages declare compiler-visible, in declaration order.</summary>
  public static IReadOnlyList<string> Declared => _declared.Value;

  /// <summary>
  /// The global options a build of a project that sets <paramref name="set"/> (property name to raw value) hands the
  /// generators. Every other declared property is present and empty.
  /// </summary>
  /// <param name="set">The properties the project sets, by MSBuild name; null sets none.</param>
  /// <returns>The <c>build_property.*</c> options, keyed case-insensitively as Roslyn keys them.</returns>
  public static Dictionary<string, string> For(IReadOnlyDictionary<string, string>? set = null) {
    var text = new StringBuilder("is_global = true\n");
    foreach (var name in Declared) {
      var value = set is not null && set.TryGetValue(name, out var configured) ? configured : string.Empty;
      text.Append("build_property.").Append(name).Append(" = ").Append(value).Append('\n');
    }

    var config = AnalyzerConfig.Parse(text.ToString(), GLOBAL_CONFIG_PATH);
    var options = AnalyzerConfigSet.Create(ImmutableArray.Create(config)).GlobalConfigOptions.AnalyzerOptions;
    return new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase);
  }

  private static List<string> _readDeclared() {
    var assembly = typeof(MSBuildGlobalOptions).Assembly;
    var resources = assembly.GetManifestResourceNames();
    var names = new List<string>();
    foreach (var package in _packages) {
      var prefix = RESOURCE_PREFIX + package + "/";
      var files = resources.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
      if (files.Count == 0) {
        throw new InvalidOperationException($"No build files of {package} are embedded; see the test project file");
      }
      foreach (var file in files) {
        using var stream = assembly.GetManifestResourceStream(file)!;
        names.AddRange(XDocument.Load(stream).Descendants("CompilerVisibleProperty")
          .Select(item => (string?)item.Attribute("Include"))
          .OfType<string>()
          .Where(name => !names.Contains(name, StringComparer.Ordinal)));
      }
    }
    return names;
  }
}
