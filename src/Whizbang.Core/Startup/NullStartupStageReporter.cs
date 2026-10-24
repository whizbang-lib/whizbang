// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Startup;

/// <summary>
/// The stage reporter of a host that records no startup stages: it is never in a stage, and entering, skipping or
/// registering changes nothing. What a health surface composed without the worker pipeline reads, so the stage is a
/// required dependency everywhere rather than an optional one that a hand-built registration can silently omit.
/// </summary>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/NullStartupStageReporterTests.cs</tests>
public sealed class NullStartupStageReporter : IStartupStageReporter {
  /// <summary>The one instance.</summary>
  public static NullStartupStageReporter Instance { get; } = new();

  private NullStartupStageReporter() { }

  /// <inheritdoc />
  public StartupStageSnapshot? Current => null;

  /// <inheritdoc />
  public ValueTask EnterAsync(StartupStage stage, string message, CancellationToken cancellationToken = default) =>
    ValueTask.CompletedTask;

  /// <inheritdoc />
  public ValueTask SkipAsync(StartupStage stage, string reason, CancellationToken cancellationToken = default) =>
    ValueTask.CompletedTask;

  /// <inheritdoc />
  public ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
