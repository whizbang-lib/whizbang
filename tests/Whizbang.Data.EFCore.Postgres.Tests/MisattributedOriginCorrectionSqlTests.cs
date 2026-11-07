// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The correction of events stored under an origin that does not own their type (#2116), against the real
/// <c>wh_integrity_origin_correction_window</c> and <c>wh_integrity_correct_origins</c> (migration 346) and the maintenance
/// step that drives them. A service that answered a redelivery with events it had only received named itself as their
/// origin, and the consumer stored them under it: the owner's lane counted short, the answerer's long, and repair could
/// never heal the bucket. Each such event takes its owner as origin with no origin sequence, its digest moves lanes
/// through the shared fold, the wrong origin's sealed epochs are refolded and both lanes' ledger buckets re-armed. Nothing
/// is deleted.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/346_MisattributedOriginCorrection.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/MisattributedOriginCorrectionStep.cs</code-under-test>
/// <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
[Category("Integration")]
[Category("Shard5")]
public class MisattributedOriginCorrectionSqlTests : EFCoreTestBase {
  private const string ORDERS_TYPE = "App.Contracts.Orders.Placed, App.Contracts";
  private const string RELAY_TYPE = "App.Contracts.Relay.Forwarded, App.Contracts";
  private const string TENANT = "tenant-a";
  private const string FINGERPRINT = "fingerprint-1";
  private static readonly Guid _orders = Guid.Parse("0a000000-0000-0000-0000-0000000000a1");
  private static readonly Guid _relay = Guid.Parse("0b000000-0000-0000-0000-0000000000b1");

  // ── the window ────────────────────────────────────────────────────────

  [Test]
  public async Task Window_IsTheNextBatchByCommitSequence_WithTheTypesRecordedUnderAnOriginAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    await _storeAsync(conn, stream, 1, ORDERS_TYPE, _relay, 11);
    await _storeAsync(conn, stream, 2, "App.Contracts.Local.Made, App.Contracts", origin: null, originSequence: null);
    await _storeAsync(conn, stream, 3, RELAY_TYPE, _relay, 12);
    await _storeAsync(conn, stream, 4, "App.Contracts.Later.Later, App.Contracts", _relay, 13);
    var third = await _scalarAsync<long>(conn, "SELECT commit_sequence FROM wh_event_store WHERE stream_id = $1 AND version = 3", stream);

    var (after, through, types) = await _windowAsync(conn, FINGERPRINT, 3);

