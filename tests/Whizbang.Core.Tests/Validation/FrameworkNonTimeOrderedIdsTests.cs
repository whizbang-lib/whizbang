// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Temporal;
using Whizbang.Core.Validation;

namespace Whizbang.Core.Tests.Validation;

/// <summary>
/// The framework's own ids that are not UUIDv7 by design (#2528), each pinned by its exact value, so a change to one is
/// a decision someone makes, and each listed with the reason no path that reads id time order sees it. The guard that a
/// new minting site cannot appear without an entry is <c>FrameworkNonTimeOrderedIdsSourceGuardTests</c>.
/// </summary>
[Category("Validation")]
[Category("GuidOrdering")]
public class FrameworkNonTimeOrderedIdsTests {
  private static readonly Guid _source = Guid.Parse("01890a5d-ac96-774b-bcce-b302099a8057");

  private static NonTimeOrderedId _entry(string site) =>
    FrameworkNonTimeOrderedIds.All.Single(e => e.Site == site);

  [Test]
  public async Task IntegrityDeliveryId_IsAVersion8IdOfTypeAndPayload_NotAMessageIdAsync() {
    var id = IntegrityDeliveryId.For("integrity.test", Encoding.UTF8.GetBytes("{\"a\":1}"));

    await Assert.That(id).IsEqualTo(Guid.Parse("de8baec0-c503-8b28-af86-c5c05749e5b9"));
    await Assert.That(id.Version).IsEqualTo(8);
    var entry = _entry("src/Whizbang.Core/Messaging/IntegrityDeliveryId.cs:For");
    await Assert.That(entry.Version).IsEqualTo(8);
    await Assert.That(entry.Use).IsEqualTo(NonTimeOrderedIdUse.NotAMessageId);
  }

  [Test]
  public async Task AuditRecordId_IsTheVersion5IdOfTheAuditedEvent_APayloadFieldAsync() {
    var id = AuditLedger.RecordIdFor(_source);

    await Assert.That(id).IsEqualTo(Guid.Parse("bc5df503-1c88-5e49-9b89-6fb2cf0ab56e"));
    await Assert.That(id.Version).IsEqualTo(5);
    await Assert.That(_entry("src/Whizbang.Core/SystemEvents/AuditLedger.cs:RecordIdFor").Use).IsEqualTo(NonTimeOrderedIdUse.NotAMessageId);
  }

  [Test]
  public async Task ChunkRequestId_IsTheVersion5IdOfTheChunk_APayloadFieldAsync() {
    var id = RebuildPerspectiveCommand.ChunkRequestId(_source, 2);

    await Assert.That(id).IsEqualTo(Guid.Parse("0171772a-cf9d-5ff9-a38e-c5230bc33f92"));
    await Assert.That(id.Version).IsEqualTo(5);
    await Assert.That(_entry("src/Whizbang.Core/Commands/System/SystemCommands.cs:ChunkRequestId").Use)
      .IsEqualTo(NonTimeOrderedIdUse.NotAMessageId);
  }

  [Test]
  public async Task DeadlineScheduleId_IsARawHashOfSagaAndDeadline_AScheduleIdAsync() {
    var id = new SagaDeadlineScheduler(new UnusedScheduleManager()).DeadlineScheduleId(_source, "payment-timeout");

    await Assert.That(id).IsEqualTo(Guid.Parse("086cb41c-5837-7836-6a87-e346a9150db7"));
    await Assert.That(_entry("src/Whizbang.Core/Temporal/SagaDeadlineScheduler.cs:DeadlineScheduleId").Use)
      .IsEqualTo(NonTimeOrderedIdUse.NotAMessageId);
  }

  /// <summary>
  /// No entry is a message id: every non-v7 id the framework mints is something other than an event or message id, so
  /// the entry points need no exemption and refuse every non-v7 id they are handed.
  /// </summary>
  [Test]
  public async Task NoEntry_IsAnEventOrMessageIdAsync() {
    await Assert.That(FrameworkNonTimeOrderedIds.All.Select(e => e.Use).Distinct())
      .IsEquivalentTo([NonTimeOrderedIdUse.NotAMessageId, NonTimeOrderedIdUse.StreamId, NonTimeOrderedIdUse.HashKey]);
    await Assert.That(FrameworkNonTimeOrderedIds.All.All(e => e.Reason.Length > 20)).IsTrue()
      .Because("every entry says why its id is safe");
    await Assert.That(FrameworkNonTimeOrderedIds.All.Select(e => e.Site).Distinct().Count()).IsEqualTo(FrameworkNonTimeOrderedIds.All.Count);
  }

  private sealed class UnusedScheduleManager : IScheduleManager {
    public Task<ScheduleHandle> CreateAsync(ScheduleDefinition definition, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<bool> PauseAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<bool> ResumeAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<bool> CancelAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<bool> CancelByKeyAsync(string key, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<Guid?> TriggerNowAsync(Guid scheduleId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<ScheduleUpdateResult?> UpdateAsync(
        Guid scheduleId, ScheduleUpdate update, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
  }
}
