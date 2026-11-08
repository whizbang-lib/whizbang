// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The cadence a long startup phase or migration step reports its progress on (#2128): one line at Information each
/// <see cref="Cadence"/>, measured on the clock the phase was given, so an operator can tell a phase that is working
/// from one that is stuck.
/// </summary>
/// <docs>operations/infrastructure/migrations#progress-of-long-startup-work</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/StartupProgressTests.cs</tests>
public static class StartupProgress {
  /// <summary>How often a long phase says how far along it is: every 15 seconds.</summary>
  public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(15);

  /// <summary>An elapsed time as the progress lines give it: whole seconds, or minutes and seconds.</summary>
  /// <param name="elapsed">The time.</param>
  /// <returns>For example <c>42s</c> or <c>6m 05s</c>.</returns>
  public static string Describe(TimeSpan elapsed) => elapsed.ToString("c", CultureInfo.InvariantCulture);
}

/// <summary>When the next progress line of one phase is due.</summary>
internal sealed class ProgressClock {
  private readonly TimeProvider _time;
  private readonly long _started;
  private long _lastReported;

  /// <summary>Starts the clock: the first line is due one cadence from now.</summary>
  public ProgressClock(TimeProvider time) {
    _time = time;
    _started = time.GetTimestamp();
    _lastReported = _started;
  }

  /// <summary>How long the phase has run.</summary>
  public TimeSpan Elapsed => _time.GetElapsedTime(_started);

  /// <summary>Whether a line is due; true at most once per cadence.</summary>
  public bool Due() {
    var now = _time.GetTimestamp();
    if (_time.GetElapsedTime(_lastReported, now) < StartupProgress.Cadence) {
      return false;
    }
    _lastReported = now;
    return true;
  }
}
