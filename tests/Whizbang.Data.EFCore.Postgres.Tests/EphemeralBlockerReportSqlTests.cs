// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
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
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2250: the ephemeral history walk names the durable event types that keep an ephemeral type's history from being
/// reclassified (counts, a sample stream, the route that writes them and what to do about each), on by default, and
/// releases a blocking type for the ephemeral type's own streams only when a setting says so.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralHistoryReclassifyStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralBlockerReportText.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralReleaseList.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/392_WhatHoldsEphemeralHistoryBackIsNamed.sql</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public partial class EphemeralBlockerReportSqlTests : EFCoreTestBase {
  private const int EPHEMERAL = 8;
  // Real types, named the way the append path stores them (#2254): the walk names each catalog entry by its Type.
  private static readonly string TYPE = TypeNameFormatter.Format(typeof(SessionPinged));
  private static readonly string TYPE_KEY = TypeNameFormatter.GetFullName(TYPE);
  private static readonly string OPENED = TypeNameFormatter.Format(typeof(SessionOpened));
  private static readonly string CLOSED = TypeNameFormatter.Format(typeof(SessionClosed));
  private static readonly string BATCH = TypeNameFormatter.Format(typeof(BatchGenerated));
  private static readonly string ITEM = TypeNameFormatter.Format(typeof(ItemGenerated));

  // ===== The report =====

  [Test]
  public async Task Report_AMixedStream_NamesTheBlockingType_ItsCounts_ASampleStream_AndItsRouteAsync() {
    await using var conn = await _openAsync();
    var mixed1 = (Guid)TrackedGuid.New();
    var mixed2 = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed1, TYPE);
    await _appendAsync(conn, mixed1, TYPE);
    await _appendAsync(conn, mixed1, OPENED);
    await _appendAsync(conn, mixed2, TYPE);
    await _appendAsync(conn, mixed2, OPENED);
    await _appendAsync(conn, (Guid)TrackedGuid.New(), TYPE);
    await _routeAsync(conn, OPENED, """[{"ty":1,"sn":"upstream","to":"x","cl":"Other","cm":"Run"},{"ty":0,"sn":"sessions-service","to":"sessions","cl":"SessionHandler","cm":"OpenAsync"}]""");
    var logger = new ListLogger();

    var result = await _step(logger).ReclassifyAsync(_services(), CancellationToken.None);

    await Assert.That(result.EventsBlocked).IsEqualTo(3L).Because("precondition: three events stay Sourced in two mixed streams");
    var rows = await _rowsAsync(conn);
    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].Blocking).IsEqualTo(OPENED);
    await Assert.That(rows[0].Events).IsEqualTo(3L);
    await Assert.That(rows[0].Streams).IsEqualTo(2L);
    await Assert.That(rows[0].Bytes).IsGreaterThan(0L).Because("the cost of keeping them is shown as well as the count");
    await Assert.That(rows[0].Sample == mixed1 || rows[0].Sample == mixed2).IsTrue();
    var block = logger.Single($"Ephemeral history of {TYPE} is held back");
    await Assert.That(block).Contains($"{OPENED}: 3 event(s) in 2 stream(s)");
    await Assert.That(block).Contains($"sample stream {rows[0].Sample}");
    await Assert.That(block).Contains("written by service sessions-service, topic sessions, SessionHandler.OpenAsync")
      .Because("the route names the hop that wrote the event (the Current hop), so the operator can find the code");
    await Assert.That(block).Contains($"mark {OPENED.Split(',')[0]} [Ephemeral]");
    await Assert.That(block).Contains($"Whizbang:Ephemeral:ReleaseBlockers:{TYPE_KEY}:0 = {OPENED}")
      .Because("the report prints the exact setting line that releases the pair");
    await Assert.That(block).Contains("keep it");
  }

  [Test]
  public async Task Report_WithoutARoute_OrAReadModel_SaysSoAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed, TYPE);
    await _appendAsync(conn, mixed, OPENED);
    await _routeAsync(conn, CLOSED, """[{"ty":0,"sn":"elsewhere"}]""");
    var logger = new ListLogger();

    await _step(logger).ReclassifyAsync(_services(), CancellationToken.None);

    var block = logger.Single($"Ephemeral history of {TYPE} is held back");
    await Assert.That(block).Contains("no route recorded").Because("only the blocking type's own routes describe it");
    await Assert.That(block).Contains("no registered read model applies it");
  }

  [Test]
  public async Task Report_ARouteWithOnlySomeParts_NamesWhatItHasAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed, TYPE);
    await _appendAsync(conn, mixed, OPENED);
    await _appendAsync(conn, mixed, CLOSED);
    await _routeAsync(conn, OPENED, """[{"sn":"sessions-service","cl":"SessionHandler"}]""");
    await _routeAsync(conn, CLOSED, """[{"ty":0,"cm":"CloseAsync"}]""");
    var logger = new ListLogger();

    await _step(logger).ReclassifyAsync(_services(), CancellationToken.None);

    var block = logger.Single($"Ephemeral history of {TYPE} is held back");
    await Assert.That(block).Contains("by 2 durable event type(s)");
    await Assert.That(block).Contains("written by service sessions-service, SessionHandler");
    await Assert.That(block).Contains("written by CloseAsync");
  }

  [Test]
  public async Task Report_NamesTheReadModelsThatApplyTheBlockingType_NextToTheReleaseLineAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed, TYPE);
    await _appendAsync(conn, mixed, OPENED);
    var registry = new Registry(
      new PerspectiveRegistrationInfo("App.SessionListPerspective", "global::App.SessionListPerspective", "global::App.SessionList",
        [OPENED + ", Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"]),
      new PerspectiveRegistrationInfo("App.AuditPerspective", "global::App.AuditPerspective", "global::App.Audit", [OPENED]),
      new PerspectiveRegistrationInfo("App.OtherPerspective", "global::App.OtherPerspective", "global::App.Other", [CLOSED]));
    var logger = new ListLogger();

    await _step(logger).ReclassifyAsync(_services(registry: registry), CancellationToken.None);

    var block = logger.Single($"Ephemeral history of {TYPE} is held back");
    await Assert.That(block).Contains("read models that apply it lose their rebuild from the reaped events: App.AuditPerspective, App.SessionListPerspective")
      .Because("a read model that applies a released type can no longer be rebuilt from those events once they are reaped");
    await Assert.That(block).DoesNotContain("App.OtherPerspective");
  }

  [Test]
  public async Task Report_TheReleaseLine_TakesTheNextFreeIndex_OfAnExistingListAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed, TYPE);
    await _appendAsync(conn, mixed, OPENED);
    var logger = new ListLogger();

    await _step(logger).ReclassifyAsync(_services(release: new() { [TYPE_KEY] = ["Contracts.Sessions.Unrelated, Contracts"] }), CancellationToken.None);

    await Assert.That(logger.Single($"Ephemeral history of {TYPE} is held back"))
      .Contains($"Whizbang:Ephemeral:ReleaseBlockers:{TYPE_KEY}:1 = {OPENED}")
      .Because("index 0 is already taken by the configured list; the printed line adds to it rather than replacing it");
  }

  [Test]
  public async Task Report_Off_RecordsNothing_LogsNothing_AndTheReclassifyIsUnchangedAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    var blocked = await _appendAsync(conn, mixed, TYPE);
    var opened = await _appendAsync(conn, mixed, OPENED);
    var clean = await _appendAsync(conn, (Guid)TrackedGuid.New(), TYPE);
    var logger = new ListLogger();
    var (metrics, factory) = _metrics();

    var result = await _step(logger).ReclassifyAsync(_services(report: false, metrics: metrics), CancellationToken.None);

    await Assert.That(result.EventsReclassified).IsEqualTo(1L).Because("the control: the walk ran and reclassified the clean stream");
    await Assert.That(result.EventsBlocked).IsEqualTo(1L);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_ephemeral_blockers")).IsEqualTo(0L)
      .Because("with the report off nothing is recorded");
    await Assert.That(logger.Entries.Where(e => e.Contains("held back", StringComparison.Ordinal) || e.Contains("blocker report", StringComparison.Ordinal))).IsEmpty()
      .Because("with the report off nothing is logged");
    await Assert.That(_observe(factory)).IsEmpty().Because("with the report off nothing is gauged");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_type_history_walks WHERE variant IS NOT NULL")).IsEqualTo(0L)
      .Because("the walk runs exactly as it did before the report: same variant, so no restart");
    await Assert.That(await _flagsAsync(conn, blocked)).IsEqualTo(0L);
    await Assert.That(await _flagsAsync(conn, opened)).IsEqualTo(0L);
    await Assert.That(await _flagsAsync(conn, clean)).IsEqualTo((long)EPHEMERAL);
  }

  [Test]
  public async Task Streams_AreCountedOnce_WhenTheirEventsSpanSeveralBatchesAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    for (var i = 0; i < 5; i++) {
      await _appendAsync(conn, mixed, TYPE);
    }
    await _appendAsync(conn, mixed, OPENED);

    var result = await new EphemeralHistoryReclassifyStep(new ListLogger(), 2, 10).ReclassifyAsync(_services(), CancellationToken.None);

    await Assert.That(result.Batches).IsEqualTo(3).Because("precondition: the stream's five events span three batches");
    var rows = await _rowsAsync(conn);
    await Assert.That(rows[0].Events).IsEqualTo(5L);
    await Assert.That(rows[0].Streams).IsEqualTo(1L).Because("a stream is one stream however many batches read its events");
  }

  [Test]
  public async Task Counts_DescribeTheCurrentWalk_AndResetWhenItRestartsAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed, TYPE);
    await _appendAsync(conn, mixed, OPENED);
    var first = await _step(new ListLogger()).ReclassifyAsync(_services(), CancellationToken.None);
    await Assert.That(first.TypesFinished).IsEqualTo(1).Because("precondition: the first walk completed");
    await Assert.That((await _rowsAsync(conn))[0].Events).IsEqualTo(1L);

    await _executeAsync(conn,
      "SELECT * FROM register_type_definition(@n, sha256(convert_to('ephemeral', 'UTF8')), sha256(convert_to('{}', 'UTF8')), 0)",
      ("n", TYPE_KEY));  // the definition registry is keyed by the bare name (#2254)
    var again = await _step(new ListLogger()).ReclassifyAsync(_services(), CancellationToken.None);

    await Assert.That(again.TypesFinished).IsEqualTo(1).Because("the newer definition walked the type again");
    var rows = await _rowsAsync(conn);
    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].Events).IsEqualTo(1L).Because("the counts describe the current walk, not the sum of every walk");
    await Assert.That(rows[0].Streams).IsEqualTo(1L);
  }

  [Test]
  public async Task Counts_OfAnEarlierWalk_AreNotReported_EvenBeforeTheRestartedWalkReadsABatchAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed, TYPE);
    await _appendAsync(conn, mixed, OPENED);
    await _step(new ListLogger()).ReclassifyAsync(_services(), CancellationToken.None);

    // A restart with nothing to read yet: the walk's start moves, and no batch has deleted the old rows.
    await _executeAsync(conn, "UPDATE wh_type_history_walks SET started_at = started_at + INTERVAL '1 second', completed_at = NULL, last_commit_sequence = NULL");

    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_ephemeral_blockers")).IsEqualTo(1L).Because("precondition: the old row is still stored");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_ephemeral_blockers_report()")).IsEqualTo(0L)
      .Because("the report reads only the rows of the type's current walk");
  }

  // ===== The release =====

  [Test]
  public async Task NoRelease_NoDurableEventChangesAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed, TYPE);
    var opened = await _appendAsync(conn, mixed, OPENED);
    var elsewhere = await _appendAsync(conn, (Guid)TrackedGuid.New(), OPENED);

    var result = await _step(new ListLogger()).ReclassifyAsync(_services(), CancellationToken.None);

    await Assert.That(result.EventsBlocked).IsEqualTo(1L).Because("the control: the blocker was found");
    await Assert.That(result.EventsReleased).IsEqualTo(0L);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_event_store WHERE event_type = @t AND (flags & 8) <> 0", ("t", OPENED)))
      .IsEqualTo(0L).Because("the report is the dry run; without a release setting no durable event changes");
    await Assert.That(await _flagsAsync(conn, opened)).IsEqualTo(0L);
    await Assert.That(await _flagsAsync(conn, elsewhere)).IsEqualTo(0L);
  }

  [Test]
  public async Task Release_StampsTheBlockingEvents_OnlyInStreamsHoldingTheEphemeralType_AndTheDigestsEqualARecountAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    var other = (Guid)TrackedGuid.New();
    var pinged = await _appendAsync(conn, mixed, TYPE);
    var opened = await _appendAsync(conn, mixed, OPENED);
    var openedTwice = await _appendAsync(conn, mixed, OPENED);
    var elsewhere = await _appendAsync(conn, other, OPENED);
    var logger = new ListLogger();

    var result = await _step(logger).ReclassifyAsync(_services(release: new() { [TYPE_KEY] = [OPENED] }), CancellationToken.None);

    await Assert.That(result.EventsReclassified).IsEqualTo(1L);
    await Assert.That(result.EventsReleased).IsEqualTo(2L);
    await Assert.That(result.EventsBlocked).IsEqualTo(0L).Because("a released type does not block the ephemeral type");
    await Assert.That(await _flagsAsync(conn, pinged)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, opened)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, openedTwice)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, elsewhere)).IsEqualTo(0L)
      .Because("a release applies only in streams that hold the ephemeral type, never to the blocking type globally");
    await StoredDigestInvariant.AssertAgreeAsync(conn, mixed, other);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_ephemeral_blockers_report()")).IsEqualTo(0L)
      .Because("the released pair's counts drop to zero");
    await Assert.That(logger.Entries.Where(e => e.Contains("held back", StringComparison.Ordinal))).IsEmpty();
  }

  [Test]
  public async Task Release_AStreamStillHeldByAnUnreleasedType_IsLeftWhole_AndReportedForThatTypeOnlyAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    var pinged = await _appendAsync(conn, mixed, TYPE);
    var opened = await _appendAsync(conn, mixed, OPENED);
    var closed = await _appendAsync(conn, mixed, CLOSED);

    var result = await _step(new ListLogger()).ReclassifyAsync(_services(release: new() { [TYPE_KEY] = [OPENED] }), CancellationToken.None);

    await Assert.That(result.EventsBlocked).IsEqualTo(1L);
    await Assert.That(result.EventsReleased).IsEqualTo(0L);
    await Assert.That(await _flagsAsync(conn, pinged)).IsEqualTo(0L);
    await Assert.That(await _flagsAsync(conn, opened)).IsEqualTo(0L)
      .Because("a released type is reclassified only with a stream that becomes wholly ephemeral; a half-released stream would be mixed");
    await Assert.That(await _flagsAsync(conn, closed)).IsEqualTo(0L);
    var rows = await _rowsAsync(conn);
    await Assert.That(rows.Select(r => r.Blocking)).IsEquivalentTo([CLOSED]).Because("the released type no longer counts as a blocker");
  }

  [Test]
  public async Task ChangingTheReleaseList_WalksTheTypeAgain_AndReleasesWhatTheCompletedWalkLeftAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    var pinged = await _appendAsync(conn, mixed, TYPE);
    var opened = await _appendAsync(conn, mixed, OPENED);
    var first = await _step(new ListLogger()).ReclassifyAsync(_services(), CancellationToken.None);
    var unchanged = await _step(new ListLogger()).ReclassifyAsync(_services(), CancellationToken.None);
    await Assert.That(first.TypesFinished).IsEqualTo(1);
    await Assert.That(unchanged.Status).IsEqualTo("complete").Because("the control: a complete walk reads nothing more");

    var released = await _step(new ListLogger()).ReclassifyAsync(_services(release: new() { [TYPE_KEY] = [OPENED] }), CancellationToken.None);

    await Assert.That(released.EventsReleased).IsEqualTo(1L)
      .Because("a new release list restarts the walk; otherwise a completed walk would never apply it");
    await Assert.That(await _flagsAsync(conn, pinged)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, opened)).IsEqualTo((long)EPHEMERAL);
  }

  [Test]
  public async Task Release_NamesWithOrWithoutTheAssembly_MatchTheConfiguredTypesAsync() {
    await using var conn = await _openAsync();
    // A real type: its events are stored under the type's formatted name, and the catalog knows it by its name alone.
    var probe = TypeNameFormatter.Format(typeof(BlockerReleaseProbe));
    var mixed = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed, TYPE);
    var opened = await _appendAsync(conn, mixed, OPENED);
    var released = await _appendAsync(conn, mixed, probe);
    var catalog = new Catalog(_ephemeral(typeof(SessionPinged)),
      new MessageTypeCatalogEntry(typeof(BlockerReleaseProbe), TypeNameFormatter.GetFullName(probe), "event", null));

    // The key in any case and without the assembly; one value with the assembly, one without it (resolved through the
    // catalog to the stored name), and a blank one that is ignored.
    var result = await _step(new ListLogger()).ReclassifyAsync(
      _services(release: new() { [TYPE_KEY.ToUpperInvariant()] = [OPENED, TypeNameFormatter.GetFullName(probe), " "] }, catalog: catalog),
      CancellationToken.None);

    await Assert.That(result.EventsReleased).IsEqualTo(2L);
    await Assert.That(await _flagsAsync(conn, opened)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _flagsAsync(conn, released)).IsEqualTo((long)EPHEMERAL)
      .Because("a value without its assembly reaches the events stored under the type's full stored name");
  }

  [Test]
  public async Task Release_ForAnotherType_OrOfTheTypeItself_ReleasesNothingAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed, TYPE);
    var opened = await _appendAsync(conn, mixed, OPENED);

    var result = await _step(new ListLogger()).ReclassifyAsync(
      _services(release: new() { ["Contracts.Sessions.SomethingElse"] = [OPENED], [TYPE] = [TYPE] }), CancellationToken.None);

    await Assert.That(result.EventsBlocked).IsEqualTo(1L);
    await Assert.That(result.EventsReleased).IsEqualTo(0L).Because("a release is per ephemeral type; another type's list does not apply");
    await Assert.That(await _flagsAsync(conn, opened)).IsEqualTo(0L);
  }

  [Test]
  public async Task Release_ANameIsBoundAsData_NeverSqlTextAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed, TYPE);
    await _appendAsync(conn, mixed, OPENED);
    const string HOSTILE = "x'); DROP TABLE wh_ephemeral_blockers; --";

    var result = await _step(new ListLogger()).ReclassifyAsync(_services(release: new() { [TYPE_KEY] = [HOSTILE] }), CancellationToken.None);

    await Assert.That(result.EventsBlocked).IsEqualTo(1L);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_ephemeral_blockers")).IsEqualTo(1L)
      .Because("the release list is a bound parameter: a name is compared, never executed");
  }

  // ===== Startup summary and gauge =====

  [Test]
  public async Task StartupSummary_IsLoggedOnce_AndTheGaugeReadsTheStoredReportAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed, TYPE);
    await _appendAsync(conn, mixed, OPENED);
    var (metrics, factory) = _metrics();
    var first = new ListLogger();
    var second = new ListLogger();

    await _step(first).ReclassifyAsync(_services(metrics: metrics), CancellationToken.None);
    await _step(second).ReclassifyAsync(_services(metrics: metrics), CancellationToken.None);

    await Assert.That(first.Entries.Count(e => e.StartsWith("Ephemeral blocker report at startup", StringComparison.Ordinal))).IsEqualTo(1);
    await Assert.That(first.Single("Ephemeral blocker report at startup")).Contains($"{TYPE} (walk complete)");
    await Assert.That(second.Entries.Where(e => e.StartsWith("Ephemeral blocker report at startup", StringComparison.Ordinal))).IsEmpty()
      .Because("the summary is once per process; each later cycle only refreshes the gauge");
    await Assert.That(_observe(factory)).IsEquivalentTo([(1L, TYPE, OPENED)]);
  }

  [Test]
  public async Task StartupSummary_WithNothingStored_IsNotLoggedAsync() {
    var (metrics, _) = _metrics();
    var logger = new ListLogger();

    var result = await _step(logger).ReclassifyAsync(_services(metrics: metrics), CancellationToken.None);

    await Assert.That(result.Status).IsEqualTo("finished").Because("the control: the walk ran, over nothing");
    await Assert.That(logger.Entries.Where(e => e.StartsWith("Ephemeral blocker report at startup", StringComparison.Ordinal))).IsEmpty()
      .Because("nothing holds history back, so there is nothing to summarize");
  }

  [Test]
  public async Task StartupSummary_MarksAWalkInProgress_AndTheBlockWaitsForTheWalkToFinishAsync() {
    await using var conn = await _openAsync();
    var mixed = (Guid)TrackedGuid.New();
    await _appendAsync(conn, mixed, TYPE);
    await _appendAsync(conn, mixed, OPENED);
    await _appendAsync(conn, (Guid)TrackedGuid.New(), TYPE);
    var (metrics, _) = _metrics();
    var logger = new ListLogger();

    await new EphemeralHistoryReclassifyStep(logger, 1, 1).ReclassifyAsync(_services(metrics: metrics), CancellationToken.None);

    await Assert.That(logger.Single("Ephemeral blocker report at startup")).Contains($"{TYPE} (walk in progress)");
    await Assert.That(logger.Entries.Where(e => e.Contains("is held back", StringComparison.Ordinal))).IsEmpty()
      .Because("the per-type block is logged when the type's walk finishes");
  }

  // ===== Read models opted into mixing =====

  [Test]
  public async Task MixedReadModel_WithNoDurableInputLeft_IsReportedReadyToDropTheAttributeAsync() {
    await using var conn = await _openAsync();
    // The batch read model applies the ephemeral batch event and a legacy durable per-item event; each stream holds one
    // kind, and no per-item event is left.
    await _appendAsync(conn, (Guid)TrackedGuid.New(), BATCH);
    await _appendAsync(conn, (Guid)TrackedGuid.New(), BATCH);
    var (metrics, _) = _metrics();
    var logger = new ListLogger();

    await _step(logger).ReclassifyAsync(_services(catalog: _batchCatalog(), registry: _batchRegistry(), metrics: metrics), CancellationToken.None);

    var block = logger.Single("Read model App.BatchPerspective");
    await Assert.That(block).Contains("ready to drop the attribute")
      .Because("with no durable input left the read model can become purely ephemeral");
    await Assert.That(block).Contains(ITEM);
    await Assert.That(logger.Entries.Where(e => e.StartsWith("Read model App.PlainPerspective", StringComparison.Ordinal))).IsEmpty()
      .Because("only a read model that carries the attribute is reported");
  }

  [Test]
  public async Task MixedReadModel_WithDurableInputsLeft_ReportsTheirCounts_InTheirOwnStreamsAsync() {
    await using var conn = await _openAsync();
    await _appendAsync(conn, (Guid)TrackedGuid.New(), BATCH);
    await _appendAsync(conn, (Guid)TrackedGuid.New(), ITEM);
    var items = (Guid)TrackedGuid.New();
    await _appendAsync(conn, items, ITEM);
    await _appendAsync(conn, items, ITEM);
    var (metrics, _) = _metrics();
    var logger = new ListLogger();

    await _step(logger).ReclassifyAsync(_services(catalog: _batchCatalog(), registry: _batchRegistry(), metrics: metrics), CancellationToken.None);

    var block = logger.Single("Read model App.BatchPerspective");
    await Assert.That(block).DoesNotContain("ready to drop");
    await Assert.That(block).Contains($"{ITEM}: 3 stored event(s) in 2 stream(s), 0 holding both kinds");
    await Assert.That(block).DoesNotContain("holds back");
  }

  [Test]
  public async Task MixedReadModel_ADurableInputSharingStreams_IsLinkedToItsBlockerEntryAsync() {
    await using var conn = await _openAsync();
    var shared = (Guid)TrackedGuid.New();
    await _appendAsync(conn, shared, BATCH);
    await _appendAsync(conn, shared, ITEM);
    var (metrics, _) = _metrics();
    var logger = new ListLogger();

    await _step(logger).ReclassifyAsync(_services(catalog: _batchCatalog(), registry: _batchRegistry(), metrics: metrics), CancellationToken.None);

    var block = logger.Single("Read model App.BatchPerspective");
    await Assert.That(block).Contains($"{ITEM}: 1 stored event(s) in 1 stream(s), 1 holding both kinds");
    await Assert.That(block).Contains($"holds back the history of {BATCH}")
      .Because("the durable input is the blocker the report above names; the two entries point at each other");
    await Assert.That(block).Contains("Whizbang:Ephemeral:ReleaseBlockers");
  }

  [Test]
  public async Task Report_OnAConnectionThatNamesNoSchema_ReadsTheConnectionsOwnAsync() {
    await using var conn = await _openAsync();
    var shared = (Guid)TrackedGuid.New();
    await _appendAsync(conn, shared, BATCH);
    await _appendAsync(conn, shared, ITEM);
    var (metrics, _) = _metrics();
    var logger = new ListLogger();

    await _step(logger).ReclassifyAsync(
      _services(catalog: _batchCatalog(), registry: _batchRegistry(), metrics: metrics, schema: null), CancellationToken.None);

    await Assert.That(logger.Single("Ephemeral blocker report at startup")).Contains(ITEM)
      .Because("with no schema named the report's functions are read unqualified, on the connection's search path");
    await Assert.That(logger.Single("Read model App.BatchPerspective")).Contains("1 holding both kinds");
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

  private sealed class ListLogger(LogLevel minimum = LogLevel.Trace) : ILogger {
    public List<string> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add(formatter(state, exception));
    public string Single(string prefix) => Entries.Single(e => e.StartsWith(prefix, StringComparison.Ordinal));
  }

  private sealed record Row(string Blocking, long Events, long Streams, long Bytes, Guid Sample);

  private static Catalog _batchCatalog() => new(
    new MessageTypeCatalogEntry(typeof(BatchGenerated), BATCH, "event", null) { Ephemeral = new EphemeralInfo(default, default) },
    new MessageTypeCatalogEntry(typeof(ItemGenerated), ITEM, "event", null));

  private static Registry _batchRegistry() => new(
    new PerspectiveRegistrationInfo("App.BatchPerspective", "global::App.BatchPerspective", "global::App.Batch", [BATCH, ITEM]) {
      AllowsMixedEphemeralAndSourced = true,
    },
    new PerspectiveRegistrationInfo("App.PlainPerspective", "global::App.PlainPerspective", "global::App.Plain", [ITEM]));

  private static MessageTypeCatalogEntry _ephemeral(Type type) =>
    new(type, TypeNameFormatter.FormatClrTypeName(type), "event", null) { Ephemeral = new EphemeralInfo(default, default) };

  private static EphemeralHistoryReclassifyStep _step(ILogger logger) => new(logger, 10, 10);

  private static (EphemeralBlockerMetrics Metrics, IMeterFactory Factory) _metrics() {
    var factory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
    return (new EphemeralBlockerMetrics(new WhizbangMetrics(factory)), factory);
  }

  private static List<(long Value, string Ephemeral, string Blocking)> _observe(IMeterFactory factory) {
    var observed = new List<(long, string, string)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Name == "whizbang.ephemeral.blocked_events" && ReferenceEquals(instrument.Meter.Scope, factory)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      var map = tags.ToArray().ToDictionary(t => t.Key, t => (string)t.Value!);
      observed.Add((value, map["ephemeral_type"], map["blocking_type"]));
    });
    listener.Start();
    listener.RecordObservableInstruments();
    return observed;
  }

  private ServiceProvider _services(
      bool report = true, Dictionary<string, List<string>>? release = null, IMessageTypeCatalog? catalog = null,
      IPerspectiveRunnerRegistry? registry = null, EphemeralBlockerMetrics? metrics = null, string? schema = "public") {
    var options = new EphemeralOptions { ReconcileHistoricalOnStartup = true };
    options.BlockerReport.Enabled = report;
    foreach (var (key, values) in release ?? []) {
      options.ReleaseBlockers[key] = values;
    }
    var services = new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, schema))
      .AddSingleton(Options.Create(options))
      .AddSingleton(catalog ?? new Catalog(_ephemeral(typeof(SessionPinged))));
    if (registry is not null) {
      services.AddSingleton(registry);
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    return services.BuildServiceProvider();
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<List<Row>> _rowsAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT blocking_type, events_blocked, streams_blocked, bytes_blocked, sample_stream_id FROM wh_ephemeral_blockers_report()";
    await using var reader = await cmd.ExecuteReaderAsync();
    var rows = new List<Row>();
    while (await reader.ReadAsync()) {
      rows.Add(new Row(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetGuid(4)));
    }
    return rows;
  }

  private static Task _routeAsync(NpgsqlConnection conn, string type, string hops) =>
    _executeAsync(conn,
      "INSERT INTO wh_routes (route_id, event_type, hops, first_seen, last_seen) VALUES (@id, @t, @h::jsonb, NOW(), NOW())",
      ("id", (Guid)TrackedGuid.New()), ("t", type), ("h", hops));

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

/// <summary>A real event type for the release tests: stored under its formatted name, named without its assembly.</summary>
internal sealed record BlockerReleaseProbe;

/// <summary>A real ephemeral batch event for the mixed read-model tests.</summary>
internal sealed record BatchGenerated;

/// <summary>A real legacy durable per-item event for the mixed read-model tests.</summary>
internal sealed record ItemGenerated;

/// <summary>A real ephemeral event type for the report tests.</summary>
internal sealed record SessionPinged;

/// <summary>A real durable event type that shares streams with <see cref="SessionPinged"/>.</summary>
internal sealed record SessionOpened;

/// <summary>A second real durable event type that shares streams with <see cref="SessionPinged"/>.</summary>
internal sealed record SessionClosed;
