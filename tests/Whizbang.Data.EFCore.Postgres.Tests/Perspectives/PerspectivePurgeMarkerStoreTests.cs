using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A purged perspective row stays purged (#1027) over a real database: the markers in
/// <c>wh_stream_purge_markers</c> (migration 180), written and read by the generated runner over the EF Core
/// perspective store. A purge writes the marker before it removes the row, a later batch on the stream creates
/// nothing, a rebuild of the stream leaves no row, and a resurrect clears the marker.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresPerspectivePurgeMarkerStore.cs</code-under-test>
/// <docs>fundamentals/perspectives/perspectives-with-actions#purge-stays-purged</docs>
[Category("Integration")]
[Category("Shard4")]
public class PerspectivePurgeMarkerStoreTests : EFCoreTestBase {
  private const string PERSPECTIVE_NAME = "action_test";
  private const string TABLE = "wh_per_action_test";

  [Test]
  public async Task Markers_RoundTrip_AndTheStreamMarkerCoversEveryPerspectiveAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var markers = new PostgresPerspectivePurgeMarkerStore(dataSource.OpenConnectionAsync);
    var perspectiveOnly = Guid.CreateVersion7();
    var wholeStream = Guid.CreateVersion7();
    var purgeEventId = Guid.CreateVersion7();

    await Assert.That(await markers.IsPurgedAsync(perspectiveOnly, PERSPECTIVE_NAME)).IsFalse();
    await markers.MarkPurgedAsync(perspectiveOnly, PERSPECTIVE_NAME, purgeEventId);
    await markers.MarkPurgedAsync(perspectiveOnly, PERSPECTIVE_NAME, purgeEventId);
    await markers.MarkPurgedAsync(wholeStream, PerspectivePurgeMarkers.ALL_PERSPECTIVES, null);

    await Assert.That(await markers.IsPurgedAsync(perspectiveOnly, PERSPECTIVE_NAME)).IsTrue();
    await Assert.That(await markers.IsPurgedAsync(perspectiveOnly, "another_perspective")).IsFalse()
      .Because("a perspective's purge is its own");
    await Assert.That(await markers.IsPurgedAsync(wholeStream, "any_perspective")).IsTrue()
      .Because("a stream marker covers every perspective");

    await markers.ClearAsync(perspectiveOnly, PERSPECTIVE_NAME);
    await markers.ClearAsync(wholeStream, PERSPECTIVE_NAME);

    await Assert.That(await markers.IsPurgedAsync(perspectiveOnly, PERSPECTIVE_NAME)).IsFalse();
    await Assert.That(await markers.IsPurgedAsync(wholeStream, PERSPECTIVE_NAME)).IsTrue()
      .Because("clearing one perspective leaves the stream marker governing the others");
  }

  [Test]
  public async Task Purge_ThenALaterBatch_LeavesNoRow_AndRebuildAgreesAsync() {
    var stream = Guid.CreateVersion7();
    var events = new InMemoryEventStore();
    _ = await _appendAsync(events, stream, new ActionTestCreatedEvent { StreamId = stream, Name = "order", Value = 1 });
    var purged = await _appendAsync(events, stream, new ActionTestPurgedEvent { StreamId = stream });
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var markers = new PostgresPerspectivePurgeMarkerStore(dataSource.OpenConnectionAsync);
    await using var storeContext = CreateDbContext();
    var rows = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext, TABLE);
    var runner = PurgeMarkerRunner.Create(events, rows, markers);

    await runner.RunAsync(stream, PERSPECTIVE_NAME, null, CancellationToken.None);
    await Assert.That(await _markerEventAsync(stream)).IsEqualTo(purged)
      .Because("the purge records the event that purged");

    // The delayed follow-up the issue describes: a version bump about thirty seconds later.
    await _appendAsync(events, stream, new ActionTestUpdatedEvent { StreamId = stream, NewValue = 2 });
    await runner.RunAsync(stream, PERSPECTIVE_NAME, purged, CancellationToken.None);
    await Assert.That(await _rowCountAsync(stream)).IsEqualTo(0).Because("the follow-up must not recreate an empty row");

    await runner.RunRebuildAsync(stream, PERSPECTIVE_NAME, CancellationToken.None);
    await Assert.That(await _rowCountAsync(stream)).IsEqualTo(0)
      .Because("a rebuild of a deleted-then-bumped stream decides exactly as the live drain did");
  }

  // -------------------------------------------------------------------------------------------

  private async Task<long> _rowCountAsync(Guid stream) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand($"SELECT count(*) FROM {TABLE} WHERE id = $1", connection);
    command.Parameters.AddWithValue(stream);
    return (long)(await command.ExecuteScalarAsync())!;
  }

  private async Task<Guid?> _markerEventAsync(Guid stream) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
      "SELECT purge_event_id FROM wh_stream_purge_markers WHERE stream_id = $1 AND perspective_name = $2", connection);
    command.Parameters.AddWithValue(stream);
    command.Parameters.AddWithValue(PERSPECTIVE_NAME);
    return await command.ExecuteScalarAsync() as Guid?;
  }

  private static async Task<Guid> _appendAsync<TEvent>(InMemoryEventStore events, Guid stream, TEvent payload) where TEvent : IEvent {
    var envelope = new MessageEnvelope<TEvent> {
      MessageId = MessageId.New(),
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    await events.AppendAsync(stream, envelope);
    return envelope.MessageId.Value;
  }
}

/// <summary>
/// Builds the generated <c>ActionTestPerspectiveRunner</c> with purge markers registered, the way the driver
/// registers them. Resolved by name: naming a generated type in source does not compile in a workspace that loads
/// without running the generators (the formatting gate).
/// </summary>
internal static class PurgeMarkerRunner {
  public static IPerspectiveRunner Create(
      IEventStore events, IPerspectiveStore<ActionTestModel> rows, IPerspectivePurgeMarkerStore markers) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddTransient<ActionTestPerspective>();
    services.AddLogging();
    services.AddSingleton(markers);
    var sp = services.BuildServiceProvider();
    var runnerType = typeof(PurgeMarkerRunner).Assembly.GetTypes().Single(t => t.Name == "ActionTestPerspectiveRunner");
    var logger = typeof(LoggerFactoryExtensions)
      .GetMethods()
      .Single(m => m.Name == "CreateLogger" && m.IsGenericMethod)
      .MakeGenericMethod(runnerType)
      .Invoke(null, [sp.GetRequiredService<ILoggerFactory>()])!;
    var ctor = runnerType.GetConstructors().Single();
    var args = new object?[ctor.GetParameters().Length];
    args[0] = sp;
    args[1] = logger;
    args[2] = events;
    args[3] = rows;
    args[4] = sp.GetRequiredService<IServiceScopeFactory>();
    return (IPerspectiveRunner)ctor.Invoke(args);
  }
}
