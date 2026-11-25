// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// The audit ledger: one deterministic stream per tenant that every audit record of that tenant
/// travels and is ordered on.
/// </summary>
/// <remarks>
/// <para>
/// An audit record is a ledger entry ABOUT a domain event, not part of the domain event's stream. Its
/// outbox row rides the tenant's ledger: one stream per tenant gives each tenant one orderable lane, and a
/// coalesced bundle of audit records inherits it because a bundle never mixes tenants. The receiver STORES
/// each record on a stream of its own, its id (#1798, #2331): perspective rows are keyed by the stored
/// stream, so a record stored on the ledger would fold a tenant's whole trail into one row, and every
/// append of the tenant would take one stream lock. Records stored before #1798 stay on the audited
/// streams; a reader by type finds both.
/// </para>
/// <para>
/// Derivation: <c>UUIDv5(UUIDv5(nil, "sys-audit"), tenant)</c>, a record with no tenant using the empty
/// name. Reproducible in Postgres as
/// <c>uuid_generate_v5(uuid_generate_v5(uuid_nil(), 'sys-audit'), COALESCE(tenant, ''))</c>.
/// <strong>Never change it:</strong> every ledger stream already written was derived from it.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/audit-logging#record-storage</docs>
/// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/SystemEvents/AuditLedgerTests.cs</tests>
public static class AuditLedger {
  /// <summary>The name the ledger namespace is derived from.</summary>
  public const string NAMESPACE_NAME = "sys-audit";

  /// <summary>The namespace every tenant's ledger stream id is derived within: <c>UUIDv5(nil, "sys-audit")</c>.</summary>
  public static Guid Namespace { get; } = NameBasedGuid.V5(Guid.Empty, NAMESPACE_NAME);

  /// <summary>The ledger stream for <paramref name="tenantId"/>.</summary>
  /// <param name="tenantId">The audited event's tenant; null or empty is the no-tenant ledger.</param>
  /// <returns>The tenant's ledger stream id; the same tenant always yields the same id.</returns>
  public static Guid StreamFor(string? tenantId) => NameBasedGuid.V5(Namespace, tenantId ?? string.Empty);

  /// <summary>The name the record namespace is derived from.</summary>
  public const string RECORD_NAMESPACE_NAME = "sys-audit-record";

  /// <summary>The namespace every audit record id is derived within: <c>UUIDv5(nil, "sys-audit-record")</c>.</summary>
  /// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/SystemEvents/AuditLedgerTests.cs:RecordNamespace_IsTheVersionFiveIdOfSysAuditRecordUnderTheNilNamespaceAsync</tests>
  public static Guid RecordNamespace { get; } = NameBasedGuid.V5(Guid.Empty, RECORD_NAMESPACE_NAME);

  /// <summary>
  /// The id of the audit record of the event <paramref name="originalEventId"/>: <c>UUIDv5(RecordNamespace, id)</c>, the
  /// event id written in its 36-character lower-case form. Every audit writer and the ledger rebuild use it, so a record
  /// derived again from the same event is the same record, and the audit perspective, which keys a record by its id,
  /// holds one row for it however often it is delivered (#1724). Reproducible in Postgres as
  /// <c>uuid_generate_v5(uuid_generate_v5(uuid_nil(), 'sys-audit-record'), original_event_id::text)</c>.
  /// <strong>Never change it:</strong> a rebuild must derive the ids the records already carry.
  /// </summary>
  /// <param name="originalEventId">The id of the audited event.</param>
  /// <returns>The record's id.</returns>
  /// <docs>fundamentals/security/audit-logging#rebuilding-the-audit-ledger</docs>
  /// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/SystemEvents/AuditLedgerTests.cs:RecordIdFor_KnownEvent_MatchesTheReferenceVectorAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/SystemEvents/AuditLedgerTests.cs:RecordIdFor_SameEvent_IsTheSameRecordAsync</tests>
  public static Guid RecordIdFor(Guid originalEventId) =>
    NameBasedGuid.V5(RecordNamespace, originalEventId.ToString("D", System.Globalization.CultureInfo.InvariantCulture));
}
