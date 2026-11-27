// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2588: the public API of integrity exclusions (<see cref="IIntegrityExclusions"/>) and the maintenance step that
/// applies its queued requests, on a real store. A few ids are marked at once; more ids, and every selection, are queued
/// and applied in bounded batches; a dry run marks nothing; this service's own events are refused by id and a selection
/// naming this service is refused outright (decision D1); every batch logs a Warning per origin and is counted; the step
/// publishes the per-origin gauges and rolls the audit log off past its retention.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresIntegrityExclusions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/IntegrityExclusionStep.cs</code-under-test>
/// <docs>resilience/integrity-exclusions#the-api</docs>
[Category("Integration")]
[Category("Shard2")]
public class IntegrityExclusionsApiTests : EFCoreTestBase {
  /// <summary>When the exclusions API stamps marks, the log and requests: a fixed instant, never the host clock.</summary>
  private static readonly FakeTimeProvider _clock = new(new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero));

  private const string TYPE = "Contracts.ApiProbe, Contracts";
  private const string REASON = "pre-1.0 history recorded under older rules";
  private const string OPERATOR = "operator-a";
  private const string MARKED = "marked";
  private const string THIS_SERVICE = "receiver-svc";

  private static readonly DateTimeOffset _now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

  // ── by ids, at once ──

  [Test]
  public async Task Exclude_AFewIds_MarksThemAtOnce_LogsAWarningPerOrigin_AndCountsThemAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var other = TrackedGuid.New().Value;
    var a = await _storeAsync(conn, origin, seq: 1);
    var b = await _storeAsync(conn, other, seq: 1);
    var own = await _storeAsync(conn, null, seq: null);
    await using var meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var marked = new List<long>();
    using var listener = _listen(meters, ("whizbang.stream_integrity.exclusions_marked", marked));
    var logger = new ListLogger();
    var exclusions = _exclusions(logger, new StreamIntegrityMetrics(new WhizbangMetrics(meterFactory: meters.GetRequiredService<IMeterFactory>())));

    var result = await exclusions.ExcludeAsync(_request([a, b, own]));
    listener.RecordObservableInstruments();

    await Assert.That(result.Queued).IsFalse();
    await Assert.That(result.Outcomes.Select(o => $"{o.OriginServiceId}|{o.Outcome}|{o.Count}").ToList())
      .IsEquivalentTo([$"{origin}|{MARKED}|1", $"{other}|{MARKED}|1", "|own_event|1"]);
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("Excluded 1 received", StringComparison.Ordinal)))
      .IsEqualTo(2).Because("one Warning per origin and batch");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("own_event: 1", StringComparison.Ordinal)))
      .IsTrue().Because("a refusal is logged with its reason");
    await Assert.That(marked.Sum()).IsEqualTo(2L);
  }

  [Test]
  public async Task Exclude_RefusesThisServicesOwnEvents_ByIdAndBySelectionAsync() {
    await using var conn = await _openAsync();
    var own = await _storeAsync(conn, null, seq: null);
    var local = await _scalarAsync<Guid>(conn, "SELECT service_id FROM wh_service_config LIMIT 1");
    var exclusions = _exclusions();

    var byId = await exclusions.ExcludeAsync(_request([own]));

    await Assert.That(byId.Outcomes.Single().Outcome).IsEqualTo("own_event")
      .Because("an origin never excludes its own events: its consumers compare against them (decision D1)");
    await Assert.That(async () => await exclusions.ExcludeAsync(new IntegrityExclusionRequest {
      Reason = REASON,
      RequestedBy = OPERATOR,
      Selection = new IntegrityExclusionSelection { OriginServiceId = local },
    })).Throws<ArgumentException>().Because("a selection naming this service as the origin is refused before anything is queued");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_integrity_exclusion_requests")).IsEqualTo(0L);
  }

  [Test]
  public async Task Exclude_AndUnexclude_ValidateTheRequestAsync() {
    var exclusions = _exclusions();
    Guid[] one = [TrackedGuid.New().Value];

    await Assert.That(async () => await exclusions.ExcludeAsync(_request(one) with { Reason = " " })).Throws<ArgumentException>();
    await Assert.That(async () => await exclusions.ExcludeAsync(_request(one) with { RequestedBy = "" })).Throws<ArgumentException>();
    await Assert.That(async () => await exclusions.ExcludeAsync(_request([]))).Throws<ArgumentException>()
      .Because("neither ids nor a selection");
    await Assert.That(async () => await exclusions.ExcludeAsync(_request(one) with { Selection = new IntegrityExclusionSelection() }))
      .Throws<ArgumentException>().Because("both ids and a selection");
    await Assert.That(async () => await exclusions.UnexcludeAsync(_unrequest() with { EventIds = one, RequestId = TrackedGuid.New().Value }))
      .Throws<ArgumentException>().Because("two ways at once");
    await Assert.That(async () => await exclusions.UnexcludeAsync(_unrequest())).Throws<ArgumentException>().Because("no way at all");
    await Assert.That(async () => await exclusions.UnexcludeAsync(_unrequest() with { RequestId = TrackedGuid.New().Value, EventTypes = [TYPE] }))
      .Throws<ArgumentException>().Because("types narrow an origin only");
    await Assert.That(async () => await exclusions.UnexcludeAsync(_unrequest() with { Reason = "", EventIds = one })).Throws<ArgumentException>();
    await Assert.That(async () => await exclusions.ExcludeAsync(null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await exclusions.UnexcludeAsync(null!)).Throws<ArgumentNullException>();
  }

  // ── dry run ──

  [Test]
  public async Task DryRun_ByIdsOrBySelection_CountsWhatWouldBeMarked_AndMarksNothingAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var a = await _storeAsync(conn, origin, seq: 1);
    _ = await _storeAsync(conn, origin, seq: 2);
    var exclusions = _exclusions();

    var byIds = await exclusions.ExcludeAsync(_request([a]) with { DryRun = true });
    var bySelection = await exclusions.ExcludeAsync(new IntegrityExclusionRequest {
      Reason = REASON,
      RequestedBy = OPERATOR,
      DryRun = true,
      Selection = new IntegrityExclusionSelection { OriginServiceId = origin, EventTypes = [TYPE], Unresolved = IntegrityUnresolved.None },
    });

    await Assert.That(byIds.DryRun).IsTrue();
    await Assert.That(byIds.Outcomes.Single()).IsEqualTo(new IntegrityExclusionOutcome(origin, MARKED, 1));
    await Assert.That(bySelection.Outcomes.Single()).IsEqualTo(new IntegrityExclusionOutcome(origin, MARKED, 2));
    await Assert.That(bySelection.Capped).IsFalse();
    var everyOrigin = await exclusions.ExcludeAsync(new IntegrityExclusionRequest {
      Reason = REASON,
      RequestedBy = OPERATOR,
      DryRun = true,
      Selection = new IntegrityExclusionSelection { EventTypes = [TYPE] },
    });
    await Assert.That(everyOrigin.Outcomes.Single()).IsEqualTo(new IntegrityExclusionOutcome(origin, MARKED, 2))
      .Because("a selection naming no origin reads every origin, and never this service's own events");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_integrity_exclusions")).IsEqualTo(0L);
  }

  // ── queued, and the step ──

  [Test]
  public async Task Step_AppliesQueuedRequests_LogsAWarningPerOriginAndBatch_AndPublishesTheGaugesAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    List<Guid> ids = [];
    for (var i = 0; i < 5; i++) {
      ids.Add(await _storeAsync(conn, origin, seq: i + 1));
    }
    var own = await _storeAsync(conn, null, seq: null);
    var held = await _storeAsync(conn, origin, seq: null, createdAt: _now.AddDays(-10));
    await using var meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var excluded = new List<long>();
    var pending = new List<long>();
    var unmarked = new List<long>();
    using var listener = _listen(meters, ("whizbang.stream_integrity.excluded_events", excluded),
      ("whizbang.stream_integrity.exclusions_pending", pending), ("whizbang.stream_integrity.exclusions_unmarked", unmarked));
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterFactory: meters.GetRequiredService<IMeterFactory>()));
    var options = new StreamIntegrityOptions { Exclusions = { InlineLimit = 2, BatchSize = 2 } };
    await using var services = _services(options, metrics);
    var exclusions = PostgresIntegrityExclusions.FromServices(services);

    var byIds = await exclusions.ExcludeAsync(_request([.. ids, own]));
    var bySelection = await exclusions.ExcludeAsync(new IntegrityExclusionRequest {
      Reason = REASON,
      RequestedBy = OPERATOR,
      Selection = new IntegrityExclusionSelection { OriginServiceId = origin, Unresolved = IntegrityUnresolved.Undecided, Until = _now },
    });
    var summaryQueued = await exclusions.SummarizeAsync();
    var logger = new ListLogger();
    var step = new IntegrityExclusionStep(logger);
    var cycle = await step.ApplyAsync(services, CancellationToken.None);
    listener.RecordObservableInstruments();

    await Assert.That(byIds.Queued).IsTrue().Because("five ids are past the inline limit of two");
    await Assert.That(bySelection.Queued).IsTrue().Because("a selection is always applied by the step");
    await Assert.That(summaryQueued.OpenRequests).IsEqualTo(2L);
    await Assert.That(summaryQueued.Origins).IsEmpty();
    await Assert.That(cycle.Applied).IsEqualTo(6).Because("the ids in three batches of at most two, then the held row");
    await Assert.That(cycle.Refused).IsEqualTo(1).Because("this service's own event is refused (decision D1)");
    await Assert.That(cycle.Batches).IsGreaterThanOrEqualTo(4);
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains(origin.ToString(), StringComparison.Ordinal)))
      .IsEqualTo(4).Because("one Warning per origin and batch");
    await Assert.That(excluded).IsEquivalentTo([6L]);
    await Assert.That(pending).IsEquivalentTo([1L]).Because("the held row's place at the origin is not known yet");
    var summary = await exclusions.SummarizeAsync(origin);
    await Assert.That(summary.Origins.Single()).IsEqualTo(new IntegrityExclusionOriginSummary(origin, 6, 1, 0, summary.Origins[0].LastMarkedAt));
    await Assert.That(summary.OpenRequests).IsEqualTo(0L);
    await Assert.That(step.Name).IsEqualTo("integrity-exclusions");

    // The reversal of three marks by id (past the inline limit), of the selection's request by its id, and of the rest by origin.
    var undoIds = await exclusions.UnexcludeAsync(_unrequest() with { EventIds = [.. ids.Take(3)] });
    var undoSelection = await exclusions.UnexcludeAsync(_unrequest() with { RequestId = bySelection.RequestId });
    var undoOrigin = await exclusions.UnexcludeAsync(_unrequest() with { OriginServiceId = origin, EventTypes = [TYPE] });
    await step.RunAsync(services, CancellationToken.None);
    listener.RecordObservableInstruments();

    await Assert.That(undoIds.Queued).IsTrue();
    await Assert.That(undoSelection.Queued).IsTrue();
    await Assert.That(undoOrigin.Queued).IsTrue();
    await Assert.That(unmarked.Sum()).IsEqualTo(6L);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_integrity_exclusions")).IsEqualTo(0L);
    await Assert.That(await _scalarAsync<int>(conn, "SELECT flags FROM wh_event_store WHERE event_id = $1", held)).IsEqualTo(0);
  }

  [Test]
  public async Task Step_LogsAFailedRequest_AndRollsTheLogOffPastItsRetentionAsync() {
    await using var conn = await _openAsync();
    var a = await _storeAsync(conn, TrackedGuid.New().Value, seq: 1);
    await _executeAsync(conn, """
      INSERT INTO wh_integrity_exclusion_requests (request_id, action, event_ids, reason, requested_by, requested_at)
      VALUES ($1, 'exclude', $2, ' ', 'someone', $3)
      """, TrackedGuid.New().Value, new[] { a }, _now);
    await _executeAsync(conn, """
      INSERT INTO wh_integrity_exclusion_log (logged_at, action, origin_service_id, event_count, reason, requested_by)
      VALUES ($1, 'exclude', $2, 1, 'old', 'someone')
      """, _now.AddDays(-400), TrackedGuid.New().Value);
    await using var services = _services(new StreamIntegrityOptions(), metrics: null);
    var logger = new ListLogger();

    var cycle = await IntegrityExclusionStep.FromServices(services).ApplyAsync(services, CancellationToken.None);
    var withLogger = await new IntegrityExclusionStep(logger).ApplyAsync(services, CancellationToken.None);

    await Assert.That(cycle.Applied).IsEqualTo(0);
    await Assert.That(cycle.RolledOff).IsEqualTo(1).Because("the year-old log row is past the 365-day retention");
    await Assert.That(withLogger.Batches).IsEqualTo(0).Because("a failed request is not retried");
    await Assert.That(await _scalarAsync<string>(conn, "SELECT status FROM wh_integrity_exclusion_requests")).IsEqualTo("Failed");
  }

  [Test]
  public async Task TheSelection_IsWrittenAsTheSqlReadsIt_EachFieldOnlyWhenGivenAsync() {
    var origin = TrackedGuid.New().Value;

    var empty = PostgresIntegrityExclusions.SelectionJson(new IntegrityExclusionSelection());
    var full = PostgresIntegrityExclusions.SelectionJson(new IntegrityExclusionSelection {
      OriginServiceId = origin,
      EventTypes = [TYPE],
      From = _now.AddDays(-1),
      Until = _now,
      Unresolved = IntegrityUnresolved.All,
    });

    await Assert.That(empty).IsEqualTo("{}");
    await Assert.That(full).IsEqualTo(
      $$$"""{"originServiceId":"{{{origin}}}","eventTypes":["{{{TYPE}}}"],"from":"2026-11-26T12:00:00+00:00","until":"2026-11-27T12:00:00+00:00","unresolved":7}""");
  }

  [Test]
  public async Task TheConstructionAndTheStepsArguments_AreCheckedAsync() {
    var database = _connection();
    var options = new StreamIntegrityOptions();
    var logger = new ListLogger();

    await Assert.That(() => new PostgresIntegrityExclusions(null!, options, _clock, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new PostgresIntegrityExclusions(database, null!, _clock, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new PostgresIntegrityExclusions(database, options, null!, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new PostgresIntegrityExclusions(database, options, _clock, null, null!)).Throws<ArgumentNullException>();
    await Assert.That(() => PostgresIntegrityExclusions.FromServices(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => PostgresIntegrityExclusions.SelectionJson(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new IntegrityExclusionStep(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => IntegrityExclusionStep.FromServices(null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await new IntegrityExclusionStep(logger).ApplyAsync(null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await using var bare = new ServiceCollection().AddSingleton(database).BuildServiceProvider();
    var bareExclusions = PostgresIntegrityExclusions.FromServices(bare);
    var notStored = await bareExclusions.ExcludeAsync(_request([TrackedGuid.New().Value]));
    var dryRun = await bareExclusions.ExcludeAsync(_request([TrackedGuid.New().Value]) with { DryRun = true });
    var cycle = await IntegrityExclusionStep.FromServices(bare).ApplyAsync(bare, CancellationToken.None);
    await Assert.That(notStored.Outcomes.Single().Outcome).IsEqualTo("not_stored")
      .Because("a host with only the store gets the defaults, the system clock, no service name and no logging");
    await Assert.That(dryRun.Outcomes.Single().Outcome).IsEqualTo("not_stored");
    await Assert.That(cycle.Batches).IsEqualTo(0);
  }

  [Test]
  public async Task TheOriginsPositionAnswer_NamesTheBucketItCountsEachEventInAsync() {
    await using var conn = await _openAsync();
    var own = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await _executeAsync(conn, """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, flags, created_at)
      VALUES ($1, $2, $2, 'TestAggregate', $3, jsonb_build_object('t', 'tenant-p'), 1, nextval('wh_commit_seq'), 0, $4)
      """, own, stream, TYPE, _now);
    var unknown = TrackedGuid.New().Value;

    var answer = await new OriginPositionStore(_connection()).PositionsOfAsync([own, unknown], CancellationToken.None);

    var positioned = answer.Single(a => a.EventId == own);
    await Assert.That(positioned.State).IsEqualTo("positioned");
    await Assert.That(positioned.TenantScope).IsEqualTo("tenant-p");
    await Assert.That(positioned.EventType).IsEqualTo(TYPE);
    await Assert.That(positioned.StreamId).IsEqualTo(stream);
    var none = answer.Single(a => a.EventId == unknown);
    await Assert.That(none.TenantScope).IsNull();
    await Assert.That(none.EventType).IsNull();
    await Assert.That(none.StreamId).IsNull();
  }

  [Test]
  public async Task UnexcludingAnOrigin_ReversesOnlyThatOriginsMarks_OfTheTypesNamedAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var other = TrackedGuid.New().Value;
    var mine = await _storeAsync(conn, origin, seq: 1);
    var theirs = await _storeAsync(conn, other, seq: 1);
    var options = new StreamIntegrityOptions();
    await using var services = _services(options, metrics: null);
    var exclusions = PostgresIntegrityExclusions.FromServices(services);
    _ = await exclusions.ExcludeAsync(_request([mine, theirs]));

    var queued = await exclusions.UnexcludeAsync(_unrequest() with { OriginServiceId = origin, EventTypes = [TYPE] });
    _ = await new IntegrityExclusionStep(new ListLogger()).ApplyAsync(services, CancellationToken.None);

    await Assert.That(queued.Queued).IsTrue().Because("reversing by origin is a selection, applied by the step");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_integrity_exclusions WHERE event_id = $1", mine))
      .IsEqualTo(0L);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_integrity_exclusions WHERE event_id = $1", theirs))
      .IsEqualTo(1L).Because("another origin's marks are not named, so they stay");
  }

  [Test]
  public async Task TheOriginsAnswerReceptor_SendsTheBucketEachEventIsCountedInAsync() {
    await using var conn = await _openAsync();
    var own = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await _executeAsync(conn, """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, flags, created_at)
      VALUES ($1, $2, $2, 'TestAggregate', $3, jsonb_build_object('t', 'tenant-r'), 1, nextval('wh_commit_seq'), 0, $4)
      """, own, stream, TYPE, _now);
    var transport = new IntegrityWireTransport();
    var json = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    await using var services = new ServiceCollection()
      .AddSingleton(_connection())
      .AddSingleton<ITransport>(transport)
      .AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(json))
      .AddScoped<IWorkCoordinator>(_ => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), json))
      .BuildServiceProvider();

    await new RequestOriginPositionsReceptor(services.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<RequestOriginPositionsReceptor>.Instance).HandleAsync(new RequestOriginPositions {
        RequesterService = "consumer-svc",
        Topic = "consumer.inbox",
        EventIds = [own],
      });

    var (envelope, _, _) = transport.Published.Single();
    var answer = (OriginPositionsAnswer)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(), json.GetTypeInfo(typeof(OriginPositionsAnswer)))!;
    var position = answer.Positions.Single();
    await Assert.That(position.TenantScope).IsEqualTo("tenant-r");
    await Assert.That(position.EventType).IsEqualTo(TYPE);
    await Assert.That(position.StreamId).IsEqualTo(stream)
      .Because("an asker that excluded the event removes it from the bucket the origin counts it in (#2588)");
  }

  [Test]
  public async Task ThePostgresDriver_RegistersTheApi_AndTheStepAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    await Assert.That(scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<IntegrityExclusionStep>().Count()).IsEqualTo(1)
      .Because("turnkey: queued exclusion requests are applied by the maintenance cycle");
    await Assert.That(services.Count(d => d.ServiceType == typeof(IIntegrityExclusions))).IsEqualTo(1)
      .Because("the API is registered by the driver, so an operator endpoint can resolve it");
  }

  [Test]
  public async Task Unexclude_AnIdThatWasNeverExcluded_IsNotExcluded_AndNotARefusalAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var marked = await _storeAsync(conn, origin, seq: 1);
    var never = await _storeAsync(conn, origin, seq: 2);
    var logger = new ListLogger();
    var exclusions = _exclusions(logger);
    _ = await exclusions.ExcludeAsync(_request([marked]));
    var again = await exclusions.ExcludeAsync(_request([marked]));

    var undone = await exclusions.UnexcludeAsync(_unrequest() with { EventIds = [marked, never] });

    await Assert.That(again.Outcomes.Single()).IsEqualTo(new IntegrityExclusionOutcome(origin, "already_excluded", 1));
    List<IntegrityExclusionOutcome> expected = [new(origin, "unmarked", 1), new(null, "not_excluded", 1)];
    await Assert.That(undone.Outcomes).IsEquivalentTo(expected);
    await Assert.That(logger.Entries.Any(e => e.Message.Contains("were refused", StringComparison.Ordinal))).IsFalse()
      .Because("marking what is already excluded, or reversing what never was, is nothing to do, not a refusal");
  }

  [Test]
  public async Task AStoreWithoutAServiceIdentity_RefusesNoSelectionByItsOriginAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    _ = await _storeAsync(conn, origin, seq: 1);
    await _executeAsync(conn, "DELETE FROM wh_service_config");

    var dryRun = await _exclusions().ExcludeAsync(new IntegrityExclusionRequest {
      Reason = REASON,
      RequestedBy = OPERATOR,
      DryRun = true,
      Selection = new IntegrityExclusionSelection { OriginServiceId = origin },
    });

    await Assert.That(dryRun.Outcomes.Single()).IsEqualTo(new IntegrityExclusionOutcome(origin, MARKED, 1))
      .Because("with no service id recorded there is nothing a selection's origin could be confused with");
  }

  [Test]
  public async Task TheApiTheStepAndTheStore_ReachTheTablesThroughTheSearchPath_WhenNoSchemaIsNamedAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var a = await _storeAsync(conn, origin, seq: 1);
    var searchPath = new PostgresArchiveConnection(async ct => {
      var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, null);
    var exclusions = new PostgresIntegrityExclusions(searchPath, new StreamIntegrityOptions(), new FakeTimeProvider(_now), THIS_SERVICE,
      new ListLogger());
    await using var services = new ServiceCollection().AddSingleton(searchPath).BuildServiceProvider();

    var marked = await exclusions.ExcludeAsync(_request([a]));
    var cycle = await new IntegrityExclusionStep(new ListLogger()).ApplyAsync(services, CancellationToken.None);
    var adjustment = await new IntegrityExclusionStore(searchPath)
      .AdjustmentAsync(origin, null, null, null, null, byStream: false, CancellationToken.None);

    await Assert.That(marked.Outcomes.Single().Outcome).IsEqualTo(MARKED);
    await Assert.That(cycle.Batches).IsEqualTo(0);
    await Assert.That(adjustment.Single().Digest.EventCount).IsEqualTo(1);
  }

  [Test]
  public async Task TheAnswerReceptor_LeavesExcludedEventsOutOfEveryList_ItFillsOrRecordsAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var excludedUnstamped = await _storeAsync(conn, origin, seq: null);
    var excludedUnknown = await _storeAsync(conn, origin, seq: null);
    var keptUnknown = await _storeAsync(conn, origin, seq: null);
    _ = await _exclusions().ExcludeAsync(_request([excludedUnstamped, excludedUnknown]));
    await using var services = new ServiceCollection().AddSingleton(_connection()).BuildServiceProvider();

    await new OriginPositionsAnswerReceptor(services.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<OriginPositionsAnswerReceptor>.Instance).HandleAsync(new OriginPositionsAnswer {
        OriginServiceId = origin,
        NotYetStamped = [excludedUnstamped],
        Unknown = [excludedUnknown, keptUnknown],
      });

    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_origin_position_unknown WHERE event_id = $1", keptUnknown))
      .IsEqualTo(1L).Because("an event that is not excluded is recorded as the origin answered");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_origin_position_unknown WHERE event_id = $1", excludedUnknown))
      .IsEqualTo(0L).Because("an excluded event's answer settles its mark instead");
    await Assert.That(await _scalarAsync<short>(conn, "SELECT origin_state FROM wh_integrity_exclusions WHERE event_id = $1", excludedUnknown))
      .IsEqualTo((short)2);
    await Assert.That(await _scalarAsync<short>(conn, "SELECT origin_state FROM wh_integrity_exclusions WHERE event_id = $1", excludedUnstamped))
      .IsEqualTo((short)0).Because("an unstamped event stays pending and is asked again");
  }

  // ── fixture ──

  private PostgresIntegrityExclusions _exclusions(ILogger? logger = null, StreamIntegrityMetrics? metrics = null) =>
    new(_connection(), new StreamIntegrityOptions(), new FakeTimeProvider(_now), THIS_SERVICE, logger ?? new ListLogger(), metrics);

  private ServiceProvider _services(StreamIntegrityOptions options, StreamIntegrityMetrics? metrics) {
    var services = new ServiceCollection()
      .AddSingleton(_connection())
      .AddSingleton(Options.Create(options))
      .AddSingleton<TimeProvider>(new FakeTimeProvider(_now))
      .AddSingleton<IServiceInstanceProvider>(new IntegrityWireInstance(THIS_SERVICE));
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    return services.BuildServiceProvider();
  }

  private static IntegrityExclusionRequest _request(Guid[] ids) =>
    new() { Reason = REASON, RequestedBy = OPERATOR, EventIds = ids };

  private static IntegrityUnexclusionRequest _unrequest() => new() { Reason = "the fix shipped", RequestedBy = OPERATOR };

  private PostgresArchiveConnection _connection() =>
    new(async ct => {
      var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, "public");

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task<Guid> _storeAsync(NpgsqlConnection conn, Guid? origin, long? seq, DateTimeOffset? createdAt = null) {
    var id = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await _executeAsync(conn, """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
         commit_sequence, flags, created_at, origin_service_id, origin_commit_sequence)
      VALUES ($1, $2, $2, 'TestAggregate', $3, NULL, 1, nextval('wh_commit_seq'), 0, $4, $5, $6)
      """, id, stream, TYPE, createdAt ?? _now.AddDays(-1), (object?)origin ?? DBNull.Value, (object?)seq ?? DBNull.Value);
    return id;
  }

  private static MeterListener _listen(ServiceProvider meters, params (string Name, List<long> Into)[] instruments) {
    var factory = meters.GetRequiredService<IMeterFactory>();
    var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Meter.Scope == factory && instruments.Any(i => i.Name == instrument.Name)) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
      instruments.First(i => i.Name == instrument.Name).Into.Add(value));
    listener.Start();
    return listener;
  }

  private static NpgsqlCommand _command(NpgsqlConnection conn, string sql, object[] args) {
    var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    return cmd;
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = _command(conn, sql, args);
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = _command(conn, sql, args);
    _ = await cmd.ExecuteNonQueryAsync();
  }

  private sealed class ListLogger : ILogger<PostgresIntegrityExclusions>, ILogger<IntegrityExclusionStep> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }
}
