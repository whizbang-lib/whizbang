// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// A rebuild of a versioned model (<see cref="IVersionedApplyTarget"/>) whose stream was appended out of event-id
/// order still writes the row it folded (#1819), through the rebuilder, the generated runner and the real store.
/// </summary>
/// <remarks>
/// Version 1 holds the event with the LATER event id and version 2 the one with the EARLIER id: the second event was
/// minted first and appended second. The stored row is the one a live apply of version 1 leaves, stamped with the
/// later id and no commit sequence. The rebuild folds from an empty model in version order and ends on version 2, so
/// its write carries the earlier id. Ordering that write on the event id refused it, and the rebuild reported the
/// stream as folded but left unchanged. A rebuild re-derives the row from every event, so the event id of its last
/// event says nothing about whether the row moved past it; a stale read is refused on the row version instead.
/// </remarks>
/// <docs>fundamentals/perspectives/versioned-apply</docs>
[Category("Integration")]
[Category("Shard1")]
public class VersionedRebuildOrderingTests : EFCoreTestBase {
  private const string PERSPECTIVE_NAME = "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.ItemProgressPerspective";
  private const string TABLE = "wh_per_item_progress";

  [Test]
  public async Task RebuildStreamsAsync_VersionedModelWithEventsAppendedOutOfIdOrder_WritesTheFoldedRowAsync() {
    var streamId = Guid.CreateVersion7();
    // Fixed instants: only their order matters, and a fixed clock keeps the ids deterministic.
    var minted = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var earlier = Guid.CreateVersion7(minted);
    var later = Guid.CreateVersion7(minted.AddSeconds(1));
    await using var sp = _buildServices();

    await using (var appendScope = sp.CreateAsyncScope()) {
      var eventStore = appendScope.ServiceProvider.GetRequiredService<IEventStore>();
      await eventStore.AppendAsync(streamId, _envelope(later, new ItemProgressStartedEvent { StreamId = streamId }));
      await eventStore.AppendAsync(streamId, _envelope(earlier, new ItemProgressCompletedEvent { StreamId = streamId }));
    }
    await using (var seed = CreateDbContext()) {
      var then = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
      seed.Set<PerspectiveRow<ItemProgress>>().Add(new PerspectiveRow<ItemProgress> {
        Id = streamId,
        Data = new ItemProgress { Id = streamId, Started = true, Attempts = 1, Completed = false },
        Metadata = new PerspectiveMetadata {
          EventId = later.ToString("D"),
          EventType = typeof(ItemProgressStartedEvent).FullName!,
          CommitSequence = null,
          Timestamp = then
        },
        Scope = new PerspectiveScope(),
        CreatedAt = then,
        UpdatedAt = then,
        Version = 1
      });
      _ = await seed.SaveChangesAsync();
    }

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>()
        .RebuildStreamsAsync(PERSPECTIVE_NAME, [streamId], CancellationToken.None);

    await using var verify = CreateDbContext();
    var row = await verify.Set<PerspectiveRow<ItemProgress>>().AsNoTracking().SingleAsync(r => r.Id == streamId);
    await Assert.That(result.Success).IsTrue().Because($"the rebuild must report success ({result.Error})");
    await Assert.That(result.StreamsProcessed).IsEqualTo(1);
    await Assert.That(row.Data.Started).IsTrue();
    await Assert.That(row.Data.Attempts).IsEqualTo(1).Because("the rebuild folds from an empty model, not on top of the row");
    await Assert.That(row.Data.Completed).IsTrue()
      .Because("the rebuild folded both events and its write must land, whatever order their event ids sort in");
    await Assert.That(row.Metadata.EventId).IsEqualTo(earlier.ToString("D"))
      .Because("the row records the last event the rebuild folded by stream version");
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
    var asm = typeof(VersionedRebuildOrderingTests).Assembly;
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
