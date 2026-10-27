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
}
