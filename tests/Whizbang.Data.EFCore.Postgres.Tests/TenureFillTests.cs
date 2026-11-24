// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// How a write is filled from the tenure ledger (#2556): a value the caller named wins, work the ledger knows is filled
/// with its claimed tenure, and work it does not know is written as before, naming no epoch. The perspective write fence
/// asks the database nothing when no stream has a tenure. Both drivers share these helpers.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/TenureFill.cs</code-under-test>
[Category("Shard5")]
public sealed class TenureFillTests {
  private static readonly Guid _known = new("00000000-0000-0000-0000-0000000000a1");
  private static readonly Guid _unknown = new("00000000-0000-0000-0000-0000000000a2");
  private static readonly Guid _knownWork = new("00000000-0000-0000-0000-0000000000b1");

  [Test]
  public async Task Epochs_FillTheKnownWork_LeaveTheUnknownUnnamed_AndKeepWhatTheCallerNamedAsync() {
    var tenures = _ledger();

    await Assert.That(TenureFill.Epochs(tenures, [_knownWork, _unknown], null)).IsEquivalentTo(
      [(long?)7, null], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(TenureFill.Epochs(tenures, [_unknown], null)).IsNull()
      .Because("a write of nothing the ledger knows names no epoch at all");
    await Assert.That(TenureFill.Epochs(null, [_knownWork], null)).IsNull();
    await Assert.That(TenureFill.Epochs(tenures, [_knownWork], [3L])).IsEquivalentTo([(long?)3]);
  }

  [Test]
  public async Task Cursors_AreFilledOnlyWhenTheyNameNoEpoch_AndTheirStreamIsKnownAsync() {
    var tenures = _ledger();
    var named = _cursor(_known) with { PartitionNumber = 1, PartitionEpoch = 2 };

    var filled = TenureFill.Cursors(tenures, [_cursor(_known), _cursor(_unknown), named]);

    await Assert.That(filled[0].PartitionEpoch).IsEqualTo(7L);
    await Assert.That(filled[0].PartitionNumber).IsEqualTo(42);
    await Assert.That(filled[1].PartitionEpoch).IsNull();
    await Assert.That(filled[2]).IsEqualTo(named);
    var unfilled = new[] { _cursor(_known) };
    await Assert.That(TenureFill.Cursors(null, unfilled)).IsSameReferenceAs(unfilled);
  }

  [Test]
  public async Task Failures_AreFilledOnlyWhenTheyNameNoEpoch_AndTheirWorkIsKnownAsync() {
    var tenures = _ledger();
    var named = _failure(_knownWork) with { PartitionNumber = 1, PartitionEpoch = 2 };

    var filled = TenureFill.Failures(tenures, [_failure(_knownWork), _failure(_unknown), named]);

    await Assert.That(filled[0].PartitionEpoch).IsEqualTo(7L);
    await Assert.That(filled[0].PartitionNumber).IsEqualTo(42);
    await Assert.That(filled[1].PartitionEpoch).IsNull();
    await Assert.That(filled[2]).IsEqualTo(named);
    var unfilled = new[] { _failure(_knownWork) };
    await Assert.That(TenureFill.Failures(null, unfilled)).IsSameReferenceAs(unfilled);
  }

  [Test]
  public async Task TheWriteFence_WithNoStreamHoldingATenure_AsksTheDatabaseNothingAsync() {
    // The connection is never opened: a query on it would throw.
    await using var connection = new NpgsqlConnection();

    var noLedger = await PerspectiveWriteFence.StaleAsync(connection, null, "", null, [_known], CancellationToken.None);
    var noTenure = await PerspectiveWriteFence.StaleAsync(connection, null, "", _ledger(), [_unknown], CancellationToken.None);

    await Assert.That(noLedger).IsEmpty();
    await Assert.That(noTenure).IsEmpty().Because("off map mode no claim records a tenure, so nothing is fenced");
  }

  private static PartitionTenures _ledger() {
    var tenures = new PartitionTenures();
    tenures.RecordClaim(new Dictionary<Guid, long> { [_known] = 7 }, new Dictionary<Guid, int> { [_known] = 42 });
    tenures.RecordFetched(_knownWork, _known);
    return tenures;
  }

  private static PerspectiveCursorCompletion _cursor(Guid stream) =>
    new() { StreamId = stream, PerspectiveName = "P", LastEventId = _knownWork, Status = PerspectiveProcessingStatus.Completed };

  private static MessageFailure _failure(Guid work) =>
    new() { MessageId = work, CompletedStatus = MessageProcessingStatus.None, Error = "boom" };
}
