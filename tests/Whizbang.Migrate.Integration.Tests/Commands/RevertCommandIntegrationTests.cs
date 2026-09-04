using Whizbang.Migrate.Commands;
using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Integration.Tests.Commands;

/// <summary>
/// Tests for the revert command against a real git repository.
/// </summary>
/// <remarks>
/// Revert is the migration's undo button, and it restores by running git reset against the
/// commit recorded before the migration began. Everything that matters about it therefore
/// requires a real repository: whether the working tree actually returns to its previous
/// state, and whether the decision file is marked so a second revert cannot rewind past that
/// commit and destroy work done since.
/// </remarks>
/// <tests>Whizbang.Migrate/Commands/RevertCommand.cs:*</tests>
[Category("Integration")]
public class RevertCommandIntegrationTests {
  private string _repoPath = null!;
  private string _decisionFilePath = null!;
  private string _originalCommit = null!;

  [Before(Test)]
  public async Task SetUpAsync() {
    _repoPath = Path.Combine(Path.GetTempPath(), $"whizbang-revert-{Guid.NewGuid():N}");
    Directory.CreateDirectory(_repoPath);

    await _runGitAsync("init");
    await _runGitAsync("config user.email \"test@test.com\"");
    await _runGitAsync("config user.name \"Test User\"");

    await File.WriteAllTextAsync(Path.Combine(_repoPath, "Handler.cs"), "public class Handler { }\n");
    await _runGitAsync("add -A");
    await _runGitAsync("commit -m initial");
    _originalCommit = (await _runGitCapturingAsync("rev-parse HEAD")).Trim();

    _decisionFilePath = DecisionFile.GetDefaultPath(Path.GetFileName(_repoPath));
  }

  [After(Test)]
  public void TearDown() {
    if (Directory.Exists(_repoPath)) { Directory.Delete(_repoPath, recursive: true); }
    var dir = Path.GetDirectoryName(_decisionFilePath);
    if (dir is not null && Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
  }

  private async Task _seedMigrationAsync() {
    var decisionFile = DecisionFile.Create(_repoPath);
    decisionFile.State.Status = MigrationStatus.InProgress;
    decisionFile.State.GitCommitBefore = _originalCommit;
    await decisionFile.SaveAsync(_decisionFilePath);
  }

  [Test]
  public async Task Execute_RestoresTheWorkingTreeToThePreMigrationCommitAsync() {
    // The whole promise of the command. A migration rewrites files in place, so an operator
    // who does not like the result needs the tree back exactly as it was -- not approximately.
    await _seedMigrationAsync();
    var handler = Path.Combine(_repoPath, "Handler.cs");
    await File.WriteAllTextAsync(handler, "public class Handler { /* migrated */ }\n");

    var result = await RevertCommand.ExecuteAsync(_repoPath);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(await File.ReadAllTextAsync(handler)).IsEqualTo("public class Handler { }\n")
      .Because("revert restores the recorded commit, so a migrated file returns to its original text");
  }

  [Test]
  public async Task Execute_RemovesFilesTheMigrationAddedAsync() {
    // Transformers create new files as well as editing existing ones. A reset alone leaves
    // those behind as untracked debris, which is why revert also cleans -- otherwise the
    // "restored" tree still contains generated endpoints and stubs.
    await _seedMigrationAsync();
    var added = Path.Combine(_repoPath, "GeneratedEndpoint.cs");
    await File.WriteAllTextAsync(added, "public class GeneratedEndpoint { }\n");

    var result = await RevertCommand.ExecuteAsync(_repoPath);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(File.Exists(added)).IsFalse()
      .Because("a file the migration introduced is part of what revert has to undo");
  }

  [Test]
  public async Task Execute_MarksTheMigrationRevertedSoASecondRunIsRefusedAsync() {
    // Running revert twice is the natural next move when something still looks wrong. The
    // second reset would rewind past the pre-migration commit and take work created since the
    // first revert with it, so the recorded status has to stop it.
    await _seedMigrationAsync();

    var first = await RevertCommand.ExecuteAsync(_repoPath);
    await Assert.That(first.Success).IsTrue();

    var second = await RevertCommand.ExecuteAsync(_repoPath);

    await Assert.That(second.Success).IsFalse()
      .Because("the decision file records the revert, and a repeat must not reset again");
  }

  [Test]
  public async Task Execute_WithDeleteRequested_RemovesTheDecisionFileAsync() {
    // Deleting the decision file is how an operator abandons a migration outright rather than
    // leaving a half-finished record that a later run would resume from.
    await _seedMigrationAsync();

    var result = await RevertCommand.ExecuteAsync(_repoPath, deleteDecisionFile: true);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(File.Exists(_decisionFilePath)).IsFalse();
  }

  [Test]
  public async Task Execute_WithAnUnknownCommit_ReportsFailureAsync() {
    // A decision file can outlive the history it names -- a rebase or a fresh clone. Reset will
    // fail, and revert must say so rather than report a restore that never happened.
    var decisionFile = DecisionFile.Create(_repoPath);
    decisionFile.State.Status = MigrationStatus.InProgress;
    decisionFile.State.GitCommitBefore = "0123456789abcdef0123456789abcdef01234567";
    await decisionFile.SaveAsync(_decisionFilePath);

    var result = await RevertCommand.ExecuteAsync(_repoPath);

    await Assert.That(result.Success).IsFalse()
      .Because("a reset that could not run is not a successful revert");
  }

  private Task<string> _runGitAsync(string arguments) => _execAsync(arguments, capture: false);
  private Task<string> _runGitCapturingAsync(string arguments) => _execAsync(arguments, capture: true);

  private async Task<string> _execAsync(string arguments, bool capture) {
    using var process = new System.Diagnostics.Process();
    process.StartInfo = new System.Diagnostics.ProcessStartInfo {
      FileName = "git",
      Arguments = arguments,
      WorkingDirectory = _repoPath,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    process.Start();
    var stdout = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0 && !capture) {
      var error = await process.StandardError.ReadToEndAsync();
      throw new InvalidOperationException($"git {arguments} failed: {error}");
    }
    return stdout;
  }
}
