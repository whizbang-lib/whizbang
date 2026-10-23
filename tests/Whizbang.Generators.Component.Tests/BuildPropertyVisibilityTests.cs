// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using System.Xml.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A generator reads an MSBuild property as <c>build_property.Name</c> only when the package declares it a
/// <c>CompilerVisibleProperty</c>; otherwise a consumer's setting never reaches the generator and the default always
/// applies (#1426). Each package's reads are found from its source, so a new read cannot go undeclared.
/// </summary>
/// <docs>fundamentals/perspectives/table-naming</docs>
[Category("SourceGenerators")]
public partial class BuildPropertyVisibilityTests {
  private const string SHARED_PROJECT = "Whizbang.Generators.Shared";
  private const string SHARED_READER = "ConfigurationUtilities";

  private static readonly string _repoRoot = _findRepoRoot();

  /// <summary>Every packable generator project.</summary>
  public static IEnumerable<string> GeneratorPackages() => [
    "Whizbang.Generators",
    "Whizbang.Data.EFCore.Postgres.Generators",
    "Whizbang.Sagas.Generators",
    "Whizbang.Transports.FastEndpoints.Generators",
    "Whizbang.Transports.HotChocolate.Generators",
  ];

  /// <summary>Every build property a package's generators read is declared visible by that package.</summary>
  [Test]
  [MethodDataSource(nameof(GeneratorPackages))]
  public async Task EveryPropertyAGeneratorReads_IsDeclaredByItsPackageAsync(string package) {
    var read = _propertiesRead(package);
    var declared = _declaredVisible(package);

    var undeclared = read.Where(name => !declared.Contains(name)).Order(StringComparer.Ordinal).ToList();

    await Assert.That(undeclared).IsEmpty()
      .Because($"{package} reads these as build_property.* but none of its packed build files declares them a CompilerVisibleProperty, so a project setting never reaches the generator");
  }

  /// <summary>
  /// The reverse: every property a package declares visible is read by its generators. A declared property nothing
  /// reads is written into every consumer's build for no reason, and it reads as a setting that does something.
  /// </summary>
  [Test]
  [MethodDataSource(nameof(GeneratorPackages))]
  public async Task EveryPropertyAPackageDeclares_IsReadByItsGeneratorsAsync(string package) {
    var read = _propertiesRead(package);

    var orphaned = _declaredVisible(package).Where(name => !read.Contains(name)).Order(StringComparer.Ordinal).ToList();

    await Assert.That(orphaned).IsEmpty()
      .Because($"{package} declares these a CompilerVisibleProperty but none of its generators reads them as build_property.*");
  }

  /// <summary>The scan finds the properties it is meant to find, so an empty result above is not a blind scan.</summary>
  [Test]
  public async Task TheScan_FindsTheTableNamingPropertiesTheCoreGeneratorsReadAsync() {
    var read = _propertiesRead("Whizbang.Generators");

    await Assert.That(read).Contains("WhizbangMaxIdentifierLength");
    await Assert.That(read).Contains("WhizbangStripTableNameSuffixes");
    await Assert.That(read).Contains("WhizbangTableNameSuffixesToStrip");
    await Assert.That(read).Contains("WhizbangDocsPath");
  }

  // The properties a package reads: the build_property literals in its own source, plus the shared reader's when the
  // package calls it (the shared library is merged into each generator assembly).
  private static HashSet<string> _propertiesRead(string package) {
    var own = _sources(package).ToList();
    var names = new HashSet<string>(StringComparer.Ordinal);
    _addReads(own, names);
    if (own.Exists(text => text.Contains(SHARED_READER, StringComparison.Ordinal))) {
      _addReads(_sources(SHARED_PROJECT), names);
    }
    return names;
  }

  private static void _addReads(IEnumerable<string> sources, HashSet<string> names) {
    foreach (var text in sources) {
      foreach (Match match in _buildPropertyRead().Matches(text)) {
        names.Add(match.Groups["name"].Value);
      }
    }
  }

  private static IEnumerable<string> _sources(string project) {
    var dir = Path.Combine(_repoRoot, "src", project);
    return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
      .Where(path => !_isBuildOutput(dir, path))
      .Select(File.ReadAllText);
  }

  // The CompilerVisibleProperty items in the build files the package packs for consumers.
  private static HashSet<string> _declaredVisible(string package) {
    var dir = Path.Combine(_repoRoot, "src", package);
    var project = XDocument.Load(Path.Combine(dir, package + ".csproj"));
    var packed = project.Descendants("None")
      .Where(item => string.Equals((string?)item.Attribute("Pack"), "true", StringComparison.OrdinalIgnoreCase))
      .Select(item => (string?)item.Attribute("Include"))
      .OfType<string>()
      .Where(include => include.EndsWith(".props", StringComparison.Ordinal) || include.EndsWith(".targets", StringComparison.Ordinal))
      .Distinct(StringComparer.Ordinal);

    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var include in packed) {
      var file = XDocument.Load(Path.Combine(dir, include.Replace('\\', Path.DirectorySeparatorChar)));
      foreach (var item in file.Descendants("CompilerVisibleProperty")) {
        if ((string?)item.Attribute("Include") is { } name) {
          names.Add(name);
        }
      }
    }
    return names;
  }

  private static bool _isBuildOutput(string projectDir, string path) {
    var relative = Path.GetRelativePath(projectDir, path).Replace('\\', '/');
    return relative.StartsWith("bin/", StringComparison.Ordinal)
      || relative.StartsWith("obj/", StringComparison.Ordinal)
      || relative.StartsWith(".whizbang/", StringComparison.Ordinal);
  }

  private static string _findRepoRoot() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null) {
      if (Directory.Exists(Path.Combine(dir, "src", "Whizbang.Core"))) {
        return dir;
      }
      dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not locate the repository root from AppContext.BaseDirectory");
  }

  [GeneratedRegex("\"build_property\\.(?<name>[A-Za-z0-9_]+)\"")]
  private static partial Regex _buildPropertyRead();
}
