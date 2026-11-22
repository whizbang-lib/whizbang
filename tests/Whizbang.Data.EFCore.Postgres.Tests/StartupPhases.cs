// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A hang detector for an end-to-end test made of phases that each wait on a signal (a host started, a stage reached, an
/// event applied). Each phase waits on its own signal for as long as it takes: how long a phase takes says how busy the
/// machine is, not whether startup is wrong (#2508), so no phase has a bound of its own. There is one deadline for the
/// whole test, started by its first phase and just inside the test's own timeout, and a test that runs past it fails
/// naming the phase it stopped in, with what <c>describe</c> says about where, instead of running into the test's
/// timeout with no word of where it hung.
/// </summary>
/// <param name="time">The clock the deadline runs on.</param>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupPhasesTests.cs</tests>
internal sealed class StartupPhases(TimeProvider time) {
  /// <summary>
  /// How long the whole test may take before the phase it is in is reported as hung: just inside the test's own
  /// timeout, so the report comes before the test runner's.
  /// </summary>
  public static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(280);

  private Task? _deadline;

  /// <summary>Runs one phase until its signal, so a hang names where it happened.</summary>
  /// <param name="phase">The phase's name, for the report.</param>
  /// <param name="work">The phase.</param>
  /// <param name="describe">Where the phase stopped, for the report: the startup stage and steps, the errors logged.</param>
  /// <returns>A task that completes when the phase does, as the phase did.</returns>
  /// <exception cref="TimeoutException">The test ran past its deadline in this phase.</exception>
  public async Task RunAsync(string phase, Func<Task> work, Func<string>? describe = null) {
    _deadline ??= Task.Delay(TestDeadline, time);
    var running = work();
    if (await Task.WhenAny(running, _deadline) != running) {
      throw new TimeoutException(
        $"Phase '{phase}' had not finished when the test reached its {TestDeadline.TotalSeconds}s deadline. {describe?.Invoke()}");
    }
    // The phase's own outcome, a fault or a cancellation included, as it is.
    await running;
  }
}
