// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Versioning;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2627 (V4 of the vector storage plan): the vector bodies stored before the packing gate opened are packed by a
/// resumable maintenance pass once it is open, and an operator can unpack every stored body for a planned downgrade.
/// The unpack closes the gate and holds it closed, writes each packed vector back as the JSON numbers it was packed
/// from, and once nothing is packed clears the record of packed paths and the journal entry, so an older build is no
/// longer stood down at startup and its reader guard no longer refuses it.
/// </summary>
/// <docs>data/event-store#packing-history</docs>
/// <docs>data/event-store#unpacking-for-a-downgrade</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/StoredVectorConversionStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/750_StoredVectorHistoryIsPackedAndCanBeUnpackedForADowngrade.sql</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class PackedVectorHistorySqlTests : EFCoreTestBase {
  private const string SERVICE = "vector-svc";
  private const string THIS_LIBRARY = "2.0.0";
  private const string OLDER_LIBRARY = "1.0.0";
  private const string BODY_SQL = "SELECT event_data::text FROM wh_event_body WHERE event_id = @e";
  private static readonly JsonSerializerOptions _json = JsonContextRegistry.CreateCombinedOptions();
  private static readonly DateTimeOffset _t0 = new(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);
  private static readonly string _sampleType = TypeNameFormatter.Format(typeof(PackedVectorSampleEvent));

  // ===== Packing the history =====

  [Test]
  public async Task Pack_TheHistoryStoredBeforeTheGateOpened_InBatches_ResumesAfterOneBatch_AndPacksEachBodyOnceAsync() {
    await using var conn = await _openAsync();
    var earlier = await _registerAnEarlierReleaseAsync(conn);
    var ids = new List<Guid>();
    for (var i = 0; i < 5; i++) {
      ids.Add(await _appendAsync(conn, _sample(i)));
    }
    var ordinary = await _appendAsync(conn, new PackedVectorOrdinaryEvent { PassageId = (Guid)TrackedGuid.New(), Amount = 7 });
    var ordinaryBefore = await _scalarAsync<string>(conn, BODY_SQL, ("e", ordinary));
    await _executeAsync(conn, "DELETE FROM wh_service_instances WHERE instance_id = @i", ("i", earlier));
    await _openTheGateAsync();

    var first = await _step(scanSize: 2, maxBatches: 1).ConvertAsync(_services(), CancellationToken.None);
    var packedPartway = await _packedCountAsync(conn, ids);
    var rest = await _step(scanSize: 2, maxBatches: 10).ConvertAsync(_services(), CancellationToken.None);
    var again = await _step(scanSize: 2, maxBatches: 10).ConvertAsync(_services(), CancellationToken.None);

    await Assert.That(first.Mode).IsEqualTo(StoredVectorConversionStep.MODE_PACK);
    await Assert.That(first.Batches).IsEqualTo(1);
    await Assert.That(first.BodiesChanged).IsEqualTo(2L);
    await Assert.That(first.BodiesLeft).IsEqualTo(3L).Because("the walk reports what it has yet to read");
    await Assert.That(packedPartway).IsEqualTo(2).Because("the first cycle stopped after one batch");
    await Assert.That(rest.BodiesChanged).IsEqualTo(3L).Because("the next cycle resumes past the first window");
    await Assert.That(rest.Status).IsEqualTo("finished");
    await Assert.That(again.BodiesChanged).IsEqualTo(0L).Because("each body is packed once");
    await Assert.That(await _packedCountAsync(conn, ids)).IsEqualTo(5);
    for (var i = 0; i < ids.Count; i++) {
      var body = await _scalarAsync<string>(conn, BODY_SQL, ("e", ids[i]));
      await _assertReadsBackAsync(body, _sample(i));
      await Assert.That(JsonNode.Parse(body)!["Centroid"]!.GetValueKind()).IsEqualTo(JsonValueKind.Array)
        .Because("an opted-out property is never packed");
    }
    await Assert.That(await _scalarAsync<string>(conn, BODY_SQL, ("e", ordinary))).IsEqualTo(ordinaryBefore)
      .Because("a type with no vector path is never walked");
  }

  [Test]
  public async Task Pack_KeepsABodyWhosePackedFormWouldStoreLarger_PacksOneThatStoresSmaller_AndFinishesOverTheMixAsync() {
    await using var conn = await _openAsync();
    var earlier = await _registerAnEarlierReleaseAsync(conn);
    var rounded = await _appendAsync(conn, _embedding(1, decimals: 4));
    var roundedBefore = await _scalarAsync<string>(conn, BODY_SQL, ("e", rounded));
    var full = await _appendAsync(conn, _embedding(2, decimals: 0));
    var roundedTwo = await _appendAsync(conn, _embedding(3, decimals: 4));
    await _executeAsync(conn, "DELETE FROM wh_service_instances WHERE instance_id = @i", ("i", earlier));
    await _openTheGateAsync();
    // The gate recorded the registered paths as it opened; the record here is what the history pass writes.
    await _executeAsync(conn, "DELETE FROM wh_packed_vector_paths");

    var keptOnly = await _step(scanSize: 1, maxBatches: 1).ConvertAsync(_services(), CancellationToken.None);
    var recordedAfterKeptOnly = await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_packed_vector_paths");
    var rest = await _step(scanSize: 1, maxBatches: 10).ConvertAsync(_services(), CancellationToken.None);
    var again = await _step(scanSize: 1, maxBatches: 10).ConvertAsync(_services(), CancellationToken.None);

    await Assert.That(keptOnly.BodiesChanged).IsEqualTo(0L).Because("control: the first window holds only the rounded body");
    await Assert.That(keptOnly.BodiesKept).IsEqualTo(1L);
    await Assert.That(recordedAfterKeptOnly).IsEqualTo(0L).Because("a body left as numbers is not a packed body: nothing needs a reader");
    await Assert.That(await _scalarAsync<string>(conn, BODY_SQL, ("e", rounded))).IsEqualTo(roundedBefore)
      .Because("short decimals store smaller as numbers than packed, so the body stays as it was written");
    await Assert.That(rest.BodiesChanged).IsEqualTo(1L);
    await Assert.That(rest.BodiesKept).IsEqualTo(1L);
    await Assert.That(rest.Status).IsEqualTo("finished").Because("the walk moves past a kept body and completes over the mix");
    await Assert.That(await _packedCountAsync(conn, [rounded, full, roundedTwo])).IsEqualTo(1);
    await Assert.That(await _packedCountAsync(conn, [full])).IsEqualTo(1).Because("full-precision floats store smaller packed");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_packed_vector_paths WHERE event_type = @t", ("t", _sampleType)))
      .IsGreaterThan(0L).Because("the batch that packed a body recorded its paths");
    await Assert.That(again.Batches).IsEqualTo(0).Because("a kept body is not read again once the walk is complete");
    await Assert.That(await _scalarAsync<long>(conn,
      "SELECT events_blocked FROM wh_type_history_walks WHERE operation = 'pack-vectors' AND event_type = @t", ("t", _sampleType)))
      .IsEqualTo(2L).Because("the walk records the bodies it kept as numbers");
    await Assert.That(again.BodiesKept).IsEqualTo(0L);

    _ = await _scalarAsync<string>(conn, "SELECT wh_request_vector_unpack('ops', 'planned downgrade')");
    var unpacked = await _step(scanSize: 10, maxBatches: 10).ConvertAsync(_services(), CancellationToken.None);

    await Assert.That(unpacked.Status).IsEqualTo(StoredVectorConversionStep.UNPACKED);
    await Assert.That(await _packedCountAsync(conn, [rounded, full, roundedTwo])).IsEqualTo(0);
    await Assert.That(await _scalarAsync<string>(conn, BODY_SQL, ("e", rounded))).IsEqualTo(roundedBefore);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_packed_vector_paths")).IsEqualTo(0L);
  }

  [Test]
  public async Task Pack_WhileTheGateIsClosed_DoesNothing_AndStartsNoWalkAsync() {
    await using var conn = await _openAsync();
    _ = await _registerAnEarlierReleaseAsync(conn);
    var id = await _appendAsync(conn, _sample(1));
    var before = await _scalarAsync<string>(conn, BODY_SQL, ("e", id));

    var result = await _step(scanSize: 10, maxBatches: 10).ConvertAsync(_services(), CancellationToken.None);

    await Assert.That(result.Mode).IsEqualTo(StoredVectorConversionStep.MODE_CLOSED);
    await Assert.That(result.Batches).IsEqualTo(0);
    await Assert.That(await _scalarAsync<string>(conn, BODY_SQL, ("e", id))).IsEqualTo(before);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_type_history_walks WHERE operation = 'pack-vectors'"))
      .IsEqualTo(0L);
  }

  [Test]
  public async Task Pack_ABatchWhoseGateClosedAfterTheStepLooked_IsSkipped_AndPacksNothingAsync() {
    await using var conn = await _openAsync();
    var id = await _appendAsync(conn, _sample(1));
    await _openTheGateAsync();
    var before = await _scalarAsync<string>(conn, BODY_SQL, ("e", id));
    await _executeAsync(conn, "SELECT wh_request_vector_unpack('ops', 'planned downgrade')");
    // The walk a step began before the request committed: the window is still this call's to apply.
    await _executeAsync(conn, "SELECT wh_type_history_ensure('pack-vectors', @t, @t, NULL)", ("t", _sampleType));

    var status = await _scalarAsync<string>(conn, """
      SELECT status FROM wh_vector_history_pack_apply(@t, NULL, 1000000000, 10, 1, '[["Embedding"]]'::jsonb)
      """, ("t", _sampleType));

    await Assert.That(status).IsEqualTo("skipped (the packing gate is closed)").Because("an unpack closed the gate: a pack batch must never race it");
    await Assert.That(await _scalarAsync<string>(conn, BODY_SQL, ("e", id))).IsEqualTo(before);
  }

  // ===== Unpacking for a downgrade =====

  [Test]
  public async Task Unpack_WritesEveryBodyBack_ByteEqualToItsJsonNumbers_AndClearsTheRecordAndTheLatchAsync() {
    await using var conn = await _openAsync();
    var earlier = await _registerAnEarlierReleaseAsync(conn);
    var historical = await _appendAsync(conn, _sample(1));
    var historicalText = await _scalarAsync<string>(conn, BODY_SQL, ("e", historical));
    await _executeAsync(conn, "DELETE FROM wh_service_instances WHERE instance_id = @i", ("i", earlier));
    await _openTheGateAsync();
    var live = _sample(2);
    var liveId = await _appendAsync(conn, live);
    var liveAsNumbers = await _scalarAsync<string>(conn, "SELECT (@b::jsonb)::text",
      ("b", JsonSerializer.Serialize(live, _json.GetTypeInfo(typeof(PackedVectorSampleEvent)))));
    _ = await _step(scanSize: 10, maxBatches: 10).ConvertAsync(_services(), CancellationToken.None);
    await Assert.That(await _packedCountAsync(conn, [historical, liveId])).IsEqualTo(2).Because("control: both bodies are packed");

    var requested = await _scalarAsync<string>(conn, "SELECT wh_request_vector_unpack('ops', 'planned downgrade')");
    var result = await _step(scanSize: 10, maxBatches: 10).ConvertAsync(_services(), CancellationToken.None);

    await Assert.That(requested).IsEqualTo("requested");
    await Assert.That(result.Mode).IsEqualTo(StoredVectorConversionStep.MODE_UNPACK);
    await Assert.That(result.BodiesChanged).IsEqualTo(2L);
    await Assert.That(result.Status).IsEqualTo(StoredVectorConversionStep.UNPACKED);
    await Assert.That(await _scalarAsync<string>(conn, BODY_SQL, ("e", historical))).IsEqualTo(historicalText)
      .Because("a body packed by the history pass is written back byte-equal to the JSON numbers it held");
    await Assert.That(await _scalarAsync<string>(conn, BODY_SQL, ("e", liveId))).IsEqualTo(liveAsNumbers)
      .Because("a body the write path packed is written back as the JSON numbers its event was written with");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_packed_vector_paths")).IsEqualTo(0L)
      .Because("nothing is packed any more, so no path needs a reader");
    await Assert.That(await _scalarAsync<long>(conn,
      "SELECT count(*) FROM wh_stored_form_migrations WHERE name LIKE 'wh_event_body:packed-vectors%'")).IsEqualTo(0L);
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT completed_at IS NOT NULL FROM wh_vector_unpack_request")).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task Unpack_OnceComplete_AnOlderBuildIsNoLongerStoodDown_AndItsReaderGuardPassesAsync(CancellationToken ct) {
    await using var conn = await _openAsync();
    await _openTheGateAsync();
    // A property packed by this build, which the build being returned to reads as numbers only.
    await _executeAsync(conn, "INSERT INTO wh_packed_vector_paths (event_type, path) VALUES (@t, '[\"Centroid\"]'::jsonb)", ("t", _sampleType));
    var beforeAssess = await _assessor(OLDER_LIBRARY).AssessAsync(ct);
    var beforeGuard = await Assert.That(async () => await _readerGuard().ExecuteAsync(ct)).Throws<PackedVectorsNoLongerReadException>();

    _ = await _scalarAsync<string>(conn, "SELECT wh_request_vector_unpack('ops', 'planned downgrade')");
    var duringAssess = await _assessor(OLDER_LIBRARY).AssessAsync(ct);
    var sameLibraryDuring = await _assessor(THIS_LIBRARY).AssessAsync(ct);
    var partway = await _step(scanSize: 10, maxBatches: 0).ConvertAsync(_services(), ct);
    var guardPartway = await Assert.That(async () => await _readerGuard().ExecuteAsync(ct)).Throws<PackedVectorsNoLongerReadException>();
    var done = await _step(scanSize: 10, maxBatches: 10).ConvertAsync(_services(), ct);
    var afterAssess = await _assessor(OLDER_LIBRARY).AssessAsync(ct);
    var afterGuard = await _readerGuard().ExecuteAsync(ct);

    await Assert.That(beforeAssess.Reason).Contains("stored-form:wh_event_body:packed-vectors (applied by library 2.0.0")
      .Because("control: the latch stands an older library down");
    await Assert.That(beforeGuard!.Paths).IsEquivalentTo([$"{_sampleType}: Centroid"]).Because("control: the guard refuses");
    await Assert.That(duringAssess.Reason).Contains("stored-form:wh_event_body:packed-vectors-unpacking")
      .Because("until every body is unpacked, an older library is still stood down");
    await Assert.That(sameLibraryDuring.Reason ?? string.Empty).DoesNotContain("packed-vectors")
      .Because("the build that requested the unpack reads both forms and keeps running");
    await Assert.That(partway.Status).StartsWith("unpacking").Because("control: a cycle that read nothing finishes nothing");
    await Assert.That(guardPartway!.Paths).IsEquivalentTo([$"{_sampleType}: Centroid"]).Because("the record stays until it is unpacked");
    await Assert.That(done.Status).IsEqualTo(StoredVectorConversionStep.UNPACKED);
    await Assert.That(afterAssess.Reason ?? string.Empty).DoesNotContain("packed-vectors");
    await Assert.That(afterGuard.Outcome).IsEqualTo(StartupStepOutcome.Completed).Because(afterGuard.Reason ?? "no reason");
  }

  [Test]
  public async Task Unpack_HoldsTheGateClosed_UntilPackingIsResumed_ThenTheHistoryIsPackedAgainAsync() {
    await using var conn = await _openAsync();
    await _openTheGateAsync();
    var id = await _appendAsync(conn, _sample(1));
    _ = await _scalarAsync<string>(conn, "SELECT wh_request_vector_unpack('ops', 'planned downgrade')");
    var unpacked = await _step(scanSize: 10, maxBatches: 10).ConvertAsync(_services(), CancellationToken.None);

    var heldGate = await _gateAsync();
    var heldCycle = await _step(scanSize: 10, maxBatches: 10).ConvertAsync(_services(), CancellationToken.None);
    var writtenWhileHeld = await _appendAsync(conn, _sample(2));
    var packedWhileHeld = await _packedCountAsync(conn, [writtenWhileHeld]);
    var resumed = await _scalarAsync<string>(conn, "SELECT wh_resume_vector_packing('ops', 'downgrade called off')");
    var reopened = await _gateAsync();
    var packedAgain = await _step(scanSize: 10, maxBatches: 10).ConvertAsync(_services(), CancellationToken.None);

    await Assert.That(unpacked.Status).IsEqualTo(StoredVectorConversionStep.UNPACKED);
    await Assert.That(heldGate).StartsWith("held").Because("a running build of this release must not pack again before the downgrade");
    await Assert.That(heldCycle.Mode).IsEqualTo(StoredVectorConversionStep.MODE_HELD);
    await Assert.That(heldCycle.Batches).IsEqualTo(0);
    await Assert.That(packedWhileHeld).IsEqualTo(0).Because("the write paths stored numbers while held");
    await Assert.That(resumed).IsEqualTo("resumed");
    await Assert.That(reopened).IsEqualTo(VectorPackingGateStep.OPENED);
    await Assert.That(packedAgain.BodiesChanged).IsEqualTo(2L).Because("the history walk starts again once packing resumes");
    await Assert.That(await _packedCountAsync(conn, [id, writtenWhileHeld])).IsEqualTo(2);
  }

  [Test]
  public async Task Unpack_ResumedBeforeItFinished_KeepsTheLatch_AndTheGateRenamesItBackAsync() {
    await using var conn = await _openAsync();
    await _openTheGateAsync();
    var firstApplied = await _scalarAsync<DateTime>(conn,
      "SELECT first_applied_at FROM wh_stored_form_migrations WHERE name = 'wh_event_body:packed-vectors'");
    _ = await _appendAsync(conn, _sample(1));
    _ = await _scalarAsync<string>(conn, "SELECT wh_request_vector_unpack('ops', 'planned downgrade')");

    var resumed = await _scalarAsync<string>(conn, "SELECT wh_resume_vector_packing('ops', 'downgrade called off')");
    var latchWhileClosed = await _scalarAsync<long>(conn,
      "SELECT count(*) FROM wh_stored_form_migrations WHERE name = 'wh_event_body:packed-vectors-unpacking'");
    var reopened = await _gateAsync();

    await Assert.That(resumed).IsEqualTo("resumed");
    await Assert.That(latchWhileClosed).IsEqualTo(1L).Because("bodies may still be packed, so the latch stays until the gate reopens");
    await Assert.That(reopened).IsEqualTo(VectorPackingGateStep.OPENED);
    await Assert.That(await _scalarAsync<DateTime>(conn,
      "SELECT first_applied_at FROM wh_stored_form_migrations WHERE name = 'wh_event_body:packed-vectors'"))
      .IsEqualTo(firstApplied).Because("the gate renames the same journal entry back, keeping when packing began");
    await Assert.That(await _scalarAsync<long>(conn,
      "SELECT count(*) FROM wh_stored_form_migrations WHERE name = 'wh_event_body:packed-vectors-unpacking'")).IsEqualTo(0L);
  }

  [Test]
  public async Task Unpack_ArchivedBodies_InBatches_AndLeavesOtherTypesAsItFoundThemAsync() {
    await using var conn = await _openAsync();
    await _openTheGateAsync();
    var asNumbers = JsonSerializer.Serialize(_sample(3), _json.GetTypeInfo(typeof(PackedVectorSampleEvent)));
    var expected = await _scalarAsync<string>(conn, "SELECT (@b::jsonb)::text", ("b", asNumbers));
    var archived = new List<Guid>();
    for (var i = 0; i < 3; i++) {
      archived.Add(await _archiveAsync(conn, _sampleType, """_wh_pack_vectors(@b::jsonb, '[["Embedding"], ["Lead", "Embedding"]]'::jsonb)""", asNumbers));
    }
    var other = await _archiveAsync(conn, "Other.Type, Other", "@b::jsonb", """{"Embedding": "P4AAAA=="}""");

    _ = await _scalarAsync<string>(conn, "SELECT wh_request_vector_unpack('ops', 'planned downgrade')");
    var first = await _step(scanSize: 2, maxBatches: 1).ConvertAsync(_services(), CancellationToken.None);
    var rest = await _step(scanSize: 2, maxBatches: 10).ConvertAsync(_services(), CancellationToken.None);

    await Assert.That(first.Status).StartsWith("unpacking").Because("the archive is walked in bounded batches too");
    await Assert.That(rest.Status).IsEqualTo(StoredVectorConversionStep.UNPACKED);
    foreach (var id in archived) {
      await Assert.That(await _scalarAsync<string>(conn, "SELECT event_data::text FROM wh_event_archive WHERE event_id = @e", ("e", id)))
        .IsEqualTo(expected);
    }
    await Assert.That(await _scalarAsync<string>(conn, "SELECT event_data::text FROM wh_event_archive WHERE event_id = @e", ("e", other)))
      .IsEqualTo("""{"Embedding": "P4AAAA=="}""").Because("a type with no packed path holds no packed vector");
  }

  [Test]
  public async Task Request_NamesWhoAndWhy_ASecondIsAbsorbed_AndResumeWithoutOneSaysSoAsync() {
    await using var conn = await _openAsync();

    var notHeld = await _scalarAsync<string>(conn, "SELECT wh_resume_vector_packing('ops', 'nothing to resume')");
    var first = await _scalarAsync<string>(conn, "SELECT wh_request_vector_unpack('ops', 'planned downgrade')");
    var second = await _scalarAsync<string>(conn, "SELECT wh_request_vector_unpack('someone else', 'again')");

    await Assert.That(notHeld).IsEqualTo("not held");
    await Assert.That(first).IsEqualTo("requested");
    await Assert.That(second).StartsWith("already requested").Because("one request holds the gate");
    await Assert.That(second).Contains("ops");
    await Assert.That(await _scalarAsync<string>(conn, "SELECT requested_by || ': ' || reason FROM wh_vector_unpack_request"))
      .IsEqualTo("ops: planned downgrade");
    await Assert.That(async () => await _executeAsync(conn, "SELECT wh_request_vector_unpack('', 'why')"))
      .Throws<PostgresException>();
    await Assert.That(async () => await _executeAsync(conn, "SELECT wh_resume_vector_packing('ops', NULL)"))
      .Throws<PostgresException>();
  }

  [Test]
  public async Task Request_WaitsForEveryWriterThatHoldsABody_SoNoneCanPackAfterItAsync() {
    await using var writer = await _openAsync();
    await using var operatorConnection = await _openAsync();
    await _openTheGateAsync();
    await using var inFlight = await writer.BeginTransactionAsync();
    await using (var hold = writer.CreateCommand()) {
      hold.Transaction = inFlight;
      hold.CommandText = "LOCK TABLE wh_event_body IN ROW EXCLUSIVE MODE";
      _ = await hold.ExecuteNonQueryAsync();
    }
    await _executeAsync(operatorConnection, "SET lock_timeout = '50ms'");

    var refused = await Assert.That(async () => await _executeAsync(operatorConnection, "SELECT wh_request_vector_unpack('ops', 'planned downgrade')"))
      .Throws<PostgresException>();
    await inFlight.RollbackAsync();

    await Assert.That(refused!.SqlState).IsEqualTo(PostgresErrorCodes.LockNotAvailable)
      .Because("the request cannot commit while a writer that may have read the open gate holds a body");
    await Assert.That(await _scalarAsync<long>(operatorConnection, "SELECT count(*) FROM wh_vector_unpack_request")).IsEqualTo(0L);
    await Assert.That(await _scalarAsync<string>(operatorConnection, "SELECT wh_request_vector_unpack('ops', 'planned downgrade')"))
      .IsEqualTo("requested").Because("once the writer is done the request goes through");
  }

  // ===== The step =====

  [Test]
  public async Task Step_IsNamed_BuildsFromServices_AndChecksItsArgumentsAsync() {
    var withLogging = new ServiceCollection().AddLogging().BuildServiceProvider();

    await Assert.That(StoredVectorConversionStep.FromServices(withLogging).Name).IsEqualTo("stored-vector-conversion");
    await Assert.That(StoredVectorConversionStep.FromServices(new ServiceCollection().BuildServiceProvider())).IsNotNull();
    await Assert.That(() => StoredVectorConversionStep.FromServices(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new StoredVectorConversionStep(null!, 1, 1)).Throws<ArgumentNullException>();
    await Assert.That(async () => await StoredVectorConversionStep.FromServices(withLogging).ConvertAsync(null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await StoredVectorConversionStep.FromServices(withLogging).RunAsync(_services(), CancellationToken.None);
    var unqualified = new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, null))
      .BuildServiceProvider();
    await Assert.That((await _step(scanSize: 10, maxBatches: 10).ConvertAsync(unqualified, CancellationToken.None)).Mode)
      .IsEqualTo(StoredVectorConversionStep.MODE_CLOSED).Because("a connection without a schema reads the default one");
  }

  // ===== Helpers =====

  private static StoredVectorConversionStep _step(int scanSize, int maxBatches) => new(NullLogger.Instance, scanSize, maxBatches);

  private static PackedVectorSampleEvent _sample(int seed) {
    var random = new Random(seed);
    float[] vector(int n) => [.. Enumerable.Range(0, n).Select(_ => (float)((random.NextDouble() * 2) - 1))];
    float[] edges = [0.1f, 1f / 3f, float.MaxValue, -float.MaxValue, float.Epsilon, 1.17549435E-38f, 16777217f, 1E-05f, 1E+15f];
    return new PackedVectorSampleEvent {
      PassageId = (Guid)TrackedGuid.New(),
      Text = $"passage {seed}",
      Embedding = [.. edges, .. vector(64)],
      Lead = new PackedVectorChunk("lead", vector(8)),
      Centroid = vector(4),
      Weights = [0.1, 0.2],
    };
  }

  /// <summary>A 3,072-dimension embedding event, at full precision or rounded to a few decimals.</summary>
  private static PackedVectorSampleEvent _embedding(int seed, int decimals) {
    var random = new Random(seed);
    float value() {
      var v = (random.NextDouble() - 0.5) / 10;
      return (float)(decimals == 0 ? v : Math.Round(v, decimals));
    }
    return new PackedVectorSampleEvent {
      PassageId = (Guid)TrackedGuid.New(),
      Text = $"chunk {seed}",
      Embedding = [.. Enumerable.Range(0, 3072).Select(_ => value())],
      Lead = new PackedVectorChunk("lead", [value()]),
    };
  }

  private static async Task _assertReadsBackAsync(string body, PackedVectorSampleEvent expected) {
    var read = (PackedVectorSampleEvent)JsonSerializer.Deserialize(body, _json.GetTypeInfo(typeof(PackedVectorSampleEvent)))!;
    await Assert.That(read.Embedding.Select(BitConverter.SingleToUInt32Bits)).IsEquivalentTo(expected.Embedding.Select(BitConverter.SingleToUInt32Bits));
    await Assert.That(read.Lead.Embedding.Select(BitConverter.SingleToUInt32Bits)).IsEquivalentTo(expected.Lead.Embedding.Select(BitConverter.SingleToUInt32Bits));
    await Assert.That(read.Centroid).IsEquivalentTo(expected.Centroid);
  }

  private static async Task<int> _packedCountAsync(NpgsqlConnection conn, IReadOnlyList<Guid> ids) {
    var packed = 0;
    foreach (var id in ids) {
      var body = JsonNode.Parse(await _scalarAsync<string>(conn, BODY_SQL, ("e", id)))!;
      if (body["Embedding"]!.GetValueKind() == JsonValueKind.String) {
        packed++;
      }
    }
    return packed;
  }

  /// <summary>Stores the event through the direct append and stamps its commit sequence, as the stamper would.</summary>
  private static async Task<Guid> _appendAsync<TEvent>(NpgsqlConnection conn, TEvent evt) where TEvent : IEvent {
    var payload = JsonSerializer.Serialize(evt, _json.GetTypeInfo(typeof(TEvent)));
    var id = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await _executeAsync(conn, """
      SELECT wh_append_event(@id, @sid, @sid, split_part(@type, ',', 1), @type, @body::jsonb, '{}'::jsonb, NULL, @at, 0)
      """, ("id", id), ("sid", stream), ("type", TypeNameFormatter.Format(typeof(TEvent))), ("body", payload), ("at", _t0));
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
    return id;
  }

  private static async Task<Guid> _archiveAsync(NpgsqlConnection conn, string type, string bodyExpression, string body) {
    var id = (Guid)TrackedGuid.New();
    await _executeAsync(conn, $$"""
      INSERT INTO wh_event_archive (event_id, stream_id, event_type, event_data, metadata, version, created_at)
      VALUES (@id, @sid, @type, {{bodyExpression}}, '{}'::jsonb, 1, @at)
      """, ("id", id), ("sid", (Guid)TrackedGuid.New()), ("type", type), ("b", body), ("at", _t0));
    return id;
  }

  private ServiceProvider _services() =>
    new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, "public"))
      .AddSingleton<ILibraryVersionProvider>(new LibraryVersionProvider(THIS_LIBRARY))
      .AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider((Guid)TrackedGuid.New(), SERVICE, "host", 1))
      .BuildServiceProvider();

  private StoredVectorReadersStartupStep _readerGuard() =>
    new(_services(), null, NullLogger<StoredVectorReadersStartupStep>.Instance);

  private EFCorePostgresStartupAssessor _assessor(string library) {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    return new EFCorePostgresStartupAssessor(
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), typeof(WorkCoordinationDbContext),
      new LibraryVersionProvider(library), new ApplicationVersionProvider(ApplicationVersion.FromRecorded(null)));
  }

  private async Task<string> _gateAsync() =>
    await new VectorPackingGateStep(NullLogger<VectorPackingGateStep>.Instance).OpenAsync(_services(), CancellationToken.None);

  private async Task _openTheGateAsync() {
    await _registerThisBuildAsync();
    await Assert.That(await _gateAsync()).IsEqualTo(VectorPackingGateStep.OPENED);
  }

  private async Task _registerThisBuildAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, _json);
    await Assert.That(await coordinator.RecordHeartbeatAsync(new HeartbeatRequest((Guid)TrackedGuid.New(), SERVICE, "this-build", 1)))
      .IsTrue();
  }

  private static async Task<Guid> _registerAnEarlierReleaseAsync(NpgsqlConnection conn) {
    var id = (Guid)TrackedGuid.New();
    await _executeAsync(conn, "SELECT record_heartbeat(@i, 'vector-svc', 'earlier-release', 2, '{\"stored_form_level\": 1}'::jsonb)", ("i", id));
    return id;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var result = await cmd.ExecuteScalarAsync();
    return result is DBNull or null ? default! : (T)result;
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
