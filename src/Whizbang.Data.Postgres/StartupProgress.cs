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
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigrationBatchProgressTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DeclaredKeySpellingPassTests.cs</tests>
public static class StartupProgress {
  /// <summary>Where progress goes in this flow of startup: the migrator duty the schema initializer holds (#2165).</summary>
  private static readonly AsyncLocal<Action?> _sink = new();

  /// <summary>How often a long phase says how far along it is: every 15 seconds.</summary>
  public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(15);

  /// <summary>
  /// Routes the progress this flow of startup reports to <paramref name="noteProgress"/> until the returned scope is
  /// disposed, then back to wherever it went before (#2165). The schema initializer points it at the migrator duty it
  /// holds, so every committed step, phase announcement and counted batch keeps the role, and a phase reports progress
  /// without knowing who holds it.
  /// </summary>
  /// <param name="noteProgress">What one unit of progress does.</param>
  /// <returns>The scope; dispose it when the duty is no longer held.</returns>
  /// <remarks>Called from the initializer's own body, not from an async method, so the route reaches every phase it
  /// then awaits.</remarks>
  /// <docs>operations/infrastructure/migrations#a-stuck-migrator-loses-its-role</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigratorStallLimitTests.cs:ReportTo_RoutesProgressUntilItIsDisposed_ThenToWhatWasThereBeforeAsync</tests>
  public static IDisposable ReportTo(Action noteProgress) {
    ArgumentNullException.ThrowIfNull(noteProgress);
    var previous = _sink.Value;
    _sink.Value = noteProgress;
    return new Route(previous);
  }

  /// <summary>One unit of startup work was done: a step committed, a phase started, a batch or row group counted.</summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigratorStallLimitTests.cs:ProgressFromEveryReportingPoint_IsNotedAsync</tests>
  public static void Note() => _sink.Value?.Invoke();

  /// <summary>An elapsed time as the progress lines give it: whole seconds, or minutes and seconds.</summary>
  /// <param name="elapsed">The time.</param>
  /// <returns>For example <c>42s</c> or <c>6m 05s</c>.</returns>
  public static string Describe(TimeSpan elapsed) => elapsed < TimeSpan.FromMinutes(1)
    ? string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalSeconds}s")
    : string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s");

  /// <summary>Restores the route that was there before, once.</summary>
  private sealed class Route(Action? previous) : IDisposable {
    public void Dispose() => _sink.Value = previous;
  }
}

/// <summary>An elapsed time as a progress line says it (<see cref="StartupProgress.Describe"/>), formatted only when written.</summary>
internal readonly struct ProgressElapsed(TimeSpan elapsed) {
  /// <inheritdoc/>
  public override string ToString() => StartupProgress.Describe(elapsed);
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
    StartupProgress.Note();
    var now = _time.GetTimestamp();
    if (_time.GetElapsedTime(_lastReported, now) < StartupProgress.Cadence) {
      return false;
    }
    _lastReported = now;
    return true;
  }
}
