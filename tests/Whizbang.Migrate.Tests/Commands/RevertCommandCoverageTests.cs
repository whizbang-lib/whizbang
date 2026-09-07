// CA1416: these tests deliberately use Unix file modes to make a write fail, which is the only
// way to drive the revert command's "reset succeeded but cleanup/save did not" branches without
// mocking the file system. CI runs on ubuntu-latest and development here is macOS, so the calls
// are always supported where they execute; the analyzer cannot see that from the call site.
#pragma warning disable CA1416 // Validate platform compatibility

using System.Diagnostics;
using Whizbang.Migrate.Commands;
using Whizbang.Migrate.Git;
using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Commands;

/// <summary>
/// Coverage-round tests for RevertCommand branches not exercised by RevertCommandTests: a
/// decision file whose contents cannot be parsed, a successful reset paired with a failed
/// cleanup, and the two non-fatal tidy-up steps (delete or re-save the decision file) that
/// run after a revert has already succeeded.
/// </summary>
/// <tests>Whizbang.Migrate/Commands/RevertCommand.cs:*</tests>
public class RevertCommandCoverageTests {

  private static async Task _runGitAsync(string workingDirectory, string arguments) {
    using var process = new Process {
      StartInfo = new ProcessStartInfo {
        FileName = GitExecutable.PathOrThrow,
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      }
    };
    process.Start();
    await process.StandardOutput.ReadToEndAsync();
    await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
  }

  // Sets up a real git repository with a single commit so RevertCommand's git reset/clean
  // calls have a genuine repository to operate on, rather than failing at the
  // "is this a git repository" gate the way the pre-existing tests do.
  private static async Task<string> _initGitRepoWithCommitAsync(string workingDirectory) {
    await _runGitAsync(workingDirectory, "init");
    await File.WriteAllTextAsync(Path.Combine(workingDirectory, "keep.txt"), "keep");
    await _runGitAsync(workingDirectory, "add -A");
    await _runGitAsync(workingDirectory, "-c user.email=test@example.com -c user.name=Test commit -m initial");
    var hash = await GitOperations.GetCurrentCommitHashAsync(workingDirectory);
    return hash ?? throw new InvalidOperationException("Test setup failed: git commit did not produce a HEAD commit.");
  }

  // A corrupt decision file that got swallowed as "nothing to revert" would leave an operator
  // believing the pre-migration commit was restored, when the revert never ran at all.
  [Test]
  public async Task Execute_ReturnsError_WhenDecisionFileContentsAreCorruptAsync() {
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    var customDecisionPath = Path.Combine(Path.GetTempPath(), $"decisions-{Guid.NewGuid()}.json");
    Directory.CreateDirectory(tempPath);

    try {
      await File.WriteAllTextAsync(customDecisionPath, "{ this is not valid json");

      var result = await RevertCommand.ExecuteAsync(tempPath, decisionFilePath: customDecisionPath);

      await Assert.That(result.Success).IsFalse()
        .Because("a decision file that cannot be parsed carries no trustworthy git commit to revert to");
      await Assert.That(result.ErrorMessage).Contains("Failed to load decision file")
        .Because("the operator needs to learn the revert did not run, and why, rather than see a silent no-op");
    } finally {
      Directory.Delete(tempPath, recursive: true);
      if (File.Exists(customDecisionPath)) {
        File.Delete(customDecisionPath);
      }
    }
  }

  // If a successful reset with a failed cleanup were reported as a plain failure (or a plain
  // success), an operator could not tell that the git history WAS restored but leftover
  // generated files may remain on disk -- the warning is what tells them to go check.
  [Test]
  public async Task Execute_ReturnsWarning_WhenCleanFailsAfterSuccessfulResetAsync() {
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);
    var lockedDir = Path.Combine(tempPath, "locked");
    var projectName = Path.GetFileName(tempPath);
    var decisionFilePath = DecisionFile.GetDefaultPath(projectName);

