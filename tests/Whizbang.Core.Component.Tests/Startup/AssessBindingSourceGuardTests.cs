// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// #1992 gravity: no start path skips <c>Assess</c>. Every schema initialization runner in <c>src</c> either reads the
/// host's one verdict through <c>StartupVerdictBinding</c> or delegates to the generated EF Core pass, and the
/// generated pass reads the verdict before it applies anything (before the bootstrap, the election and the lock). A new
/// runner, or a reordering of the generated pass, fails here instead of quietly migrating without a verdict.
/// </summary>
/// <docs>operations/startup/rolling-upgrades#one-verdict</docs>
[Category("Startup")]
public partial class AssessBindingSourceGuardTests {
  private static readonly string _repoRoot = _findRepoRoot();

  /// <summary>The runners that delegate to the generated EF Core pass, which reads the verdict itself.</summary>
  private static readonly HashSet<string> _delegatesToTheGeneratedPass = new(StringComparer.Ordinal) {
    "src/Whizbang.Data.EFCore.Postgres/DbContextSchemaInitializationRunner.cs",
  };

  [GeneratedRegex(@"\bclass\s+\w+[^{;]*?:[^{;]*?\bISchemaInitializationRunner\b", RegexOptions.Singleline)]
  private static partial Regex _runnerDeclaration();

  [Test]
  public async Task EverySchemaRunner_ReadsTheVerdict_OrDelegatesToThePassThatDoesAsync() {
    var runners = Directory.EnumerateFiles(Path.Combine(_repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
      .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
      .Select(f => (Path: Path.GetRelativePath(_repoRoot, f).Replace('\\', '/'), Code: File.ReadAllText(f)))
      .Where(f => _runnerDeclaration().IsMatch(f.Code))
      .ToList();

    await Assert.That(runners.Select(r => r.Path)).Contains("src/Whizbang.Data.Dapper.Postgres/DapperSchemaInitializationRunner.cs")
      .Because("the scan must recognize a runner, or it is not reading the source tree");
    var unbound = runners
      .Where(r => !_delegatesToTheGeneratedPass.Contains(r.Path) && !r.Code.Contains("StartupVerdictBinding.ReadAsync", StringComparison.Ordinal))
      .Select(r => r.Path)
      .ToList();
    await Assert.That(unbound).IsEmpty()
      .Because("a runner that does not read the host's verdict can migrate a schema newer than its binary: " + string.Join(", ", unbound));
  }

  [Test]
  public async Task TheGeneratedPass_ReadsTheVerdict_BeforeItAppliesAnythingAsync() {
    var template = await File.ReadAllTextAsync(Path.Combine(_repoRoot,
      "src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs"));
    var core = template[template.IndexOf("private static async Task _ensureWhizbangDatabaseInitializedCoreAsync(", StringComparison.Ordinal)..];

    var verdict = core.IndexOf("StartupVerdictBinding.ReadAsync", StringComparison.Ordinal);
    await Assert.That(verdict).IsGreaterThan(0);
    foreach (var applies in new[] { "SchemaBootstrapPhase.ApplyAsync", "MigratorDutyStaging.ElectAsync", "rewriteStoredFormsAsync(segmentConnectionFactory)", "_beginPassAsync(" }) {
      await Assert.That(core.IndexOf(applies, StringComparison.Ordinal)).IsGreaterThan(verdict)
        .Because($"{applies} runs only once the verdict is read");
    }
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
