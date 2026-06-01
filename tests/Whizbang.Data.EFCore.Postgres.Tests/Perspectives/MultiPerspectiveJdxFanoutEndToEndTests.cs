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
/// End-to-end reproduction of the a consumer 2026-05-03 UI loader bug at the generated
/// PerspectiveRunner layer. One <see cref="ConsumerFanoutEvent"/> fans out to TWO
/// generated runners on the SAME stream sharing the SAME real-Postgres DbContext.
/// a consumer symptom: the scalar perspective's row appears, the list-property
/// perspective's row does not. If this test goes RED, the bug is reproduced;
/// if it stays GREEN, the bug is not in the generated runner + per-runner DbContext
/// flow.
/// </summary>
public class MultiPerspectiveConsumerFanoutEndToEndTests : EFCoreTestBase {

  private static IPerspectiveRunner CreateRunnerByName<TModel>(
      string runnerTypeName,
      IServiceProvider sp,
      InMemoryEventStore eventStore,
      EFCorePostgresPerspectiveStore<TModel> perspectiveStore)
      where TModel : class {
    var runnerType = typeof(MultiPerspectiveConsumerFanoutEndToEndTests).Assembly.GetTypes()
        .Single(t => t.Name == runnerTypeName);
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var createLoggerMethod = typeof(LoggerFactoryExtensions)
        .GetMethods()
        .Single(m => m.Name == "CreateLogger" && m.IsGenericMethod)
        .MakeGenericMethod(runnerType);
    var logger = createLoggerMethod.Invoke(null, [loggerFactory])!;
    var ctor = runnerType.GetConstructors().Single();
    return (IPerspectiveRunner)ctor.Invoke([
      sp,
      logger,
      (IEventStore)eventStore,
      (IPerspectiveStore<TModel>)perspectiveStore,
      sp.GetRequiredService<IServiceScopeFactory>(),
      null, // tracingOptions
      null, // snapshotStore
      null, // snapshotOptions
      null  // applyCoordinator (added in IPerspectiveApplyCoordinator rewind-race fix)
    ]);
  }

  private static async Task AppendEventAsync<TEvent>(InMemoryEventStore eventStore, Guid streamId, TEvent payload)
      where TEvent : IEvent {
    var envelope = new MessageEnvelope<TEvent> {
      MessageId = MessageId.New(),
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = []
    };
    await eventStore.AppendAsync(streamId, envelope);
  }

  private static IServiceProvider _buildPerspectiveSp() {
    var services = new ServiceCollection();
    services.AddTransient<ConsumerScalarPerspective>();
    services.AddTransient<ConsumerListPerspective>();
    services.AddLogging();
    return services.BuildServiceProvider();
  }

  /// <summary>
  /// The smoking-gun reproduction. Single event, two perspectives, one shared
  /// DbContext for both upserts. RED here = the a consumer bug is reproduced and
  /// localized to the generated runner / store layer with list-property models.
  /// </summary>
  [Test, Timeout(60000)]
  public async Task TwoRunners_OneEvent_BothPerspectiveRowsPersistAsync(CancellationToken ct) {
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ConsumerFanoutEvent {
      StreamId = streamId,
      Title = "a consumer-Job-1",
      ConditionLabels = ["WC-A", "WC-B", "WC-C"]
    });

    await using var sharedContext = CreateDbContext();
    var scalarStore = new EFCorePostgresPerspectiveStore<ConsumerScalarModel>(sharedContext, "wh_per_jdx_scalar");
    var listStore = new EFCorePostgresPerspectiveStore<ConsumerListModel>(sharedContext, "wh_per_jdx_list");

    var sp = _buildPerspectiveSp();
    var scalarRunner = CreateRunnerByName("ConsumerScalarPerspectiveRunner", sp, eventStore, scalarStore);
    var listRunner = CreateRunnerByName("ConsumerListPerspectiveRunner", sp, eventStore, listStore);

    await scalarRunner.RunAsync(streamId, "wh_per_jdx_scalar", null, ct);
    await listRunner.RunAsync(streamId, "wh_per_jdx_list", null, ct);

