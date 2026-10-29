// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Workers;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// The prefix truncate's archive path writes through the registered <see cref="IArchiveStore"/> (WS-3 S7), which is
/// the store's production caller today: an archiving truncate is a prefix batch (the truncate's guards apply), the
/// store's <c>archived</c> reads as <c>truncated</c>, and a refusal keeps its name. A discarding truncate, and an
/// archiving one with no store registered, still go to the coordinator.
/// </summary>
/// <docs>fundamentals/events/archive-store#the-prefix-truncate-archives-through-the-store</docs>
public class StreamPrefixTruncatorArchiveStoreTests {
  private static readonly IServiceScopeFactory _noServices =
    new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

  private static readonly Guid _stream = new("0192f1a0-0000-7000-8000-0000000000aa");

  private sealed class RecordingStore(ArchiveWriteResult result) : IArchiveStore {
    public List<ArchiveBatch> Written { get; } = [];

    public Task<ArchiveWriteResult> WriteAsync(ArchiveBatch batch, CancellationToken cancellationToken = default) {
      Written.Add(batch);
      return Task.FromResult(result);
    }

    public async IAsyncEnumerable<ArchivedEvent> ReadAsync(
        Guid streamId, long fromVersion = 0, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }

    public Task<ArchiveRestoreResult> RestoreAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task<long> DeleteAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
  }

  private sealed class CountingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<(Guid Stream, long Through, bool Archive)> Truncates { get; } = [];

    Task<StreamTruncateResult> IWorkCoordinator.TruncateStreamPrefixAsync(
        Guid streamId, long throughVersion, bool archive, CancellationToken cancellationToken) {
      Truncates.Add((streamId, throughVersion, archive));
      return Task.FromResult(new StreamTruncateResult("truncated", 5));
    }
  }

  private sealed class CountingHook : IDestructionHook {
    public int After { get; private set; }

    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(DestructionResult.Proceed());

    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      After++;
      return ValueTask.CompletedTask;
    }
  }

  private static ArchiveWriteResult _archived(long events) => new(
    ArchiveStatus.ARCHIVED, events, null, new ArchiveLocator("postgres-table", "wh_event_archive", ArchiveCompression.None), 4);

  [Test]
  public async Task ArchivingTruncate_WritesAPrefixBatchThroughTheStore_NotTheCoordinatorAsync() {
    var store = new RecordingStore(_archived(3));
    var coordinator = new CountingCoordinator();
    var hook = new CountingHook();
    var truncator = new StreamPrefixTruncator(coordinator, NullLogger<StreamPrefixTruncator>.Instance, hook, _noServices, store);

    var result = await truncator.TruncateAsync(_stream, throughVersion: 9, archive: true);

    await Assert.That(store.Written).Count().IsEqualTo(1);
    await Assert.That(store.Written[0]).IsEqualTo(ArchiveBatch.Prefix(_stream, 9))
      .Because("an archiving truncate is a prefix batch, so the truncate's carry-forward guard still applies");
    await Assert.That(coordinator.Truncates).IsEmpty()
      .Because("the store does the copy and the truncate in one transaction; the coordinator must not truncate again");
    await Assert.That(result).IsEqualTo(new StreamTruncateResult("truncated", 3))
      .Because("callers of the truncate read one vocabulary: an archive that moved events is a truncate that happened");
    await Assert.That(hook.After).IsEqualTo(1).Because("the PostDestruction hook fires for an archive that moved events");
  }

  [Test]
  [Arguments("blocked")]
  [Arguments("no_carry_forward")]
  [Arguments("nothing_to_archive")]
  public async Task ArchivingTruncate_TheStoreRefuses_TheRefusalKeepsItsName_AndNoPostHookFiresAsync(string status) {
    var store = new RecordingStore(new ArchiveWriteResult(status, 0, null, null, 4));
    var hook = new CountingHook();
    var truncator = new StreamPrefixTruncator(new CountingCoordinator(), NullLogger<StreamPrefixTruncator>.Instance, hook, _noServices, store);

    var result = await truncator.TruncateAsync(_stream, throughVersion: 9, archive: true);

    await Assert.That(result).IsEqualTo(new StreamTruncateResult(status, 0));
    await Assert.That(hook.After).IsEqualTo(0).Because("nothing was destroyed, so nothing is announced as destroyed");
  }

  [Test]
  public async Task DiscardingTruncate_GoesToTheCoordinator_EvenWithAStoreRegisteredAsync() {
    var store = new RecordingStore(_archived(3));
    var coordinator = new CountingCoordinator();
    var truncator = new StreamPrefixTruncator(coordinator, NullLogger<StreamPrefixTruncator>.Instance, new CountingHook(), _noServices, store);

    var result = await truncator.TruncateAsync(_stream, throughVersion: 9, archive: false);

    await Assert.That(store.Written).IsEmpty().Because("a discarding truncate keeps nothing, so it never reaches the archive");
    await Assert.That(coordinator.Truncates).IsEquivalentTo([(_stream, 9L, false)]);
    await Assert.That(result.EventsTruncated).IsEqualTo(5L);
  }

  [Test]
  public async Task ArchivingTruncate_WithNoStoreRegistered_KeepsTheCoordinatorsCopyAsync() {
    var coordinator = new CountingCoordinator();
    var truncator = new StreamPrefixTruncator(coordinator, NullLogger<StreamPrefixTruncator>.Instance, new CountingHook(), _noServices, archiveStore: null);

    _ = await truncator.TruncateAsync(_stream, throughVersion: 9, archive: true);

    await Assert.That(coordinator.Truncates).IsEquivalentTo([(_stream, 9L, true)])
      .Because("an engine with no archive store keeps archiving the way it did before the store existed");
  }
}
