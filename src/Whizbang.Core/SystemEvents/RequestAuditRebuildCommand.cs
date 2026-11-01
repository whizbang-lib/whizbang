// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Asks an origin service to re-derive the audit records of the events it stored (#1724). The origin selects its stored
/// events the way a redelivery does, keeps the types audit eligibility admits, and queues each record to the audit topic
/// under the id its live writer gave it (<see cref="AuditLedger.RecordIdFor"/>), so the audit perspective holds one row
/// per event however often the ledger is rebuilt.
/// </summary>
/// <remarks>
/// <para>
/// Dispatch it on the origin service (an operator rebuilding a lost or truncated ledger), or send it to the origin from
/// the audit service. Every filter is optional and conjunctive; an empty command rebuilds everything up to the origin's
/// cap, <see cref="RedeliveryPumpOptions.MaxEventsPerRequest"/>, which <see cref="MaxEvents"/> can lower but never
/// raise. Page through a larger history with <see cref="FromCommitSequence"/>.
/// </para>
/// <para>
/// Only the type rules are applied again: the mode, <see cref="AuditEventAttribute"/> and the ephemeral exclusion. An
/// <see cref="IAuditDecisionHook"/> is not consulted, because a hook judges an event's payload and the rebuild, like a
/// redelivery, never rehydrates payloads.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/audit-logging#rebuilding-the-audit-ledger</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditLedgerRebuildTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditRebuildRequestReceptorTests.cs</tests>
[PinnedId("1cd01d44-ef74-4cd5-881e-08e958aada64")]
[SystemControlTag(Tag = Tags.SystemTags.CONTROL, Properties = [])]
public sealed record RequestAuditRebuildCommand : ICommand, IControlPlaneMessage, Whizbang.Core.Audit.IBoundedCommandAudit {
  private static readonly string[] _idLists = [nameof(StreamIds)];

  /// <inheritdoc />
  /// <docs>fundamentals/security/audit-logging#bounded-command-audit</docs>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditLedgerRebuildTests.cs:RequestAuditRebuildCommand_IsAuditedWithItsStreamCountAsync</tests>
  public IReadOnlyCollection<string> ListsAuditedAsLengths() => _idLists;

  /// <summary>Optional tenant filter (the stored scope's <c>t</c> key), as in <see cref="RedeliveryRequest.TenantScope"/>.</summary>
  public string? TenantScope { get; init; }

  /// <summary>Optional stream filter; null rebuilds every stream.</summary>
  public IReadOnlyList<Guid>? StreamIds { get; init; }

  /// <summary>Exclusive commit-sequence floor; null starts from the beginning.</summary>
  public long? FromCommitSequence { get; init; }

  /// <summary>Inclusive commit-sequence ceiling; null runs to the head.</summary>
  public long? ToCommitSequence { get; init; }

  /// <summary>Requested event cap; null takes the origin's cap. Always clamped by the origin.</summary>
  public int? MaxEvents { get; init; }
}