    await Assert.That(after).IsEqualTo(0L);
    await Assert.That(through).IsEqualTo(third).Because("the batch is bounded by rows scanned, not rows corrected");
    await Assert.That(types).IsEquivalentTo([ORDERS_TYPE, RELAY_TYPE])
      .Because("only events recorded under an origin can name the wrong one; this service's own events carry none");
  }

  [Test]
  public async Task Window_WithAChangedFingerprint_RewalksFromTheStartAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    await _storeAsync(conn, stream, 1, ORDERS_TYPE, _relay, 11);
    var (after, through, _) = await _windowAsync(conn, FINGERPRINT, 100);
    _ = await _correctAsync(conn, after, through);

    var (sameAfter, _, _) = await _windowAsync(conn, FINGERPRINT, 100);
    var (newAfter, _, newTypes) = await _windowAsync(conn, "fingerprint-2", 100);

    await Assert.That(sameAfter).IsEqualTo(through).Because("the cursor is durable");
    await Assert.That(newAfter).IsEqualTo(0L)
      .Because("a changed ownership map restarts the walk, so history reaches an owner learned later");
    await Assert.That(newTypes).IsEquivalentTo([ORDERS_TYPE]);
  }

  // ── the correction ────────────────────────────────────────────────────

  [Test]
  public async Task Correct_ARowUnderAnOriginThatDoesNotOwnItsType_TakesTheOwner_AndItsDigestMovesLanesAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    var misattributed = await _storeAsync(conn, stream, 1, ORDERS_TYPE, _relay, 11);
    var relayed = await _storeAsync(conn, stream, 2, RELAY_TYPE, _relay, 12);
    var (after, through, _) = await _windowAsync(conn, FINGERPRINT, 100);
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, _relay, stream)).IsEqualTo(2L).Because("precondition: both buckets are right");

    var rows = await _correctAsync(conn, after, through, (ORDERS_TYPE, _orders));

    await Assert.That(rows).IsEquivalentTo([(true, (string?)ORDERS_TYPE, (Guid?)_relay, (Guid?)_orders, 1)])
      .Because("the caller logs and counts what was corrected, per type, wrong origin and owner");
    await Assert.That(await _originOfAsync(conn, misattributed)).IsEqualTo(((Guid?)_orders, (long?)null))
      .Because("the sequence the row carried is the answering service's; only the owner knows its own, and its next redelivery fills it");
    await Assert.That(await _originOfAsync(conn, relayed)).IsEqualTo(((Guid?)_relay, (long?)12))
      .Because("an event of a type its origin owns stands");
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, _orders, stream)).IsEqualTo(1L)
      .Because("the owner's bucket now holds the event, every lane folded through the shared helper");
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, _relay, stream)).IsEqualTo(1L);
    await Assert.That(await _scalarAsync<long>(conn,
      "SELECT count(*) FROM wh_stream_digests WHERE origin_service_id = $1 AND event_type = $2", _relay, ORDERS_TYPE)).IsEqualTo(0L)
      .Because("the wrong origin's emptied bucket is removed, not left at zero");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_event_store WHERE stream_id = $1", stream)).IsEqualTo(2L)
      .Because("the correction never deletes an event");
  }

  [Test]
  public async Task Correct_LeavesTheOwnersRows_AndTypesNotListedAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    var owners = await _storeAsync(conn, stream, 1, ORDERS_TYPE, _orders, 21);
    var unlisted = await _storeAsync(conn, stream, 2, "App.Contracts.Unknown.Thing, App.Contracts", _relay, 22);
    var ephemeral = await _storeAsync(conn, stream, 3, ORDERS_TYPE, _relay, 23, flags: 8);
    var (after, through, _) = await _windowAsync(conn, FINGERPRINT, 100);

    var rows = await _correctAsync(conn, after, through, (ORDERS_TYPE, _orders));

    await Assert.That(await _originOfAsync(conn, owners)).IsEqualTo(((Guid?)_orders, (long?)21));
    await Assert.That(await _originOfAsync(conn, unlisted)).IsEqualTo(((Guid?)_relay, (long?)22))
      .Because("a type with no known owner is not listed, and its record stands");
    await Assert.That(await _originOfAsync(conn, ephemeral)).IsEqualTo(((Guid?)_orders, (long?)null))
      .Because("an ephemeral event takes its owner too; it simply has no digest to move");
    await Assert.That(rows.Single().Count).IsEqualTo(1);
  }

  [Test]
  public async Task Correct_AnOwnerThatIsThisService_IsLeftAsync() {
    await using var conn = await _openAsync();
    var local = await _scalarAsync<Guid>(conn, "SELECT service_id FROM wh_service_config LIMIT 1");
    var stream = TrackedGuid.New().Value;
    var row = await _storeAsync(conn, stream, 1, ORDERS_TYPE, _relay, 31);
    var (after, through, _) = await _windowAsync(conn, FINGERPRINT, 100);

    var rows = await _correctAsync(conn, after, through, (ORDERS_TYPE, local));

    await Assert.That(rows).IsEquivalentTo([(true, (string?)null, (Guid?)null, (Guid?)null, 0)]);
    await Assert.That(await _originOfAsync(conn, row)).IsEqualTo(((Guid?)_relay, (long?)31))
      .Because("this service's own events have no origin column to correct; moving them to the local lane is not this pass's job");
  }

  [Test]
  public async Task Correct_RefoldsTheWrongOriginsSealedEpoch_AndReArmsBothLanesAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    await _storeAsync(conn, stream, 1, ORDERS_TYPE, _relay, 5);
    await _executeAsync(conn, """
      INSERT INTO wh_digest_epoch_frontiers (origin_service_id, closed_through_epoch, epoch_width, updated_at)
      VALUES (@relay, 0, 1000, NOW())
      """, ("relay", _relay));
    _ = await _scalarAsync<int>(conn, "SELECT refold_digest_epochs($1, 0, 0)", _relay);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_digest_epochs WHERE origin_service_id = $1", _relay))
      .IsEqualTo(1L).Because("precondition: the wrong origin's sealed epoch holds the event");
    await _executeAsync(conn, """
      INSERT INTO wh_integrity_ledger
        (origin_service_id, tenant_scope, event_type, stream_id, origin_lo, origin_hi, local_lo, local_hi,
         repair_attempts, unhealable_at, exhausted_at, answer_pending_until, answered_at)
      VALUES
        (@orders, @tenant, @type, @stream, 1, 1, 0, 0, 5, NOW(), NOW(), NOW() + INTERVAL '1 hour', NOW()),
        (@orders, @tenant, @type, '00000000-0000-0000-0000-000000000000', 1, 1, 0, 0, 5, NOW(), NOW(), NOW() + INTERVAL '1 hour', NOW()),
        (@relay, @tenant, @type, @stream, 1, 1, 0, 0, 5, NOW(), NOW(), NOW() + INTERVAL '1 hour', NOW()),
        (@relay, @tenant, @type, '00000000-0000-0000-0000-000000000000', 1, 1, 0, 0, 5, NOW(), NOW(), NOW() + INTERVAL '1 hour', NOW()),
        (@orders, @tenant, 'App.Contracts.Orders.Other, App.Contracts', @stream, 1, 1, 0, 0, 5, NOW(), NOW(), NOW() + INTERVAL '1 hour', NOW())
      """, ("orders", _orders), ("relay", _relay), ("tenant", TENANT), ("type", ORDERS_TYPE), ("stream", stream));
    var (after, through, _) = await _windowAsync(conn, FINGERPRINT, 100);

    _ = await _correctAsync(conn, after, through, (ORDERS_TYPE, _orders));

    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_digest_epochs WHERE origin_service_id = $1", _relay))
      .IsEqualTo(0L).Because("the wrong origin's sealed epoch no longer holds an event that origin never wrote");
    await Assert.That(await _scalarAsync<long>(conn, """
      SELECT count(*) FROM wh_integrity_ledger
      WHERE event_type = $1 AND repair_attempts = 0 AND unhealable_at IS NULL AND exhausted_at IS NULL
        AND answer_pending_until IS NULL AND answered_at IS NULL
      """, ORDERS_TYPE)).IsEqualTo(4L)
      .Because("both lanes' buckets (the stream and the type lane) changed on this side, so their ladders start afresh: a bucket marked unhealable can heal now");
    await Assert.That(await _scalarAsync<int>(conn,
      "SELECT repair_attempts FROM wh_integrity_ledger WHERE event_type = 'App.Contracts.Orders.Other, App.Contracts'"))
      .IsEqualTo(5).Because("a bucket no corrected row touches keeps its ladder");
  }

  [Test]
  public async Task Correct_FromAStaleCursor_ChangesNothingAndReportsNotAdvancedAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    var row = await _storeAsync(conn, stream, 1, ORDERS_TYPE, _relay, 11);
    var (after, through, _) = await _windowAsync(conn, FINGERPRINT, 100);

    var rows = await _correctAsync(conn, after + 1, through, (ORDERS_TYPE, _orders));

    await Assert.That(rows).IsEquivalentTo([(false, (string?)null, (Guid?)null, (Guid?)null, 0)]);
    await Assert.That(await _originOfAsync(conn, row)).IsEqualTo(((Guid?)_relay, (long?)11))
      .Because("the cursor moved under the batch, so the batch is discarded whole");
  }

  [Test]
  public async Task Correct_MismatchedArrays_AreRefusedAsync() {
    await using var conn = await _openAsync();

    await Assert.That(async () => await _executeAsync(conn,
      "SELECT * FROM wh_integrity_correct_origins(0, 1, @types, @owners)",
      ("types", new[] { ORDERS_TYPE }), ("owners", Array.Empty<Guid>()))).Throws<PostgresException>();
  }

  [Test]
  public async Task WindowAndCorrection_UnderAGenericPlan_RangeScanTheCommitSequenceIndexAsync() {
    await using var conn = await _openAsync();
    // A wide store, analyzed, so a scan of it is visible as thousands of rows where a batch needs a handful.
    await _executeAsync(conn, """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, created_at)
      SELECT gen_random_uuid(), s, s, 'Order', 'Contracts.Other, Contracts', NULL, 1, nextval('wh_commit_seq'), NOW()
      FROM (SELECT gen_random_uuid() AS s FROM generate_series(1, 5000)) g;
      ANALYZE wh_event_store;
      """);
    var floor = await _scalarAsync<long>(conn, "SELECT max(commit_sequence) FROM wh_event_store");
    await _executeAsync(conn, "INSERT INTO wh_integrity_origin_correction (singleton, through_commit_sequence, ownership_fingerprint) VALUES (true, @f, @fp)",
      ("f", floor), ("fp", FINGERPRINT));
    var stream = TrackedGuid.New().Value;
    await _storeAsync(conn, stream, 1, ORDERS_TYPE, _relay, 11);
    await _storeAsync(conn, stream, 2, ORDERS_TYPE, _relay, 12);
    await _executeAsync(conn, "SET plan_cache_mode = force_generic_plan");

    var nodes = await _plansAsync(conn, async () => {
      var (after, through, _) = await _windowAsync(conn, FINGERPRINT, 10);
      _ = await _correctAsync(conn, after, through, (ORDERS_TYPE, _orders));
    });

    var storeReads = nodes.Where(n => n.Relation == "wh_event_store").ToList();
    await Assert.That(storeReads).IsNotEmpty().Because("precondition: the window and the correction read the store");
    await Assert.That(storeReads.Any(n => n.Type == "Seq Scan")).IsFalse()
      .Because("both read the batch's commit-sequence range through its index; a sequential scan reads the whole store every batch");
    await Assert.That(storeReads.Max(n => n.RowsReturned)).IsLessThanOrEqualTo(10)
      .Because("the batch is the two rows past the cursor");
    await Assert.That(nodes.Where(n => n.Relation is "wh_stream_digests").All(n => n.Type != "Seq Scan")).IsTrue();
  }

  // ── the step ──────────────────────────────────────────────────────────

  [Test]
  public async Task Step_CorrectsInBoundedBatches_AndResumesFromItsCursorAsync() {
    await using var conn = await _openAsync();
    await _claimAsync(conn, "app.contracts.orders", _orders, "orders-svc");
    await _claimAsync(conn, "app.contracts.relay", _relay, "relay-svc");
    var stream = TrackedGuid.New().Value;
    var first = await _storeAsync(conn, stream, 1, ORDERS_TYPE, _relay, 11);
    await _storeAsync(conn, stream, 2, RELAY_TYPE, _relay, 12);
    var third = await _storeAsync(conn, stream, 3, ORDERS_TYPE, _relay, 13);
    var logger = new ListLogger();
    var measured = new List<long>();
    using var listener = _listen(measured);
    var services = _services(new StreamIntegrityOptions { ReattributionBatchSize = 1, MaxReattributionBatchesPerPass = 2 });
    var step = new MisattributedOriginCorrectionStep(logger);

    var cycle1 = await step.CorrectAsync(services, CancellationToken.None);

    await Assert.That(cycle1).IsEqualTo(new MisattributedOriginCorrectionResult(2, 1, "budget"))
      .Because("a cycle runs at most its batch budget, each batch a bounded number of rows");
    await Assert.That((await _originOfAsync(conn, first)).Origin).IsEqualTo(_orders);
    await Assert.That((await _originOfAsync(conn, third)).Origin).IsEqualTo(_relay).Because("not reached yet");

    await step.RunAsync(services, CancellationToken.None);

    await Assert.That((await _originOfAsync(conn, third)).Origin).IsEqualTo(_orders)
      .Because("the next cycle resumes from the durable cursor");
    var cycle3 = await step.CorrectAsync(services, CancellationToken.None);
    await Assert.That(cycle3).IsEqualTo(new MisattributedOriginCorrectionResult(0, 0, "caught-up"));
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains(ORDERS_TYPE, StringComparison.Ordinal)
      && e.Message.Contains(_relay.ToString(), StringComparison.Ordinal) && e.Message.Contains(_orders.ToString(), StringComparison.Ordinal)))
      .IsEqualTo(2).Because("every correction is named at Warning: the type, the origin that did not own it and its owner");
    // A passive counter: the process accumulates and the meter observes the running total when it is collected.
    listener.RecordObservableInstruments();
    await Assert.That(measured).IsEquivalentTo([2L]).Because("and counted");
    await Assert.That(step.Name).IsEqualTo("misattributed-origin-correction");
  }

  [Test]
  public async Task Step_ATypeThisServiceOwns_IsLeft_AndTheWalkStillAdvancesAsync() {
    await using var conn = await _openAsync();
    await _claimAsync(conn, "app.contracts", _orders, "orders-svc");
    var stream = TrackedGuid.New().Value;
    var row = await _storeAsync(conn, stream, 1, ORDERS_TYPE, _relay, 11);
    var routing = new RoutingOptions();
    routing.OwnDomains("App.Contracts.Orders");
    var services = _services(new StreamIntegrityOptions(), routing);

    var result = await new MisattributedOriginCorrectionStep(NullLogger.Instance).CorrectAsync(services, CancellationToken.None);

    await Assert.That(result).IsEqualTo(new MisattributedOriginCorrectionResult(1, 0, "caught-up"));
    await Assert.That((await _originOfAsync(conn, row)).Origin).IsEqualTo(_relay);
  }

  [Test]
  public async Task Step_WithNoKnownOwner_OrSwitchedOff_TouchesNothingAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    var row = await _storeAsync(conn, stream, 1, ORDERS_TYPE, _relay, 11);
    var step = new MisattributedOriginCorrectionStep(NullLogger.Instance);

    var noOwners = await step.CorrectAsync(_services(new StreamIntegrityOptions()), CancellationToken.None);
    await _claimAsync(conn, "app.contracts.orders", _orders, "orders-svc");
    var off = await step.CorrectAsync(_services(new StreamIntegrityOptions { ReattributionEnabled = false }), CancellationToken.None);

    await Assert.That(noOwners.Status).IsEqualTo("skipped (no service has announced an owned domain)");
    await Assert.That(off.Status).IsEqualTo("skipped (Whizbang:StreamIntegrity:ReattributionEnabled=false)");
    await Assert.That((await _originOfAsync(conn, row)).Origin).IsEqualTo(_relay);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_integrity_origin_correction")).IsEqualTo(0L)
      .Because("a cycle with nothing to know reads the claims and nothing else");
  }

  [Test]
  public async Task FromServices_AndItsArguments_AreCheckedAsync() {
    await Assert.That(MisattributedOriginCorrectionStep.FromServices(new ServiceCollection().AddLogging().BuildServiceProvider())).IsNotNull();
    await Assert.That(MisattributedOriginCorrectionStep.FromServices(new ServiceCollection().BuildServiceProvider())).IsNotNull();
    await Assert.That(() => MisattributedOriginCorrectionStep.FromServices(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new MisattributedOriginCorrectionStep(null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await new MisattributedOriginCorrectionStep(NullLogger.Instance).CorrectAsync(null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
    // With no options registered the defaults apply: switched on, and with no claims it skips.
    var bare = new ServiceCollection().AddSingleton(_connection()).BuildServiceProvider();
    await Assert.That((await new MisattributedOriginCorrectionStep(NullLogger.Instance).CorrectAsync(bare, CancellationToken.None)).Status)
      .IsEqualTo("skipped (no service has announced an owned domain)");
  }

  [Test]
  public async Task ThePostgresDriver_RegistersTheStepAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<MisattributedOriginCorrectionStep>().ToList();

    await Assert.That(steps.Count).IsEqualTo(1).Because("the correction runs in the maintenance cycle, idle-gated, under the maintainer turn");
  }

  // ── fixture ───────────────────────────────────────────────────────────

  private sealed class ListLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  private sealed record PlanNode(string Type, string? Relation, long RowsReturned);

  private PostgresArchiveConnection _connection() =>
    new(async ct => {
      var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, "public");

  private ServiceProvider _services(StreamIntegrityOptions options, RoutingOptions? routing = null) {
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterFactory: _meters.GetRequiredService<IMeterFactory>()));
    return new ServiceCollection()
      .AddSingleton(_connection())
      .AddSingleton(Options.Create(options))
      .AddSingleton(Options.Create(routing ?? new RoutingOptions()))
      .AddSingleton(metrics)
      .BuildServiceProvider();
  }

  private readonly ServiceProvider _meters = new ServiceCollection().AddMetrics().BuildServiceProvider();

  [After(Test)]
  public async Task DisposeMetersAsync() => await _meters.DisposeAsync();

  private MeterListener _listen(List<long> measured) {
    var factory = _meters.GetRequiredService<IMeterFactory>();
    var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Name == "whizbang.stream_integrity.misattributed_origins_corrected"
            && instrument.Meter.Scope == factory) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => measured.Add(value));
    listener.Start();
    return listener;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>Stores one event as the emit chain does, folding its digest into its origin's bucket through the shared
  /// helper.</summary>
  private static async Task<Guid> _storeAsync(NpgsqlConnection conn, Guid stream, int version, string eventType,
      Guid? origin, long? originSequence, int flags = 0) {
    var eventId = TrackedGuid.New().Value;
    await _executeAsync(conn, """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
         commit_sequence, flags, created_at, origin_service_id, origin_commit_sequence)
      VALUES (@event, @stream, @stream, 'Thing', @type, jsonb_build_object('t', @tenant::text), @version,
              nextval('wh_commit_seq'), @flags, NOW() - INTERVAL '2 hours', @origin, @sequence)
      """, ("event", eventId), ("stream", stream), ("type", eventType), ("tenant", TENANT), ("version", version),
      ("flags", flags), ("origin", (object?)origin ?? DBNull.Value), ("sequence", (object?)originSequence ?? DBNull.Value));
    if ((flags & 8) == 0) {
      await _executeAsync(conn,
        "SELECT _wh_fold_stream_digests(ARRAY[@lane], ARRAY[@tenant::text], ARRAY[@type::text], ARRAY[@stream], ARRAY[@event], 'fold', NOW())",
        ("lane", origin ?? Guid.Empty), ("tenant", TENANT), ("type", eventType), ("stream", stream), ("event", eventId));
    }
    return eventId;
  }

  private static Task _claimAsync(NpgsqlConnection conn, string domain, Guid owner, string name) =>
    _executeAsync(conn, "SELECT * FROM wh_integrity_record_domain_claims(@owner, @name, NULL, @domains)",
      ("owner", owner), ("name", name), ("domains", new[] { domain }));

  private static async Task<(long After, long Through, string[] Types)> _windowAsync(NpgsqlConnection conn, string fingerprint, int max) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT after_commit_sequence, through_commit_sequence, event_types FROM wh_integrity_origin_correction_window($1, $2)";
    cmd.Parameters.AddWithValue(fingerprint);
    cmd.Parameters.AddWithValue(max);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetInt64(1), reader.GetFieldValue<string[]>(2));
  }

  private static async Task<List<(bool Advanced, string? Type, Guid? From, Guid? Owner, int Count)>> _correctAsync(
      NpgsqlConnection conn, long after, long through, params (string Type, Guid Owner)[] targets) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT advanced, event_type, from_origin, to_owner, corrected_count FROM wh_integrity_correct_origins($1, $2, $3, $4)";
    cmd.Parameters.AddWithValue(after);
    cmd.Parameters.AddWithValue(through);
    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text, Value = targets.Select(t => t.Type).ToArray() });
    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, Value = targets.Select(t => t.Owner).ToArray() });
    var rows = new List<(bool, string?, Guid?, Guid?, int)>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add((reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
        reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetInt32(4)));
    }
    return rows;
  }

  private static async Task<(Guid? Origin, long? Sequence)> _originOfAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT origin_service_id, origin_commit_sequence FROM wh_event_store WHERE event_id = $1";
    cmd.Parameters.AddWithValue(eventId);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (await reader.IsDBNullAsync(0) ? null : reader.GetGuid(0), await reader.IsDBNullAsync(1) ? null : reader.GetInt64(1));
  }

  /// <summary>How many digest buckets of the lane equal a recompute, every lane, over the stream's events recorded in it.</summary>
  private static Task<long> _bucketsMatchingTheStoreAsync(NpgsqlConnection conn, Guid lane, Guid stream) =>
    _scalarAsync<long>(conn, """
      SELECT count(*) FROM (
        SELECT event_type, bit_xor(_wh_digest_lane(event_id, 0)) AS lo, bit_xor(_wh_digest_lane(event_id, 1)) AS hi,
               bit_xor(_wh_digest_lane(event_id, 2)) AS ilo, bit_xor(_wh_digest_lane(event_id, 3)) AS ihi, count(*)::int AS n
        FROM wh_event_store
        WHERE stream_id = $2 AND COALESCE(origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid) = $1
          AND COALESCE(flags, 0) & 8 = 0
        GROUP BY event_type
      ) e
      JOIN wh_stream_digests d ON d.origin_service_id = $1 AND d.stream_id = $2 AND d.event_type = e.event_type
      WHERE d.digest_lo = e.lo AND d.digest_hi = e.hi AND d.id_lo = e.ilo AND d.id_hi = e.ihi AND d.event_count = e.n
      """, lane, stream);

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<List<PlanNode>> _plansAsync(NpgsqlConnection conn, Func<Task> body) {
    var notices = new List<string>();
    void Collect(object? _, NpgsqlNoticeEventArgs e) => notices.Add(e.Notice.MessageText);
    await _executeAsync(conn, """
      LOAD 'auto_explain';
      SET auto_explain.log_min_duration = 0;
      SET auto_explain.log_analyze = on;
      SET auto_explain.log_timing = off;
      SET auto_explain.log_nested_statements = on;
      SET auto_explain.log_format = 'json';
      SET auto_explain.log_level = 'notice';
      """);
    conn.Notice += Collect;
    await _executeAsync(conn, "BEGIN");
    await body();
    await _executeAsync(conn, "ROLLBACK");
    conn.Notice -= Collect;
    await _executeAsync(conn, "SET auto_explain.log_min_duration = -1");
    var nodes = new List<PlanNode>();
    foreach (var notice in notices.Where(n => n.Contains("\"Plan\"", StringComparison.Ordinal))) {
      using var document = JsonDocument.Parse(notice[notice.IndexOf('{', StringComparison.Ordinal)..]);
      _flatten(document.RootElement.GetProperty("Plan"), nodes);
    }
    return nodes;
  }

  private static void _flatten(JsonElement node, List<PlanNode> nodes) {
    if (node.TryGetProperty("Plans", out var children)) {
      foreach (var child in children.EnumerateArray()) {
        _flatten(child, nodes);
      }
    }
    nodes.Add(new PlanNode(
      node.GetProperty("Node Type").GetString()!,
      node.TryGetProperty("Relation Name", out var relation) ? relation.GetString() : null,
      (long)(node.GetProperty("Actual Rows").GetDouble() * node.GetProperty("Actual Loops").GetDouble())));
  }
}
