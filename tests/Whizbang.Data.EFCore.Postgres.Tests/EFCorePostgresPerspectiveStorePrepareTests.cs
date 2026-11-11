// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What the store prepares for a set-based write (#2246), and when it declines. A row it cannot prepare is written by
/// the per-stream write instead, so declining is always safe; preparing a row the set-based write would then get wrong
/// is not.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresPerspectiveStore.cs</code-under-test>
[Category("Shard1")]
public class EFCorePostgresPerspectiveStorePrepareTests : EFCoreTestBase {
  private const string TABLE = "wh_per_action_test";

  private static PerspectiveMetadata _metadata() =>
    new() { EventType = "E", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow };

  [Test]
  public async Task PrepareWrite_ThroughTheDefaultStrategy_PreparesTheRowAsync() {
    await using var context = CreateDbContext();
    IPerspectiveStore<ActionTestModel> store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE);
    var streamId = Guid.CreateVersion7();

    var write = store.PrepareWrite(
      streamId, new ActionTestModel { Id = streamId, Name = "n" }, new PerspectiveScope(), forceUpdateScope: false,
      _metadata(), PerspectiveRowVersion.Absent, physicalFieldValues: null);

    await Assert.That(write).IsNotNull();
    await Assert.That(write!.StreamId).IsEqualTo(streamId);
    await Assert.That(write.Operation).IsEqualTo(PerspectiveWriteOperation.Insert);
    await Assert.That(write.Row!.Data).Contains("\"n\"");
  }

  [Test]
  public async Task PrepareWrite_ThroughAStrategyThatPreparesNothing_DeclinesAsync() {
    await using var context = CreateDbContext();
    IPerspectiveStore<ActionTestModel> store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE, new NonPreparingStrategy());

    var write = store.PrepareWrite(
      Guid.CreateVersion7(), new ActionTestModel(), new PerspectiveScope(), forceUpdateScope: false,
      _metadata(), PerspectiveRowVersion.Unchecked, physicalFieldValues: null);

    await Assert.That(write).IsNull()
      .Because("a strategy that is not the shared preparation's cannot promise the bytes the per-stream write stores");
  }

  [Test]
  public async Task PreparePurge_OnPostgres_DeletesFromTheMappedTableAsync() {
    await using var context = CreateDbContext();
    IPerspectiveStore<ActionTestModel> store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE);
    var streamId = Guid.CreateVersion7();

    var purge = store.PreparePurge(streamId);

    await Assert.That(purge).IsNotNull();
    await Assert.That(purge!.Operation).IsEqualTo(PerspectiveWriteOperation.Purge);
    await Assert.That(purge.StreamId).IsEqualTo(streamId);
    await Assert.That(purge.Table).EndsWith("\"" + TABLE + "\"");
  }

  [Test]
  public async Task PreparePurge_OnAnotherProvider_DeclinesAsync() {
    var options = new DbContextOptionsBuilder<InMemoryContext>().UseInMemoryDatabase($"prepare-{Guid.NewGuid()}").Options;
    await using var context = new InMemoryContext(options);
    IPerspectiveStore<ActionTestModel> store = new EFCorePostgresPerspectiveStore<ActionTestModel>(context, TABLE);

    await Assert.That(store.PreparePurge(Guid.CreateVersion7())).IsNull()
      .Because("the set-based write runs PostgreSQL statements, so another provider purges through the per-stream path");
  }

  private sealed class InMemoryContext(DbContextOptions<InMemoryContext> options) : DbContext(options);

  /// <summary>An upsert strategy of the consumer's own: it writes, and prepares nothing.</summary>
  private sealed class NonPreparingStrategy : IDbUpsertStrategy {
    public Task UpsertPerspectiveRowAsync<TModel>(
        DbContext context, string tableName, Guid id, TModel model, PerspectiveMetadata metadata, PerspectiveScope scope,
        CancellationToken cancellationToken = default) where TModel : class => Task.CompletedTask;

    public Task UpsertPerspectiveRowAsync<TModel>(
        DbContext context, string tableName, Guid id, TModel model, PerspectiveMetadata metadata, PerspectiveScope scope,
        bool forceUpdateScope, CancellationToken cancellationToken = default) where TModel : class => Task.CompletedTask;

    public Task UpsertPerspectiveRowWithPhysicalFieldsAsync<TModel>(
        DbContext context, string tableName, Guid id, TModel model, PerspectiveMetadata metadata, PerspectiveScope scope,
        IDictionary<string, object?> physicalFieldValues, CancellationToken cancellationToken = default)
        where TModel : class => Task.CompletedTask;

    public Task UpsertPerspectiveRowWithPhysicalFieldsAsync<TModel>(
        DbContext context, string tableName, Guid id, TModel model, PerspectiveMetadata metadata, PerspectiveScope scope,
        IDictionary<string, object?> physicalFieldValues, bool forceUpdateScope, CancellationToken cancellationToken = default)
        where TModel : class => Task.CompletedTask;
  }
}
