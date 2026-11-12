// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2278: the ephemeral history walk converts stream by stream. A session stream whose event types are all declared
/// ephemeral, written while they were durable, is converted by the first walk that reaches it, every declared-ephemeral
/// event in it at once, so a stream goes from durable to ephemeral as a unit. Before, each type's walk refused the stream
/// because the other declared types' events were still durable, and none could ever go first. A stream holding a
/// genuinely durable type is refused whole, and the report names only that type.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#a-stream-becomes-ephemeral-whole</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralHistoryReclassifyStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/397_TypesDeclaredEphemeralTogetherConvertTheirStreamsWhole.sql</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class EphemeralStreamByStreamSqlTests : EFCoreTestBase {
  private const int EPHEMERAL = 8;
  // Real types, named the way the append path stores them (#2254).
  private static readonly string UPDATED = TypeNameFormatter.Format(typeof(SessionStreamUpdated));
  private static readonly string CREATED = TypeNameFormatter.Format(typeof(SessionStreamCreated));
  private static readonly string OWNED = TypeNameFormatter.Format(typeof(SessionStreamOwned));
  private static readonly string NOTED = TypeNameFormatter.Format(typeof(SessionStreamNoted));
  private const string GENERATION = "SELECT setting_value::bigint FROM wh_settings WHERE setting_key = 'integrity_origin_generation'";

  // ===== One walk converts the stream =====

  [Test]
  public async Task OneWalk_ConvertsEveryDeclaredEphemeralTypeInTheStream_Together_AndTheDigestsEqualARecountAsync() {
    await using var conn = await _openAsync();
    var session = (Guid)TrackedGuid.New();
    var elsewhere = (Guid)TrackedGuid.New();
    var updated1 = await _appendAsync(conn, session, UPDATED);
    var created = await _appendAsync(conn, session, CREATED);
    var updated2 = await _appendAsync(conn, session, UPDATED);
    var createdElsewhere = await _appendAsync(conn, elsewhere, CREATED);
    var generation = await _scalarAsync(conn, GENERATION);

    // One batch: the walk of the first type only.
    var result = await new EphemeralHistoryReclassifyStep(new ListLogger(), 10, 1).ReclassifyAsync(_services(), CancellationToken.None);

    await Assert.That(result.Batches).IsEqualTo(1).Because("precondition: only the first type's walk ran");
    await Assert.That(result.EventsBlocked).IsEqualTo(0L)
      .Because("the other declared-ephemeral type is no blocker: before #2278 it blocked the stream forever");
    await Assert.That(result.EventsReclassified).IsEqualTo(2L);
    await Assert.That(result.EventsReclassifiedWithStream).IsEqualTo(1L)
      .Because("the other declared type's event in the stream is converted with it and counted on its own");
    await Assert.That(await _flagsAsync(conn, updated1)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, updated2)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, created)).IsEqualTo((long)EPHEMERAL)
      .Because("the stream is converted as a unit, in the same statement, never left partly converted");
    await Assert.That(await _flagsAsync(conn, createdElsewhere)).IsEqualTo(0L)
      .Because("only the walked type's streams are converted by its walk; the other type's own walk reaches its own streams");
    await StoredDigestInvariant.AssertAgreeAsync(conn, session, elsewhere);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_stream_digests WHERE stream_id = @s", ("s", session)))
      .IsEqualTo(0L).Because("every converted event left its bucket, the other type's included, and the emptied buckets are removed");
    await Assert.That(await _scalarAsync(conn, GENERATION)).IsEqualTo(generation + 1)
      .Because("one batch, one conversion: the origin generation moves once, as for a single-type conversion");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_ephemeral_blockers_report()")).IsEqualTo(0L);
  }

  [Test]
  public async Task TheOtherTypesLaterWalk_FindsItsEventsAlreadyEphemeral_AndTheCountersStayTruthfulAsync() {
    await using var conn = await _openAsync();
    var session = (Guid)TrackedGuid.New();
    await _appendAsync(conn, session, UPDATED);
    await _appendAsync(conn, session, CREATED);
    await _appendAsync(conn, session, UPDATED);
    var createdElsewhere = await _appendAsync(conn, (Guid)TrackedGuid.New(), CREATED);

    var result = await _step().ReclassifyAsync(_services(), CancellationToken.None);

    await Assert.That(result.TypesFinished).IsEqualTo(2);
    await Assert.That(result.EventsReclassified).IsEqualTo(3L).Because("each walk counts the events of its own type it converted");
    await Assert.That(result.EventsReclassifiedWithStream).IsEqualTo(1L);
    await Assert.That(result.EventsBlocked).IsEqualTo(0L);
    await Assert.That(await _flagsAsync(conn, createdElsewhere)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _walkAsync(conn, UPDATED)).IsEqualTo((2L, 2L, 0L));
    await Assert.That(await _walkAsync(conn, CREATED)).IsEqualTo((2L, 1L, 0L))
      .Because("the later walk reads both of its events and changes only the one the first walk's stream did not hold");
  }

  // ===== A genuinely durable type refuses the stream whole =====

  [Test]
  public async Task AStreamHoldingAGenuinelyDurableType_IsRefusedWhole_AndTheReportNamesOnlyThatTypeAsync() {
    await using var conn = await _openAsync();
    var session = (Guid)TrackedGuid.New();
    var updated = await _appendAsync(conn, session, UPDATED);
    var created = await _appendAsync(conn, session, CREATED);
    var owned = await _appendAsync(conn, session, OWNED);

    var result = await _step().ReclassifyAsync(_services(), CancellationToken.None);

    await Assert.That(result.EventsBlocked).IsEqualTo(2L).Because("each declared type's walk leaves its event Sourced");
    await Assert.That(result.EventsReclassifiedWithStream).IsEqualTo(0L);
    await Assert.That(await _flagsAsync(conn, updated)).IsEqualTo(0L);
    await Assert.That(await _flagsAsync(conn, created)).IsEqualTo(0L);
    await Assert.That(await _flagsAsync(conn, owned)).IsEqualTo(0L);
    await Assert.That(await _blockersAsync(conn)).IsEquivalentTo([$"{CREATED}|{OWNED}", $"{UPDATED}|{OWNED}"])
      .Because("the report is the list a developer acts on: a declared-ephemeral type is never named as a blocker");
  }

  // ===== Released and allowed types, for every declared type in the stream =====

  [Test]
  public async Task AReleaseOfTheOtherDeclaredType_AppliesInTheStreamsItIsIn_ToTheFirstWalkAsync() {
    await using var conn = await _openAsync();
    var session = (Guid)TrackedGuid.New();
    var updated = await _appendAsync(conn, session, UPDATED);
    var created = await _appendAsync(conn, session, CREATED);
    var owned = await _appendAsync(conn, session, OWNED);
    var ownedElsewhere = await _appendAsync(conn, (Guid)TrackedGuid.New(), OWNED);

    var result = await new EphemeralHistoryReclassifyStep(new ListLogger(), 10, 1).ReclassifyAsync(
      _services(release: new() { [TypeNameFormatter.GetFullName(CREATED)] = [OWNED] }), CancellationToken.None);

    await Assert.That(result.EventsBlocked).IsEqualTo(0L)
      .Because("the durable type is released for a declared type the stream holds, so the stream converts whole");
    await Assert.That(result.EventsReleased).IsEqualTo(1L);
    await Assert.That(await _flagsAsync(conn, updated)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, created)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, owned)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, ownedElsewhere)).IsEqualTo(0L)
      .Because("a release still applies only in streams that hold the type it is released for");
    await StoredDigestInvariant.AssertAgreeAsync(conn, session);
  }

  [Test]
  public async Task AnAllowedDurableType_ThatOnlyOneDeclaredTypeIsPairedWith_RefusesTheStream_AndIsReportedAsync() {
    await using var conn = await _openAsync();
    var session = (Guid)TrackedGuid.New();
    var updated = await _appendAsync(conn, session, UPDATED);
    var created = await _appendAsync(conn, session, CREATED);
    var owned = await _appendAsync(conn, session, OWNED);

    var result = await _step().ReclassifyAsync(_services(registry: _registry((UPDATED, OWNED))), CancellationToken.None);

    await Assert.That(result.EventsBlocked).IsEqualTo(2L)
      .Because("converting the stream would leave the durable type beside an ephemeral type it is not paired with");
    await Assert.That(await _flagsAsync(conn, updated)).IsEqualTo(0L);
    await Assert.That(await _flagsAsync(conn, created)).IsEqualTo(0L);
    await Assert.That(await _flagsAsync(conn, owned)).IsEqualTo(0L);
    await Assert.That(await _blockersAsync(conn)).IsEquivalentTo([$"{CREATED}|{OWNED}", $"{UPDATED}|{OWNED}"])
      .Because("the unpaired durable type is the blocker; the declared types are not");
  }

  [Test]
  public async Task AnAllowedDurableType_PairedWithEveryDeclaredTypeInTheStream_StaysDurable_AndTheRestConvertAsync() {
    await using var conn = await _openAsync();
    var session = (Guid)TrackedGuid.New();
    var updated = await _appendAsync(conn, session, UPDATED);
    var created = await _appendAsync(conn, session, CREATED);
    var owned = await _appendAsync(conn, session, OWNED);

    var result = await new EphemeralHistoryReclassifyStep(new ListLogger(), 10, 1).ReclassifyAsync(
      _services(registry: _registry((UPDATED, OWNED), (CREATED, OWNED))), CancellationToken.None);

    await Assert.That(result.EventsBlocked).IsEqualTo(0L);
    await Assert.That(result.EventsReclassifiedWithStream).IsEqualTo(1L);
    await Assert.That(result.EventsReleased).IsEqualTo(0L).Because("an allowed type is kept, not released");
    await Assert.That(await _flagsAsync(conn, updated)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, created)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, owned)).IsEqualTo(0L);
    await StoredDigestInvariant.AssertAgreeAsync(conn, session);
  }

  // ===== The variant =====

  [Test]
  public async Task DeclaringAnotherType_WalksTheTypeAgain_SoItsReportNoLongerNamesTheNewlyDeclaredTypeAsync() {
    await using var conn = await _openAsync();
    var session = (Guid)TrackedGuid.New();
    var updated = await _appendAsync(conn, session, UPDATED);
    var created = await _appendAsync(conn, session, CREATED);
    var first = await _step().ReclassifyAsync(_services(catalog: new Catalog(_ephemeral(typeof(SessionStreamUpdated)))), CancellationToken.None);
    await Assert.That(first.TypesFinished).IsEqualTo(1).Because("precondition: the walk completed with the other type still durable");
    await Assert.That(await _blockersAsync(conn)).IsEquivalentTo([$"{UPDATED}|{CREATED}"]);
    await Assert.That(await _stringAsync(conn, "SELECT variant FROM wh_type_history_walks WHERE event_type = @t", ("t", UPDATED)))
      .IsEqualTo("blocker-report").Because("the control: a type declared alone has no declared part in its variant, as before #2278");

    // One batch: the first type's walk only. It runs only if the declaration restarted it.
    var second = await new EphemeralHistoryReclassifyStep(new ListLogger(), 10, 1).ReclassifyAsync(_services(), CancellationToken.None);

    await Assert.That(second.Batches).IsEqualTo(1);
    await Assert.That(second.EventsReclassified).IsEqualTo(1L)
      .Because("the newly declared type changed the first type's variant, so its completed walk ran again");
    await Assert.That(await _flagsAsync(conn, updated)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, created)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _blockersAsync(conn)).IsEmpty()
      .Because("the restarted walk describes the current declarations, so it no longer names a type that is now ephemeral");
    await Assert.That(await _stringAsync(conn, "SELECT variant FROM wh_type_history_walks WHERE event_type = @t", ("t", UPDATED)))
      .StartsWith("blocker-report;declared=");
  }

  [Test]
  public async Task TheVariant_IsTheSame_WhicheverOrderTheCatalogListsTheDeclaredTypesInAsync() {
    await using var conn = await _openAsync();
    await _appendAsync(conn, (Guid)TrackedGuid.New(), UPDATED);
    await _step().ReclassifyAsync(_services(catalog: new Catalog(_ephemeral(typeof(SessionStreamUpdated)), _ephemeral(typeof(SessionStreamCreated)), _ephemeral(typeof(SessionStreamNoted)))), CancellationToken.None);
    var before = await _stringAsync(conn, "SELECT variant FROM wh_type_history_walks WHERE event_type = @t", ("t", UPDATED));

    var again = await _step().ReclassifyAsync(_services(catalog: new Catalog(_ephemeral(typeof(SessionStreamNoted)), _ephemeral(typeof(SessionStreamCreated)), _ephemeral(typeof(SessionStreamUpdated)))), CancellationToken.None);

    await Assert.That(again.Status).IsEqualTo("complete").Because("the same declarations in another order are the same variant: no walk restarts");
    await Assert.That(await _stringAsync(conn, "SELECT variant FROM wh_type_history_walks WHERE event_type = @t", ("t", UPDATED))).IsEqualTo(before);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_type_history_walks WHERE event_type = @t", ("t", NOTED))).IsEqualTo(1L);
  }

  [Test]
  public async Task ANameIsBoundAsData_NeverSqlTextAsync() {
    await using var conn = await _openAsync();
    var session = (Guid)TrackedGuid.New();
    await _appendAsync(conn, session, UPDATED);
    await _appendAsync(conn, session, CREATED);
    const string HOSTILE = "x'); DROP TABLE wh_ephemeral_blockers; --";

    var result = await _step().ReclassifyAsync(
      _services(release: new() { [TypeNameFormatter.GetFullName(CREATED)] = [HOSTILE] }), CancellationToken.None);

    await Assert.That(result.EventsBlocked).IsEqualTo(0L).Because("the control: the walk ran and converted the stream");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_ephemeral_blockers")).IsEqualTo(0L)
      .Because("the other declared types' rules are bound parameters: the table is still there to count");
  }

  // ===== Helpers =====

  private sealed class Catalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  private sealed class Registry(params PerspectiveRegistrationInfo[] perspectives) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => perspectives;
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class ListLogger : ILogger {
    public List<string> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add(formatter(state, exception));
  }

  // One read model per allowed pair, each opted in to mixing the pair.
  private static Registry _registry(params (string Ephemeral, string Durable)[] pairs) => new(
    [.. pairs.Select((p, i) => new PerspectiveRegistrationInfo($"App.Session{i}", $"global::App.Session{i}", $"global::App.SessionModel{i}", [p.Ephemeral, p.Durable]) {
      AllowsMixedEphemeralAndSourced = true,
      MixedStreams = MixedStreams.Allow,
      MixedStreamPairs = [new MixedStreamPair(p.Ephemeral, p.Durable)],
    })]);

  private static MessageTypeCatalogEntry _ephemeral(Type type) =>
    new(type, TypeNameFormatter.FormatClrTypeName(type), "event", null) { Ephemeral = new EphemeralInfo(default, default) };

  private static EphemeralHistoryReclassifyStep _step() => new(new ListLogger(), 10, 10);

  private ServiceProvider _services(
      Dictionary<string, List<string>>? release = null, IMessageTypeCatalog? catalog = null, IPerspectiveRunnerRegistry? registry = null) {
    var options = new EphemeralOptions { ReconcileHistoricalOnStartup = true };
    foreach (var (key, values) in release ?? []) {
      options.ReleaseBlockers[key] = values;
    }
    var services = new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, "public"))
      .AddSingleton(Options.Create(options))
      .AddSingleton(catalog ?? new Catalog(_ephemeral(typeof(SessionStreamUpdated)), _ephemeral(typeof(SessionStreamCreated))));
    if (registry is not null) {
      services.AddSingleton(registry);
    }
    return services.BuildServiceProvider();
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<List<string>> _blockersAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT ephemeral_type || '|' || blocking_type FROM wh_ephemeral_blockers_report() ORDER BY 1";
    await using var reader = await cmd.ExecuteReaderAsync();
    var rows = new List<string>();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetString(0));
    }
    return rows;
  }

  private static async Task<(long Matched, long Changed, long Blocked)> _walkAsync(NpgsqlConnection conn, string type) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT events_matched, events_changed, events_blocked FROM wh_type_history_walks WHERE operation = 'ephemeral' AND event_type = @t";
    cmd.Parameters.AddWithValue("t", type);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
  }

  /// <summary>An event stored while its type was Sourced, numbered by the commit-order stamper.</summary>
  private static async Task<Guid> _appendAsync(NpgsqlConnection conn, Guid stream, string type) {
    var id = (Guid)TrackedGuid.New();
    await _executeAsync(conn,
      "SELECT wh_append_event(@id, @s, @s, split_part(@t, ',', 1), @t, '{\"OrderId\":42}'::jsonb, @m::jsonb, NULL, NOW(), 0)",
      ("id", id), ("s", stream), ("t", type), ("m", $$"""{"MessageId":"{{id}}","Hops":[]}"""));
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
    return id;
  }

  private static Task<long> _flagsAsync(NpgsqlConnection conn, Guid eventId) =>
    _scalarAsync(conn, "SELECT COALESCE(flags, 0) FROM wh_event_store WHERE event_id = @id", ("id", eventId));

  private static async Task<string?> _stringAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return await cmd.ExecuteScalarAsync() as string;
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
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

/// <summary>A real event type of a session stream, declared ephemeral after its history was written durable.</summary>
internal sealed record SessionStreamUpdated;

/// <summary>A second real event type of the same session streams, declared ephemeral too.</summary>
internal sealed record SessionStreamCreated;

/// <summary>A real event type that is meant to stay durable and shares the session streams.</summary>
internal sealed record SessionStreamOwned;

/// <summary>A third real event type, declared ephemeral, for the variant's ordering test.</summary>
internal sealed record SessionStreamNoted;
