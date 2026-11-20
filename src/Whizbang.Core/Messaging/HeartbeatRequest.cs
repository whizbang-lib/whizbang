// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Parameters for <see cref="IWorkCoordinator.RecordHeartbeatAsync"/>.
/// Decoupled from the polling-loop call so the heartbeat timer can fire on its own
/// cadence independent of work claim cadence.
/// </summary>
/// <param name="InstanceId">This service instance's identity.</param>
/// <param name="ServiceName">Service name (used for diagnostics and cross-instance grouping).</param>
/// <param name="HostName">Pod / host name.</param>
/// <param name="ProcessId">OS process id.</param>
/// <param name="Metadata">Optional metadata blob; defaults to empty object.</param>
/// <param name="LifecyclePhase">
/// The instance's current lifecycle phase as text, or null to leave the registry's value alone.
/// The heartbeat carries it so a row created before the first phase transition landed (a fallback
/// insert by the claim path) is backfilled on the next beat instead of staying blank.
/// </param>
/// <param name="LibraryVersion">The library version the instance runs, or null to leave the registry's value alone.</param>
/// <param name="StaleThresholdSeconds">
/// How stale a peer's heartbeat row may be before this beat's opportunistic reap treats the peer
/// as dead. Derived from the writer's cadence (see <c>HeartbeatLivenessThreshold</c>); null keeps
/// the SQL default.
/// </param>
/// <param name="ConnectionMode">
/// How the instance reaches the database, recorded at registration so the partition assigner judges its liveness by
/// the right signal (#1254); null leaves the recorded mode alone.
/// </param>
/// <param name="ApplicationVersion">
/// The application version the instance runs, as recorded (<see cref="Whizbang.Core.Versioning.ApplicationVersion.Recorded"/>),
/// or null when it is unknown or to leave the registry's value alone (#2027).
/// </param>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
/// <docs>data/event-store#trim-gate</docs>
public sealed record HeartbeatRequest(
  Guid InstanceId,
  string ServiceName,
  string HostName,
  int ProcessId,
  JsonElement? Metadata = null,
  string? LifecyclePhase = null,
  string? LibraryVersion = null,
  int? StaleThresholdSeconds = null,
  Whizbang.Core.Workers.InstanceConnectionMode? ConnectionMode = null,
  string? ApplicationVersion = null) {
  /// <summary>
  /// The key in the registry row's metadata (<c>wh_service_instances.metadata</c>) that says which stored forms this
  /// instance reads (#2323). A release before it writes none, which the trim gate reads as "cannot read trimmed rows".
  /// </summary>
  /// <docs>data/event-store#trim-gate</docs>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/HeartbeatRequestStoredFormLevelTests.cs</tests>
  public const string STORED_FORM_LEVEL_KEY = "stored_form_level";

  /// <summary>
  /// The stored form level this build reads: 1 reads stored metadata that leaves out the message id and hop scope its row
  /// holds (#2323). Monotonic: a later stored form raises it, never this one.
  /// </summary>
  /// <docs>data/event-store#trim-gate</docs>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/HeartbeatRequestStoredFormLevelTests.cs:StoredFormLevel_IsOne_TheLevelThatReadsTrimmedRowsAsync</tests>
  public const int STORED_FORM_LEVEL = 1;

  /// <summary>
  /// The metadata the heartbeat records for this instance: <see cref="Metadata"/>'s keys, with
  /// <see cref="STORED_FORM_LEVEL_KEY"/> set to <see cref="STORED_FORM_LEVEL"/> (#2323). Every driver writes this, so no
  /// caller can leave the marker out. Metadata that is not a JSON object is replaced by the marker alone.
  /// </summary>
  /// <returns>The metadata as JSON text.</returns>
  /// <docs>data/event-store#trim-gate</docs>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/HeartbeatRequestStoredFormLevelTests.cs</tests>
  public string MetadataJson() {
    using var buffer = new System.IO.MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer)) {
      writer.WriteStartObject();
      if (Metadata is { ValueKind: JsonValueKind.Object } given) {
        foreach (var property in given.EnumerateObject()) {
          if (property.Name != STORED_FORM_LEVEL_KEY) {
            property.WriteTo(writer);
          }
        }
      }
      writer.WriteNumber(STORED_FORM_LEVEL_KEY, STORED_FORM_LEVEL);
      writer.WriteEndObject();
    }
    return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
  }
}
