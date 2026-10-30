// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.TestIsolation;

/// <summary>
/// A test that resets or clears one of the process-wide registries the database tests read (the model registrars,
/// the physical field and hydrator registries, the DbContext registration and initialization registries) holds the
/// <c>EFCorePostgresTests</c> key, so the reset never overlaps a test that relies on what it removes (#1903).
/// </summary>
/// <remarks>
/// The GraphQL jsonb containment test failed in a full shard run and passed alone: a sibling under the shared key
/// had emptied the physical field registry, the test's own context build refills it through the model registrars,
/// and a registrar test under a different key had emptied those for the length of each of its tests. The query then
/// had no physical column to translate to and failed with an execution error in milliseconds. A registry reset under
/// any other key leaves the same window open for every database test that runs beside it.
/// </remarks>
[Category("Shard1")]
public partial class SharedRegistryResetIsolationTests {
  private const string SHARED_KEY = "EFCorePostgresTests";

  [GeneratedRegex(@"\b\w+Registry\.(ResetForTesting|Clear)\(")]
  private static partial Regex _resetsARegistry();

  [GeneratedRegex(@"\[NotInParallel\((""EFCorePostgresTests""|\[[^\]]*""EFCorePostgresTests""[^\]]*\])\)\]")]
  private static partial Regex _holdsTheSharedKey();

  private static string _thisFile([CallerFilePath] string path = "") => path;

  [Test]
  public async Task EveryTestThatResetsASharedRegistry_HoldsTheDatabaseTestsKeyAsync() {
    var projectDirectory = Path.GetDirectoryName(Path.GetDirectoryName(_thisFile()))!;
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
      .Where(source => !_holdsTheSharedKey().IsMatch(source.Text))
      .Select(source => Path.GetRelativePath(projectDirectory, source.Path))
      .Order(StringComparer.Ordinal)
      .ToList();
    await Assert.That(offenders).IsEmpty()
      .Because($"these reset a registry the database tests read without holding the {SHARED_KEY} key: {string.Join(", ", offenders)}");
  }
}
