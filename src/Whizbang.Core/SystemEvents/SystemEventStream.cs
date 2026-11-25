// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// The streams system events are stored on: the global <c>$wb-system</c> stream for facts about the
/// system as a whole, and a stream of its own (its own id) for each audit record.
/// </summary>
/// <remarks>
/// <para>
/// The system stream uses a <c>$</c> prefix following the convention for system/internal streams
/// (similar to EventStoreDB's <c>$all</c>, <c>$ce-</c> streams).
/// </para>
/// <para>
/// <c>$wb-system</c> holds only the global kinds: the security facts (<c>AccessDenied</c>,
/// <c>AccessGranted</c>, <c>PermissionChanged</c>, <c>ScopeContextEstablished</c>) and the advisories
/// (<see cref="PerspectiveIndexAdvised"/>, <see cref="PerspectiveScanAdvised"/>). An audit record
/// (<see cref="EventAudited"/>, <see cref="CommandAudited"/>) is stored on its own id, whichever path
/// produced it: the stream a receiver stores a record on that the outbox path shipped on its tenant's
/// ledger (<see cref="AuditLedger.StreamFor"/>), the lane records travel and are ordered on (#1555,
/// #1798, #2331). Stored on the ledger, every record of a tenant would fold onto one perspective row and
/// every append of the tenant would take one stream lock. <see cref="StreamFor"/> is the one place that
/// decides for a record stored locally.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/system-events#stream</docs>
/// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/SystemEvents/ISystemEventTests.cs:SystemEventStreamName_IsConstant_ForDedicatedStreamAsync</tests>
/// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/SystemEvents/SystemEventStreamRoutingTests.cs:StreamFor_NamesTheRecordsOwnIdForAuditKindsAndTheGlobalStreamForTheRestAsync</tests>
/// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/SystemEvents/SystemEventEmitterTests.cs:EmitAsync_WithEventAudited_WhenAuditEnabled_AppendsOnTheRecordsOwnIdAsync</tests>
public static class SystemEventStreams {
  /// <summary>
  /// The name of the dedicated system event stream.
  /// </summary>
  public static string Name => "$wb-system";

  /// <summary>
  /// Stream prefix for system events (can be used for filtering/subscriptions).
  /// </summary>
  public static string Prefix => "$wb-";

  /// <summary>
  /// The well-known GUID for the system event stream.
  /// Uses a namespace-based UUID (UUIDv5) derived from the stream name for determinism.
  /// </summary>
  /// <remarks>
  /// This is a fixed GUID: 00000000-0000-0000-0000-000000000001 (System stream)
  /// Using a known GUID ensures every global system event goes to the same stream.
  /// </remarks>
  public static Guid StreamId { get; } = new Guid("00000000-0000-0000-0000-000000000001");

  /// <summary>
  /// The stream a system event is stored on: its own id for an audit record, the global
  /// <see cref="StreamId"/> for every other kind.
  /// </summary>
  /// <param name="systemEvent">The system event about to be stored.</param>
  /// <returns>The stream id to append it to.</returns>
  /// <docs>fundamentals/security/audit-logging#record-storage</docs>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/AuditRecordStorageStreamTests.cs:AnEventAuditTheEmitterStoresLocally_IsStoredOnItsOwnIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/SystemEvents/SystemEventStreamRoutingTests.cs:StreamFor_NamesTheRecordsOwnIdForAuditKindsAndTheGlobalStreamForTheRestAsync</tests>
  public static Guid StreamFor(ISystemEvent systemEvent) => systemEvent switch {
    EventAudited audited => audited.Id,
    CommandAudited audited => audited.Id,
    _ => StreamId,
  };
}
