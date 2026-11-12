// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>An event type stored while it was Sourced, then declared ephemeral (the step's catalog says so).</summary>
public sealed record EphemeralHistoryProbePinged : IEvent {
  /// <summary>The session, which is the stream.</summary>
  [StreamId]
  public Guid SessionId { get; init; }
}

/// <summary>The name <see cref="EphemeralHistoryProbePinged"/> had before a rename: its former name in the ledger.</summary>
public sealed record EphemeralHistoryProbeLegacyPinged : IEvent {
  /// <summary>The session, which is the stream.</summary>
  [StreamId]
  public Guid SessionId { get; init; }
}

/// <summary>A Sourced event type of another kind, which makes a stream mixed.</summary>
public sealed record EphemeralHistoryProbeOpened : IEvent {
  /// <summary>The session, which is the stream.</summary>
  [StreamId]
  public Guid SessionId { get; init; }
}

/// <summary>
/// #2206: the history of a type made [Ephemeral] is reclassified by the maintenance cycle, through the per-type history
/// walker (migration 375): the type's own stamped events in commit order, in bounded batches that each commit on their
/// own, resumable after a crash, under the mixed-stream guard, with the digests kept equal to a recount. It used to be one
/// statement over every event of the type, run by the startup reconciler, which startup waited on.
/// Every event here is written through the event store's own append path (#2254), so <c>event_type</c> carries the stored
/// form the walk has to name; the catalog entries are built the way the generator builds them (bare CLR names).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralHistoryReclassifyStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/381_EphemeralHistoryReclassifiesInBatches.sql</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class EphemeralHistoryReclassifySqlTests : EFCoreTestBase {
  private const int EPHEMERAL = 8;
  private static readonly string _type = TypeNameFormatter.Format(typeof(EphemeralHistoryProbePinged));

  // ===== Batches, crashes, resumption =====

  [Test]
  public async Task Reclassify_RunsInBoundedBatches_AndResumesFromItsCursor_AfterACrashAsync() {
    await using var conn = await _openAsync();
    var ids = new List<Guid>();
    for (var i = 0; i < 5; i++) {
      ids.Add(await _appendAsync(conn, (Guid)TrackedGuid.New()));
    }

    // A batch that crashed before its commit leaves nothing behind: not a flag, not a cursor move.
    await _executeAsync(conn, "SELECT wh_type_history_ensure('ephemeral', @t, @t)", ("t", _type));
    await using (var crashed = await conn.BeginTransactionAsync()) {
      var through = await _scalarAsync(conn, "SELECT through_sequence FROM wh_type_history_begin('ephemeral', @t, @t, 10)", ("t", _type));
      await _executeAsync(conn, "SELECT * FROM wh_ephemeral_history_apply(@t, @n, NULL, @through, 10, 5)",
        ("t", _type), ("n", new[] { _type }), ("through", through));
      await Assert.That(await _flagsAsync(conn, ids[0])).IsEqualTo((long)EPHEMERAL).Because("the control: the batch did its work");
      await crashed.RollbackAsync();
    }
    await Assert.That(await _flagsAsync(conn, ids[0])).IsEqualTo(0L);
    await Assert.That(await _scalarAsync(conn,
      "SELECT count(*) FROM wh_type_history_walks WHERE last_commit_sequence IS NOT NULL OR events_changed > 0 OR completed_at IS NOT NULL")).IsEqualTo(0L);

    var first = await _step(2, 1).ReclassifyAsync(_services(), CancellationToken.None);
    var rest = await _step(2, 10).ReclassifyAsync(_services(), CancellationToken.None);
    var again = await _step(2, 10).ReclassifyAsync(_services(), CancellationToken.None);

    await Assert.That(first.Batches).IsEqualTo(1).Because("one batch is the cycle's limit, each its own transaction");
    await Assert.That(first.EventsReclassified).IsEqualTo(2L);
    await Assert.That(rest.Batches).IsEqualTo(2).Because("ceil(5 / 2) batches in all: the next cycle resumes past the first window");
    await Assert.That(rest.EventsReclassified).IsEqualTo(3L);
    await Assert.That(rest.TypesFinished).IsEqualTo(1);
    await Assert.That(again.Status).IsEqualTo("complete").Because("a finished walk reads nothing more");
    foreach (var id in ids) {
      await Assert.That(await _flagsAsync(conn, id)).IsEqualTo((long)EPHEMERAL);
    }
  }

  // ===== The mixed-stream guard and the digests =====

  /// <summary>
  /// A stream holding a Sourced event of another type stays as it is and is reported; the homogeneous streams are
  /// reclassified across several batches; and the stored digests of every stream equal a fold of their Sourced events
  /// afterwards.
  /// </summary>
  [Test]
  public async Task Reclassify_AMixedStream_IsReportedAndLeftUntouched_AndTheDigestsEqualARecountAsync() {
    await using var conn = await _openAsync();
    var a = (Guid)TrackedGuid.New();
    var b = (Guid)TrackedGuid.New();
    var mixed = (Guid)TrackedGuid.New();
    for (var i = 0; i < 3; i++) {
      await _appendAsync(conn, a);
    }
    var mixedEvent = await _appendAsync(conn, mixed);
    await _appendOtherAsync(conn, mixed);
    for (var i = 0; i < 2; i++) {
      await _appendAsync(conn, b);
    }
    var logger = new ListLogger();

    var result = await new EphemeralHistoryReclassifyStep(logger, 2, 10).ReclassifyAsync(_services(), CancellationToken.None);

    await Assert.That(result.Batches).IsEqualTo(3).Because("the type's six events, two per batch");
    await Assert.That(result.EventsReclassified).IsEqualTo(5L);
    await Assert.That(result.EventsBlocked).IsEqualTo(1L).Because("the mixed stream is reported, not silently mixed");
    await Assert.That(await _flagsAsync(conn, mixedEvent)).IsEqualTo(0L)
      .Because("reclassifying it would leave a stream holding both kinds of event");
    await StoredDigestInvariant.AssertAgreeAsync(conn, a, b, mixed);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_stream_digests WHERE stream_id = ANY(@s)", ("s", new[] { a, b })))
      .IsEqualTo(0L).Because("every reclassified event left its bucket, and the emptied buckets are removed");
    await Assert.That(logger.Entries.Count(e => e.Contains("5 reclassified, 1 left Sourced", StringComparison.Ordinal))).IsEqualTo(1);
    await Assert.That(logger.Entries.Count(e => e.Contains($"Ephemeral history of {_type} reclassified", StringComparison.Ordinal))).IsEqualTo(1)
      .Because("one line when the type's walk completes");
  }

  [Test]
  public async Task Reclassify_OnlyMixedStreams_ReportsThemBlocked_AndStillFinishesAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    var target = await _appendAsync(conn, mixed);
    await _appendOtherAsync(conn, mixed);
    var logger = new ListLogger();

    var result = await new EphemeralHistoryReclassifyStep(logger, 10, 10).ReclassifyAsync(_services(), CancellationToken.None);

    await Assert.That(result.EventsReclassified).IsEqualTo(0L);
    await Assert.That(result.EventsBlocked).IsEqualTo(1L);
    await Assert.That(result.TypesFinished).IsEqualTo(1).Because("a blocked event is reported once, not read on every cycle");
    await Assert.That(await _flagsAsync(conn, target)).IsEqualTo(0L);
    await Assert.That(logger.Entries.Count(e => e.Contains("0 reclassified, 1 left Sourced", StringComparison.Ordinal))).IsEqualTo(1)
      .Because("a cycle that only found blocked events still says so");
  }

  /// <summary>History stored under a former name is the same type: it is walked, and a stream holding the type under both
  /// names is not mixed. The ledger names the former type bare, as it names every type; the walk reads it in the stored
  /// form, in the type's assembly (#2254).</summary>
  [Test]
  public async Task Reclassify_FormerNames_AreWalked_AndAStreamHoldingBothIsNotMixedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var old = await _appendLegacyAsync(conn, stream);
    var current = await _appendAsync(conn, stream);

    var result = await _step(10, 10).ReclassifyAsync(_services(new Catalog(_entry(typeof(EphemeralHistoryProbePinged), TypeNameFormatter.FormatClrTypeName(typeof(EphemeralHistoryProbeLegacyPinged)), "")), register: true), CancellationToken.None);

    await Assert.That(result.EventsReclassified).IsEqualTo(2L);
    await Assert.That(result.EventsBlocked).IsEqualTo(0L);
    await Assert.That(result.TypesFinished).IsEqualTo(2).Because("one walk per stored name; a blank ledger name is none");
    await Assert.That(await _flagsAsync(conn, old)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, current)).IsEqualTo((long)EPHEMERAL);
  }

  [Test]
  public async Task Reclassify_EventsUnstampedAtTheStart_AreReclassifiedOnceStampedAsync() {
    await using var conn = await _openAsync();
    var stamped = await _appendAsync(conn, (Guid)TrackedGuid.New());
    var unstamped = await _appendAsync(conn, (Guid)TrackedGuid.New(), stamp: false);

    var first = await _step(10, 10).ReclassifyAsync(_services(), CancellationToken.None);
    await _stampAsync(conn);
    var second = await _step(10, 10).ReclassifyAsync(_services(), CancellationToken.None);

    await Assert.That(first.Status).StartsWith("waiting");
    await Assert.That(await _flagsAsync(conn, stamped)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(second.EventsReclassified).IsEqualTo(1L);
    await Assert.That(second.TypesFinished).IsEqualTo(1);
    await Assert.That(await _flagsAsync(conn, unstamped)).IsEqualTo((long)EPHEMERAL);
  }

  // ===== The names the walk reads by (#2254) =====

  /// <summary>
  /// The walk names the type by the stored form the append path wrote (<c>Ns.Type, Assembly</c>), never the catalog's bare
  /// CLR name, which matches no stored <c>event_type</c>: walked under the bare name, a type's walk read nothing and was
  /// marked complete with every count at 0. The type-definition registry keys the type by its bare name, so the walk
  /// takes its definition from that one.
  /// </summary>
  [Test]
  public async Task TheWalk_NamesTheTypeAsTheAppendPathStoredIt_AndItsDefinitionByTheBareNameAsync() {
    await using var conn = await _openAsync();
    var bare = TypeNameFormatter.FormatClrTypeName(typeof(EphemeralHistoryProbePinged));
    var definition = await _scalarAsync(conn,
      "SELECT definition_id FROM register_type_definition(@t, decode('aa', 'hex'), decode('bb', 'hex'), 0)", ("t", bare));
    var first = await _appendAsync(conn, (Guid)TrackedGuid.New());
    var second = await _appendAsync(conn, (Guid)TrackedGuid.New());
    var stored = await _textsAsync(conn, "SELECT DISTINCT event_type FROM wh_event_store WHERE event_id = ANY(@ids)", ("ids", new[] { first, second }));

    var result = await _step(10, 10).ReclassifyAsync(_services(), CancellationToken.None);

    await Assert.That(stored).IsEquivalentTo([_type]).Because("the control: the append path stores the type's stored form");
    await Assert.That(result.EventsReclassified).IsEqualTo(2L).Because("the walk read the events the append path stored");
    await Assert.That(await _flagsAsync(conn, first)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, second)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _textsAsync(conn, "SELECT event_type FROM wh_type_history_walks WHERE operation = 'ephemeral'"))
      .IsEquivalentTo(stored).Because("one walk, keyed by the name the events are stored under");
    await Assert.That(await _scalarAsync(conn,
        "SELECT definition_watermark FROM wh_type_history_walks WHERE operation = 'ephemeral' AND event_type = @t", ("t", _type)))
      .IsEqualTo(definition).Because("the definition registry knows the type by its bare name, so the walk starts under that definition");
  }

  /// <summary>
  /// A database the bare-name walk already ran on holds a completed walk under the bare name. It stays as it is, and the
  /// stored-name walk is a row of its own that reads the events.
  /// </summary>
  [Test]
  public async Task AWalkCompletedUnderTheBareName_DoesNotStopTheStoredNameWalkAsync() {
    await using var conn = await _openAsync();
    var bare = TypeNameFormatter.FormatClrTypeName(typeof(EphemeralHistoryProbePinged));
    await _executeAsync(conn, "SELECT wh_type_history_ensure('ephemeral', @t, @t)", ("t", bare));
    await _executeAsync(conn, "UPDATE wh_type_history_walks SET completed_at = NOW() WHERE operation = 'ephemeral' AND event_type = @t", ("t", bare));
    var id = await _appendAsync(conn, (Guid)TrackedGuid.New());

    var result = await _step(10, 10).ReclassifyAsync(_services(), CancellationToken.None);

    await Assert.That(result.EventsReclassified).IsEqualTo(1L);
    await Assert.That(result.TypesFinished).IsEqualTo(1);
    await Assert.That(await _flagsAsync(conn, id)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _scalarAsync(conn,
        "SELECT count(*) FROM wh_type_history_walks WHERE operation = 'ephemeral' AND event_type = @t AND completed_at IS NOT NULL AND events_changed = 0",
        ("t", bare)))
      .IsEqualTo(1L).Because("the bare-name walk is left where it is; it never held anything to change");
  }

  // ===== Cost =====

  [Test]
  public async Task TheWalk_ReadsTheTypeThroughItsIndex_NeverTheStoreAsync() {
    await using (var conn = await _openAsync()) {
      for (var half = 0; half < 2; half++) {
        for (var i = 0; i < 25; i++) {
          await _appendAsync(conn, (Guid)TrackedGuid.New());
        }
        await EventStorePlanCapture.SeedOtherTypesAsync(conn, 20_000);
      }
      await EventStorePlanCapture.BuildTypeIndexAsync(conn);
    }
    var capture = new EventStorePlanCapture();

    var result = await _step(20, 100).ReclassifyAsync(_services(capture.Connection(ConnectionString)), CancellationToken.None);

    var scans = capture.Scans();
    await Assert.That(result.EventsReclassified).IsEqualTo(50L).Because("precondition: the walk did its work");
    await Assert.That(result.Batches).IsEqualTo(3).Because("ceil(50 / 20) windows of the type's own events");
    await Assert.That(scans.Any(s => s.Node.Contains(EventStorePlanCapture.TYPE_INDEX, StringComparison.Ordinal)
      && s.IndexConditions.Contains("event_type", StringComparison.Ordinal))).IsTrue()
      .Because("the window is the type's range of idx_event_store_type_commit_sequence");
    await Assert.That(scans.Where(s => s.Node.StartsWith("Seq Scan", StringComparison.Ordinal)).Select(s => s.Node)).IsEmpty();
    var fetched = scans.Sum(s => s.RowsFetched);
    await Assert.That(fetched).IsLessThan(2_000L)
      .Because($"the walk reads the type's 50 events a few times over; it fetched {fetched} rows of a 40,050-event store: "
        + string.Join("; ", scans.Where(s => s.RowsFetched > 100).Select(s => $"{s.Node} [{s.IndexConditions}] {s.RowsFetched}")));
  }

  // ===== The step =====

  [Test]
  public async Task Step_WithTheOptInOff_TouchesNothingAsync() {
    await using var conn = await _openAsync();
    var id = await _appendAsync(conn, (Guid)TrackedGuid.New());
    var services = new ServiceCollection()
      .AddSingleton(_connection())
      .AddSingleton<IMessageTypeCatalog>(new Catalog(_entry(typeof(EphemeralHistoryProbePinged))))
      .BuildServiceProvider();

    var result = await _step(10, 10).ReclassifyAsync(services, CancellationToken.None);
    await _step(10, 10).RunAsync(services, CancellationToken.None);

    await Assert.That(result.Status).IsEqualTo("skipped (Whizbang:Ephemeral:ReconcileHistoricalOnStartup=false)")
      .Because("reclassifying stored history is a deliberate step; it stays an opt-in");
    await Assert.That(await _flagsAsync(conn, id)).IsEqualTo(0L);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_type_history_walks")).IsEqualTo(0L);
  }

  [Test]
  public async Task Step_WalksOnlyEphemeralEventTypes_AndNothingWithoutACatalogAsync() {
    await using var conn = await _openAsync();
    var durable = await _appendOtherAsync(conn, (Guid)TrackedGuid.New());
    var command = await _appendAsync(conn, (Guid)TrackedGuid.New());
    var catalogs = new IMessageTypeCatalog?[] {
      new Catalog(new MessageTypeCatalogEntry(typeof(EphemeralHistoryProbeOpened), TypeNameFormatter.FormatClrTypeName(typeof(EphemeralHistoryProbeOpened)), "event", null),
        new MessageTypeCatalogEntry(typeof(EphemeralHistoryProbePinged), TypeNameFormatter.FormatClrTypeName(typeof(EphemeralHistoryProbePinged)), "command", null) { Ephemeral = new EphemeralInfo(default, default) }),
      NullMessageTypeCatalog.Instance,
      null,
    };

    foreach (var catalog in catalogs) {
      var result = await _step(10, 10).ReclassifyAsync(_services(catalog, register: catalog is not null), CancellationToken.None);
      await Assert.That(result.Batches).IsEqualTo(0);
      await Assert.That(result.Status).IsEqualTo("ok");
    }

    await Assert.That(await _flagsAsync(conn, durable)).IsEqualTo(0L).Because("a type that is not ephemeral keeps its history");
    await Assert.That(await _flagsAsync(conn, command)).IsEqualTo(0L).Because("only event types are reclassified");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_type_history_walks")).IsEqualTo(0L);
  }

  [Test]
  public async Task Apply_AWindowThatIsNotItsOwn_ChangesNothingAsync() {
    await using var conn = await _openAsync();
    var id = await _appendAsync(conn, (Guid)TrackedGuid.New());
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT status FROM wh_ephemeral_history_apply(@t, @n, NULL, 9223372036854775807, 10, 1)";
    cmd.Parameters.AddWithValue("t", _type);
    cmd.Parameters.AddWithValue("n", new[] { _type });

    var status = (string)(await cmd.ExecuteScalarAsync())!;

    await Assert.That(status).StartsWith("skipped").Because("an apply with no walk begun in its transaction applies nothing");
    await Assert.That(await _flagsAsync(conn, id)).IsEqualTo(0L);
  }

  [Test]
  public async Task FromServices_AndItsArguments_AreCheckedAsync() {
    await Assert.That(EphemeralHistoryReclassifyStep.FromServices(new ServiceCollection().AddLogging().BuildServiceProvider())).IsNotNull();
    await Assert.That(EphemeralHistoryReclassifyStep.FromServices(new ServiceCollection().BuildServiceProvider())).IsNotNull();
    await Assert.That(() => EphemeralHistoryReclassifyStep.FromServices(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new EphemeralHistoryReclassifyStep(null!, 1, 1)).Throws<ArgumentNullException>();
    await Assert.That(async () => await _step(1, 1).ReclassifyAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
    await Assert.That(_step(1, 1).Name).IsEqualTo("ephemeral-history-reclassify");
  }

  [Test]
  public async Task ThePostgresDriver_RegistersTheStepAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<EphemeralHistoryReclassifyStep>().ToList();

    await Assert.That(steps.Count).IsEqualTo(1)
      .Because("the history is reclassified by the maintenance cycle, idle-gated, under the maintainer turn, never at startup");
  }

  // ===== Helpers =====

  private sealed class Catalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  private sealed class ListLogger : ILogger {
    public List<string> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add(formatter(state, exception));
  }

  /// <summary>A catalog entry as the generator writes it: the bare CLR name, the ledger's bare former names.</summary>
  private static MessageTypeCatalogEntry _entry(Type type, params string[] formerNames) =>
    new(type, TypeNameFormatter.FormatClrTypeName(type), "event", null) {
      Ephemeral = new EphemeralInfo(default, default),
      FormerNames = formerNames,
    };

  private static EphemeralHistoryReclassifyStep _step(int scanSize, int maxBatches) => new(NullLogger.Instance, scanSize, maxBatches);

  private PostgresArchiveConnection _connection() =>
    new(async ct => {
      var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, "public");

  private ServiceProvider _services(PostgresArchiveConnection? connection = null) =>
    _services(new Catalog(_entry(typeof(EphemeralHistoryProbePinged))), register: true, connection);

  private ServiceProvider _services(IMessageTypeCatalog? catalog, bool register, PostgresArchiveConnection? connection = null) {
    var services = new ServiceCollection()
      .AddSingleton(connection ?? _connection())
      .AddSingleton(Options.Create(new EphemeralOptions { ReconcileHistoricalOnStartup = true }));
    if (register) {
      services.AddSingleton(catalog!);
    }
    return services.BuildServiceProvider();
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  /// <summary>An event of the type, stored through the event store's append path while the type was Sourced, numbered by
  /// the commit-order stamper unless told not to.</summary>
  private Task<Guid> _appendAsync(NpgsqlConnection conn, Guid stream, bool stamp = true) =>
    _storeAsync(conn, stream, new EphemeralHistoryProbePinged { SessionId = stream }, stamp);

  /// <summary>An event stored under the type's former name, before the rename.</summary>
  private Task<Guid> _appendLegacyAsync(NpgsqlConnection conn, Guid stream) =>
    _storeAsync(conn, stream, new EphemeralHistoryProbeLegacyPinged { SessionId = stream }, stamp: true);

  /// <summary>A Sourced event of another type.</summary>
  private Task<Guid> _appendOtherAsync(NpgsqlConnection conn, Guid stream) =>
    _storeAsync(conn, stream, new EphemeralHistoryProbeOpened { SessionId = stream }, stamp: true);

  private async Task<Guid> _storeAsync<T>(NpgsqlConnection conn, Guid stream, T payload, bool stamp) where T : IEvent {
    var envelope = new MessageEnvelope<T> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UnixEpoch, ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
    await using (var context = CreateDbContext()) {
      await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(stream, envelope);
    }
    if (stamp) {
      await _stampAsync(conn);
    }
    return envelope.MessageId.Value;
  }

  private static Task _stampAsync(NpgsqlConnection conn) => _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");

  private static Task<long> _flagsAsync(NpgsqlConnection conn, Guid eventId) =>
    _scalarAsync(conn, "SELECT COALESCE(flags, 0) FROM wh_event_store WHERE event_id = @id", ("id", eventId));

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<List<string>> _textsAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var texts = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      texts.Add(reader.GetString(0));
    }
    return texts;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
