// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Startup;

/// <summary>
/// Runs a duty-bound startup step as owed duty work, so a step that a non-holder skipped at its own
/// startup still runs, on whichever instance holds the role (#966, requirement 7). This is what
/// closes the rolling-deploy gap: every new instance skips the step while an old one holds the
/// duty, and each owes it; the new instance that wins the role runs it once.
/// </summary>
/// <remarks>
/// The step is deferred, not failed, while this instance's own startup pipeline is still running:
/// that run may be executing the same step, and the step's dependencies are only met once the
/// pipeline is through. Deferring rather than waiting keeps the holder's loop, and therefore its
/// lease, moving.
/// </remarks>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStepDutyWorkTests.cs</tests>
public sealed class StartupStepDutyWork : IDutyWorkHandler {
  private readonly IStartupStep _step;
  private readonly IStartupPipelineState? _state;

  /// <summary>Wraps <paramref name="step"/>.</summary>
  /// <param name="step">A step whose <see cref="StartupStepDescriptor.RequiredCapability"/> is a duty.</param>
  /// <param name="state">This instance's pipeline state, when a pipeline is registered.</param>
  public StartupStepDutyWork(IStartupStep step, IStartupPipelineState? state) {
    ArgumentNullException.ThrowIfNull(step);
    _step = step;
    _state = state;
  }

  /// <inheritdoc />
  public string Role => _step.Descriptor.RequiredCapability;

  /// <inheritdoc />
  public string WorkKey => _step.Descriptor.Name;

  /// <inheritdoc />
  public async ValueTask<DutyWorkResult> RunAsync(IDutyGrant grant, CancellationToken cancellationToken) {
    if (!_step.Descriptor.Enabled) {
      return DutyWorkResult.Done();   // a disabled step owes nothing
    }
    if (_state is { IsComplete: false }) {
      return DutyWorkResult.Deferred("this instance's startup pipeline is still running");
    }
    var report = await _step.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    return report.Outcome == StartupStepOutcome.Failed
      ? DutyWorkResult.NotDone(report.Reason ?? "the step failed")
      : DutyWorkResult.Done();
  }
}
