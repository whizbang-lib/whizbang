// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Startup;

/// <summary>
/// The stage an instance is in, from the moment its host starts to the moment it stops (#1636). Each value is a fixed
/// number recorded on the instance's row (<c>wh_service_instances.startup_stage</c>), so a query can compare it
/// (<c>startup_stage &gt;= 60</c>) and a stored value means the same thing in every release.
/// </summary>
/// <remarks>
/// <para>
/// The numbers are explicit and leave gaps, so a stage added later takes a number between two existing ones and no
/// stored value changes its meaning. They are never list positions: the <c>X of 7</c> an instance logs is the stage's
/// position in <see cref="StartupStages.Startup"/>, computed, not stored.
/// </para>
/// <para>
/// The first seven are startup stages, logged as <c>Stage X of 7: &lt;name&gt; - &lt;message&gt;</c>.
/// <see cref="Draining"/> and <see cref="Stopped"/> are shutdown stages, logged as
/// <c>Shutdown stage: &lt;name&gt; - &lt;message&gt;</c>. Only <see cref="AcceptingCommands"/> and
/// <see cref="Running"/> make an instance eligible for work.
/// </para>
/// </remarks>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStagesTests.cs</tests>
public enum StartupStage : short {
  /// <summary>Checks and preparation before anything is elected: connectivity, the schema, the bootstrap.</summary>
  Preflight = 10,

  /// <summary>The vote for the instance that migrates the schema. Skipped when no election can be held.</summary>
  Elect = 20,

  /// <summary>The schema pass. Skipped when the schema is already current.</summary>
  Migrate = 30,

  /// <summary>Statistics for new indexes and the managed-object reconcile, after the pass committed.</summary>
  Converge = 40,

  /// <summary>Partition recompute and the work the fleet coordinates, before the schema-ready gate opens.</summary>
  Coordinate = 50,

  /// <summary>The write side is live: commands are accepted while the read models catch up. Eligible for work.</summary>
  AcceptingCommands = 60,

  /// <summary>Fully operational: reads and writes are served. Eligible for work.</summary>
  Running = 70,

  /// <summary>The host is stopping: in-flight work finishes, no new work is taken. Not eligible for work.</summary>
  Draining = 80,

  /// <summary>The host has stopped.</summary>
  Stopped = 90,
}

/// <summary>
/// What each <see cref="StartupStage"/> is called, where it sits in the startup list, which lifecycle phase it records
/// on the instance's row, and the line an instance logs when it enters it.
/// </summary>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStagesTests.cs</tests>
public static class StartupStages {
  private static readonly StartupStage[] _startup = [
    StartupStage.Preflight, StartupStage.Elect, StartupStage.Migrate, StartupStage.Converge,
    StartupStage.Coordinate, StartupStage.AcceptingCommands, StartupStage.Running,
  ];

  /// <summary>The startup stages in order; a stage's <c>X</c> in <c>Stage X of Y</c> is its position here, from 1.</summary>
  public static IReadOnlyList<StartupStage> Startup { get; } = Array.AsReadOnly(_startup);

  /// <summary>The stage's name as it is logged and reported, for example <c>Accept commands</c>.</summary>
  /// <param name="stage">The stage.</param>
  /// <returns>The display name.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The value is not a defined stage.</exception>
  public static string NameOf(StartupStage stage) => stage switch {
    StartupStage.Preflight => "Preflight",
    StartupStage.Elect => "Elect",
    StartupStage.Migrate => "Migrate",
    StartupStage.Converge => "Converge",
    StartupStage.Coordinate => "Coordinate",
    StartupStage.AcceptingCommands => "Accept commands",
    StartupStage.Running => "Running",
    StartupStage.Draining => "Draining",
    StartupStage.Stopped => "Stopped",
    _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Not a startup stage."),
  };

  /// <summary>The stage's position in <see cref="Startup"/>, from 1, or <see langword="null"/> for a shutdown stage.</summary>
  /// <param name="stage">The stage.</param>
  /// <returns>The ordinal, or null.</returns>
  public static int? OrdinalOf(StartupStage stage) {
    var index = Array.IndexOf(_startup, stage);
    return index < 0 ? null : index + 1;
  }

  /// <summary>
  /// The lifecycle phase the instance's row records alongside the stage (<c>lifecycle_phase</c>, kept for the readers
  /// and dashboards that read it).
  /// </summary>
  /// <param name="stage">The stage.</param>
  /// <returns>The mapped phase.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The value is not a defined stage.</exception>
  public static LifecyclePhase LifecyclePhaseOf(StartupStage stage) => stage switch {
    StartupStage.Preflight => LifecyclePhase.Connecting,
    StartupStage.Elect or StartupStage.Migrate or StartupStage.Converge or StartupStage.Coordinate => LifecyclePhase.Migrating,
    StartupStage.AcceptingCommands => LifecyclePhase.AcceptingCommands,
    StartupStage.Running => LifecyclePhase.Running,
    StartupStage.Draining => LifecyclePhase.Stopping,
    StartupStage.Stopped => LifecyclePhase.Stopped,
    _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Not a startup stage."),
  };

  /// <summary>
  /// The line an instance logs on entering the stage, and the text the <c>startup</c> health component reports:
  /// <c>Stage X of 7: &lt;name&gt; - &lt;message&gt;</c> for a startup stage, and
  /// <c>Shutdown stage: &lt;name&gt; - &lt;message&gt;</c> for a shutdown stage.
  /// </summary>
  /// <param name="stage">The stage.</param>
  /// <param name="message">What the stage is doing.</param>
  /// <returns>The text.</returns>
  public static string Describe(StartupStage stage, string message) {
    var name = NameOf(stage);
    return OrdinalOf(stage) is { } position
      ? string.Create(CultureInfo.InvariantCulture, $"Stage {position} of {_startup.Length}: {name} - {message}")
      : $"Shutdown stage: {name} - {message}";
  }
}

/// <summary>The stage an instance is in, as its <see cref="IStartupStageReporter"/> last entered it.</summary>
/// <param name="Stage">The stage.</param>
/// <param name="Message">What the stage is doing, or why it was skipped.</param>
/// <param name="Skipped">Whether the stage was passed through without doing its work.</param>
/// <param name="EnteredAt">When the stage was entered, by the host's clock. Progress within a stage keeps it.</param>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStagesTests.cs</tests>
public sealed record StartupStageSnapshot(StartupStage Stage, string Message, bool Skipped, DateTimeOffset EnteredAt) {
  /// <summary>The stage's display name (<see cref="StartupStages.NameOf"/>).</summary>
  public string Name => StartupStages.NameOf(Stage);

  /// <summary>The logged line (<see cref="StartupStages.Describe"/>).</summary>
  public string Text => StartupStages.Describe(Stage, Message);
}
