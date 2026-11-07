// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Whizbang.Core.RunControl;

/// <summary>The one exit path (#2126): not implemented yet.</summary>
/// <docs>operations/startup/instance-exits</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/RunControl/WhizbangInstanceExitTests.cs</tests>
public sealed class WhizbangInstanceExit : IWhizbangInstanceExit {
  /// <summary>The page that explains every exit, linked from each exit's log entry.</summary>
  public const string DOCS_LINK = "https://whizbang-lib.github.io/docs/v1.0.0/operations/startup/instance-exits";

  /// <summary>The event id of the one entry an exit writes.</summary>
  public static readonly EventId ExitEventId = new(1, "InstanceExit");

  /// <summary>Creates the path.</summary>
  /// <param name="services">The host's services.</param>
  /// <param name="runControl">The run-control path.</param>
  /// <param name="exitCode">Sets the process exit code.</param>
  /// <param name="timeProvider">The clock.</param>
  /// <param name="logger">The logger.</param>
  public WhizbangInstanceExit(
      IServiceProvider services, IWhizbangRunController runControl, IProcessExitCode exitCode, TimeProvider? timeProvider,
      ILogger<WhizbangInstanceExit> logger) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(runControl);
    ArgumentNullException.ThrowIfNull(exitCode);
    ArgumentNullException.ThrowIfNull(logger);
    _ = timeProvider;
  }

  /// <inheritdoc />
  public ExitCause? Cause => throw new NotImplementedException();

  /// <inheritdoc />
  public InstanceExitReport? Report => throw new NotImplementedException();

  /// <inheritdoc />
  public Task Completion => throw new NotImplementedException();

  /// <inheritdoc />
  public void RequestExit(InstanceExitRequest request) => throw new NotImplementedException();

  /// <inheritdoc />
  public Task ExitAsync(InstanceExitRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();

  /// <inheritdoc />
  public void OnHostStarting() => throw new NotImplementedException();

  /// <inheritdoc />
  public Task OnHostStoppingAsync(CancellationToken stopToken) => throw new NotImplementedException();
}

/// <summary>The default <see cref="IProcessExitCode"/>: not implemented yet.</summary>
/// <docs>operations/startup/instance-exits#exit-codes</docs>
public sealed class ProcessExitCode : IProcessExitCode {
  /// <summary>The one instance.</summary>
  public static ProcessExitCode Instance { get; } = new();

  private ProcessExitCode() { }

  /// <inheritdoc />
  public void SetExitCode(int exitCode) => throw new NotImplementedException();
}
