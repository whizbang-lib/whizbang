using System.Diagnostics;
using Whizbang.Migrate.Git;

namespace Whizbang.Migrate.Tests.Git;

/// <summary>
/// Coverage-round tests for <see cref="GitWorktreeService"/> branches not exercised elsewhere:
/// the best-effort branch-name lookup swallowing a failure before removal still proceeds, and
/// the main-repository-path fallback used when a repository's git directory has been relocated
/// somewhere that is not literally named <c>.git</c>.
/// </summary>
/// <tests>Whizbang.Migrate/Git/GitWorktreeService.cs:50,182,183,206</tests>
public class GitWorktreeServiceCoverageTests {

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

  // deleteBranch's branch-name lookup is best-effort: it is not the reason the caller wanted a
  // removal, so a failure there must be swallowed rather than pre-empt the rest of the method. If
  // the empty catch were removed, calling RemoveWorktreeAsync(deleteBranch: true) against a path
  // that cannot answer "what branch is this" would fail for the wrong reason and never even
  // attempt the removal itself.
  [Test]
  public async Task RemoveWorktreeAsync_DeleteBranchOnANonGitDirectory_SwallowsTheLookupFailureThenThrowsAsync() {
    var notAGitRepo = Path.Combine(Path.GetTempPath(), $"whizbang-migrate-notrepo-{Guid.NewGuid():N}");
    Directory.CreateDirectory(notAGitRepo);

    try {
      var service = new GitWorktreeService();

      // The branch-name lookup fails first (swallowed by the empty catch), then the subsequent
      // "find the main repository" step fails too, since this was never a git repository at all
      // -- that second, unrelated failure is what surfaces here.
      await Assert.That(async () => await service.RemoveWorktreeAsync(notAGitRepo, deleteBranch: true))
        .ThrowsExactly<InvalidOperationException>()
        .Because("a directory that was never a git repository cannot be resolved to a main repository path either");
    } finally {
      Directory.Delete(notAGitRepo, recursive: true);
    }
  }

  // _getMainRepoPathAsync assumes a repository's git directory is literally named ".git", and
  // falls back to the parent of whatever git-common-dir reports otherwise. Relocating a
  // repository's git directory via 'git init --separate-git-dir' (a supported, real git feature)
  // breaks that assumption: the fallback lands on the relocated directory's own parent, not the
  // repository's working-tree root, so the worktree-remove command that follows targets the
  // wrong directory and fails. If this regressed to throw before reaching that fallback, or to
  // silently "succeed" against the wrong directory, either way an operator would be left with a
  // worktree that git still thinks exists.
  [Test]
  public async Task RemoveWorktreeAsync_RepositoryWithARelocatedGitDirectory_ResolvesTheWrongRepoPathAndThrowsAsync() {
    var mainRepoDir = Path.Combine(Path.GetTempPath(), $"whizbang-migrate-main-{Guid.NewGuid():N}");
    var gitDirContainer = Path.Combine(Path.GetTempPath(), $"whizbang-migrate-gitdir-{Guid.NewGuid():N}");
    var relocatedGitDir = Path.Combine(gitDirContainer, "vcs-store");
    var linkedWorktreeDir = Path.Combine(Path.GetTempPath(), $"whizbang-migrate-worktree-{Guid.NewGuid():N}");
    Directory.CreateDirectory(mainRepoDir);
    Directory.CreateDirectory(gitDirContainer);

    try {
      await _runGitAsync(mainRepoDir, $"init --separate-git-dir=\"{relocatedGitDir}\"");
      await _runGitAsync(mainRepoDir, "-c user.email=test@example.com -c user.name=Test commit --allow-empty -m initial");
      await _runGitAsync(mainRepoDir, $"worktree add \"{linkedWorktreeDir}\"");

      // Sanity checks on setup: if either of these is false, the scenario below never actually
      // exercises the relocated-git-directory fallback, and the exception assertion would pass
      // for the wrong reason.
      await Assert.That(Directory.Exists(relocatedGitDir)).IsTrue()
        .Because("git init --separate-git-dir must have created the relocated git directory");
      await Assert.That(Directory.Exists(linkedWorktreeDir)).IsTrue()
        .Because("git worktree add must have created the linked worktree for this scenario to be meaningful");

      var service = new GitWorktreeService();

      await Assert.That(async () => await service.RemoveWorktreeAsync(linkedWorktreeDir, deleteBranch: false))
        .ThrowsExactly<InvalidOperationException>()
        .Because("the resolved main-repo path is the relocated git directory's container, not the repository root, so 'git worktree remove' fails there");
    } finally {
      if (Directory.Exists(linkedWorktreeDir)) {
        Directory.Delete(linkedWorktreeDir, recursive: true);
      }
      Directory.Delete(mainRepoDir, recursive: true);
      Directory.Delete(gitDirContainer, recursive: true);
    }
  }
}
