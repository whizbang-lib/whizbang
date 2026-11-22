// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A hang detector for an end-to-end test made of phases that each wait on a signal (a host started, a stage reached, an
/// event applied). A phase that does not finish is reported by name, with what <c>describe</c> says about where it
/// stopped, instead of running into the test's own timeout with no word of where it hung.
/// </summary>
/// <param name="time">The clock the bound runs on.</param>
internal sealed class StartupPhases(TimeProvider time) {
  /// <summary>How long a phase may take before it is reported as hung.</summary>
  public static readonly TimeSpan PhaseBound = TimeSpan.FromSeconds(60);

  /// <summary>Runs one phase, bounded, so a hang names where it happened.</summary>
  /// <param name="phase">The phase's name, for the report.</param>
  /// <param name="work">The phase.</param>
  /// <param name="describe">Where the phase stopped, for the report: the startup stage and steps, the errors logged.</param>
  /// <param name="bound">The phase's own bound, when not <see cref="PhaseBound"/>.</param>
  /// <returns>A task that completes when the phase does.</returns>
  /// <exception cref="TimeoutException">The phase did not finish within its bound.</exception>
  public async Task RunAsync(string phase, Func<Task> work, Func<string>? describe = null, TimeSpan? bound = null) {
    var limit = bound ?? PhaseBound;
    try {
      await work().WaitAsync(limit, time);
    } catch (TimeoutException ex) {
      throw new TimeoutException($"Phase '{phase}' did not finish within {limit.TotalSeconds}s. {describe?.Invoke()}", ex);
    }
  }
}
