using System.Diagnostics;
using Whizbang.Migrate.Git;
using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Coverage-round tests for GitOperations branches not exercised by GitOperationsTests: a
/// remote URL that carries no repo-name segment to extract, and a process launch that fails
/// before git ever runs.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/GitOperations.cs:*</tests>
public class GitOperationsCoverageTests {

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

  // A configured remote whose URL ends at a bare separator (no name after it) carries no repo
  // name to extract. If the empty extraction result were used as-is instead of falling back,
  // every journal entry and progress report for the migration would be filed under a blank
  // project name that an operator could never find again.
  [Test]
  public async Task DeriveProjectNameAsync_FallsBackToDirectoryName_WhenRemoteUrlHasNoNameSegmentAsync() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"git-ops-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempPath);

    try {
      await _runGitAsync(tempPath, "init");
      await _runGitAsync(tempPath, "remote add origin git@example.com:");

      // Act
      var projectName = await GitOperations.DeriveProjectNameAsync(tempPath);

      // Assert
      await Assert.That(projectName).IsEqualTo(Path.GetFileName(tempPath))
        .Because("a remote URL with nothing after its final separator names no repository, so "
               + "derivation must fall back to the directory name rather than an empty string");
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  // Launching git can throw before the process ever starts (a working directory that vanished
  // between the caller resolving it and the call running). If that exception escaped
  // _runGitCommandAsync uncaught, one bad path would crash the whole migration CLI instead of
  // reporting "not a repository" for that one check.
  [Test]
  public async Task IsGitRepositoryAsync_ReturnsFalse_WhenWorkingDirectoryDoesNotExistAsync() {
    // Arrange - deliberately not created, so Process.Start fails to launch into it
    var missingPath = Path.Combine(Path.GetTempPath(), $"git-ops-missing-{Guid.NewGuid():N}");

    // Act
    var isRepo = await GitOperations.IsGitRepositoryAsync(missingPath);

    // Assert
    await Assert.That(isRepo).IsFalse()
      .Because("a failed process launch must be swallowed into an unsuccessful result, not "
             + "thrown out of the git operation");
  }
}
