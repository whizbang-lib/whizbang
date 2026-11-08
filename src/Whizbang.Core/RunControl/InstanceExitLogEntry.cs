// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections;
using System.Globalization;

namespace Whizbang.Core.RunControl;

/// <summary>
/// The one log entry an exit writes (#2126): a self-explanatory message, and every fact as a structured field under a
/// stable name (<c>ExitCause</c>, <c>ExitCode</c>, <c>InstanceId</c>, ...), so a log query can find every exit of a
/// cause without parsing text.
/// </summary>
/// <docs>operations/startup/instance-exits#the-log-entry</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/RunControl/WhizbangInstanceExitTests.cs</tests>
internal sealed class InstanceExitLogEntry : IReadOnlyList<KeyValuePair<string, object?>> {
  private const string UNKNOWN = "unknown";
  private readonly List<KeyValuePair<string, object?>> _fields;
  private readonly string _message;

  /// <summary>Builds the entry.</summary>
  /// <param name="record">The facts of the exit.</param>
  /// <param name="recording">Whether they were recorded, and the eviction read with them.</param>
  /// <param name="haltFailure">Why the run-control halt could not complete; null when it did.</param>
  /// <param name="docsLink">The page that explains exits.</param>
  public InstanceExitLogEntry(InstanceExitRecord record, InstanceExitRecordResult recording, string? haltFailure, string docsLink) {
    var eviction = recording.Eviction;
    _fields = [
      new("ExitCause", record.Cause.ToString()),
      new("ExitCode", record.ExitCode),
      new("Intended", record.Intended),
      new("InstanceId", record.InstanceId),
      new("ServiceName", record.ServiceName),
      new("HostName", record.HostName),
      new("ProcessId", record.ProcessId),
      new("Reason", record.Reason),
      new("StartupStage", record.StartupStage),
      new("LifecyclePhase", record.LifecyclePhase),
      new("UptimeSeconds", record.UptimeSeconds),
      new("SecondsSinceLastHeartbeat", record.SecondsSinceLastHeartbeat),
      new("HeldRoles", record.HeldRoles),
      new("HeldLeases", record.HeldLeases),
      new("InHand", record.InHand),
      new("ExitRecorded", recording.Recorded),
      new("ExitRecordNote", recording.NotRecordedBecause),
      new("EvictedAt", eviction?.EvictedAt),
      new("EvictedBy", eviction?.EvictedBy),
      new("EvictionReason", eviction?.Reason),
      new("EvictedLastHeartbeatAt", eviction?.LastHeartbeatAt),
      new("LivenessWindowSeconds", eviction?.LivenessWindowSeconds),
      new("DocsLink", docsLink),
    ];
    _message = _compose(record, recording, haltFailure, docsLink);
    _fields.Add(new("{OriginalFormat}", _message));
  }

  /// <inheritdoc />
  public int Count => _fields.Count;

  /// <inheritdoc />
  public KeyValuePair<string, object?> this[int index] => _fields[index];

  /// <inheritdoc />
  public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _fields.GetEnumerator();

  IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

  /// <summary>The message.</summary>
  /// <returns>The message.</returns>
  public override string ToString() => _message;

  private static string _compose(InstanceExitRecord record, InstanceExitRecordResult recording, string? haltFailure, string docsLink) {
    var c = CultureInfo.InvariantCulture;
    var text = new System.Text.StringBuilder();
    text.Append(c, $"Instance {record.InstanceId} of service {record.ServiceName} on host {record.HostName} (process {record.ProcessId}) ")
      .Append(c, $"is exiting: {record.Cause}, {(record.Intended ? "an intended stop" : "a failure")}. ")
      .Append(_capitalized(_sentence(record.Reason))).Append(' ');
    if (record.Cause == ExitCause.ClusterEviction || recording.Eviction is not null) {
      text.Append(_eviction(recording)).Append(' ');
    }
    return text.Append(_exitCode(record)).Append(_where(record)).Append(_held(record, haltFailure)).Append(_recorded(recording))
      .Append("If this was not expected, ").Append(_whatToCheck(record.Cause)).Append(' ')
      .Append(c, $"See {docsLink}")
      .ToString();
  }

