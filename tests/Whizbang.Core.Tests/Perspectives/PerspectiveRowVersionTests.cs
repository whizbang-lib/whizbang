// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The row-version contract a per-stream apply carries from its read to its write (issue #928): the
/// three states, the defaults a store that does not track versions falls back to, and the conflict the
/// write raises when the row changed underneath it.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives</docs>
public class PerspectiveRowVersionTests {

  [Test]
  public async Task Default_IsUnchecked_SoAStoreThatTracksNothingChecksNothingAsync() {
    var version = default(PerspectiveRowVersion);

    await Assert.That(version).IsEqualTo(PerspectiveRowVersion.Unchecked);
    await Assert.That(version.State).IsEqualTo(PerspectiveRowVersionState.Unchecked);
    await Assert.That(version.IsChecked).IsFalse();
    await Assert.That(version.ToString()).IsEqualTo("unchecked");
  }

  [Test]
  public async Task Absent_IsChecked_AndCarriesNoValueAsync() {
    var version = PerspectiveRowVersion.Absent;

    await Assert.That(version.State).IsEqualTo(PerspectiveRowVersionState.Absent);
    await Assert.That(version.IsChecked).IsTrue();
    await Assert.That(version.Value).IsEqualTo(0L);
    await Assert.That(version.ToString()).IsEqualTo("absent");
  }

  [Test]
  public async Task Of_IsChecked_AndEqualByValueAsync() {
    var version = PerspectiveRowVersion.Of(4_000_000_123L);

    await Assert.That(version.State).IsEqualTo(PerspectiveRowVersionState.Present);
    await Assert.That(version.IsChecked).IsTrue();
    await Assert.That(version.Value).IsEqualTo(4_000_000_123L);
    await Assert.That(version).IsEqualTo(PerspectiveRowVersion.Of(4_000_000_123L));
    await Assert.That(version).IsNotEqualTo(PerspectiveRowVersion.Of(4_000_000_124L));
    await Assert.That(version).IsNotEqualTo(PerspectiveRowVersion.Absent);
    await Assert.That(version.ToString()).IsEqualTo("4000000123");
  }

  [Test]
  public async Task ApplyRead_Unchecked_HasNoMetadataAsync() {
    var read = PerspectiveApplyRead.Unchecked;

    await Assert.That(read.Version).IsEqualTo(PerspectiveRowVersion.Unchecked);
    await Assert.That(read.Metadata).IsNull();
  }

  [Test]
  public async Task Conflict_CarriesTheRowAndBothVersions_InItsMessageAsync() {
    var streamId = Guid.CreateVersion7();

    var conflict = new PerspectiveRowConflictException(
      typeof(Model), streamId, PerspectiveRowVersion.Of(10), PerspectiveRowVersion.Of(11));

    await Assert.That(conflict.ModelType).IsEqualTo(typeof(Model));
    await Assert.That(conflict.StreamId).IsEqualTo(streamId);
    await Assert.That(conflict.ExpectedVersion).IsEqualTo(PerspectiveRowVersion.Of(10));
    await Assert.That(conflict.ActualVersion).IsEqualTo(PerspectiveRowVersion.Of(11));
    await Assert.That(conflict.Message).Contains(streamId.ToString());
    await Assert.That(conflict.Message).Contains(nameof(Model));
    await Assert.That(conflict.Message).Contains("version read: 10");
    await Assert.That(conflict.Message).Contains("version now: 11");
  }

  [Test]
  public async Task Conflict_StandardConstructors_KeepMessageAndInnerAsync() {
    var inner = new InvalidOperationException("inner");

    await Assert.That(new PerspectiveRowConflictException().Message).IsNotEmpty();
    await Assert.That(new PerspectiveRowConflictException("m").Message).IsEqualTo("m");
    var wrapped = new PerspectiveRowConflictException("m", inner);
    await Assert.That(wrapped.InnerException).IsSameReferenceAs(inner);
    await Assert.That(wrapped.ModelType).IsNull();
    await Assert.That(wrapped.ExpectedVersion).IsEqualTo(PerspectiveRowVersion.Unchecked);
  }

  [Test]
  public async Task ReadForApplyAsync_Default_IsUncheckedAndDoesNoReadAsync() {
    var store = new MinimalStore();

    var read = await ((IPerspectiveStore<Model>)store).ReadForApplyAsync(Guid.CreateVersion7());

    await Assert.That(read).IsSameReferenceAs(PerspectiveApplyRead.Unchecked);
    await Assert.That(store.MetadataReads).IsEqualTo(0)
      .Because("a store that does not track row versions keeps the runner's old read pattern: the "
        + "runner itself reads metadata after the model, so the default must not read it too");
  }

  [Test]
  public async Task UpsertAsync_WithExpectedVersion_Default_ForwardsToTheMetadataOverloadAsync() {
    var store = new MinimalStore();
    var metadata = new PerspectiveMetadata { EventId = "e", EventType = "t" };

    await ((IPerspectiveStore<Model>)store).UpsertAsync(
      Guid.CreateVersion7(), new Model(), new PerspectiveScope(), false, metadata, PerspectiveRowVersion.Of(3));

    await Assert.That(store.MetadataUpserts).IsEqualTo(1);
    await Assert.That(store.LastMetadata).IsSameReferenceAs(metadata);
  }

  [Test]
  public async Task UpsertWithPhysicalFieldsAsync_WithExpectedVersion_Default_ForwardsToTheMetadataOverloadAsync() {
    var store = new MinimalStore();
    var metadata = new PerspectiveMetadata { EventId = "e", EventType = "t" };

    await ((IPerspectiveStore<Model>)store).UpsertWithPhysicalFieldsAsync(
      Guid.CreateVersion7(), new Model(), new Dictionary<string, object?>(), null, false, metadata,
      PerspectiveRowVersion.Absent);

    await Assert.That(store.PhysicalMetadataUpserts).IsEqualTo(1);
    await Assert.That(store.LastMetadata).IsSameReferenceAs(metadata);
  }

  private sealed class Model {
    public string Name { get; set; } = string.Empty;
  }

  /// <summary>Overrides only what the defaults forward to, and counts it.</summary>
  private sealed class MinimalStore : IPerspectiveStore<Model> {
    public int MetadataReads { get; private set; }
    public int MetadataUpserts { get; private set; }
    public int PhysicalMetadataUpserts { get; private set; }
    public PerspectiveMetadata? LastMetadata { get; private set; }

    public Task<Model?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default)
      => Task.FromResult<Model?>(null);

    public Task<PerspectiveMetadata?> GetMetadataByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) {
      MetadataReads++;
      return Task.FromResult<PerspectiveMetadata?>(null);
    }

    public Task UpsertAsync(Guid streamId, Model model, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpsertAsync(Guid streamId, Model model, PerspectiveScope scope, bool forceUpdateScope,
        PerspectiveMetadata metadata, CancellationToken cancellationToken = default) {
      MetadataUpserts++;
      LastMetadata = metadata;
      return Task.CompletedTask;
    }

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, Model model, IDictionary<string, object?> physicalFieldValues,
        PerspectiveScope? scope = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, Model model, IDictionary<string, object?> physicalFieldValues,
        PerspectiveScope? scope, bool forceUpdateScope, PerspectiveMetadata metadata, CancellationToken cancellationToken = default) {
      PhysicalMetadataUpserts++;
      LastMetadata = metadata;
      return Task.CompletedTask;
    }

    public Task<Model?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.FromResult<Model?>(null);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, Model model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.CompletedTask;

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.CompletedTask;
  }
}
