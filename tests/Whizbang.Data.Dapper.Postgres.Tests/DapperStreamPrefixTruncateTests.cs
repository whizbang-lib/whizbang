// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The prefix truncate on the Dapper driver, with the contract the EF Core driver keeps: the consumption gate, the
/// carry-forward guard, discarding and archiving, the archive read-back, the truncate's targets, the consuming
/// perspectives the full-history guard asks for, the event version a compaction truncates through, and the apply-path
/// fold that runs first (#2043).
/// </summary>
/// <remarks>
/// <para>
/// This coordinator inherited the interface's defaults for all of them, so a prefix truncate on this driver told the
/// destruction hook, the Pre stages and any compaction handler that data was about to be destroyed, and then reported
/// <c>unsupported</c> having removed nothing. A hook that exported or archived on Proceed did so for data that stayed.
/// </para>
/// <para>
/// Each test has a twin on the EF Core driver (<c>StreamCloseSqlTests</c>, <c>ApplyPathFoldSqlTests</c>); both drivers run
/// the statements in <c>StreamPrefixTruncateSql</c>. Design choice documented at
/// fundamentals/events/event-streams#prefix-truncate-on-every-data-layer.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/event-streams#prefix-truncate-on-every-data-layer</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.PrefixTruncate.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/StreamPrefixTruncateSql.cs</code-under-test>
[Category("Integration")]
public class DapperStreamPrefixTruncateTests : PostgresTestBase {
  private const string DETAIL = "Whizbang.Tests.LedgerEntry";
  private const string CLOSING = "Whizbang.Tests.MonthClosed";

