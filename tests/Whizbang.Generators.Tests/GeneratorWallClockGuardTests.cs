// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// No generator decides what it emits by the wall clock: the generator projects use no regular expressions (#1942).
/// </summary>
/// <remarks>
/// <para>
/// A regular expression in a generator carries a match timeout (the analyzers require one), and a match timeout
/// measures elapsed time, not work. On a loaded build machine a generator thread descheduled for longer than the
/// timeout throws <see cref="RegexMatchTimeoutException"/> from the middle of a pass, Roslyn drops everything that
/// generator would have emitted, and the build or test sees a generator that produced nothing. It happened twice:
/// snippet extraction (fixed by a scan) and then the schema generator's managed-object names, which failed
/// <c>PerspectiveSchemaGeneratorRegistryTests</c> with no <c>PerspectiveSchemas.g.sql.cs</c> in a run whose test took
/// fourteen seconds. A scan has no clock to lose to.
/// </para>
/// <para>
/// Templates are excluded: they are the source of emitted code, which runs in the consumer's process, not in the
/// compiler.
/// </para>
/// </remarks>
public partial class GeneratorWallClockGuardTests {
  [GeneratedRegex(@"\bRegex\s*\.|\bnew\s+(System\.Text\.RegularExpressions\.)?Regex\s*\(|\busing\s+System\.Text\.RegularExpressions\s*;")]
  private static partial Regex _usesARegularExpression();

  // The build maps source paths (CallerFilePath reads "/_/..." in CI), so the repository is found from the test
  // assembly's location instead.
  private static string _sourceDirectory() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null) {
      if (Directory.Exists(Path.Combine(dir, "src", "Whizbang.Generators"))
          && Directory.Exists(Path.Combine(dir, "tests", "Whizbang.Generators.Tests"))) {
        return Path.Combine(dir, "src");
      }
      dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not locate the whizbang repository root from AppContext.BaseDirectory");
  }

  [Test]
  public async Task NoGeneratorProjectUsesARegularExpressionAsync() {
    var src = _sourceDirectory();
    var separator = Path.DirectorySeparatorChar;
    var projects = Directory.EnumerateDirectories(src)
      .Where(dir => Path.GetFileName(dir).Contains("Generators", StringComparison.Ordinal))
      .ToList();

    await Assert.That(projects.Select(Path.GetFileName)).Contains("Whizbang.Data.EFCore.Postgres.Generators")
      .Because("the scan must reach every generator project, or an empty answer proves nothing");

    var offenders = projects
      .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
      .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
        && !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
        && !path.Contains($"{separator}Templates{separator}", StringComparison.Ordinal)
        && _usesARegularExpression().IsMatch(File.ReadAllText(path)))
      .Select(path => Path.GetRelativePath(src, path).Replace(separator, '/'))
      .Order(StringComparer.Ordinal)
      .ToList();

    await Assert.That(offenders).IsEmpty()
      .Because("a regular expression in a generator runs under a match timeout, and a thread descheduled past it "
        + $"makes the generator emit nothing; scan instead: {string.Join(", ", offenders)}");
  }
}
