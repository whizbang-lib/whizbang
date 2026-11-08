// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.RunControl;

/// <summary>
/// The one exit path (#2126). Every way this process exits, intended or not, goes through it: a host shutdown, an
/// operator's stop, a stand-down whose newer version committed, a crash, a startup failure, an incompatible schema, and an
/// eviction by the fleet. Library code never stops the host or sets the exit code itself; an analyzer and a source guard
/// fail the build when it tries.
/// </summary>
/// <remarks>
/// <para>
/// An exit does four things, once, whichever cause asks first: it stops cleanly through the one run-control path
/// (<see cref="IWhizbangRunController.HaltAsync"/> with <see cref="RunHaltReason.Stop"/>, bounded by the host's stop
/// token), so held work and roles are released; it writes one detailed log entry that says why (Error for a failure,
/// Information for an intended stop); it records the same facts in <c>wh_instance_exits</c> when the database is
/// reachable, and the entry says when it could not; and it sets the process exit code (<see cref="ExitCauses.ExitCodeOf"/>)
/// and stops the host through <c>IHostApplicationLifetime</c>, never <c>Environment.Exit</c>.
/// </para>
/// <para>
/// In a host, a requested exit stops the host at once and the host's own stop completes it, on the host's stop token. A
/// host stop that no cause asked for is a <see cref="ExitCause.HostShutdown"/>, or a <see cref="ExitCause.Crash"/> when a
/// hosted worker faulted. Outside a host the request completes the exit itself.
/// </para>
/// </remarks>
/// <docs>operations/startup/instance-exits</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/RunControl/WhizbangInstanceExitTests.cs</tests>
public interface IWhizbangInstanceExit {
  /// <summary>The cause of the exit under way, or <see langword="null"/> while the instance runs.</summary>
  ExitCause? Cause { get; }

  /// <summary>What the exit did, once it completed; <see langword="null"/> before then.</summary>
  InstanceExitReport? Report { get; }

  /// <summary>Completes when the exit has halted, logged, recorded and set the exit code.</summary>
  Task Completion { get; }

  /// <summary>
  /// Starts the exit for <paramref name="request"/> and returns at once. The first request wins: a later one, of any
  /// cause, changes nothing, so the process exits once. Safe to call from inside a unit of work, since it never waits for
  /// the halt.
  /// </summary>
  /// <param name="request">Why the process exits.</param>
  void RequestExit(InstanceExitRequest request);

  /// <summary>Starts the exit (<see cref="RequestExit"/>) and waits for it to complete.</summary>
  /// <param name="request">Why the process exits.</param>
  /// <param name="cancellationToken">Ends the wait; the exit goes on.</param>
  /// <returns>A task that completes with the exit.</returns>
  Task ExitAsync(InstanceExitRequest request, CancellationToken cancellationToken);

  /// <summary>The host is starting: uptime is measured from here, and from here a requested exit stops the host and lets
  /// the host's stop complete it.</summary>
  void OnHostStarting();

  /// <summary>
  /// The host is stopping: completes the exit under way, or, when no cause asked for one, the exit of a host shutdown (a
  /// crash when a hosted worker faulted), bounded by <paramref name="stopToken"/>.
  /// </summary>
  /// <param name="stopToken">The host's stop token.</param>
  /// <returns>A task that completes with the exit.</returns>
  Task OnHostStoppingAsync(CancellationToken stopToken);
}

/// <summary>Why the process exits: the cause, a sentence that explains it, and the failure behind it when there is one.</summary>
/// <param name="Cause">The cause.</param>
/// <param name="Reason">What happened, in a sentence an operator can act on.</param>
/// <param name="Exception">The failure behind the exit, when there is one.</param>
/// <docs>operations/startup/instance-exits</docs>
public sealed record InstanceExitRequest(ExitCause Cause, string Reason, Exception? Exception = null);

/// <summary>
/// An eviction as the evicting peer recorded it on the tombstone (#2126): when, by which peer, why, and what the one
/// liveness rule judged the instance by.
/// </summary>
/// <param name="EvictedAt">When the tombstone was written, on the database's clock.</param>
/// <param name="EvictedBy">The peer that evicted the instance; null when no peer recorded itself (a maintenance sweep).</param>
/// <param name="Reason">The reason recorded on the tombstone.</param>
/// <param name="LastHeartbeatAt">The instance's last heartbeat as the reap saw it; null on a tombstone written before 354.</param>
/// <param name="LivenessWindowSeconds">The liveness window the instance was judged by; null when its row recorded none (the rule then uses 30 seconds).</param>
/// <docs>operations/startup/instance-exits#evictions</docs>
public sealed record InstanceEviction(
  DateTimeOffset EvictedAt, Guid? EvictedBy, string? Reason, DateTimeOffset? LastHeartbeatAt, int? LivenessWindowSeconds);