    try {
      var commitHash = await _initGitRepoWithCommitAsync(tempPath);

      Directory.CreateDirectory(lockedDir);
      await File.WriteAllTextAsync(Path.Combine(lockedDir, "untracked.txt"), "junk");
      // Removing write permission on the directory stops git from unlinking the file inside
      // it, so `git clean -fd` fails even though the preceding `git reset --hard` succeeds.
      File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

      var decisionFile = DecisionFile.Create(tempPath);
      decisionFile.State.Status = MigrationStatus.InProgress;
      decisionFile.State.GitCommitBefore = commitHash;
      await decisionFile.SaveAsync(decisionFilePath);

      var result = await RevertCommand.ExecuteAsync(tempPath);

      await Assert.That(result.Success).IsTrue()
        .Because("the git reset -- the part that actually undoes the migration -- succeeded");
      await Assert.That(result.GitCommitReverted).IsEqualTo(commitHash)
        .Because("the reverted commit is what the operator checks to confirm which state they landed on");
      await Assert.That(result.WarningMessage).Contains("failed to clean untracked files")
        .Because("leftover generated files need a human to notice; a bare success would hide them");
    } finally {
      if (Directory.Exists(lockedDir)) {
        File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
      }
      Directory.Delete(tempPath, recursive: true);
      var decisionDir = Path.GetDirectoryName(decisionFilePath);
      if (Directory.Exists(decisionDir)) {
        Directory.Delete(decisionDir, recursive: true);
      }
    }
  }

  // The decision-file delete is tidy-up after a revert that already succeeded; if a failure
  // here bubbled up as an overall failure, an operator would redo manual recovery steps for a
  // revert that had, in fact, already worked.
  [Test]
  public async Task Execute_DeletingDecisionFileFails_StillReportsOverallSuccessAsync() {
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    var decisionsDir = Path.Combine(Path.GetTempPath(), $"decisions-locked-{Guid.NewGuid()}");
    var decisionFilePath = Path.Combine(decisionsDir, "decisions.json");
    Directory.CreateDirectory(tempPath);
    Directory.CreateDirectory(decisionsDir);

    try {
      var commitHash = await _initGitRepoWithCommitAsync(tempPath);

      var decisionFile = DecisionFile.Create(tempPath);
      decisionFile.State.Status = MigrationStatus.InProgress;
      decisionFile.State.GitCommitBefore = commitHash;
      await decisionFile.SaveAsync(decisionFilePath);

      // Removing write on the containing directory stops File.Delete from unlinking the
      // entry, without blocking the reads RevertCommand still needs to do to get there.
      File.SetUnixFileMode(decisionsDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

      var result = await RevertCommand.ExecuteAsync(
          tempPath,
          decisionFilePath: decisionFilePath,
          deleteDecisionFile: true);

      await Assert.That(result.Success).IsTrue()
        .Because("the revert itself completed; failing to delete the now-stale decision file afterwards is best-effort tidy-up");
      await Assert.That(result.DecisionFileDeleted).IsTrue()
        .Because("this reports what deletion was requested, so callers know it was asked for even though it did not take");
      await Assert.That(File.Exists(decisionFilePath)).IsTrue()
        .Because("the delete attempt must have actually failed for this branch to be exercised, not silently succeeded");
    } finally {
      File.SetUnixFileMode(decisionsDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
      Directory.Delete(tempPath, recursive: true);
      Directory.Delete(decisionsDir, recursive: true);
    }
  }

  // Same tidy-up concern on the other branch: re-saving the decision file with
  // Status = Reverted instead of deleting it. A save failure here must not mask a completed
  // revert as a reported failure either.
  [Test]
  public async Task Execute_SavingUpdatedDecisionFileFails_StillReportsOverallSuccessAsync() {
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    var projectName = Path.GetFileName(tempPath);
    var decisionFilePath = DecisionFile.GetDefaultPath(projectName);
    Directory.CreateDirectory(tempPath);

    try {
      var commitHash = await _initGitRepoWithCommitAsync(tempPath);

      var decisionFile = DecisionFile.Create(tempPath);
      decisionFile.State.Status = MigrationStatus.InProgress;
      decisionFile.State.GitCommitBefore = commitHash;
      await decisionFile.SaveAsync(decisionFilePath);
      var contentBeforeRevert = await File.ReadAllTextAsync(decisionFilePath);

      // Removing write on the file itself (not its directory) blocks the re-save without
      // blocking the reads RevertCommand still needs to do before it gets there.
      File.SetUnixFileMode(decisionFilePath, UnixFileMode.UserRead);

      var result = await RevertCommand.ExecuteAsync(tempPath);

      await Assert.That(result.Success).IsTrue()
        .Because("the revert itself completed; failing to persist Status = Reverted afterwards is best-effort tidy-up");
      await Assert.That(result.DecisionFileDeleted).IsFalse()
        .Because("this call never asked for deletion");
      var contentAfterRevert = await File.ReadAllTextAsync(decisionFilePath);
      await Assert.That(contentAfterRevert).IsEqualTo(contentBeforeRevert)
        .Because("the save attempt failed outright, so the file on disk must be untouched rather than partially written");
    } finally {
      File.SetUnixFileMode(decisionFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
      Directory.Delete(tempPath, recursive: true);
      var decisionDir = Path.GetDirectoryName(decisionFilePath);
      if (Directory.Exists(decisionDir)) {
        Directory.Delete(decisionDir, recursive: true);
      }
    }
  }
}
#pragma warning restore CA1416
