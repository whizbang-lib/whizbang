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
  /// The stored form level this build reads. Each level reads everything the levels below it read:
  /// <list type="bullet">
  ///   <item>1 reads stored metadata that leaves out the message id and hop scope its row holds (#2323);</item>
  ///   <item>2 reads a strongly-typed id inside a perspective document stored as its UUID string (#2518).</item>
  /// </list>
  /// Monotonic: a later stored form raises it, never lowers it. Each gate asks for the level its form needs, so a gate
  /// for level 1 opens for an instance at level 2.
  /// </summary>
  /// <docs>data/event-store#trim-gate</docs>
  /// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/HeartbeatRequestStoredFormLevelTests.cs:StoredFormLevel_IsTwo_TheLevelThatReadsStrongIdsAsUuidStringsAsync</tests>
  public const int STORED_FORM_LEVEL = 2;

  /// <summary>
  /// The key in the registry row's metadata that says this instance writes a strongly-typed id inside a perspective
  /// document as its UUID string (#2518), present with the value <see cref="STRONG_ID_DOCUMENT_FORM_UUID_STRING"/> once
  /// it does (<see cref="Whizbang.Core.Perspectives.WhizbangIdDocumentForm"/>). The stored-form rewrite starts only once
  /// every live instance says so, so no instance writes the object form behind it.
  /// </summary>
  /// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/HeartbeatRequestStoredFormLevelTests.cs:MetadataJson_SaysItWritesUuidStrings_OnlyOnceItDoesAsync</tests>
  public const string STRONG_ID_DOCUMENT_FORM_KEY = "strong_id_document_form";

  /// <summary>The value of <see cref="STRONG_ID_DOCUMENT_FORM_KEY"/> for an instance that writes the UUID string.</summary>
  public const string STRONG_ID_DOCUMENT_FORM_UUID_STRING = "uuid-string";

  /// <summary>
  /// The key in the registry row's metadata (<c>wh_service_instances.metadata</c>) that says which partition ownership
  /// modes this instance supports (#2555). A release before it writes none, which the ownership switch reads as "claims
  /// only by the stored owner and its rank".
  /// </summary>
  /// <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/HeartbeatRequestStoredFormLevelTests.cs:PartitionOwnershipLevel_IsOne_TheLevelThatSupportsOwnershipByMapAsync</tests>
  public const string PARTITION_OWNERSHIP_LEVEL_KEY = "partition_ownership_level";

  /// <summary>
  /// The partition ownership level this build supports: 1 claims through the partition ownership table when the
  /// service's switch is on (#2555). Monotonic: a later ownership protocol raises it, never this one.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/HeartbeatRequestStoredFormLevelTests.cs:PartitionOwnershipLevel_IsOne_TheLevelThatSupportsOwnershipByMapAsync</tests>
  public const int PARTITION_OWNERSHIP_LEVEL = 1;

  /// <summary>
  /// The metadata the heartbeat records for this instance: <see cref="Metadata"/>'s keys, with
  /// <see cref="STORED_FORM_LEVEL_KEY"/> set to <see cref="STORED_FORM_LEVEL"/> (#2323),
  /// <see cref="PARTITION_OWNERSHIP_LEVEL_KEY"/> set to <see cref="PARTITION_OWNERSHIP_LEVEL"/> (#2555), and
  /// <see cref="STRONG_ID_DOCUMENT_FORM_KEY"/> once this process writes strongly-typed ids as UUID strings (#2518). Every
  /// driver writes this, so no caller can leave the markers out or set them. Metadata that is not a JSON object is
  /// replaced by the markers alone.
  /// </summary>
  /// <returns>The metadata as JSON text.</returns>
  /// <docs>data/event-store#trim-gate</docs>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/HeartbeatRequestStoredFormLevelTests.cs</tests>
  public string MetadataJson() => MetadataJson(Whizbang.Core.Perspectives.WhizbangIdDocumentForm.WritesUuidString);

  /// <summary>
  /// The metadata, saying the given strong-id document form: for a driver whose store writes a strongly-typed id as its
  /// UUID string whatever the gate says (the Dapper driver, #2518), which passes true from its first beat.
  /// </summary>
  /// <param name="writesUuidStrings">Whether this process writes strongly-typed ids as UUID strings.</param>
  /// <returns>The metadata as JSON text.</returns>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStrongIdDocumentGateTests.cs:TheDapperHeartbeat_SaysItWritesTheUuidString_FromItsFirstBeatAsync</tests>
  public string MetadataJson(bool writesUuidStrings) {
    using var buffer = new System.IO.MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer)) {
      writer.WriteStartObject();
      if (Metadata is { ValueKind: JsonValueKind.Object } given) {
        foreach (var property in given.EnumerateObject().Where(p => p.Name is not (STORED_FORM_LEVEL_KEY or PARTITION_OWNERSHIP_LEVEL_KEY or STRONG_ID_DOCUMENT_FORM_KEY))) {
          property.WriteTo(writer);
        }
      }
      writer.WriteNumber(STORED_FORM_LEVEL_KEY, STORED_FORM_LEVEL);
      writer.WriteNumber(PARTITION_OWNERSHIP_LEVEL_KEY, PARTITION_OWNERSHIP_LEVEL);
      if (writesUuidStrings) {
        writer.WriteString(STRONG_ID_DOCUMENT_FORM_KEY, STRONG_ID_DOCUMENT_FORM_UUID_STRING);
      }
      writer.WriteEndObject();
    }
    return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
  }
}
