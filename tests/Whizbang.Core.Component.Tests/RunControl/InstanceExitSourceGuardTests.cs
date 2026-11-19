// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Component.Tests.RunControl;

/// <summary>
/// #2126 gravity: every exit goes through the one exit path. Library code in <c>src</c> never stops the host
/// (<c>IHostApplicationLifetime.StopApplication</c>), ends the process (<c>Environment.Exit</c>,
/// <c>Environment.FailFast</c>) or sets the exit code (<c>Environment.ExitCode</c>) anywhere but the exit path itself. The
/// banned-symbol analyzer fails the build on a call through the interface; this scan also catches a call it cannot see,
/// such as one on a concrete lifetime type.
/// </summary>
/// <docs>operations/startup/instance-exits#one-exit-path</docs>
[Category("RunControl")]
public partial class InstanceExitSourceGuardTests {
  /// <summary>The one file allowed to stop the host and set the exit code.</summary>
  public const string EXIT_PATH = "src/Whizbang.Core/RunControl/WhizbangInstanceExit.cs";

  private static readonly string _repoRoot = _findRepoRoot();

  [GeneratedRegex(@"\.\s*StopApplication\s*\(|\bEnvironment\s*\.\s*(?:Exit|FailFast)\s*\(|\bEnvironment\s*\.\s*ExitCode\b")]
  private static partial Regex _exitCall();

  [Test]
  public async Task NoLibraryCode_StopsTheHostOrSetsTheExitCode_OutsideTheOneExitPathAsync() {
    var sources = Directory.EnumerateFiles(Path.Combine(_repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
      .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
      .Select(f => (Path: Path.GetRelativePath(_repoRoot, f).Replace('\\', '/'), Code: File.ReadAllText(f)))
      .ToList();

    var offenders = sources.Where(s => s.Path != EXIT_PATH).SelectMany(s => Exits(s.Path, s.Code)).ToList();

    await Assert.That(Exits(EXIT_PATH, sources.Single(s => s.Path == EXIT_PATH).Code).Count).IsEqualTo(3)
      .Because("the scan must see the exit path's own two host stops and its exit code, or it is not reading the source tree");
    await Assert.That(offenders).IsEmpty()
      .Because("every exit goes through IWhizbangInstanceExit (#2126): " + string.Join("; ", offenders));
  }

  [Test]
  [Arguments("lifetime.StopApplication();")]
  [Arguments("_host.StopApplication ();")]
  [Arguments("Environment.Exit(3);")]
  [Arguments("System.Environment.Exit(1);")]
  [Arguments("Environment.ExitCode = 1;")]
  [Arguments("var code = Environment.ExitCode;")]
  [Arguments("Environment.FailFast(\"boom\");")]
  public async Task TheScan_FindsAnExitWrittenAnywhereElseAsync(string line) {
    var found = Exits("src/Whizbang.Core/Workers/SomeWorker.cs", "class SomeWorker {\n  void M() {\n    " + line + "\n  }\n}\n");

    await Assert.That(found).IsEquivalentTo(["src/Whizbang.Core/Workers/SomeWorker.cs:3"]);
  }

  [Test]
  public async Task TheScan_IgnoresAnExitNamedInACommentAsync() {
    var found = Exits("src/x.cs", "/// <see cref=\"Environment.ExitCode\"/>\n// lifetime.StopApplication();\nint x;  // Environment.Exit(1)\n");

    await Assert.That(found).IsEmpty();
  }

  [Test]
  public async Task TheBannedSymbols_BanEveryExitApi_InLibraryProjectsAsync() {
    var banned = await File.ReadAllLinesAsync(Path.Combine(_repoRoot, "BannedSymbols.Exit.txt"));
    var props = await File.ReadAllTextAsync(Path.Combine(_repoRoot, "src", "Directory.Build.props"));

    foreach (var symbol in new[] {
        "M:Microsoft.Extensions.Hosting.IHostApplicationLifetime.StopApplication;",
        "M:System.Environment.Exit(System.Int32);",
        "P:System.Environment.ExitCode;",
        "M:System.Environment.FailFast(System.String);",
        "M:System.Environment.FailFast(System.String,System.Exception);",
      }) {
      await Assert.That(banned.Any(line => line.StartsWith(symbol, StringComparison.Ordinal))).IsTrue().Because(symbol);
    }
    await Assert.That(props).Contains("BannedSymbols.Exit.txt")
      .Because("the ban applies to every library project, through src/Directory.Build.props");
  }

  /// <summary>Every line of <paramref name="code"/> that exits, as <c>path:line</c>; comments are not code.</summary>
  /// <param name="path">The file's path, for the answer.</param>
  /// <param name="code">The file's text.</param>
  /// <returns>The offending lines.</returns>
  public static List<string> Exits(string path, string code) {
    var found = new List<string>();
    var lines = code.Split('\n');
    for (var i = 0; i < lines.Length; i++) {
      var line = lines[i];
      var comment = line.IndexOf("//", StringComparison.Ordinal);
      var codePart = comment >= 0 ? line[..comment] : line;
      if (_exitCall().IsMatch(codePart)) {
        found.Add($"{path}:{i + 1}");
      }
    }
    return found;
  }

  private static string _findRepoRoot() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null) {
      if (Directory.Exists(Path.Combine(dir, "src", "Whizbang.Core"))) {
        return dir;
      }
      dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not locate whizbang repo root from AppContext.BaseDirectory");
  }
}
