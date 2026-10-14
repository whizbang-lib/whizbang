// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Containers;

namespace Whizbang.Core.Integration.Tests.Testing;

/// <summary>
/// The test infrastructure's process runner (#1366). A child process whose output is redirected blocks once it has
/// written a pipe buffer's worth (64 KB on Linux) that nobody has read; a caller that waits for it to exit before
/// reading never sees it exit. The runner reads both streams while it waits, so output of any size completes, and a
/// canceled wait kills the process instead of leaving it behind.
/// </summary>
/// <docs>contributors/testing/test-infrastructure#process-runner</docs>
[Category("Integration")]
public class ProcessRunnerTests {
  private const string SHELL = "/bin/sh";
  private const int ONE_MEGABYTE = 1024 * 1024;

  [Test]
  public async Task RunAsync_AProcessWritingFarMoreThanAPipeBufferToBothStreams_CompletesWithAllOfItAsync() {
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

    var result = await ProcessRunner.RunAsync(
      SHELL, $"-c \"head -c {ONE_MEGABYTE} /dev/zero | tr '\\\\0' a; head -c {ONE_MEGABYTE} /dev/zero | tr '\\\\0' b 1>&2\"",
      deadline.Token);

    await Assert.That(result.ExitCode).IsEqualTo(0);
    await Assert.That(result.StandardOutput.Length).IsEqualTo(ONE_MEGABYTE)
      .Because("a megabyte on each stream is sixteen pipe buffers; waiting before reading would never return");
    await Assert.That(result.StandardError.Length).IsEqualTo(ONE_MEGABYTE);
  }

  [Test]
  public async Task RunAsync_ReturnsTheProcessExitCodeAsync() {
    var result = await ProcessRunner.RunAsync(SHELL, "-c \"echo out; echo err 1>&2; exit 3\"");

    await Assert.That(result.ExitCode).IsEqualTo(3);
    await Assert.That(result.StandardOutput.Trim()).IsEqualTo("out");
    await Assert.That(result.StandardError.Trim()).IsEqualTo("err");
  }

  [Test]
  public async Task RunAsync_WhenTheWaitIsCanceled_KillsTheProcessAndThrowsAsync() {
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    await Assert.That(async () => await ProcessRunner.RunAsync(SHELL, "-c \"sleep 600\"", canceled.Token))
      .Throws<OperationCanceledException>()
      .Because("a caller's deadline ends the run, and the process it started does not outlive it");
  }

  [Test]
  public async Task RunAsync_AFileThatDoesNotExist_ThrowsAsync() {
    await Assert.That(async () => await ProcessRunner.RunAsync("/no/such/executable", ""))
      .Throws<System.ComponentModel.Win32Exception>();
  }
}