/// <summary>The facts of one exit, as the log entry states them and <c>wh_instance_exits</c> records them (#2126).</summary>
/// <param name="InstanceId">The exiting instance.</param>
/// <param name="ServiceName">Its service.</param>
/// <param name="HostName">Its host or pod.</param>
/// <param name="ProcessId">Its process id.</param>
/// <param name="Cause">Why it exits.</param>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="Intended">Whether the exit was intended (exit code 0) rather than a failure.</param>
/// <param name="Reason">What happened.</param>
/// <param name="ExceptionType">The failure's type, when there is one.</param>
/// <param name="ExceptionMessage">The failure's message, when there is one.</param>
/// <param name="StartupStage">The startup stage it was in; null when the host records none.</param>
/// <param name="LifecyclePhase">The lifecycle phase it was in; null when the host has no lifecycle.</param>
/// <param name="UptimeSeconds">How long the host had run; null when no host start was seen.</param>
/// <param name="SecondsSinceLastHeartbeat">How long since its last accepted heartbeat; null when none was accepted.</param>
/// <param name="HeldRoles">The roles it held when the exit began, which the run-control path releases.</param>
/// <param name="HeldLeases">The leased streams it held when the exit began, which the run-control path releases.</param>
/// <param name="InHand">The units of work in hand when the exit began, which the run-control path lets finish.</param>
/// <param name="LibraryVersion">The library version it ran.</param>
/// <docs>operations/startup/instance-exits#the-exit-record</docs>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "A record of the facts one exit states; each parameter is one column of wh_instance_exits, and a nested object would only rename them.")]
public sealed record InstanceExitRecord(
  Guid InstanceId, string ServiceName, string HostName, int ProcessId,
  ExitCause Cause, int ExitCode, bool Intended, string Reason,
  string? ExceptionType, string? ExceptionMessage,
  string? StartupStage, string? LifecyclePhase,
  double? UptimeSeconds, double? SecondsSinceLastHeartbeat,
  IReadOnlyList<string> HeldRoles, int HeldLeases, int InHand,
  string? LibraryVersion);

/// <summary>Whether an exit was recorded in <c>wh_instance_exits</c>, and the eviction the database holds for the instance.</summary>
/// <param name="Recorded">Whether the row was written.</param>
/// <param name="Eviction">The instance's eviction tombstone, read with the write; null when it has none or nothing was read.</param>
/// <param name="NotRecordedBecause">Why the row was not written; null when it was.</param>
/// <docs>operations/startup/instance-exits#the-exit-record</docs>
public sealed record InstanceExitRecordResult(bool Recorded, InstanceEviction? Eviction, string? NotRecordedBecause) {
  /// <summary>The answer of a work coordinator that keeps no instance records.</summary>
  public static InstanceExitRecordResult NotKept { get; } =
    new(false, null, "this host's work coordinator keeps no instance records");

  /// <summary>The row was written.</summary>
  /// <param name="eviction">The instance's eviction tombstone, when it has one.</param>
  /// <returns>The result.</returns>
  public static InstanceExitRecordResult Written(InstanceEviction? eviction) => new(true, eviction, null);

  /// <summary>The row was not written.</summary>
  /// <param name="because">Why.</param>
  /// <returns>The result.</returns>
  public static InstanceExitRecordResult NotWritten(string because) => new(false, null, because);
}

/// <summary>What one exit did: its facts, whether they were recorded, and the eviction behind it when there was one.</summary>
/// <param name="Record">The facts.</param>
/// <param name="Recording">Whether the facts were recorded.</param>
/// <docs>operations/startup/instance-exits</docs>
public sealed record InstanceExitReport(InstanceExitRecord Record, InstanceExitRecordResult Recording);

/// <summary>
/// Sets the process exit code (#2126): the one place it is set. The default sets <c>Environment.ExitCode</c>; a test host
/// registers its own so a test process keeps its own exit code.
/// </summary>
/// <docs>operations/startup/instance-exits#exit-codes</docs>
public interface IProcessExitCode {
  /// <summary>Sets the code the process exits with.</summary>
  /// <param name="exitCode">The exit code.</param>
  void SetExitCode(int exitCode);
}
