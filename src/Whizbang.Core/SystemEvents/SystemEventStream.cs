// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// The streams system events are stored on: the global <c>$wb-system</c> stream for facts about the
/// system as a whole, and the per-tenant audit ledger for audit records.
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
/// (<see cref="EventAudited"/>, <see cref="CommandAudited"/>) is a ledger entry about a domain event
/// and is written on its tenant's ledger (<see cref="AuditLedger.StreamFor"/>), whichever path produced
/// it (#1555). <see cref="StreamFor"/> is the one place that decides.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/system-events#stream</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/ISystemEventTests.cs:SystemEventStreamName_IsConstant_ForDedicatedStreamAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventStreamRoutingTests.cs:StreamFor_NamesTheLedgerForAuditKindsAndTheGlobalStreamForTheRestAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventEmitterTests.cs:EmitAsync_WithEventAudited_WhenAuditEnabled_AppendsToTheNoTenantAuditLedgerAsync</tests>
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
  /// The stream a system event is stored on: its tenant's audit ledger for an audit record, the global
  /// <see cref="StreamId"/> for every other kind.
  /// </summary>
  /// <param name="systemEvent">The system event about to be stored.</param>
  /// <returns>The stream id to append it to.</returns>
  /// <docs>fundamentals/events/system-events#stream</docs>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventStreamRoutingTests.cs:StreamFor_NamesTheLedgerForAuditKindsAndTheGlobalStreamForTheRestAsync</tests>
  public static Guid StreamFor(ISystemEvent systemEvent) => systemEvent switch {
    EventAudited audited => AuditLedger.StreamFor(audited.TenantId),
    CommandAudited audited => AuditLedger.StreamFor(audited.TenantId),
    _ => StreamId,
  };
}
