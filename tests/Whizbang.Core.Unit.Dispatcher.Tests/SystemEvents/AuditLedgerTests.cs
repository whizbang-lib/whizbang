// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Core;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// The audit ledger is one deterministic stream per tenant (#1485): <c>UUIDv5(UUIDv5(nil, "sys-audit"), tenant)</c>.
/// The vectors are the reference implementation's output, so a change to the derivation, which would move every
/// tenant's ledger to a new stream, fails here first.
/// </summary>
[Category("SystemEvents")]
public class AuditLedgerTests {
  [Test]
  public async Task Namespace_IsTheVersionFiveIdOfSysAuditUnderTheNilNamespaceAsync() {
    await Assert.That(AuditLedger.Namespace).IsEqualTo(new Guid("5aee3f04-dca7-5d18-9928-d5612227ff01"));
  }

  [Test]
  public async Task For_KnownTenant_MatchesTheReferenceVectorAsync() {
    await Assert.That(AuditLedger.StreamFor("tenant-a")).IsEqualTo(new Guid("d4231ae6-9fc4-5fe4-9ee1-332fa310fc4e"));
  }

  [Test]
  public async Task For_SameTenant_IsTheSameStreamAsync() {
    await Assert.That(AuditLedger.StreamFor("tenant-a")).IsEqualTo(AuditLedger.StreamFor("tenant-a"))
      .Because("every writer of one tenant's audit records must land on the one ledger without coordinating");
  }

  [Test]
  public async Task For_DifferentTenants_AreDifferentStreamsAsync() {
    await Assert.That(AuditLedger.StreamFor("tenant-a")).IsNotEqualTo(AuditLedger.StreamFor("tenant-b"))
      .Because("a ledger never mixes tenants");
  }

  [Test]
  public async Task For_NoTenant_IsTheEmptyTenantLedgerAsync() {
    await Assert.That(AuditLedger.StreamFor(null)).IsEqualTo(AuditLedger.StreamFor(string.Empty));
    await Assert.That(AuditLedger.StreamFor(null)).IsEqualTo(new Guid("c616516d-36b6-5c40-9fee-8e757ba5b3f2"));
  }

  // #1724: a record's id is derived from the event it audits, so deriving it again lands on the same record.

  [Test]
  public async Task RecordNamespace_IsTheVersionFiveIdOfSysAuditRecordUnderTheNilNamespaceAsync() {
    await Assert.That(AuditLedger.RecordNamespace).IsEqualTo(new Guid("533e2e10-d6f9-536a-b681-536685021e0f"));
  }

  [Test]
  public async Task RecordIdFor_KnownEvent_MatchesTheReferenceVectorAsync() {
    // Python: uuid.uuid5(uuid.uuid5(uuid.UUID(int=0), "sys-audit-record"), "0190f1a2-3b4c-7d5e-8f60-718293a4b5c6")
    await Assert.That(AuditLedger.RecordIdFor(new Guid("0190f1a2-3b4c-7d5e-8f60-718293a4b5c6")))
      .IsEqualTo(new Guid("addda4ad-49fc-584f-8df5-ee8bef9aa3a0"));
  }

  [Test]
  public async Task RecordIdFor_SameEvent_IsTheSameRecordAsync() {
    var eventId = new Guid("0190f1a2-3b4c-7d5e-8f60-718293a4b5c6");

    await Assert.That(AuditLedger.RecordIdFor(eventId)).IsEqualTo(AuditLedger.RecordIdFor(eventId))
      .Because("a record derived again from the same event must be the same record, not a second one");
  }

  [Test]
  public async Task RecordIdFor_DifferentEvents_AreDifferentRecordsAsync() {
    await Assert.That(AuditLedger.RecordIdFor(new Guid("0190f1a2-3b4c-7d5e-8f60-718293a4b5c6")))
      .IsNotEqualTo(AuditLedger.RecordIdFor(new Guid("0190f1a2-3b4c-7d5e-8f60-718293a4b5c7")));
  }

  [Test]
  public async Task RecordIdFor_AnEventId_IsNeverTheEventIdItselfAsync() {
    var eventId = new Guid("0190f1a2-3b4c-7d5e-8f60-718293a4b5c6");

    await Assert.That(AuditLedger.RecordIdFor(eventId)).IsNotEqualTo(eventId)
      .Because("the record is stored as an event of its own, so it must not reuse the audited event's id");
  }
}
