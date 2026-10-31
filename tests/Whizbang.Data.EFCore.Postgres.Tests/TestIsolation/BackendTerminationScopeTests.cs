// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.TestIsolation;

/// <summary>
/// A test that terminates or cancels PostgreSQL backends ends only its own database's (#1915).
/// </summary>
/// <remarks>
/// Every test process on a machine shares one PostgreSQL container, so the test projects and the shards of a project
/// run against one cluster. A termination chosen without naming the database reaches the sessions of whatever other
/// test is running at that moment: a test that ended every backend holding an advisory lock ended the schema step of
/// another shard's test setup, whose transaction then read as disposed (ObjectDisposedException: NpgsqlTransaction),
/// and the shards failed only when they ran together. The <c>NotInParallel</c> key orders the tests of one process
/// and does nothing across processes; the database name is what isolates them. A statement either names the
/// database (<c>datname</c>, <c>current_database()</c>, or an advisory lock's <c>database</c>) or terminates one
/// backend whose pid it was handed.
/// </remarks>
[Category("Shard1")]
public partial class BackendTerminationScopeTests {
  /// <summary>Where a statement held in a C# string literal ends.</summary>
  private static readonly string[] _literalEnds = ["\"\"\"", "\";", "\",", "\")"];

  [GeneratedRegex(@"pg_(terminate|cancel)_backend\(")]
  private static partial Regex _endsABackend();

  [GeneratedRegex(@"pg_(terminate|cancel)_backend\(\s*(@\w+|\$\d+|\{\w+\})\s*\)")]
  private static partial Regex _endsOneHandedBackend();

  [GeneratedRegex(@"datname|current_database\(\)|\.database\b")]
  private static partial Regex _namesTheDatabase();

  // The build maps source paths (CallerFilePath reads "/_/..." in CI), so the tests are found from the test
  // assembly's location instead: walk up to the repository root, then into its tests folder.
  private static string _testsDirectory() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null) {
      var candidate = Path.Combine(dir, "tests");
      if (Directory.Exists(Path.Combine(dir, "src", "Whizbang.Core")) && Directory.Exists(candidate)) {
        return candidate;
      }
      dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not locate the whizbang repository root from AppContext.BaseDirectory");
  }

  /// <summary>The statement text from a termination to the end of the string literal that holds it.</summary>
  private static string _statementAt(string text, int index) {
    var end = text.Length;
    foreach (var terminator in _literalEnds) {
      var at = text.IndexOf(terminator, index, StringComparison.Ordinal);
      if (at >= 0 && at < end) {
        end = at;
      }
    }
    return text[index..end];
  }

  [Test]
  public async Task EveryBackendTermination_EndsOnlyItsOwnDatabasesSessionsAsync() {
    var testsDirectory = _testsDirectory();
    var separator = Path.DirectorySeparatorChar;
    var statements = Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories)
      .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
        && !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
        && !path.EndsWith(nameof(BackendTerminationScopeTests) + ".cs", StringComparison.Ordinal))
      .SelectMany(path => {
        var text = File.ReadAllText(path);
        return _endsABackend().Matches(text).Select(match => (Path: path, Statement: _statementAt(text, match.Index)));
      })
      .ToList();

    await Assert.That(statements.Select(s => Path.GetFileName(s.Path)))
      .Contains("RoleAssignmentElectorE2ETests.cs")
      .Because("the scan must find the elector test whose termination reached other shards, or it proves nothing");

    var offenders = statements
      .Where(s => !_endsOneHandedBackend().IsMatch(s.Statement) && !_namesTheDatabase().IsMatch(s.Statement))
      .Select(s => $"{Path.GetRelativePath(testsDirectory, s.Path)}: {s.Statement.ReplaceLineEndings(" ")}")
      .Order(StringComparer.Ordinal)
      .ToList();
    await Assert.That(offenders).IsEmpty()
      .Because($"these end backends in every database of the shared cluster: {string.Join(" | ", offenders)}");
  }
}
