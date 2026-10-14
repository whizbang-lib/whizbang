// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Whizbang.Testing.Containers;

/// <summary>
/// Runs a child process to completion and returns its exit code and output. Both output streams are read while the
/// process runs: a child whose redirected output is not read blocks once it has written a pipe buffer's worth (64 KB on
/// Linux), so a caller that waits for exit before reading waits forever. A canceled wait kills the process tree.
/// </summary>
/// <docs>contributors/testing/test-infrastructure#process-runner</docs>
/// <tests>tests/Whizbang.Core.Integration.Tests/Testing/ProcessRunnerTests.cs:RunAsync_AProcessWritingFarMoreThanAPipeBufferToBothStreams_CompletesWithAllOfItAsync</tests>
/// <tests>tests/Whizbang.Core.Integration.Tests/Testing/ProcessRunnerTests.cs:RunAsync_WhenTheWaitIsCanceled_KillsTheProcessAndThrowsAsync</tests>
public static class ProcessRunner {
  /// <summary>How a process ended.</summary>
  /// <param name="ExitCode">Its exit code.</param>
  /// <param name="StandardOutput">Everything it wrote to standard output.</param>
  /// <param name="StandardError">Everything it wrote to standard error.</param>
  public sealed record Result(int ExitCode, string StandardOutput, string StandardError);

  /// <summary>Runs <paramref name="fileName"/> with <paramref name="arguments"/> and waits for it to exit.</summary>
  /// <param name="fileName">An absolute path to the executable.</param>
  /// <param name="arguments">The command line arguments.</param>
  /// <param name="cancellationToken">Ends the wait; the process is then killed.</param>
  /// <returns>The exit code and both streams' output.</returns>
  /// <exception cref="OperationCanceledException">The wait was canceled; the process has been killed.</exception>
  public static async Task<Result> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default) {
    using var process = new Process {
      StartInfo = new ProcessStartInfo {
        FileName = fileName,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      }
    };
    process.Start();
    // Both readers start before the wait, so neither pipe can fill while the process is still writing.
    var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
    var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
    try {
      await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      _kill(process);
      throw;
    }
    return new Result(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
  }

  private static void _kill(Process process) {
    try {
      process.Kill(entireProcessTree: true);
    } catch (InvalidOperationException) {
      // It exited between the cancellation and the kill: nothing is left running.
    }
  }
}
