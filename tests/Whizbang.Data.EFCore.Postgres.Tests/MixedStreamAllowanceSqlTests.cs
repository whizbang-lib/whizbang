// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
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
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2255: a pair that every read model applying both allows to share a stream
/// (<c>[DangerouslyAllowMixedEphemeralAndSourcedEvents(Streams = MixedStreams.Allow)]</c>) may do so. The store takes
/// either kind on a stream holding the other, the history walk reclassifies the ephemeral events in such streams and
/// keeps the durable ones, the reaper reaps the ephemeral bodies, and the blocker report no longer names the pair. Every
/// unpaired type is refused as before, and with no pair the store behaves exactly as without the allowance.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/393_AllowedPairsMayShareAStream.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralHistoryReclassifyStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralBlockerReportText.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class MixedStreamAllowanceSqlTests : EFCoreTestBase {
  private const int EPHEMERAL = 8;
  private const int MIXED_STREAM_REASON = 22;
  // Real types, by the name the append path stores them under.
  private static readonly string PINGED = TypeNameFormatter.Format(typeof(MixedBoardPinged));
  private static readonly string OPENED = TypeNameFormatter.Format(typeof(MixedBoardOpened));
  private static readonly string CLOSED = TypeNameFormatter.Format(typeof(MixedBoardClosed));
  private static readonly string TYPED = TypeNameFormatter.Format(typeof(MixedBoardTyped));

  // ===== The allowance table =====

  [Test]
  public async Task Sync_ReplacesThePairs_AndAnEmptyListClearsThemAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);

    await ((IWorkCoordinator)coordinator).SyncMixedStreamAllowancesAsync([new MixedStreamPair(PINGED, OPENED), new MixedStreamPair(PINGED, CLOSED)]);
    await ((IWorkCoordinator)coordinator).SyncMixedStreamAllowancesAsync([new MixedStreamPair(PINGED, OPENED)]);

    var conn = await _openAsync(dbContext);
    await Assert.That(await _pairsAsync(conn)).IsEquivalentTo([$"{PINGED}|{OPENED}"])
      .Because("a full replace: a pair no longer allowed is removed, so a read model that dropped the opt-in refuses again");
    await ((IWorkCoordinator)coordinator).SyncMixedStreamAllowancesAsync([]);
    await Assert.That(await _pairsAsync(conn)).IsEmpty();
  }

  // ===== Writes =====

  [Test]
  public async Task Append_WithNoAllowance_IsRefusedAsBeforeAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _appendAsync(conn, stream, OPENED);

    var refusal = await Assert.That(() => _appendAsync(conn, stream, PINGED, EPHEMERAL)).Throws<PostgresException>();

    await Assert.That(refusal!.SqlState).IsEqualTo("WH133").Because("the default is Refuse: a stream never mixes, as before");
  }

  [Test]
  public async Task Append_AnAllowedPair_StoresEitherKindOnAStreamHoldingTheOtherAsync() {
    await using var conn = await _openAsync();
    await _syncAsync(conn, (PINGED, OPENED));
    var durableFirst = (Guid)TrackedGuid.New();
    var ephemeralFirst = (Guid)TrackedGuid.New();

    await _appendAsync(conn, durableFirst, OPENED);
    var pinged = await _appendAsync(conn, durableFirst, PINGED, EPHEMERAL);
    await _appendAsync(conn, ephemeralFirst, PINGED, EPHEMERAL);
    var opened = await _appendAsync(conn, ephemeralFirst, OPENED);

    await Assert.That(await _flagsAsync(conn, pinged)).IsEqualTo((long)EPHEMERAL).Because("stored, and stored ephemeral");
    await Assert.That(await _flagsAsync(conn, opened)).IsEqualTo(0L).Because("stored, and stored Sourced");
  }

  [Test]
  public async Task Append_AnUnpairedType_IsStillRefused_OnAStreamTheAllowanceMixedAsync() {
    await using var conn = await _openAsync();
    await _syncAsync(conn, (PINGED, OPENED));
    var mixed = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed, OPENED);
    await _appendAsync(conn, mixed, PINGED, EPHEMERAL);
    var unpaired = (Guid)TrackedGuid.New();
    await _appendAsync(conn, unpaired, CLOSED);

    var closed = await Assert.That(() => _appendAsync(conn, mixed, CLOSED)).Throws<PostgresException>();
    var typed = await Assert.That(() => _appendAsync(conn, mixed, TYPED, EPHEMERAL)).Throws<PostgresException>();
    var pinged = await Assert.That(() => _appendAsync(conn, unpaired, PINGED, EPHEMERAL)).Throws<PostgresException>();

    await Assert.That(closed!.MessageText).Contains($"holds ephemeral events ({PINGED})")
      .Because("a Sourced type the ephemeral type is not paired with is refused on the mixed stream, and the refusal names why");
    await Assert.That(typed!.MessageText).Contains($"holds Sourced events ({OPENED})")
      .Because("an ephemeral type not paired with the stream's Sourced type is refused there too");
    await Assert.That(pinged!.MessageText).Contains($"holds Sourced events ({CLOSED})")
      .Because("the allowance is for the pair only: the ephemeral type still never joins another Sourced type's stream");
  }

  [Test]
  public async Task Store_AnAllowedPairInOneOutboxWrite_StoresBothKindsAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await ((IWorkCoordinator)coordinator).SyncMixedStreamAllowancesAsync([new MixedStreamPair(PINGED, OPENED)]);
    var stream = (Guid)TrackedGuid.New();
    Guid[] ids = [.. Enumerable.Range(0, 3).Select(_ => (Guid)TrackedGuid.New()).Order()];
    var pinged = _outbox(ids[0], stream, PINGED, EventFlags.Ephemeral);
    var opened = _outbox(ids[1], stream, OPENED, EventFlags.None);
    var closed = _outbox(ids[2], stream, CLOSED, EventFlags.None);

    await coordinator.StoreOutboxMessagesAsync([closed, opened, pinged], partitionCount: 10000);

    var conn = await _openAsync(dbContext);
    await Assert.That(await _storedIdsAsync(conn, stream)).IsEquivalentTo([pinged.MessageId, opened.MessageId])
      .Because("the stream's first event is ephemeral; its paired Sourced type joins it in the same write, an unpaired one does not");
    await Assert.That(await _scalarAsync(conn, "SELECT failure_reason FROM wh_dead_letters WHERE source_id = @id", ("id", closed.MessageId)))
      .IsEqualTo((long)MIXED_STREAM_REASON);
  }

  // ===== The history walk and the report =====

  [Test]
  public async Task Reclassify_AnAllowedPair_ReclassifiesTheEphemeralEvents_KeepsTheDurable_AndTheDigestsEqualARecountAsync() {
    await using var conn = await _openAsync();
    var allowed = (Guid)TrackedGuid.New();
    var held = (Guid)TrackedGuid.New();
    var history = await _appendAsync(conn, allowed, PINGED);
    var opened = await _appendAsync(conn, allowed, OPENED);
    var heldHistory = await _appendAsync(conn, held, PINGED);
    await _appendAsync(conn, held, OPENED);
    var closed = await _appendAsync(conn, held, CLOSED);

    var result = await _step(new ListLogger()).ReclassifyAsync(_services(_registry()), CancellationToken.None);

    await Assert.That(result.EventsReclassified).IsEqualTo(1L);
    await Assert.That(result.EventsBlocked).IsEqualTo(1L).Because("the second stream also holds an unallowed Sourced type");
    await Assert.That(result.EventsReleased).IsEqualTo(0L).Because("allowing mixing is not a release: the durable events stay");
    await Assert.That(await _flagsAsync(conn, history)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, opened)).IsEqualTo(0L);
    await Assert.That(await _flagsAsync(conn, heldHistory)).IsEqualTo(0L);
    await Assert.That(await _flagsAsync(conn, closed)).IsEqualTo(0L);
    await StoredDigestInvariant.AssertAgreeAsync(conn, allowed, held);
  }

  [Test]
  public async Task Reclassify_WithNoAllowingReadModel_TheAllowedPairStillBlocksAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var history = await _appendAsync(conn, stream, PINGED);
    await _appendAsync(conn, stream, OPENED);

    var result = await _step(new ListLogger()).ReclassifyAsync(_services(_registry(allows: false)), CancellationToken.None);

    await Assert.That(result.EventsBlocked).IsEqualTo(1L).Because("Refuse, the default, keeps the walk exactly as before");
    await Assert.That(await _flagsAsync(conn, history)).IsEqualTo(0L);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_type_history_walks WHERE variant LIKE '%allow=%'")).IsEqualTo(0L)
      .Because("with nothing allowed the walk's variant is unchanged, so no type is walked again");
  }

  [Test]
  public async Task Report_AnAllowedPair_IsNoBlocker_AndAnUnallowedOneOffersAllowMixingAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var other = (Guid)TrackedGuid.New();
    await _appendAsync(conn, stream, PINGED);
    await _appendAsync(conn, stream, OPENED);
    await _appendAsync(conn, other, PINGED);
    await _appendAsync(conn, other, CLOSED);
    var logger = new ListLogger();

    await _step(logger).ReclassifyAsync(_services(_registry()), CancellationToken.None);

    var blockers = await _blockersAsync(conn);
    await Assert.That(blockers).IsEquivalentTo([CLOSED]).Because("an allowed pair holds nothing back, so it is no blocker");
    var block = logger.Entries.Single(e => e.StartsWith($"Ephemeral history of {PINGED} is held back", StringComparison.Ordinal));
    await Assert.That(block).Contains("(4) allow mixing")
      .Because("the report offers the fourth action beside mark-ephemeral, release and keep");
    await Assert.That(block).Contains("[DangerouslyAllowMixedEphemeralAndSourcedEvents(Streams = MixedStreams.Allow)]")
      .Because("it prints the exact attribute line");
    await Assert.That(block).Contains("App.Timeline").Because("it names the read models that apply both and must opt in");
    await Assert.That(block).DoesNotContain($"{OPENED}:");
  }

  [Test]
  public async Task Report_WhenNoReadModelAppliesBoth_SaysAllowMixingNeedsOneAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _appendAsync(conn, stream, PINGED);
    await _appendAsync(conn, stream, CLOSED);
    var logger = new ListLogger();

    await _step(logger).ReclassifyAsync(_services(registry: null), CancellationToken.None);

    var block = logger.Entries.Single(e => e.StartsWith($"Ephemeral history of {PINGED} is held back", StringComparison.Ordinal));
    await Assert.That(block).Contains("(4) allow mixing");
    await Assert.That(block).Contains("no registered read model applies both");
  }

  // ===== The reaper =====

  [Test]
  public async Task Reap_AnAllowedMixedStream_ReapsTheEphemeralBodies_AndKeepsTheDurableOnesAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await ((IWorkCoordinator)coordinator).SyncMixedStreamAllowancesAsync([new MixedStreamPair(PINGED, OPENED)]);
    var stream = (Guid)TrackedGuid.New();
    var opened = _outbox((Guid)TrackedGuid.New(), stream, OPENED, EventFlags.None);
    await coordinator.StoreOutboxMessagesAsync([opened], partitionCount: 10000);
    var pinged = _outbox((Guid)TrackedGuid.New(), stream, PINGED, EventFlags.Ephemeral);
    await coordinator.StoreOutboxMessagesAsync([pinged], partitionCount: 10000);
    var conn = await _openAsync(dbContext);
    await Assert.That(await _storedIdsAsync(conn, stream)).IsEquivalentTo([opened.MessageId, pinged.MessageId])
      .Because("precondition: the allowed pair shares the stream");
    await _executeAsync(conn, "UPDATE wh_settings SET setting_value = '0' WHERE setting_key = 'ephemeral_rewind_grace_seconds'");

    await _executeAsync(conn, "SELECT count(*) FROM perform_maintenance()");

    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_event_body WHERE event_id = @id", ("id", pinged.MessageId)))
      .IsEqualTo(0L).Because("the ephemeral body in a mixed stream is reaped on its normal rules");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_event_body WHERE event_id = @id", ("id", opened.MessageId)))
      .IsEqualTo(1L).Because("the durable event in the same stream is kept");
  }

  // ===== The rebuild guard =====

  [Test]
  public async Task StateBased_IgnoringTheAllowedEphemeralType_LeavesTheMixedStreamRebuildableAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var conn = await _openAsync(dbContext);
    await _syncAsync(conn, (PINGED, OPENED));
    var mixed = (Guid)TrackedGuid.New();
    var plain = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed, OPENED);
    await _appendAsync(conn, mixed, PINGED, EPHEMERAL);
    await _appendAsync(conn, plain, TYPED, EPHEMERAL);

    var forDurable = await ((IWorkCoordinator)coordinator).GetStateBasedStreamIdsAsync([mixed, plain], [PINGED]);
    var forMixed = await ((IWorkCoordinator)coordinator).GetStateBasedStreamIdsAsync([mixed, plain]);

    await Assert.That(forDurable).IsEquivalentTo([plain])
      .Because("a read model applying only the Sourced type passes over the allowed ephemeral type, and rebuilds the stream");
    await Assert.That(forMixed).IsEquivalentTo([mixed, plain])
      .Because("the opted-in read model applies the ephemeral type: its full rebuild of the stream stays refused");
    await Assert.That(await ((IWorkCoordinator)coordinator).GetStateBasedStreamIdsAsync([], [PINGED])).IsEmpty();
  }

  // ===== Helpers =====

  private static Registry _registry(bool allows = true) => new(
    new PerspectiveRegistrationInfo("App.Board", "global::App.Board", "global::App.BoardModel", [PINGED, OPENED]) {
      AllowsMixedEphemeralAndSourced = true,
      MixedStreams = allows ? MixedStreams.Allow : MixedStreams.Refuse,
      MixedStreamPairs = [new MixedStreamPair(PINGED, OPENED)],
    },
    new PerspectiveRegistrationInfo("App.Timeline", "global::App.Timeline", "global::App.TimelineModel", [PINGED, CLOSED]) {
      MixedStreamPairs = [new MixedStreamPair(PINGED, CLOSED)],
    },
    new PerspectiveRegistrationInfo("App.Orders", "global::App.Orders", "global::App.OrderModel", [OPENED]));

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

  private static EphemeralHistoryReclassifyStep _step(ILogger logger) => new(logger, 10, 10);

  private ServiceProvider _services(IPerspectiveRunnerRegistry? registry) {
    var services = new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, "public"))
      .AddSingleton(Options.Create(new EphemeralOptions { ReconcileHistoricalOnStartup = true }))
      .AddSingleton<IMessageTypeCatalog>(new Catalog(
        new MessageTypeCatalogEntry(typeof(MixedBoardPinged), TypeNameFormatter.FormatClrTypeName(typeof(MixedBoardPinged)), "event", null) {
          Ephemeral = new EphemeralInfo(default, default),
        }));
    if (registry is not null) {
      services.AddSingleton(registry);
    }
    return services.BuildServiceProvider();
  }

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext dbContext) =>
    new(dbContext, JsonContextRegistry.CreateCombinedOptions());

  private static OutboxMessage _outbox(Guid id, Guid stream, string type, EventFlags flags) =>
    CreateTestOutboxMessage(id, "mixed-allowance-topic", stream, isEvent: true) with { MessageType = type, Flags = flags };

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static Task _syncAsync(NpgsqlConnection conn, params (string Ephemeral, string Durable)[] pairs) =>
    _executeAsync(conn, "SELECT wh_sync_mixed_stream_allowances(@e, @d)",
      ("e", pairs.Select(p => p.Ephemeral).ToArray()), ("d", pairs.Select(p => p.Durable).ToArray()));

  private static async Task<List<string>> _pairsAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT ephemeral_type || '|' || durable_type FROM wh_mixed_stream_allowances ORDER BY 1";
    await using var reader = await cmd.ExecuteReaderAsync();
    var pairs = new List<string>();
    while (await reader.ReadAsync()) {
      pairs.Add(reader.GetString(0));
    }
    return pairs;
  }

  private static async Task<List<string>> _blockersAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT blocking_type FROM wh_ephemeral_blockers_report()";
    await using var reader = await cmd.ExecuteReaderAsync();
    var rows = new List<string>();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetString(0));
    }
    return rows;
  }

  private static async Task<List<Guid>> _storedIdsAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_id FROM wh_event_store WHERE stream_id = @s ORDER BY version";
    cmd.Parameters.AddWithValue("s", stream);
    await using var reader = await cmd.ExecuteReaderAsync();
    var ids = new List<Guid>();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  /// <summary>An event through the direct append path, under the store's mixed-stream refusal, numbered by the stamper.</summary>
  private static async Task<Guid> _appendAsync(NpgsqlConnection conn, Guid stream, string type, int flags = 0) {
    var id = (Guid)TrackedGuid.New();
    await _executeAsync(conn,
      "SELECT wh_append_event(@id, @s, @s, split_part(@t, ',', 1), @t, '{\"OrderId\":42}'::jsonb, @m::jsonb, NULL, NOW(), @f)",
      ("id", id), ("s", stream), ("t", type), ("m", $$"""{"MessageId":"{{id}}","Hops":[]}"""), ("f", flags));
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
    return id;
  }

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

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}

/// <summary>A real ephemeral event type that a read model allows to share a stream with <see cref="MixedBoardOpened"/>.</summary>
internal sealed record MixedBoardPinged;

/// <summary>A real Sourced event type paired with <see cref="MixedBoardPinged"/>.</summary>
internal sealed record MixedBoardOpened;

/// <summary>A real Sourced event type no read model allows to share a stream with an ephemeral type.</summary>
internal sealed record MixedBoardClosed;

/// <summary>A second real ephemeral event type, paired with nothing.</summary>
internal sealed record MixedBoardTyped;