    await using var verify = CreateDbContext();
    var savedScalar = await verify.Set<PerspectiveRow<ConsumerScalarModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);
    var savedList = await verify.Set<PerspectiveRow<ConsumerListModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);

    await Assert.That(savedScalar).IsNotNull()
      .Because("ConsumerScalarPerspectiveRunner must persist its row (Order in a consumer terms).");
    await Assert.That(savedList).IsNotNull()
      .Because("ConsumerListPerspectiveRunner must persist its row symmetrically (OrderWorkingConditions in a consumer). RED here = a consumer bug reproduced at runner+store layer.");
    await Assert.That(savedScalar!.Data.Title).IsEqualTo("a consumer-Job-1");
    await Assert.That(savedList!.Data.Conditions.Count).IsEqualTo(3);
    await Assert.That(savedList.Data.Conditions[0].Label).IsEqualTo("WC-A");
    await Assert.That(savedList.Data.Conditions[2].Label).IsEqualTo("WC-C");
  }

  /// <summary>
  /// Cross-DbContext variant: each runner uses its OWN DbContext (the production
  /// pattern when DI provides a scoped DbContext per perspective dispatch). Both
  /// rows must persist. If the smoking-gun test passes but THIS one fails, the
  /// bug is shared-DbContext-specific. If both pass, neither is the bug.
  /// </summary>
  [Test, Timeout(60000)]
  public async Task TwoRunners_SeparateDbContexts_BothPerspectiveRowsPersistAsync(CancellationToken ct) {
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ConsumerFanoutEvent {
      StreamId = streamId,
      Title = "a consumer-Job-2",
      ConditionLabels = ["A", "B"]
    });

    var sp = _buildPerspectiveSp();
    await using (var scalarContext = CreateDbContext()) {
      var scalarStore = new EFCorePostgresPerspectiveStore<ConsumerScalarModel>(scalarContext, "wh_per_jdx_scalar");
      var scalarRunner = CreateRunnerByName("ConsumerScalarPerspectiveRunner", sp, eventStore, scalarStore);
      await scalarRunner.RunAsync(streamId, "wh_per_jdx_scalar", null, ct);
    }
    await using (var listContext = CreateDbContext()) {
      var listStore = new EFCorePostgresPerspectiveStore<ConsumerListModel>(listContext, "wh_per_jdx_list");
      var listRunner = CreateRunnerByName("ConsumerListPerspectiveRunner", sp, eventStore, listStore);
      await listRunner.RunAsync(streamId, "wh_per_jdx_list", null, ct);
    }

    await using var verify = CreateDbContext();
    var savedScalar = await verify.Set<PerspectiveRow<ConsumerScalarModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);
    var savedList = await verify.Set<PerspectiveRow<ConsumerListModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);

    await Assert.That(savedScalar).IsNotNull();
    await Assert.That(savedList).IsNotNull();
    await Assert.That(savedList!.Data.Conditions.Count).IsEqualTo(2);
  }

  /// <summary>
  /// List runner FIRST, scalar second on shared DbContext. Catches a regression
  /// where list-perspective ChangeTracker state breaks scalar perspective.
  /// </summary>
  [Test, Timeout(60000)]
  public async Task TwoRunners_ListFirstScalarSecond_BothPersistAsync(CancellationToken ct) {
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ConsumerFanoutEvent {
      StreamId = streamId,
      Title = "a consumer-Job-Reverse",
      ConditionLabels = ["only-one"]
    });

    await using var sharedContext = CreateDbContext();
    var scalarStore = new EFCorePostgresPerspectiveStore<ConsumerScalarModel>(sharedContext, "wh_per_jdx_scalar");
    var listStore = new EFCorePostgresPerspectiveStore<ConsumerListModel>(sharedContext, "wh_per_jdx_list");

    var sp = _buildPerspectiveSp();
    var scalarRunner = CreateRunnerByName("ConsumerScalarPerspectiveRunner", sp, eventStore, scalarStore);
    var listRunner = CreateRunnerByName("ConsumerListPerspectiveRunner", sp, eventStore, listStore);

    // List FIRST
    await listRunner.RunAsync(streamId, "wh_per_jdx_list", null, ct);
    // Then scalar
    await scalarRunner.RunAsync(streamId, "wh_per_jdx_scalar", null, ct);

    await using var verify = CreateDbContext();
    var savedScalar = await verify.Set<PerspectiveRow<ConsumerScalarModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);
    var savedList = await verify.Set<PerspectiveRow<ConsumerListModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);

    await Assert.That(savedScalar).IsNotNull()
      .Because("Scalar perspective must persist even when list ran first on shared DbContext.");
    await Assert.That(savedList).IsNotNull();
    await Assert.That(savedList!.Data.Conditions[0].Label).IsEqualTo("only-one");
  }

  /// <summary>
  /// Two events for the same stream, both runners process both events on a shared
  /// DbContext. Mirrors the a consumer scenario where a Job has multiple events
  /// (Created, Updated) and both perspectives must keep up.
  /// </summary>
  [Test, Timeout(60000)]
  public async Task TwoRunners_MultipleEventsForSameStream_BothPerspectivesAdvanceAsync(CancellationToken ct) {
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ConsumerFanoutEvent {
      StreamId = streamId,
      Title = "v1",
      ConditionLabels = ["c1"]
    });
    await AppendEventAsync(eventStore, streamId, new ConsumerFanoutEvent {
      StreamId = streamId,
      Title = "v2",
      ConditionLabels = ["c1", "c2"]
    });

    await using var sharedContext = CreateDbContext();
    var scalarStore = new EFCorePostgresPerspectiveStore<ConsumerScalarModel>(sharedContext, "wh_per_jdx_scalar");
    var listStore = new EFCorePostgresPerspectiveStore<ConsumerListModel>(sharedContext, "wh_per_jdx_list");

    var sp = _buildPerspectiveSp();
    var scalarRunner = CreateRunnerByName("ConsumerScalarPerspectiveRunner", sp, eventStore, scalarStore);
    var listRunner = CreateRunnerByName("ConsumerListPerspectiveRunner", sp, eventStore, listStore);

    await scalarRunner.RunAsync(streamId, "wh_per_jdx_scalar", null, ct);
    await listRunner.RunAsync(streamId, "wh_per_jdx_list", null, ct);

    await using var verify = CreateDbContext();
    var savedScalar = await verify.Set<PerspectiveRow<ConsumerScalarModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);
    var savedList = await verify.Set<PerspectiveRow<ConsumerListModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);

    await Assert.That(savedScalar).IsNotNull();
    await Assert.That(savedScalar!.Data.Title).IsEqualTo("v2")
      .Because("Scalar perspective must end at the latest event (v2).");
    await Assert.That(savedList).IsNotNull();
    await Assert.That(savedList!.Data.Title).IsEqualTo("v2");
    await Assert.That(savedList.Data.Conditions.Count).IsEqualTo(2)
      .Because("List perspective must end with the v2 conditions array (2 entries).");
  }

  /// <summary>
  /// Re-run scenario after both perspectives are caught up: running both runners
  /// AGAIN with no new events should be a no-op (idempotency guard kicks in).
  /// Both rows must remain intact and equal to their post-first-run state.
  /// </summary>
  [Test, Timeout(60000)]
  public async Task TwoRunners_RerunWithNoNewEvents_IsIdempotent_RowsUnchangedAsync(CancellationToken ct) {
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ConsumerFanoutEvent {
      StreamId = streamId,
      Title = "stable",
      ConditionLabels = ["x", "y"]
    });

    await using var sharedContext = CreateDbContext();
    var scalarStore = new EFCorePostgresPerspectiveStore<ConsumerScalarModel>(sharedContext, "wh_per_jdx_scalar");
    var listStore = new EFCorePostgresPerspectiveStore<ConsumerListModel>(sharedContext, "wh_per_jdx_list");

    var sp = _buildPerspectiveSp();
    var scalarRunner = CreateRunnerByName("ConsumerScalarPerspectiveRunner", sp, eventStore, scalarStore);
    var listRunner = CreateRunnerByName("ConsumerListPerspectiveRunner", sp, eventStore, listStore);

    await scalarRunner.RunAsync(streamId, "wh_per_jdx_scalar", null, ct);
    await listRunner.RunAsync(streamId, "wh_per_jdx_list", null, ct);

    await using var verifyAfterFirst = CreateDbContext();
    var firstScalar = await verifyAfterFirst.Set<PerspectiveRow<ConsumerScalarModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);
    var firstList = await verifyAfterFirst.Set<PerspectiveRow<ConsumerListModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);
    var firstScalarVersion = firstScalar!.Version;
    var firstListVersion = firstList!.Version;

    // Re-run both — idempotency guard should make this a no-op
    await scalarRunner.RunAsync(streamId, "wh_per_jdx_scalar", null, ct);
    await listRunner.RunAsync(streamId, "wh_per_jdx_list", null, ct);

    await using var verifyAfterRerun = CreateDbContext();
    var afterScalar = await verifyAfterRerun.Set<PerspectiveRow<ConsumerScalarModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);
    var afterList = await verifyAfterRerun.Set<PerspectiveRow<ConsumerListModel>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == streamId, ct);

    await Assert.That(afterScalar).IsNotNull();
    await Assert.That(afterList).IsNotNull();
    await Assert.That(afterScalar!.Version).IsEqualTo(firstScalarVersion)
      .Because("Re-run with no new events must NOT bump scalar version (idempotency).");
    await Assert.That(afterList!.Version).IsEqualTo(firstListVersion)
      .Because("Re-run with no new events must NOT bump list version (idempotency).");
    await Assert.That(afterList.Data.Conditions.Count).IsEqualTo(2);
  }
}
