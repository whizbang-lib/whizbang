// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.TestIsolation;

/// <summary>
/// A test that resets or clears one of the process-wide registries the database tests read (the model registrars,
/// the physical field and hydrator registries, the DbContext registration and initialization registries, the data
/// coalescers, the stream groups), or switches the containment mode every perspective query compiles under, runs
/// alone, under a <c>[NotInParallel]</c> with no key, so the change never overlaps a test that relies on what it
/// removes or switches (#1903, #2069).
/// </summary>
/// <remarks>
/// <para>
/// The GraphQL jsonb containment test failed in a full shard run and passed alone: a sibling under the shared key
/// had emptied the physical field registry, the test's own context build refills it through the model registrars,
/// and a registrar test under a different key had emptied those for the length of each of its tests. The query then
/// had no physical column to translate to and failed with an execution error in milliseconds.
/// </para>
/// <para>
/// Every database test reads these registries and, since #2069, runs in parallel, so no key is narrow enough: a key
/// only orders the tests that hold it. A test with no key runs after every other test has finished, one at a time,
/// which is the one placement that cannot overlap a reader.
/// </para>
/// </remarks>
[Category("Shard1")]
public partial class SharedRegistryResetIsolationTests {
  [GeneratedRegex(@"\b\w+(Registry|Coalescer|Hydrator|TestSeam)\.(ResetForTesting|Clear)\(|\bJsonbContainmentSwitch\.(Set|SetMode|Reset|ApplyRuntimeConfiguration)\(")]
  private static partial Regex _resetsARegistry();

  /// <summary>The attribute with no key, which TUnit runs after everything else and one test at a time.</summary>
  [GeneratedRegex(@"^\s*\[NotInParallel\]", RegexOptions.Multiline)]
  private static partial Regex _runsAlone();

  // The build maps source paths (CallerFilePath reads "/_/..." in CI), so the project is found from the test
  // assembly's location instead: walk up to the repository root, then into this project's folder.
  private static string _projectDirectory() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null) {
      var candidate = Path.Combine(dir, "tests", "Whizbang.Data.EFCore.Postgres.Tests");
      if (Directory.Exists(Path.Combine(dir, "src", "Whizbang.Core")) && Directory.Exists(candidate)) {
        return candidate;
      }
      dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not locate the whizbang repository root from AppContext.BaseDirectory");
  }

  [Test]
  public async Task EveryTestThatResetsASharedRegistry_RunsAloneAsync() {
    var projectDirectory = _projectDirectory();
    var sources = Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
      .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
      .Select(path => (Path: path, Text: File.ReadAllText(path)))
      .Where(source => _resetsARegistry().IsMatch(source.Text))
      .ToList();

    await Assert.That(sources.Select(source => Path.GetFileName(source.Path)))
      .Contains("ModelRegistrationRegistryCoverageTests.cs")
      .Because("the scan must find the registrar reset that raced the GraphQL test, or it proves nothing");

    var offenders = sources
      .Where(source => !_runsAlone().IsMatch(source.Text))
      .Select(source => Path.GetRelativePath(projectDirectory, source.Path))
      .Order(StringComparer.Ordinal)
      .ToList();
    await Assert.That(offenders).IsEmpty()
      .Because("these reset a registry the database tests read without running alone ([NotInParallel] with no key): "
        + string.Join(", ", offenders));
  }
}
