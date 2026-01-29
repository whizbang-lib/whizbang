using System.Diagnostics;

namespace Whizbang.Migrate.Wizard;

/// <summary>
/// Handles git operations for migration state tracking and revert functionality.
/// </summary>
/// <docs>migrate-from-marten-wolverine/cli-wizard</docs>
public sealed class GitOperations {
  /// <summary>
  /// Gets the current HEAD commit hash.
  /// </summary>
  /// <param name="workingDirectory">The git repository working directory.</param>
  /// <returns>The 40-character commit hash, or null if not a git repo.</returns>
  public static async Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken ct = default) {
    if (!await IsGitRepositoryAsync(workingDirectory, ct)) {
      return null;
    }

    var result = await _runGitCommandAsync(workingDirectory, "rev-parse HEAD", ct);
    return result.Success ? result.Output?.Trim() : null;
  }

  /// <summary>
  /// Checks if the directory is inside a git repository.
  /// </summary>
  public static async Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken ct = default) {
    var result = await _runGitCommandAsync(workingDirectory, "rev-parse --is-inside-work-tree", ct);
    return result.Success && result.Output?.Trim() == "true";
  }

  /// <summary>
  /// Checks if there are uncommitted changes in the working tree.
  /// </summary>
  public static async Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken ct = default) {
    var result = await _runGitCommandAsync(workingDirectory, "status --porcelain", ct);
    return result.Success && !string.IsNullOrWhiteSpace(result.Output);
  }

  /// <summary>
  /// Gets the remote origin URL if available.
  /// </summary>
  public static async Task<string?> GetRemoteOriginUrlAsync(string workingDirectory, CancellationToken ct = default) {
    var result = await _runGitCommandAsync(workingDirectory, "remote get-url origin", ct);
    return result.Success ? result.Output?.Trim() : null;
  }

  /// <summary>
  /// Resets the working directory to a specific commit.
  /// </summary>
  /// <param name="workingDirectory">The git repository working directory.</param>
  /// <param name="commitHash">The commit hash to reset to.</param>
  /// <param name="hard">If true, performs a hard reset (discards changes).</param>
  public static async Task<bool> ResetToCommitAsync(
      string workingDirectory,
      string commitHash,
      bool hard = true,
      CancellationToken ct = default) {
    var resetType = hard ? "--hard" : "--soft";
    var result = await _runGitCommandAsync(workingDirectory, $"reset {resetType} {commitHash}", ct);
    return result.Success;
  }

  /// <summary>
  /// Cleans untracked files from the working directory.
  /// </summary>
  /// <param name="workingDirectory">The git repository working directory.</param>
  /// <param name="directories">If true, also removes untracked directories.</param>
  /// <param name="force">If true, forces the clean operation.</param>
  public static async Task<bool> CleanUntrackedFilesAsync(
      string workingDirectory,
      bool directories = true,
      bool force = true,
      CancellationToken ct = default) {
    var flags = "";
    if (force) {
      flags += "f";
    }
    if (directories) {
      flags += "d";
    }

    var result = await _runGitCommandAsync(workingDirectory, $"clean -{flags}", ct);
    return result.Success;
  }

  /// <summary>
  /// Extracts the project name from a path.
  /// </summary>
  public static string GetProjectName(string projectPath) {
    var trimmed = projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return Path.GetFileName(trimmed) ?? "unknown";
  }

  /// <summary>
  /// Derives a project name from git remote URL or directory name.
  /// </summary>
  public static async Task<string> DeriveProjectNameAsync(string workingDirectory, CancellationToken ct = default) {
    // Try to get project name from git remote
    var remoteUrl = await GetRemoteOriginUrlAsync(workingDirectory, ct);
    if (!string.IsNullOrEmpty(remoteUrl)) {
      var name = _extractRepoNameFromUrl(remoteUrl);
      if (!string.IsNullOrEmpty(name)) {
        return name;
      }
    }

    // Fall back to directory name
    return GetProjectName(workingDirectory);
  }

  private static string? _extractRepoNameFromUrl(string url) {
    // Handle SSH URLs: git@github.com:user/repo.git
    // Handle HTTPS URLs: https://github.com/user/repo.git
    var trimmed = url.TrimEnd('/');

    // Remove .git suffix if present
    if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) {
      trimmed = trimmed[..^4];
    }

    // Get the last path segment
    var lastSlash = trimmed.LastIndexOfAny(['/', ':']);
    if (lastSlash >= 0 && lastSlash < trimmed.Length - 1) {
      return trimmed[(lastSlash + 1)..];
    }

    return null;
  }

  private static async Task<GitResult> _runGitCommandAsync(
      string workingDirectory,
      string arguments,
      CancellationToken ct) {
    try {
      using var process = new Process {
        StartInfo = new ProcessStartInfo {
          FileName = "git",
          Arguments = arguments,
          WorkingDirectory = workingDirectory,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
          CreateNoWindow = true
        }
      };

      process.Start();

      var output = await process.StandardOutput.ReadToEndAsync(ct);
      var error = await process.StandardError.ReadToEndAsync(ct);

      await process.WaitForExitAsync(ct);

      return new GitResult(
          process.ExitCode == 0,
          output,
          error);
    } catch {
      return new GitResult(false, null, null);
    }
  }

  private sealed record GitResult(bool Success, string? Output, string? Error);
}