  /// <summary>Records what the truncate told it, and proceeds.</summary>
  private sealed class RecordingHook : IDestructionHook {
    public int Before { get; private set; }
    public int After { get; private set; }

    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      Before++;
      return ValueTask.FromResult(DestructionResult.Proceed());
    }

    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      After++;
      return ValueTask.CompletedTask;
    }
  }

  // Typed as the interface, as the truncator holds it: a member left at the interface default is what it would call.
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Deliberate: the interface is what the truncator calls through, so an unimplemented member reaches its default here as it does there.")]
  private IWorkCoordinator _coordinator() =>
    new DapperWorkCoordinator(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task Truncate_ConsumptionGate_BlocksThenTruncatesAsync() {
    var coordinator = _coordinator();
    var stream = Guid.NewGuid();
    await _associateAsync(DETAIL, "LedgerBalance");
    await _commitAsync(stream, DETAIL, DETAIL, DETAIL, CLOSING);

    var blocked = await coordinator.TruncateStreamPrefixAsync(stream, throughVersion: 3);
    await Assert.That(blocked.Status).IsEqualTo("blocked")
      .Because("a perspective has not processed the events at or below the truncate point");
    await Assert.That(blocked.EventsTruncated).IsEqualTo(0L);
    await Assert.That(await _eventCountAsync(stream)).IsEqualTo(4L);

    await _executeAsync("UPDATE wh_perspective_events SET processed_at = NOW() WHERE stream_id = @stream", new { stream });

    var truncated = await coordinator.TruncateStreamPrefixAsync(stream, throughVersion: 3);
    await Assert.That(truncated.Status).IsEqualTo("truncated")
      .Because("once every perspective has consumed past the truncate point the truncate proceeds, as it does on EF Core");
    await Assert.That(truncated.EventsTruncated).IsEqualTo(3L);
    await Assert.That(await _eventCountAsync(stream)).IsEqualTo(1L)
      .Because("the carry-forward event above the truncate point survives as the new origin");
  }

  [Test]
  public async Task Truncate_NoCarryForward_RefusesTotalTruncationAsync() {
    var stream = Guid.NewGuid();
    await _commitAsync(stream, DETAIL, DETAIL, DETAIL);

    var result = await _coordinator().TruncateStreamPrefixAsync(stream, throughVersion: 3);

    await Assert.That(result.Status).IsEqualTo("no_carry_forward");
    await Assert.That(await _eventCountAsync(stream)).IsEqualTo(3L)
      .Because("a truncate that would leave no surviving origin removes nothing");
  }

  [Test]
  public async Task Truncate_Discard_TruncatesAndLeavesTheArchiveEmptyAsync() {
    var coordinator = _coordinator();
    var stream = Guid.NewGuid();
    await _commitAsync(stream, DETAIL, DETAIL, CLOSING);

    var result = await coordinator.TruncateStreamPrefixAsync(stream, throughVersion: 2, archive: false);

    await Assert.That(result.Status).IsEqualTo("truncated");
    await Assert.That(result.EventsTruncated).IsEqualTo(2L);
    await Assert.That(await _eventCountAsync(stream)).IsEqualTo(1L);
    await Assert.That(await coordinator.GetArchivedEventsAsync(stream)).IsEmpty()
      .Because("a discarding truncate preserves nothing");
  }

  [Test]
  public async Task Truncate_Archive_WritesTheDetailToColdStorage_AndReadsItBackInVersionOrderAsync() {
    var coordinator = _coordinator();
    var stream = Guid.NewGuid();
    var ids = await _commitAsync(stream, DETAIL, DETAIL, CLOSING);

    var result = await coordinator.TruncateStreamPrefixAsync(stream, throughVersion: 2, archive: true);

    await Assert.That(result.Status).IsEqualTo("truncated");
    await Assert.That(await _eventCountAsync(stream)).IsEqualTo(1L)
      .Because("an archiving truncate still removes the detail from the hot store");
    var archived = await coordinator.GetArchivedEventsAsync(stream);
    await Assert.That(archived.Select(a => a.EventId)).IsEquivalentTo([ids[0], ids[1]], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(archived.Select(a => a.Version)).IsEquivalentTo([1L, 2L], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(archived[0].EventType).IsEqualTo(DETAIL);
    await Assert.That(archived[0].EventDataJson!.Contains("OrderId", StringComparison.Ordinal)).IsTrue()
      .Because("the archived body is the real payload");
  }

  [Test]
  public async Task GetStreamTruncateTargets_ListsTheStreamsEventsThroughTheTruncatePointAsync() {
    var stream = Guid.NewGuid();
    var ids = await _commitAsync(stream, DETAIL, "Whizbang.Tests.LedgerAdjusted", CLOSING);
    await _commitAsync(Guid.NewGuid(), DETAIL);

    var targets = await _coordinator().GetStreamTruncateTargetsAsync(stream, throughVersion: 2);

    await Assert.That(targets.Select(t => t.EventId)).IsEquivalentTo([ids[0], ids[1]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("every event at or below the truncate point, in version order, and nothing above it or from another stream");
    await Assert.That(targets.Select(t => t.EventType)).IsEquivalentTo(
      [DETAIL, "Whizbang.Tests.LedgerAdjusted"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(targets.All(t => t.StreamId == stream)).IsTrue();
  }

  [Test]
  public async Task GetConsumingPerspectiveNames_ReturnsTheDistinctPerspectivesInTheRangeAsync() {
    var coordinator = _coordinator();
    var stream = Guid.NewGuid();
    await _associateAsync(DETAIL, "Tests.LedgerListPerspective");
    await _associateAsync(CLOSING, "Tests.ClosingPerspective");
    await _commitAsync(stream, DETAIL, DETAIL, CLOSING);

    var names = await coordinator.GetConsumingPerspectiveNamesAsync(stream, throughVersion: 2);

    await Assert.That(names).IsEquivalentTo(["Tests.LedgerListPerspective"])
      .Because("two events of one associated type are one perspective, and the event above the range is not asked about");
    var unassociated = Guid.NewGuid();
    await _commitAsync(unassociated, "Whizbang.Tests.Unassociated");
    await Assert.That(await coordinator.GetConsumingPerspectiveNamesAsync(unassociated, 10)).IsEmpty();
  }

  [Test]
  public async Task GetEventVersion_ReturnsThePerStreamVersion_NullForAnUnknownEventAsync() {
    var coordinator = _coordinator();
    var ids = await _commitAsync(Guid.NewGuid(), DETAIL, DETAIL);

    await Assert.That(await coordinator.GetEventVersionAsync(ids[0])).IsEqualTo(1L);
    await Assert.That(await coordinator.GetEventVersionAsync(ids[1])).IsEqualTo(2L);
    await Assert.That(await coordinator.GetEventVersionAsync(Guid.NewGuid())).IsNull();
  }

  [Test]
  public async Task FoldStreamApplyPaths_FoldsAStreamOnce_AndAnEmptySetIsNothingAsync() {
    var coordinator = _coordinator();
    var stream = Guid.NewGuid();
    await _commitAsync(stream, "Tests.FoldOpened", "Tests.FoldClosed");

    await Assert.That(await coordinator.FoldStreamApplyPathsAsync([stream])).IsEqualTo(1);
    await Assert.That(await coordinator.FoldStreamApplyPathsAsync([stream])).IsEqualTo(0)
      .Because("the fold watermark folds a stream exactly once");
    await Assert.That(await coordinator.FoldStreamApplyPathsAsync([])).IsEqualTo(0);
    await using var connection = new NpgsqlConnection(ConnectionString);
    await Assert.That(await connection.ExecuteScalarAsync<long>(
        "SELECT stream_count FROM wh_apply_paths WHERE path = ARRAY['Tests.FoldOpened','Tests.FoldClosed']::text[]"))
      .IsEqualTo(1L);
  }

  /// <summary>
  /// The issue's invariant: when the destruction hook was told the truncate would proceed, the truncate happened. On this
  /// driver it used to report <c>unsupported</c> after the hook had been told.
  /// </summary>
  [Test]
  public async Task Truncator_AHookToldProceed_SeesTheDetailActuallyTruncated_AndIsAnnouncedAsync() {
    var stream = Guid.NewGuid();
    await _commitAsync(stream, DETAIL, DETAIL, CLOSING);
    var hook = new RecordingHook();

    var result = await _truncator(hook).TruncateAsync(stream, throughVersion: 2);

    await Assert.That(hook.Before).IsEqualTo(1);
    await Assert.That(result.Status).IsEqualTo("truncated")
      .Because("a hook told the data is about to be destroyed must never be told about a destruction that does not happen");
    await Assert.That(await _eventCountAsync(stream)).IsEqualTo(1L);
    await Assert.That(hook.After).IsEqualTo(1)
      .Because("the PostDestruction hook hears about a truncate that committed");
  }

  [Test]
  public async Task Truncator_ArchiveWithoutAnArchiveStore_ArchivesThroughTheCoordinatorAsync() {
    var coordinator = _coordinator();
    var stream = Guid.NewGuid();
    await _commitAsync(stream, DETAIL, DETAIL, CLOSING);

    var result = await _truncator(new RecordingHook()).TruncateAsync(stream, throughVersion: 2, Disposition.Archive);

    await Assert.That(result.Status).IsEqualTo("truncated");
    await Assert.That(await coordinator.GetArchivedEventsAsync(stream)).Count().IsEqualTo(2)
      .Because("with no IArchiveStore registered an archiving truncate copies into wh_event_archive, as on EF Core");
  }

  private StreamPrefixTruncator _truncator(IDestructionHook hook) =>
    new(_coordinator(), NullLogger<StreamPrefixTruncator>.Instance, hook,
      new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), archiveStore: null);

  /// <summary>Commits one Sourced event per type to the stream, in order, so they get versions 1, 2, 3 and so on.</summary>
  private async Task<Guid[]> _commitAsync(Guid stream, params string[] eventTypes) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var ids = new Guid[eventTypes.Length];
    for (var i = 0; i < eventTypes.Length; i++) {
      ids[i] = Guid.NewGuid();
      await connection.ExecuteAsync("SELECT commit_handler_result(@Req::jsonb)", new { Req = Commit(ids[i], stream, eventTypes[i]) });
    }
    return ids;
  }

  internal static string Commit(Guid eventId, Guid streamId, string eventType) => $$"""
    {
      "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
      "new_outbox_messages": [{
        "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{eventType}}", "EnvelopeType": null,
        "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
        "Metadata": {}, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": 0
      }]
    }
    """;

  private Task _associateAsync(string eventType, string perspective) =>
    _executeAsync(@"
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @eventType, 'perspective', @perspective, 'test-service', @eventType, NOW(), NOW())
      ON CONFLICT DO NOTHING", new { eventType, perspective });

  private async Task _executeAsync(string sql, object parameters) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.ExecuteAsync(sql, parameters);
  }

  private async Task<long> _eventCountAsync(Guid stream) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    return await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_event_store WHERE stream_id = @stream", new { stream });
  }
}
