// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// A stage reporter that records each call in order and tracks the current stage the way the real one does for a
/// transition, so a caller's decisions that read <see cref="Current"/> can be driven.
/// </summary>
internal sealed class RecordingStageReporter : IStartupStageReporter {
  private readonly List<string> _calls = [];

  /// <summary>Every call, in order: <c>Enter Stage: message</c>, <c>Skip Stage: reason</c>, <c>Registered</c>.</summary>
  public IReadOnlyList<string> Calls => _calls;

  /// <summary>The token each call was given, in order.</summary>
  public List<CancellationToken> Tokens { get; } = [];

  public StartupStageSnapshot? Current { get; set; }

  public ValueTask EnterAsync(StartupStage stage, string message, CancellationToken cancellationToken = default) {
    _calls.Add($"Enter {stage}: {message}");
    Tokens.Add(cancellationToken);
    Current = new StartupStageSnapshot(stage, message, Skipped: false, DateTimeOffset.UnixEpoch);
    return ValueTask.CompletedTask;
  }

  public ValueTask SkipAsync(StartupStage stage, string reason, CancellationToken cancellationToken = default) {
    _calls.Add($"Skip {stage}: {reason}");
    Tokens.Add(cancellationToken);
    Current = new StartupStageSnapshot(stage, "skipped: " + reason, Skipped: true, DateTimeOffset.UnixEpoch);
    return ValueTask.CompletedTask;
  }

  public ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default) {
    _calls.Add("Registered");
    Tokens.Add(cancellationToken);
    return ValueTask.CompletedTask;
  }
}