  private static string _exitCode(InstanceExitRecord record) => record.Intended
    ? string.Create(CultureInfo.InvariantCulture, $"Exit code {record.ExitCode}: an orderly exit. ")
    : string.Create(CultureInfo.InvariantCulture,
      $"Exit code {record.ExitCode}: the host (Kubernetes or another supervisor) restarts the process, and the new process registers under a new instance id. ");

  private static string _where(InstanceExitRecord record) {
    var c = CultureInfo.InvariantCulture;
    var up = record.UptimeSeconds is { } seconds
      ? string.Create(c, $"up {seconds:F1} seconds")
      : "up for an unknown time (no host start was seen)";
    var beat = record.SecondsSinceLastHeartbeat is { } since
      ? string.Create(c, $"its last accepted heartbeat was {since:F1} seconds before")
      : "it had no accepted heartbeat";
    return string.Create(c,
      $"At the exit it was in startup stage {record.StartupStage ?? UNKNOWN}, lifecycle phase {record.LifecyclePhase ?? UNKNOWN}, {up}; {beat}. ");
  }

  private static string _held(InstanceExitRecord record, string? haltFailure) {
    var roles = record.HeldRoles.Count == 0 ? "no roles" : "the roles " + string.Join(", ", record.HeldRoles);
    var halt = haltFailure is null
      ? "the run-control path released what it could"
      : "the run-control path could not complete (" + haltFailure + ")";
    return string.Create(CultureInfo.InvariantCulture,
      $"It held {roles}, {record.HeldLeases} leased stream(s) and {record.InHand} unit(s) of work in hand; {halt}, and a peer takes over anything left when its lease lapses. ");
  }

  private static string _recorded(InstanceExitRecordResult recording) => recording.Recorded
    ? "The exit is recorded in wh_instance_exits. "
    : "The exit could not be recorded in wh_instance_exits: " + _sentence(recording.NotRecordedBecause ?? UNKNOWN) + " ";

  private static string _eviction(InstanceExitRecordResult recording) {
    if (recording.Eviction is not { } eviction) {
      return "The eviction's details could not be read with the exit record.";
    }
    var c = CultureInfo.InvariantCulture;
    var by = eviction.EvictedBy is { } peer ? string.Create(c, $"peer instance {peer}") : "a sweep that recorded no peer";
    var lastBeat = eviction.LastHeartbeatAt is { } at
      ? string.Create(c, $"its last heartbeat, at {at:O},")
      : "its last heartbeat (not recorded on this tombstone)";
    var window = eviction.LivenessWindowSeconds is { } seconds
      ? string.Create(c, $"its liveness window of {seconds} seconds")
      : "the rule's default window of 30 seconds (its row recorded none)";
    return string.Create(c,
      $"It was evicted at {eviction.EvictedAt:O} by {by}, recorded reason \"{eviction.Reason ?? UNKNOWN}\": judged dead by the one liveness rule because {lastBeat} was older than {window}.");
  }

  private static string _whatToCheck(ExitCause cause) => cause switch {
    ExitCause.HostShutdown => "check what asked the host to stop: a deployment, a scale-down, a node drain or a failed liveness probe.",
    ExitCause.OperatorStop => "check who used the killswitch.",
    ExitCause.UpgradeStandDown => "check the rollout that migrated the schema to the newer version.",
    ExitCause.Crash => "check the exception attached to this entry and the worker that threw it.",
    ExitCause.ClusterEviction => "check for heartbeat starvation (thread-pool starvation, long garbage-collection or CPU-throttling pauses), "
      + "database connectivity from this host, and long process pauses (a debugger, a suspended VM or container) around the last heartbeat.",
    ExitCause.StartupFailure => "check database connectivity from this host, the schema version record, and the capabilities this host is configured to need.",
    ExitCause.SchemaIncompatible => "the schema was migrated by a newer release: deploy that release, or restore the schema.",
    _ => "check the log entries just before this one.",
  };

  // A sentence always has its period, so it is never empty.
  private static string _capitalized(string sentence) => char.ToUpperInvariant(sentence[0]) + sentence[1..];

  private static string _sentence(string text) {
    var trimmed = text.TrimEnd();
    return trimmed.EndsWith('.') ? trimmed : trimmed + ".";
  }
}
