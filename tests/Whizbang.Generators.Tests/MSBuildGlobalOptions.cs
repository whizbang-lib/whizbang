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
/// The declared names are read from the packages' own build files, so a property added there is delivered here too.
/// A real build also sets <c>ProjectDir</c>; it stays empty here unless a test sets it, so nothing is discovered from
/// the machine running the test.
/// </remarks>
internal static class MSBuildGlobalOptions {
  private static readonly string[] _packages = ["Whizbang.Generators", "Whizbang.Data.EFCore.Postgres.Generators"];

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

    var config = AnalyzerConfig.Parse(
      text.ToString(), Path.Combine(_repoRoot(), "obj", "Consumer.GeneratedMSBuildEditorConfig.editorconfig"));
    var options = AnalyzerConfigSet.Create(ImmutableArray.Create(config)).GlobalConfigOptions.AnalyzerOptions;
    return new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase);
  }

  private static List<string> _readDeclared() {
    var root = _repoRoot();
    var names = new List<string>();
    foreach (var package in _packages) {
      var build = Path.Combine(root, "src", package, "build");
      foreach (var file in Directory.EnumerateFiles(build).Where(_isBuildFile).Order(StringComparer.Ordinal)) {
        names.AddRange(XDocument.Load(file).Descendants("CompilerVisibleProperty")
          .Select(item => (string?)item.Attribute("Include"))
          .OfType<string>()
          .Where(name => !names.Contains(name, StringComparer.Ordinal)));
      }
    }
    return names;
  }

  private static bool _isBuildFile(string path) =>
    path.EndsWith(".props", StringComparison.Ordinal) || path.EndsWith(".targets", StringComparison.Ordinal);

  private static string _repoRoot() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "Whizbang.Core"))) {
      dir = Path.GetDirectoryName(dir);
    }
    return dir ?? throw new InvalidOperationException("Could not locate the repository root from AppContext.BaseDirectory");
  }
}
