// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// The audit ledger: one deterministic stream per tenant that every audit record of that tenant is
/// written on.
/// </summary>
/// <remarks>
/// <para>
/// An audit record is a ledger entry ABOUT a domain event, not part of the domain event's stream, so
/// it belongs on neither the audited stream nor a stream of its own. Minting a fresh stream per record
/// spent the full per-stream machinery (ownership, ordering, partition routing) on every record and
/// left the records of one tenant with no order among themselves. One stream per tenant gives each
/// tenant one orderable ledger, and a coalesced bundle of audit records inherits it because a bundle
/// never mixes tenants.
/// </para>
/// <para>
/// Derivation: <c>UUIDv5(UUIDv5(nil, "sys-audit"), tenant)</c>, a record with no tenant using the empty
/// name. Reproducible in Postgres as
/// <c>uuid_generate_v5(uuid_generate_v5(uuid_nil(), 'sys-audit'), COALESCE(tenant, ''))</c>.
/// <strong>Never change it:</strong> every ledger stream already written was derived from it.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/audit-logging#dedicated-system-stream</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditLedgerTests.cs</tests>
public static class AuditLedger {
  /// <summary>The name the ledger namespace is derived from.</summary>
  public const string NAMESPACE_NAME = "sys-audit";

  /// <summary>The namespace every tenant's ledger stream id is derived within: <c>UUIDv5(nil, "sys-audit")</c>.</summary>
  public static Guid Namespace { get; } = NameBasedGuid.V5(Guid.Empty, NAMESPACE_NAME);

  /// <summary>The ledger stream for <paramref name="tenantId"/>.</summary>
  /// <param name="tenantId">The audited event's tenant; null or empty is the no-tenant ledger.</param>
  /// <returns>The tenant's ledger stream id; the same tenant always yields the same id.</returns>
  public static Guid StreamFor(string? tenantId) => NameBasedGuid.V5(Namespace, tenantId ?? string.Empty);
}
