// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A correction rebuild after events were removed from a stream (#2252), through the rebuilder, the generated runner and
/// the real store. The stored row is stamped at the commit sequence of an event that is no longer in the stream, so the
/// rebuild's write, stamped at the stream's last remaining event, sorts below it and the row's ordering guard refuses
/// it. When the fold equals the stored row the read model was already right: the stream is rebuilt and verified. When
/// it differs, the refusal is a real failure and is reported as one.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#reading-the-result</docs>
[Category("Integration")]
[Category("Shard1")]
public class RebuildAlreadyCorrectRowTests : EFCoreTestBase {
  private const string PERSPECTIVE_NAME = "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.ItemProgressPerspective";
  private const string TABLE = "wh_per_item_progress";

  // The removed duplicate's position, above every event the stream still holds.
  private const long REMOVED_EVENT_SEQUENCE = 900_000;

  [Test]
  public async Task RebuildStreamsAsync_AFoldEqualToTheStoredRowTheGuardRefused_IsRebuiltAndVerifiedAsync() {
    var streamId = TrackedGuid.New().Value;
    await using var sp = _buildServices();
    await _appendStartedThenCompletedAsync(sp, streamId);
    await _seedRowAsync(streamId, new ItemProgress { Id = streamId, Started = true, Attempts = 1, Completed = true });

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>()
        .RebuildStreamsAsync(PERSPECTIVE_NAME, [streamId], CancellationToken.None);

    await Assert.That(result.Success).IsTrue().Because($"the row already held the fold ({result.Error})");
    await Assert.That(result.StreamsProcessed).IsEqualTo(1);
    await Assert.That(result.StreamsAlreadyCorrect).IsEqualTo(1);
    await Assert.That(result.StreamsWriteRefused).IsEqualTo(0);
  }

  [Test]
  public async Task RebuildStreamsAsync_AFoldThatDiffersFromTheStoredRowTheGuardRefused_FailsAsRefusedAsync() {
    var streamId = TrackedGuid.New().Value;
    await using var sp = _buildServices();
    await _appendStartedThenCompletedAsync(sp, streamId);
    // Two Started events were folded before the duplicate was removed.
    await _seedRowAsync(streamId, new ItemProgress { Id = streamId, Started = true, Attempts = 2, Completed = true });

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>()
        .RebuildStreamsAsync(PERSPECTIVE_NAME, [streamId], CancellationToken.None);

    await using var verify = CreateDbContext();
    var row = await verify.Set<PerspectiveRow<ItemProgress>>().AsNoTracking().SingleAsync(r => r.Id == streamId);
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.StreamsWriteRefused).IsEqualTo(1);
    await Assert.That(result.StreamsAlreadyCorrect).IsEqualTo(0);
    await Assert.That(result.Error).Contains("1 folded but the row's ordering guard refused the write");
    await Assert.That(row.Data.Attempts).IsEqualTo(2).Because("the refused write left the row as it was");
  }

  private async Task _appendStartedThenCompletedAsync(ServiceProvider sp, Guid streamId) {
    var minted = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var started = Guid.CreateVersion7(minted);
    var completed = Guid.CreateVersion7(minted.AddSeconds(1));
    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      await eventStore.AppendAsync(streamId, _envelope(started, new ItemProgressStartedEvent { StreamId = streamId }));
      await eventStore.AppendAsync(streamId, _envelope(completed, new ItemProgressCompletedEvent { StreamId = streamId }));
    }
    await using var stamp = CreateDbContext();
    _ = await stamp.Database.ExecuteSqlRawAsync(
        "UPDATE wh_event_store SET commit_sequence = CASE WHEN event_id = {0} THEN 10 ELSE 11 END WHERE stream_id = {1}",
        started, streamId);
  }

  private async Task _seedRowAsync(Guid streamId, ItemProgress data) {
    await using var seed = CreateDbContext();
    var then = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    seed.Set<PerspectiveRow<ItemProgress>>().Add(new PerspectiveRow<ItemProgress> {
      Id = streamId,
      Data = data,
      Metadata = new PerspectiveMetadata {
        EventId = TrackedGuid.New().Value.ToString("D"),
        EventType = typeof(ItemProgressStartedEvent).FullName!,
        CommitSequence = REMOVED_EVENT_SEQUENCE,
        Timestamp = then
      },
      Scope = new PerspectiveScope(),
      CreatedAt = then,
      UpdatedAt = then,
      Version = 1
    });
    _ = await seed.SaveChangesAsync();
  }

  private static MessageEnvelope<TEvent> _envelope<TEvent>(Guid id, TEvent payload) where TEvent : IEvent => new() {
    MessageId = MessageId.From(id),
    Payload = payload,
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    Hops = []
  };

  private ServiceProvider _buildServices() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddScoped<WorkCoordinationDbContext>(_ => new WorkCoordinationDbContext(DbContextOptions));
    services.AddScoped<DbContext>(sp => sp.GetRequiredService<WorkCoordinationDbContext>());
    services.AddScoped<IEventStore>(sp =>
        new EFCoreEventStore<WorkCoordinationDbContext>(sp.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddScoped<IEventStoreQuery>(sp =>
        new EFCoreFilterableEventStoreQuery(sp.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddScoped<IPerspectiveStore<ItemProgress>>(sp =>
        new EFCorePostgresPerspectiveStore<ItemProgress>(sp.GetRequiredService<WorkCoordinationDbContext>(), TABLE));
    services.AddScoped<ItemProgressPerspective>();

    // Located by name so this file compiles before the generator has produced output, as the other rebuild tests do.
    var asm = typeof(RebuildAlreadyCorrectRowTests).Assembly;
    services.AddScoped(asm.GetTypes().Single(t => t.Name == "ItemProgressPerspectiveRunner"));
    var registryType = asm.GetTypes().Single(t => t.Name == "PerspectiveRunnerRegistry" &&
        t.Namespace == "Whizbang.Data.EFCore.Postgres.Tests.Generated");
    services.AddSingleton(typeof(IPerspectiveRunnerRegistry), registryType);

    services.AddScoped<IPerspectiveCheckpointCompleter>(sp =>
        new EFCorePostgresPerspectiveCheckpointCompleter(sp.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddSingleton<IPerspectiveRebuilder, PerspectiveRebuilder>();
    return services.BuildServiceProvider();
  }
}
