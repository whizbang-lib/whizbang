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
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The one-off return to the local lane of events this service wrote that were stored under another service's origin
/// before 594 (#2573), against the real <c>wh_written_here_restoration_window</c> and
/// <c>wh_restore_written_here_origins</c> (migration 599) and the maintenance step that drives them. Two shapes were
/// misattributed: a receptor's reaction moved by the re-attribution walk to the trigger's owner with no origin position
/// (the owner then answered "unknown"), and this service's own event that adopted the origin of another service's
/// redelivery bundle. Each event is written here through the real outbox emit chain, then given the wrong origin the way
/// the old passes did, so its stored first hop, staged form and route are exactly what such a row carries. The proof that
/// a row was written here is its first hop naming this service; a row the evidence does not prove is left and counted.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/599_EventsWrittenHereAreReturnedToTheLocalLane.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/WrittenHereOriginRestorationStep.cs</code-under-test>
/// <docs>resilience/stream-integrity#events-written-here-returned</docs>
[Category("Integration")]
[Category("Shard5")]
public class WrittenHereOriginRestorationSqlTests : EFCoreTestBase {
  // This service is "orders"; the reaction's type is in the domain another service ("billing-ünïcode") owns.
  private const string REACTION_TYPE = "MyApp.Billing.InvoiceSucceeded, MyApp";
  private const string TENANT = "tenant-a";
  private const string WRITE_FORM = "metadata_write_form";
  private const string RESTORED_METRIC = "whizbang.stream_integrity.written_here_origins_restored";
  private const string AMBIGUOUS_METRIC = "whizbang.stream_integrity.written_here_restoration_ambiguous";
  private static readonly DateTimeOffset _t0 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
  private static readonly Guid _billing = Guid.Parse("0b000000-0000-0000-0000-0000000025b1");
  private static readonly string _ordersName = StoredMetadataCorpus.Orders.ServiceName;

  // ── the restore ───────────────────────────────────────────────────────

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Restore_AReactionAndAReturn_GoBackToTheLocalLane_AndAReceivedEventStaysAsync(bool distilled) {
    // distilled: the route keeps the first hop's service; otherwise the staged full form does.
    await using var conn = await _openAsync();
    var reaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    var returned = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    var received = await _emitHereAsync(conn, StoredMetadataCorpus.Billing);
    if (distilled) {
      await _distillAsync(conn);
    }
    await _misattributeAsync(conn, reaction, _billing, null);   // the re-attribution walk's shape
    await _misattributeAsync(conn, returned, _billing, 7);      // the redelivery adoption's shape
    await _misattributeAsync(conn, received, _billing, 9);      // genuinely received history

    var rows = await _restoreAllAsync(conn, _ordersName);

    await Assert.That(rows).IsEquivalentTo([(true, (Guid?)_billing, 1, 1, 0)])
      .Because("the caller logs and counts, per origin, the reactions and returns it restored");
    await Assert.That(await _originOfAsync(conn, reaction)).IsEqualTo(((Guid?)null, (long?)null));
    await Assert.That(await _originOfAsync(conn, returned)).IsEqualTo(((Guid?)null, (long?)null))
      .Because("the bundle's position belongs to the other service's copy; written here, the event has no origin position");
    await Assert.That(await _originOfAsync(conn, received)).IsEqualTo(((Guid?)_billing, (long?)9L))
      .Because("an event whose first hop names its origin was received from it, and stands");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_event_store")).IsEqualTo(3L)
      .Because("the restore never deletes an event");
  }

