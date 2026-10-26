// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Processes;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Integration.Tests.Processes;

/// <summary>
/// The real process runner behind <see cref="IExternalProcessRunner"/>, against a real child process: the
/// <c>dotnet</c> host that runs these tests, so nothing has to be installed. Integration because it crosses a process
/// boundary. Nothing here waits on time: each test waits only for the child to exit, and the cancellation case is
/// canceled before the wait begins.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Processes/ExternalProcessRunner.cs</code-under-test>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
[Category("Integration")]
public sealed class ExternalProcessRunnerTests {
  // The CLI sets DOTNET_HOST_PATH for the processes it runs; a runner without it has dotnet on its PATH.
  private static string _dotnet => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

  [Test]
  public async Task Run_ACleanExit_ReturnsItsExitCodeAndOutputAsync() {
    var result = await new ExternalProcessRunner().RunAsync(new ExternalProcessRequest(_dotnet, ["--version"]), CancellationToken.None);

    await Assert.That(result.Started).IsTrue();
    await Assert.That(result.ExitCode).IsEqualTo(0);
    await Assert.That(result.StandardOutput.Trim()).IsNotEmpty().Because("the version is written to standard output");
  }

  [Test]
  public async Task Run_AFailingExit_ReturnsItsExitCodeAsync() {
    var result = await new ExternalProcessRunner().RunAsync(
      new ExternalProcessRequest(_dotnet, ["whizbang-no-such-command"]), CancellationToken.None);

    await Assert.That(result.Started).IsTrue();
    await Assert.That(result.ExitCode).IsNotEqualTo(0);
  }

  [Test]
  public async Task Run_AProgramThatDoesNotExist_DidNotStart_WithTheReasonAsync() {
    var missing = Path.Combine(Path.GetTempPath(), "whizbang-no-such-program-" + Guid.NewGuid().ToString("N"));

    var result = await new ExternalProcessRunner().RunAsync(new ExternalProcessRequest(missing, ["--version"]), CancellationToken.None);

    await Assert.That(result.Started).IsFalse();
    await Assert.That(result.StandardError).Contains(missing).Because("the reason names the program that could not start");
  }

  [Test]
  public async Task Run_Canceled_StopsTheChildAndThrowsAsync() {
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    await Assert.That(() => new ExternalProcessRunner().RunAsync(new ExternalProcessRequest(_dotnet, ["--version"]), canceled.Token))
      .Throws<OperationCanceledException>();
  }
}