  [Test]
  public async Task Restore_CleansTheBookkeepingTheRowsPolluted_AndNoOtherAsync() {
    await using var conn = await _openAsync();
    var reaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    var received = await _emitHereAsync(conn, StoredMetadataCorpus.Billing);
    await _distillAsync(conn);
    await _misattributeAsync(conn, reaction, _billing, null);
    await _misattributeAsync(conn, received, _billing, null);   // received, still waiting for its position
    await _executeAsync(conn, "INSERT INTO wh_origin_position_unknown (event_id, origin_service_id) VALUES (@e, @o)",
      ("e", reaction), ("o", _billing));
    var stream = await _streamOfAsync(conn, reaction);
    var receivedStream = await _streamOfAsync(conn, received);
    await _executeAsync(conn, """
      INSERT INTO wh_integrity_ledger
        (origin_service_id, tenant_scope, event_type, stream_id, origin_lo, origin_hi, local_lo, local_hi,
         repair_attempts, unhealable_at, exhausted_at, answer_pending_until, answered_at)
      VALUES
        (@billing, @tenant, @type, @stream, 1, 1, 0, 0, 5, NOW(), NOW(), NOW() + INTERVAL '1 hour', NOW()),
        (@billing, @tenant, @type, '00000000-0000-0000-0000-000000000000', 1, 1, 0, 0, 5, NOW(), NOW(), NOW() + INTERVAL '1 hour', NOW()),
        (@billing, @tenant, @type, @other, 1, 1, 0, 0, 5, NOW(), NOW(), NOW() + INTERVAL '1 hour', NOW())
      """, ("billing", _billing), ("tenant", TENANT), ("type", REACTION_TYPE), ("stream", stream), ("other", receivedStream));
    await Assert.That(await _heldAsync(conn, _billing)).IsEqualTo(2L).Because("precondition: both rows are held without a position");

    _ = await _restoreAllAsync(conn, _ordersName);

    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_origin_position_unknown")).IsEqualTo(0L)
      .Because("the origin's \"unknown\" answer was about an event it never wrote, and the event is no longer its");
    await Assert.That(await _heldAsync(conn, _billing)).IsEqualTo(1L)
      .Because("the reaction left the origin's held set, so the windows it kept undecided can be decided; the received row stays held");
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, Guid.Empty, stream)).IsEqualTo(1L)
      .Because("the reaction's digest joined the local lane, folded through the shared helper");
    await Assert.That(await _scalarAsync<long>(conn,
      "SELECT count(*) FROM wh_stream_digests WHERE origin_service_id = $1 AND stream_id = $2", _billing, stream)).IsEqualTo(0L)
      .Because("the origin's emptied bucket is removed, not left at zero");
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, _billing, receivedStream)).IsEqualTo(1L);
    await Assert.That(await _scalarAsync<long>(conn, """
      SELECT count(*) FROM wh_integrity_ledger
      WHERE stream_id <> $1 AND repair_attempts = 0 AND unhealable_at IS NULL AND exhausted_at IS NULL
        AND answer_pending_until IS NULL AND answered_at IS NULL
      """, receivedStream)).IsEqualTo(2L)
      .Because("the origin's buckets the reaction left (the stream and the type lane) changed on this side: a bucket marked unhealable can heal now");
    await Assert.That(await _scalarAsync<int>(conn, "SELECT repair_attempts FROM wh_integrity_ledger WHERE stream_id = $1", receivedStream))
      .IsEqualTo(5).Because("a bucket no restored row touches keeps its ladder");
  }

  [Test]
  public async Task Restore_ARowItCannotProve_IsLeftAndCountedAsAmbiguousAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, WRITE_FORM, "Slim");   // nothing staged or distilled: the row is all there is
    var slimReaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    var slimReceived = await _emitHereAsync(conn, StoredMetadataCorpus.Billing);
    await _misattributeAsync(conn, slimReaction, _billing, null);
    await _misattributeAsync(conn, slimReceived, _billing, 4);
    await _executeAsync(conn, "INSERT INTO wh_origin_position_unknown (event_id, origin_service_id) VALUES (@e, @o)",
      ("e", slimReaction), ("o", _billing));

    var rows = await _restoreAllAsync(conn, _ordersName);

    await Assert.That(rows).IsEquivalentTo([(true, (Guid?)_billing, 0, 0, 1)])
      .Because("an unpositioned row that names no service anywhere cannot be told from a received slim row: it is counted, never moved");
    await Assert.That(await _originOfAsync(conn, slimReaction)).IsEqualTo(((Guid?)_billing, (long?)null));
    await Assert.That(await _originOfAsync(conn, slimReceived)).IsEqualTo(((Guid?)_billing, (long?)4L))
      .Because("a positioned row with no evidence is ordinary received history, neither moved nor counted");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_origin_position_unknown")).IsEqualTo(1L)
      .Because("an ambiguous row keeps its bookkeeping");
  }

  [Test]
  public async Task Restore_ARowRecordedUnderThisServiceOrTheEmptyOrigin_IsNoCandidateAsync() {
    await using var conn = await _openAsync();
    var local = await _scalarAsync<Guid>(conn, "SELECT service_id FROM wh_service_config LIMIT 1");
    var underLocal = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    var underEmpty = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    await _distillAsync(conn);
    await _misattributeAsync(conn, underLocal, local, 3);
    await _misattributeAsync(conn, underEmpty, Guid.Empty, 4);

    var rows = await _restoreAllAsync(conn, _ordersName);

    await Assert.That(rows).IsEquivalentTo([(true, (Guid?)null, 0, 0, 0)])
      .Because("the emit chain already reads this service's id and the empty id as locally originated");
    await Assert.That(await _originOfAsync(conn, underLocal)).IsEqualTo(((Guid?)local, (long?)3L));
  }

  [Test]
  [Arguments("")]
  [Arguments("Unknown")]
  public async Task Restore_WithoutAServiceName_ProvesNothingAsync(string name) {
    await using var conn = await _openAsync();
    var reaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    await _distillAsync(conn);
    await _misattributeAsync(conn, reaction, _billing, null);

    var rows = await _restoreAllAsync(conn, name);

    await Assert.That(rows.All(r => r.Reactions == 0 && r.Returns == 0)).IsTrue();
    await Assert.That(await _originOfAsync(conn, reaction)).IsEqualTo(((Guid?)_billing, (long?)null));
  }

  [Test]
  public async Task Restore_ReturnsTheSealedEpochItLeft_ToTheStoreAsync() {
    await using var conn = await _openAsync();
    var returned = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    await _distillAsync(conn);
    await _misattributeAsync(conn, returned, _billing, 5);
    await _executeAsync(conn, """
      INSERT INTO wh_digest_epoch_frontiers (origin_service_id, closed_through_epoch, epoch_width, updated_at)
      VALUES (@billing, 0, 1000, NOW())
      """, ("billing", _billing));
    _ = await _scalarAsync<int>(conn, "SELECT refold_digest_epochs($1, 0, 0)", _billing);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_digest_epochs WHERE origin_service_id = $1", _billing))
      .IsEqualTo(1L).Because("precondition: the origin's sealed epoch counts the event");

    _ = await _restoreAllAsync(conn, _ordersName);

    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_digest_epochs WHERE origin_service_id = $1", _billing))
      .IsEqualTo(0L).Because("the origin's seal no longer counts an event it never wrote, so it can match the origin's answer");
  }

  [Test]
  public async Task Restore_RunTwice_ChangesNothingTheSecondTimeAsync() {
    await using var conn = await _openAsync();
    var reaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    await _distillAsync(conn);
    await _misattributeAsync(conn, reaction, _billing, null);
    var stream = await _streamOfAsync(conn, reaction);
    _ = await _restoreAllAsync(conn, _ordersName);
    var digests = await _scalarAsync<string>(conn,
      "SELECT string_agg(origin_service_id::text || event_count::text || digest_lo::text, ',') FROM wh_stream_digests WHERE stream_id = $1", stream);
    await _executeAsync(conn, "UPDATE wh_written_here_restoration SET through_commit_sequence = 0");

    var again = await _restoreAllAsync(conn, _ordersName);

    await Assert.That(again).IsEquivalentTo([(true, (Guid?)null, 0, 0, 0)])
      .Because("a restored row has no origin, so it is never a candidate again");
    await Assert.That(await _scalarAsync<string>(conn,
      "SELECT string_agg(origin_service_id::text || event_count::text || digest_lo::text, ',') FROM wh_stream_digests WHERE stream_id = $1", stream))
      .IsEqualTo(digests);
  }

  [Test]
  public async Task Restore_FromAStaleCursor_ChangesNothingAndReportsNotAdvancedAsync() {
    await using var conn = await _openAsync();
    var reaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    await _distillAsync(conn);
    await _misattributeAsync(conn, reaction, _billing, null);
    var (after, through) = await _windowAsync(conn, _ordersName, 100);

    var rows = await _restoreAsync(conn, after + 1, through, _ordersName);

    await Assert.That(rows).IsEquivalentTo([(false, (Guid?)null, 0, 0, 0)]);
    await Assert.That(await _originOfAsync(conn, reaction)).IsEqualTo(((Guid?)_billing, (long?)null))
      .Because("the cursor moved under the batch, so the batch is discarded whole");
  }

  // ── the window ────────────────────────────────────────────────────────

  [Test]
  public async Task Window_WalksUpToTheCeilingTheFirstWindowSet_InBoundedBatchesAsync() {
    await using var conn = await _openAsync();
    _ = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    var second = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    var third = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    var secondSeq = await _sequenceOfAsync(conn, second);
    var thirdSeq = await _sequenceOfAsync(conn, third);

    var (after1, through1) = await _windowAsync(conn, _ordersName, 2);
    _ = await _restoreAsync(conn, after1, through1, _ordersName);
    _ = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);   // written after the walk began: 594 keeps it right
    var (after2, through2) = await _windowAsync(conn, _ordersName, 2);
    _ = await _restoreAsync(conn, after2, through2, _ordersName);
    var (after3, through3) = await _windowAsync(conn, _ordersName, 2);

    await Assert.That((after1, through1)).IsEqualTo((0L, secondSeq)).Because("a batch is bounded by rows scanned");
    await Assert.That((after2, through2)).IsEqualTo((secondSeq, thirdSeq))
      .Because("the walk stops at the highest commit sequence when it began");
    await Assert.That((after3, through3)).IsEqualTo((thirdSeq, thirdSeq)).Because("a finished walk has nothing more to read");
  }

  [Test]
  public async Task Window_WithAnotherServiceName_RewalksFromTheStartAsync() {
    await using var conn = await _openAsync();
    var only = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    var (after, through) = await _windowAsync(conn, _ordersName, 100);
    _ = await _restoreAsync(conn, after, through, _ordersName);

    var (sameAfter, _) = await _windowAsync(conn, " " + _ordersName.ToUpperInvariant() + " ");
    var (otherAfter, otherThrough) = await _windowAsync(conn, "orders-renamed", 100);

    await Assert.That(sameAfter).IsEqualTo(through).Because("the cursor is durable, and the name compares trimmed and without case");
    await Assert.That((otherAfter, otherThrough)).IsEqualTo((0L, await _sequenceOfAsync(conn, only)))
      .Because("rows are proved by the service's name, so another name walks again");
  }

  [Test]
  public async Task WindowAndRestore_UnderAGenericPlan_RangeScanTheCommitSequenceIndexAsync() {
    await using var conn = await _openAsync();
    // A wide store, analyzed, so a scan of it is visible as thousands of rows where a batch needs a handful.
    await _executeAsync(conn, """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, created_at,
         origin_service_id, origin_commit_sequence)
      SELECT gen_random_uuid(), s, s, 'Order', 'Contracts.Other, Contracts', NULL, 1, nextval('wh_commit_seq'), NOW(), @billing, 1
      FROM (SELECT gen_random_uuid() AS s FROM generate_series(1, 5000)) g;
      ANALYZE wh_event_store;
      """, ("billing", _billing));
    var floor = await _scalarAsync<long>(conn, "SELECT max(commit_sequence) FROM wh_event_store");
    var reaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    var returned = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    await _distillAsync(conn);
    await _misattributeAsync(conn, reaction, _billing, null);
    await _misattributeAsync(conn, returned, _billing, 8);
    await _executeAsync(conn, """
      INSERT INTO wh_written_here_restoration (singleton, through_commit_sequence, ceiling_commit_sequence, service_name)
      VALUES (true, @f, @f + 1000, @name)
      """, ("f", floor), ("name", _ordersName));
    await _executeAsync(conn, "SET plan_cache_mode = force_generic_plan");

    var nodes = await _plansAsync(conn, async () => {
      var (after, through) = await _windowAsync(conn, _ordersName, 10);
      _ = await _restoreAsync(conn, after, through, _ordersName);
    });

    var storeReads = nodes.Where(n => n.Relation == "wh_event_store").ToList();
    await Assert.That(storeReads).IsNotEmpty().Because("precondition: the window and the restore read the store");
    await Assert.That(storeReads.Any(n => n.Type == "Seq Scan")).IsFalse()
      .Because("both read the batch's commit-sequence range through its index; a sequential scan reads the whole store every batch");
    await Assert.That(storeReads.Max(n => n.RowsReturned)).IsLessThanOrEqualTo(10).Because("the batch is the two rows past the cursor");
    await Assert.That(nodes.Where(n => n.Relation is "wh_stream_digests").All(n => n.Type != "Seq Scan")).IsTrue();
  }

  // ── the step ──────────────────────────────────────────────────────────

  [Test]
  public async Task Step_RestoresInBoundedBatches_LogsAndCountsEach_AndResumesFromItsCursorAsync() {
    await using var conn = await _openAsync();
    var reaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    var returned = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    await _settingAsync(conn, WRITE_FORM, "Slim");
    var unproven = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    await _distillAsync(conn);
    await _misattributeAsync(conn, reaction, _billing, null);
    await _misattributeAsync(conn, returned, _billing, 7);
    await _misattributeAsync(conn, unproven, _billing, null);
    var logger = new ListLogger();
    var restored = new List<(long Value, string? Shape)>();
    var ambiguous = new List<long>();
    using var listener = _listen(restored, ambiguous);
    var services = _services(new StreamIntegrityOptions { ReattributionBatchSize = 1, MaxReattributionBatchesPerPass = 2 }, _ordersName);
    var step = new WrittenHereOriginRestorationStep(logger);

    var cycle1 = await step.RestoreAsync(services, CancellationToken.None);

    await Assert.That(cycle1).IsEqualTo(new WrittenHereOriginRestorationResult(2, 1, 1, 0, "budget"))
      .Because("a cycle runs at most its batch budget, each batch a bounded number of rows");
    await Assert.That((await _originOfAsync(conn, unproven)).Origin).IsEqualTo(_billing).Because("not reached yet");

    await step.RunAsync(services, CancellationToken.None);
    var cycle3 = await step.RestoreAsync(services, CancellationToken.None);

    await Assert.That(cycle3).IsEqualTo(new WrittenHereOriginRestorationResult(0, 0, 0, 0, "caught-up"));
    await Assert.That(await _originOfAsync(conn, reaction)).IsEqualTo(((Guid?)null, (long?)null));
    await Assert.That(await _originOfAsync(conn, returned)).IsEqualTo(((Guid?)null, (long?)null));
    await Assert.That((await _originOfAsync(conn, unproven)).Origin).IsEqualTo(_billing);
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains(_billing.ToString(), StringComparison.Ordinal)
      && e.Message.Contains("local lane", StringComparison.Ordinal))).IsEqualTo(2)
      .Because("every restoration is named at Warning with the origin the events were wrongly recorded under");
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("ambiguous", StringComparison.Ordinal)))
      .IsEqualTo(1).Because("a row the evidence does not prove is reported, so an operator knows what is left");
    listener.RecordObservableInstruments();
    // A passive counter also observes its untagged running total, which nothing here adds to.
    await Assert.That(restored.Where(r => r.Value > 0)).IsEquivalentTo([(1L, (string?)"reaction"), (1L, (string?)"return")])
      .Because("and counted, by shape");
    await Assert.That(ambiguous.Where(v => v > 0)).IsEquivalentTo([1L]);
    await Assert.That(step.Name).IsEqualTo("written-here-origin-restoration");
  }

  [Test]
  public async Task Step_WithoutAServiceName_OrSwitchedOff_TouchesNothingAsync() {
    await using var conn = await _openAsync();
    var reaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    await _distillAsync(conn);
    await _misattributeAsync(conn, reaction, _billing, null);
    var step = new WrittenHereOriginRestorationStep(NullLogger.Instance);

    var off = await step.RestoreAsync(_services(new StreamIntegrityOptions { ReattributionEnabled = false }, _ordersName), CancellationToken.None);
    var unnamed = await step.RestoreAsync(_services(new StreamIntegrityOptions(), ServiceInstanceInfo.Unknown.ServiceName), CancellationToken.None);
    var noProvider = await step.RestoreAsync(_services(new StreamIntegrityOptions(), null), CancellationToken.None);

    await Assert.That(off.Status).IsEqualTo("skipped (Whizbang:StreamIntegrity:ReattributionEnabled=false)");
    await Assert.That(unnamed.Status).IsEqualTo("skipped (no service name: only this service's name proves an event was written here)");
    await Assert.That(noProvider.Status).IsEqualTo(unnamed.Status);
    await Assert.That((await _originOfAsync(conn, reaction)).Origin).IsEqualTo(_billing);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_written_here_restoration")).IsEqualTo(0L)
      .Because("a skipped cycle reads nothing");
  }

  [Test]
  public async Task Step_WithOnlyTheDatabaseAndTheServiceName_UsesTheDefaults_AndRestoresAsync() {
    await using var conn = await _openAsync();
    var reaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    var returned = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    await _settingAsync(conn, WRITE_FORM, "Slim");
    var unproven = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    await _distillAsync(conn);
    await _misattributeAsync(conn, reaction, _billing, null);
    await _misattributeAsync(conn, returned, _billing, 7);
    await _misattributeAsync(conn, unproven, _billing, null);
    var bare = new ServiceCollection()
      .AddSingleton(_connection(schema: null))
      .AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.Empty, _ordersName, "host", 1))
      .BuildServiceProvider();

    var result = await new WrittenHereOriginRestorationStep(NullLogger.Instance).RestoreAsync(bare, CancellationToken.None);

    // No metrics are registered, so every shape (reaction, return, ambiguous) is recorded without a counter.
    await Assert.That(result).IsEqualTo(new WrittenHereOriginRestorationResult(1, 1, 1, 1, "caught-up"));
    await Assert.That((await _originOfAsync(conn, reaction)).Origin).IsNull();
    await Assert.That((await _originOfAsync(conn, returned)).Origin).IsNull();
    await Assert.That((await _originOfAsync(conn, unproven)).Origin).IsEqualTo(_billing);
  }

  [Test]
  public async Task FromServices_AndItsArguments_AreCheckedAsync() {
    await Assert.That(WrittenHereOriginRestorationStep.FromServices(new ServiceCollection().AddLogging().BuildServiceProvider())).IsNotNull();
    await Assert.That(WrittenHereOriginRestorationStep.FromServices(new ServiceCollection().BuildServiceProvider())).IsNotNull();
    await Assert.That(() => WrittenHereOriginRestorationStep.FromServices(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new WrittenHereOriginRestorationStep(null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await new WrittenHereOriginRestorationStep(NullLogger.Instance).RestoreAsync(null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ThePostgresDriver_RegistersTheStepAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<WrittenHereOriginRestorationStep>().ToList();

    await Assert.That(steps.Count).IsEqualTo(1).Because("the restoration runs in the maintenance cycle, idle-gated, under the maintainer turn");
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

  private readonly ServiceProvider _meters = new ServiceCollection().AddMetrics().BuildServiceProvider();

  [After(Test)]
  public async Task DisposeMetersAsync() => await _meters.DisposeAsync();

  private PostgresArchiveConnection _connection(string? schema = "public") =>
    new(async ct => {
      var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, schema);

  private ServiceProvider _services(StreamIntegrityOptions options, string? serviceName) {
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterFactory: _meters.GetRequiredService<IMeterFactory>()));
    var services = new ServiceCollection()
      .AddSingleton(_connection())
      .AddSingleton(Options.Create(options))
      .AddSingleton(metrics);
    if (serviceName is not null) {
      services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.Empty, serviceName, "host", 1));
    }
    return services.BuildServiceProvider();
  }

  private MeterListener _listen(List<(long Value, string? Shape)> restored, List<long> ambiguous) {
    var factory = _meters.GetRequiredService<IMeterFactory>();
    var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Name is RESTORED_METRIC or AMBIGUOUS_METRIC && instrument.Meter.Scope == factory) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
      if (instrument.Name == AMBIGUOUS_METRIC) {
        ambiguous.Add(value);
        return;
      }
      string? shape = null;
      foreach (var tag in tags) {
        if (tag.Key == "shape") {
          shape = tag.Value as string;
        }
      }
      restored.Add((value, shape));
    });
    listener.Start();
    return listener;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>Stores one event through the outbox emit chain, its first hop written by <paramref name="writer"/>, and
  /// stamps its commit sequence as the stamper would.</summary>
  private static async Task<Guid> _emitHereAsync(NpgsqlConnection conn, ServiceInstanceInfo writer) {
    var instanceId = (Guid)TrackedGuid.New();
    var id = (Guid)TrackedGuid.New();
    var hops = StoredMetadataCorpus.HopsJson([StoredMetadataCorpus.RichHop(writer)]);
    await _executeAsync(conn, """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
         status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
      VALUES
        (@id, 'reaction-topic', @type, 'env',
         jsonb_build_object('id', @id::text, 'p', jsonb_build_object('n', 1), 'h', @hops::jsonb),
         '{}'::jsonb, jsonb_build_object('t', @tenant::text),
         1, 0, @inst, @lease, 0, @sid, true, 0, @now)
      """,
      ("id", id), ("type", REACTION_TYPE), ("hops", hops), ("tenant", TENANT), ("inst", instanceId), ("lease", _t0.AddMinutes(5)),
      ("sid", (Guid)TrackedGuid.New()), ("now", _t0));
    await using (var emit = conn.CreateCommand()) {
      emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
      emit.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, new[] { id });
      emit.Parameters.AddWithValue("inst", instanceId);
      emit.Parameters.AddWithValue("lease", _t0.AddMinutes(5));
      emit.Parameters.AddWithValue("now", _t0);
      _ = await emit.ExecuteScalarAsync();
    }
    await _executeAsync(conn, "UPDATE wh_event_store SET commit_sequence = nextval('wh_commit_seq') WHERE event_id = @id", ("id", id));
    return id;
  }

  /// <summary>Gives a stored event the origin and origin position the pre-594 passes gave it, and leaves the stream's
  /// digest buckets as those passes left them: each event folded into the lane of the origin it is recorded under.</summary>
  private static async Task _misattributeAsync(NpgsqlConnection conn, Guid eventId, Guid origin, long? originSequence) {
    await _executeAsync(conn, "UPDATE wh_event_store SET origin_service_id = @o, origin_commit_sequence = @s WHERE event_id = @e",
      ("o", origin), ("s", (object?)originSequence ?? DBNull.Value), ("e", eventId));
    var stream = await _streamOfAsync(conn, eventId);
    await _executeAsync(conn, """
      DELETE FROM wh_stream_digests WHERE stream_id = @stream;
      SELECT _wh_fold_stream_digests(
        array_agg(COALESCE(origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid)),
        array_agg(COALESCE(scope ->> 't', '')), array_agg(event_type), array_agg(stream_id), array_agg(event_id), 'fold', NOW())
      FROM wh_event_store WHERE stream_id = @stream;
      """, ("stream", stream));
  }

  private static Task _distillAsync(NpgsqlConnection conn) => _executeAsync(conn, "SELECT wh_distill_event_metadata(1000)");

  private static Task _settingAsync(NpgsqlConnection conn, string key, string value) =>
    _executeAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES (@k, @v, 'string')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """, ("k", key), ("v", value));

  private static Task<Guid> _streamOfAsync(NpgsqlConnection conn, Guid eventId) =>
    _scalarAsync<Guid>(conn, "SELECT stream_id FROM wh_event_store WHERE event_id = $1", eventId);

  private static Task<long> _sequenceOfAsync(NpgsqlConnection conn, Guid eventId) =>
    _scalarAsync<long>(conn, "SELECT commit_sequence FROM wh_event_store WHERE event_id = $1", eventId);

  /// <summary>The events held from the origin without its position, as the maintained held fold (452) counts them.</summary>
  private static Task<long> _heldAsync(NpgsqlConnection conn, Guid origin) =>
    _scalarAsync<long>(conn, """
      SELECT (COALESCE((SELECT sum(event_count) FROM wh_unsequenced_digest_totals WHERE origin_service_id = $1), 0)
            + COALESCE((SELECT sum(event_count) FROM wh_unsequenced_digest_changes WHERE origin_service_id = $1), 0))::bigint
      """, origin);

  private static async Task<(long After, long Through)> _windowAsync(NpgsqlConnection conn, string name, int max = 100) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT after_commit_sequence, through_commit_sequence FROM wh_written_here_restoration_window($1, $2)";
    cmd.Parameters.AddWithValue(name);
    cmd.Parameters.AddWithValue(max);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetInt64(1));
  }

  private static async Task<List<(bool Advanced, Guid? Origin, int Reactions, int Returns, int Ambiguous)>> _restoreAsync(
      NpgsqlConnection conn, long after, long through, string name) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT advanced, from_origin, restored_reactions, restored_returns, ambiguous_count
      FROM wh_restore_written_here_origins($1, $2, $3)
      """;
    cmd.Parameters.AddWithValue(after);
    cmd.Parameters.AddWithValue(through);
    cmd.Parameters.AddWithValue(name);
    var rows = new List<(bool, Guid?, int, int, int)>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add((reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));
    }
    return rows;
  }

  /// <summary>One window and its restore: every test store is a single batch.</summary>
  private static async Task<List<(bool Advanced, Guid? Origin, int Reactions, int Returns, int Ambiguous)>> _restoreAllAsync(
      NpgsqlConnection conn, string name) {
    var (after, through) = await _windowAsync(conn, name, 10000);
    return await _restoreAsync(conn, after, through, name);
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
